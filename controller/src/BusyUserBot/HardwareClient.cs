using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BusyUserBot.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BusyUserBot;

/// <summary>
/// Transport abstraction. Implementations either drive the real BLE dongle
/// or no-op for --dry-run.
/// </summary>
public interface IHardwareClient
{
    Task<bool> ConnectAsync(CancellationToken ct);
    Task<HwResponse> SendAsync(IReadOnlyList<HidAction> actions, CancellationToken ct);
    Task ResetAsync(CancellationToken ct);
}

public sealed record HwResponse(bool Ok, int Executed, string? Error);

// ---------------------------------------------------------------------------
// Dry-run client (no BLE, no hardware needed)
// ---------------------------------------------------------------------------
public sealed class DryRunHardwareClient : IHardwareClient
{
    private readonly Action<string> _log;
    public DryRunHardwareClient(Action<string> log) => _log = log;

    public Task<bool> ConnectAsync(CancellationToken ct)
    {
        _log("[dry-run] connect -> ok");
        return Task.FromResult(true);
    }

    public Task<HwResponse> SendAsync(IReadOnlyList<HidAction> actions, CancellationToken ct)
    {
        foreach (var a in actions)
            _log("[dry-run] " + JsonSerializer.Serialize(a));
        return Task.FromResult(new HwResponse(true, actions.Count, null));
    }

    public Task ResetAsync(CancellationToken ct)
    {
        _log("[dry-run] reset");
        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------------------
// BLE client (WinRT)
// ---------------------------------------------------------------------------
public sealed class BleHardwareClient : IHardwareClient, IDisposable
{
    // Must match firmware/BusyUserBot/BusyUserBot.ino.
    private static readonly Guid SvcUuid  = new("6e601000-b5a3-f393-e0a9-e50e24dcca9e");
    private static readonly Guid AuthUuid = new("6e601001-b5a3-f393-e0a9-e50e24dcca9e");
    private static readonly Guid CmdUuid  = new("6e601002-b5a3-f393-e0a9-e50e24dcca9e");
    private static readonly Guid StatUuid = new("6e601003-b5a3-f393-e0a9-e50e24dcca9e");

    private readonly DongleConfig _cfg;
    private readonly Action<DongleConfig>? _persistConfig;
    private readonly Action<string> _log;

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _authChar;
    private GattCharacteristic? _cmdChar;
    private GattCharacteristic? _statChar;

    // Notification reassembly.
    private readonly object _rxLock = new();
    private readonly List<byte> _rxBuf = new();
    private int _rxExpected;
    private TaskCompletionSource<string>? _pendingResponse;
    private bool _authed;

    public BleHardwareClient(DongleConfig cfg, Action<string> log, Action<DongleConfig>? persistConfig = null)
    {
        _cfg = cfg;
        _log = log;
        _persistConfig = persistConfig;
    }

    public Task<bool> ConnectAsync(CancellationToken ct) => ConnectAsync(ct, allowIdResetRetry: true);

    private async Task<bool> ConnectAsync(CancellationToken ct, bool allowIdResetRetry)
    {
        // Idempotent: if a previous ConnectAsync (e.g. the "Test HW" button)
        // already brought the link up and authed, do not tear it down and
        // reopen it — that round-trips through FromIdAsync /
        // GetGattServicesForUuidAsync again and on Windows reliably trips
        // the 0x80070016 stale-cache path, after which the characteristics
        // come back null and the loop aborts.
        if (_authed && _device is not null
            && _authChar is not null && _cmdChar is not null && _statChar is not null
            && _device.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            _log("BLE: already connected; reusing existing link.");
            return true;
        }

        // If a previous attempt partially initialized fields, start from a
        // clean baseline before opening a new link.
        if (_device is not null || _authChar is not null || _cmdChar is not null || _statChar is not null)
            await ResetAsync(ct);

        string step = "init";
        try
        {
            // 1. Resolve the device: cached id first, otherwise scan paired list by name.
            BluetoothLEDevice? dev = null;
            if (!string.IsNullOrEmpty(_cfg.DeviceId))
            {
                _log("BLE: opening cached device id");
                step = "open-cached-device";
                dev = await BluetoothLEDevice.FromIdAsync(_cfg.DeviceId).AsTask(ct);
            }
            if (dev is null)
            {
                _log($"BLE: searching paired devices for '{_cfg.Name}'");
                step = "find-paired";
                dev = await FindPairedAsync(_cfg.Name, ct);
                if (dev is not null && _persistConfig is not null)
                {
                    _cfg.DeviceId = dev.DeviceId;
                    _persistConfig(_cfg);
                }
            }
            if (dev is null)
            {
                _log("BLE: device not found. Pair it once via Windows Settings > Bluetooth.");
                return false;
            }
            _device = dev;
            _device.ConnectionStatusChanged += OnConnectionChanged;

            // 2. Resolve service + characteristics. Try Uncached first; if the
            //    Windows GATT cache is stale (firmware reflashed, services
            //    changed) Uncached can return 0x80070016 ERROR_BAD_COMMAND.
            //    The reliable fix is to dispose the device handle, reopen it,
            //    and retry Uncached. Cached mode is a last-resort fallback.
            step = "get-service";
            GattDeviceServicesResult? svcResult = null;
            try
            {
                svcResult = await _device.GetGattServicesForUuidAsync(SvcUuid, BluetoothCacheMode.Uncached).AsTask(ct);
            }
            catch (Exception ex) when ((uint)ex.HResult == 0x80070016)
            {
                _log("BLE: GATT cache stale (0x80070016). Reopening device handle.");
                _device.ConnectionStatusChanged -= OnConnectionChanged;
                var id = _device.DeviceId;
                _device.Dispose();
                _device = await BluetoothLEDevice.FromIdAsync(id).AsTask(ct);
                if (_device is null)
                {
                    _log("BLE: failed to reopen device after cache reset.");
                    return false;
                }
                _device.ConnectionStatusChanged += OnConnectionChanged;
                try
                {
                    svcResult = await _device.GetGattServicesForUuidAsync(SvcUuid, BluetoothCacheMode.Uncached).AsTask(ct);
                }
                catch (Exception ex2) when ((uint)ex2.HResult == 0x80070016)
                {
                    _log("BLE: still 0x80070016 after reopen. Falling back to Cached mode.");
                    svcResult = await _device.GetGattServicesForUuidAsync(SvcUuid, BluetoothCacheMode.Cached).AsTask(ct);
                }
            }
            if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            {
                _log($"BLE: service not found (status={svcResult.Status}). " +
                     "If status=AccessDenied, another app holds the device; close it and retry. " +
                     "If status=Unreachable, the dongle is powered off or out of range.");
                if (allowIdResetRetry && await RetryWithClearedDeviceIdAsync(
                    "BLE: service lookup failed using cached device id.", ct))
                    return await ConnectAsync(ct, allowIdResetRetry: false);

                if (!string.IsNullOrEmpty(_cfg.DeviceId) && _persistConfig is not null)
                {
                    _cfg.DeviceId = "";
                    _persistConfig(_cfg);
                    _log("BLE: cleared cached device id; remove + re-pair the dongle in Windows Bluetooth settings, then retry.");
                }
                return false;
            }
            var svc = svcResult.Services[0];

            step = "get-characteristics";
            _authChar = await GetCharAsync(svc, AuthUuid, ct);
            _cmdChar  = await GetCharAsync(svc, CmdUuid,  ct);
            _statChar = await GetCharAsync(svc, StatUuid, ct);
            if (_authChar is null || _cmdChar is null || _statChar is null)
            {
                _log("BLE: characteristics missing in Uncached lookup; retrying Cached.");
                _authChar ??= await GetCharAsync(svc, AuthUuid, ct, BluetoothCacheMode.Cached);
                _cmdChar  ??= await GetCharAsync(svc, CmdUuid,  ct, BluetoothCacheMode.Cached);
                _statChar ??= await GetCharAsync(svc, StatUuid, ct, BluetoothCacheMode.Cached);
            }
            if (_authChar is null || _cmdChar is null || _statChar is null)
            {
                _log("BLE: required characteristics missing. Remove + re-pair the dongle in Windows Bluetooth settings.");
                if (allowIdResetRetry && await RetryWithClearedDeviceIdAsync(
                    "BLE: required characteristics missing using cached device id.", ct))
                    return await ConnectAsync(ct, allowIdResetRetry: false);

                if (!string.IsNullOrEmpty(_cfg.DeviceId) && _persistConfig is not null)
                {
                    _cfg.DeviceId = "";
                    _persistConfig(_cfg);
                }
                return false;
            }

            // 3. Subscribe to notifications.
            step = "subscribe-notify";
            _statChar.ValueChanged += OnNotify;
            var cccd = await _statChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
            if (cccd != GattCommunicationStatus.Success)
            {
                _log($"BLE: notify subscribe failed ({cccd})");
                return false;
            }

            // 4. Authenticate. The firmware requires encryption to write to AUTH,
            //    which triggers the Windows pairing prompt the first time.
            step = "auth-write";
            _pendingResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var w = await WriteAsync(_authChar, Encoding.UTF8.GetBytes(_cfg.Token), withResponse: true, ct);
            if (w != GattCommunicationStatus.Success)
            {
                _log($"BLE: AUTH write failed ({w}). Pair the device in Windows Bluetooth settings, then retry.");
                return false;
            }

            step = "auth-response";
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var hello = await _pendingResponse.Task.WaitAsync(to.Token);
                _log($"BLE: hello {hello}");
                using var doc = JsonDocument.Parse(hello);
                if (!doc.RootElement.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
                {
                    _log("BLE: auth rejected by dongle.");
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                _log("BLE: timed out waiting for auth response.");
                return false;
            }

            _log("BLE: connected and authed.");
            _authed = true;
            return true;
        }
        catch (Exception ex)
        {
            _log($"BLE connect failed at step '{step}': {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}".TrimEnd());
            if (allowIdResetRetry && await RetryWithClearedDeviceIdAsync(
                "BLE: connect failed while using cached device id.", ct))
                return await ConnectAsync(ct, allowIdResetRetry: false);

            if ((uint)ex.HResult == 0x80070016 && !string.IsNullOrEmpty(_cfg.DeviceId) && _persistConfig is not null)
            {
                _cfg.DeviceId = "";
                _persistConfig(_cfg);
                _log("BLE: cleared cached device id. Remove the dongle in Windows Bluetooth settings, re-pair it, then retry.");
            }
            return false;
        }
    }

    public async Task<HwResponse> SendAsync(IReadOnlyList<HidAction> actions, CancellationToken ct)
    {
        if (_cmdChar is null) return new HwResponse(false, 0, "not connected");

        var json = JsonSerializer.Serialize(new CommandRequest(actions));
        var body = Encoding.UTF8.GetBytes(json);
        if (body.Length > ushort.MaxValue) return new HwResponse(false, 0, "payload too large");

        // 2-byte LE length prefix + body.
        var framed = new byte[2 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(0, 2), (ushort)body.Length);
        System.Buffer.BlockCopy(body, 0, framed, 2, body.Length);

        _pendingResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 180 bytes is safe for ATT_MTU=185+ (which the firmware requests at 247).
        // The firmware reassembles across writes.
        const int chunk = 180;
        for (int off = 0; off < framed.Length; off += chunk)
        {
            int len = Math.Min(chunk, framed.Length - off);
            var slice = framed.AsSpan(off, len).ToArray();
            var status = await WriteAsync(_cmdChar, slice, withResponse: false, ct);
            if (status != GattCommunicationStatus.Success)
                return new HwResponse(false, 0, $"write failed: {status}");
        }

        try
        {
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(15));
            var raw = await _pendingResponse.Task.WaitAsync(to.Token);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            int executed = root.TryGetProperty("executed", out var exEl) && exEl.TryGetInt32(out var n) ? n : 0;
            string? err = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
            return new HwResponse(ok, executed, err);
        }
        catch (OperationCanceledException)
        {
            return new HwResponse(false, 0, "timeout waiting for dongle response");
        }
    }

    public Task ResetAsync(CancellationToken ct)
    {
        // The firmware releases all held keys/buttons automatically on
        // disconnect, so the simplest reliable reset is to drop the link.
        try
        {
            if (_statChar is not null) _statChar.ValueChanged -= OnNotify;
            if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionChanged;
            _device?.Dispose();
        }
        catch { /* best-effort */ }
        _device = null;
        _authChar = _cmdChar = _statChar = null;
        _pendingResponse = null;
        lock (_rxLock)
        {
            _rxBuf.Clear();
            _rxExpected = 0;
        }
        _authed = false;
        return Task.CompletedTask;
    }

    private async Task<bool> RetryWithClearedDeviceIdAsync(string reason, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_cfg.DeviceId)) return false;

        _log(reason + " Clearing cached id and retrying once via paired-device lookup.");
        _cfg.DeviceId = "";
        _persistConfig?.Invoke(_cfg);
        await ResetAsync(ct);
        return true;
    }

    public void Dispose() => ResetAsync(CancellationToken.None).GetAwaiter().GetResult();

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static async Task<GattCharacteristic?> GetCharAsync(GattDeviceService svc, Guid uuid, CancellationToken ct,
        BluetoothCacheMode mode = BluetoothCacheMode.Uncached)
    {
        var r = await svc.GetCharacteristicsForUuidAsync(uuid, mode).AsTask(ct);
        return r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0
            ? r.Characteristics[0]
            : null;
    }

    private static async Task<GattCommunicationStatus> WriteAsync(
        GattCharacteristic c, byte[] data, bool withResponse, CancellationToken ct)
    {
        var writer = new DataWriter();
        writer.WriteBytes(data);
        var opt = withResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        return await c.WriteValueAsync(writer.DetachBuffer(), opt).AsTask(ct);
    }

    private void OnConnectionChanged(BluetoothLEDevice sender, object args)
    {
        _log($"BLE: connection status = {sender.ConnectionStatus}");
    }

    private void OnNotify(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);

        string? completePayload = null;
        lock (_rxLock)
        {
            int off = 0;
            while (off < bytes.Length)
            {
                if (_rxExpected == 0)
                {
                    while (_rxBuf.Count < 2 && off < bytes.Length) _rxBuf.Add(bytes[off++]);
                    if (_rxBuf.Count < 2) return;
                    _rxExpected = _rxBuf[0] | (_rxBuf[1] << 8);
                    _rxBuf.Clear();
                }
                int need = _rxExpected - _rxBuf.Count;
                int take = Math.Min(need, bytes.Length - off);
                for (int i = 0; i < take; i++) _rxBuf.Add(bytes[off + i]);
                off += take;
                if (_rxBuf.Count == _rxExpected)
                {
                    completePayload = Encoding.UTF8.GetString(_rxBuf.ToArray());
                    _rxBuf.Clear();
                    _rxExpected = 0;
                    break;
                }
            }
        }

        if (completePayload is not null)
        {
            _log($"BLE rx: {completePayload}");
            _pendingResponse?.TrySetResult(completePayload);
        }
    }

    /// <summary>
    /// Find an already-paired BLE device whose name matches. We never initiate
    /// pairing from code — the user pairs once via Windows Settings, then the
    /// app connects by id from then on.
    /// </summary>
    private static async Task<BluetoothLEDevice?> FindPairedAsync(string name, CancellationToken ct)
    {
        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var devices = await DeviceInformation.FindAllAsync(selector).AsTask(ct);
        foreach (var di in devices)
        {
            if (string.Equals(di.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                var d = await BluetoothLEDevice.FromIdAsync(di.Id).AsTask(ct);
                if (d is not null) return d;
            }
        }
        return null;
    }
}
