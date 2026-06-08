using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BusyUserBot.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
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
    private GattSession? _session;
    private GattCharacteristic? _authChar;
    private GattCharacteristic? _cmdChar;
    private GattCharacteristic? _statChar;

    // Notification reassembly.
    private readonly object _rxLock = new();
    private readonly List<byte> _rxBuf = new();
    private int _rxExpected;
    private TaskCompletionSource<string>? _pendingResponse;
    private Func<string, bool>? _pendingResponseFilter;
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
            // 1. Resolve the device by live advertisement first. Cached WinRT
            //    ids can retain stale GATT tables across firmware reflashes.
            BluetoothLEDevice? dev = null;
            _log($"BLE: searching BLE devices for '{_cfg.Name}'");
            step = "find-by-name";
            dev = await FindByNameAsync(_cfg.Name, ct);
            if (!string.IsNullOrEmpty(_cfg.DeviceId))
            {
                if (dev is null)
                {
                    _log("BLE: opening cached device id");
                    step = "open-cached-device";
                    dev = await BluetoothLEDevice.FromIdAsync(_cfg.DeviceId).AsTask(ct);
                }
                else
                {
                    _log("BLE: live advertisement found; ignoring cached device id for this connection.");
                }
            }
            if (dev is null)
            {
                _log("BLE: device not found. Make sure the dongle is powered and advertising, then retry.");
                return false;
            }
            _device = dev;
            _device.ConnectionStatusChanged += OnConnectionChanged;

            step = "read-pairing-state";
            await LogPairingStateAsync(_device, ct);

            step = "open-gatt-session";
            await OpenGattSessionAsync(_device, ct);

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
                await OpenGattSessionAsync(_device, ct);
                try
                {
                    svcResult = await _device.GetGattServicesForUuidAsync(SvcUuid, BluetoothCacheMode.Uncached).AsTask(ct);
                }
                catch (Exception ex2) when ((uint)ex2.HResult == 0x80070016)
                {
                    // A handle reopen was not enough: Windows is still serving the
                    // attribute table it cached *before* the firmware reflash. The
                    // Cached fallback would bind to those dead handles — the AUTH
                    // write then "succeeds" against a stale handle but the firmware
                    // never sees it, so no STATUS notification ever comes back and
                    // auth times out (exactly the failure in the connect logs).
                    // Flush the cache by unpairing and re-discovering from scratch;
                    // only use Cached as a true last resort.
                    _log("BLE: still 0x80070016 after reopen. Stale GATT cache from before the reflash; flushing via unpair.");
                    if (await FlushGattCacheByUnpairAsync(ct))
                    {
                        try
                        {
                            svcResult = await _device!.GetGattServicesForUuidAsync(SvcUuid, BluetoothCacheMode.Uncached).AsTask(ct);
                        }
                        catch (Exception ex3) when ((uint)ex3.HResult == 0x80070016)
                        {
                            _log("BLE: still 0x80070016 after unpair + rediscover. Falling back to Cached mode (last resort).");
                            svcResult = await _device!.GetGattServicesForUuidAsync(SvcUuid, BluetoothCacheMode.Cached).AsTask(ct);
                        }
                    }
                    else
                    {
                        _log("BLE: cache flush failed; falling back to Cached mode (may use stale handles after a reflash).");
                        svcResult = await _device.GetGattServicesForUuidAsync(SvcUuid, BluetoothCacheMode.Cached).AsTask(ct);
                    }
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
                    _log("BLE: cleared cached device id; power-cycle the dongle and retry while it is advertising.");
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
                _log("BLE: required characteristics missing. Power-cycle the dongle and retry while it is advertising.");
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

            // 4. Authenticate with the firmware-level token before sending any
            //    HID commands.
            step = "auth-write";
            _pendingResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            GattCommunicationStatus w;
            try
            {
                w = await WriteAsync(_authChar, Encoding.UTF8.GetBytes(_cfg.Token), withResponse: true, ct);
            }
            catch (OperationCanceledException ex) when ((uint)ex.HResult == 0x800704C7)
            {
                _log("BLE: AUTH write was canceled by Windows. If this firmware was just reflashed, remove any stuck Windows Bluetooth entry, power-cycle the dongle, then retry while it is advertising.");
                return false;
            }
            if (w != GattCommunicationStatus.Success)
            {
                _log($"BLE: AUTH write failed ({w}). Power-cycle the dongle and retry while it is advertising.");
                return false;
            }

            step = "auth-response";
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                var hello = await _pendingResponse.Task.WaitAsync(to.Token);
                _pendingResponse = null;
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
                _pendingResponse = null;
                _log("BLE: auth hello not received; probing token auth with a no-op command.");
                step = "auth-probe";
                await Task.Delay(150, ct);
                var probe = await SendAsync(new[] { new HidAction("wait", Ms: 0) }, ct);
                if (!probe.Ok)
                {
                    _log("BLE: auth probe failed: " + (probe.Error ?? "unknown"));
                    return false;
                }
            }

            _log("BLE: connected and authed.");
            _authed = true;
            if (_device is not null && _persistConfig is not null && _cfg.DeviceId != _device.DeviceId)
            {
                _cfg.DeviceId = _device.DeviceId;
                _persistConfig(_cfg);
            }
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
                _log("BLE: cleared cached device id. Remove any stuck Windows Bluetooth entry, power-cycle the dongle, then retry while it is advertising.");
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
        _pendingResponseFilter = IsCommandResponse;

        // 180 bytes is safe for ATT_MTU=185+ (which the firmware requests at 247).
        // The firmware reassembles across writes. Use acknowledged writes here;
        // some Windows BLE stacks silently drop WriteWithoutResponse after a
        // cache-stale reconnect path.
        const int chunk = 180;
        for (int off = 0; off < framed.Length; off += chunk)
        {
            int len = Math.Min(chunk, framed.Length - off);
            var slice = framed.AsSpan(off, len).ToArray();
            var status = await WriteAsync(_cmdChar, slice, withResponse: true, ct);
            if (status != GattCommunicationStatus.Success)
            {
                _pendingResponse = null;
                _pendingResponseFilter = null;
                return new HwResponse(false, 0, $"write failed: {status}");
            }
        }

        try
        {
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(15));
            var raw = await _pendingResponse.Task.WaitAsync(to.Token);
            return ParseHwResponse(raw);
        }
        catch (OperationCanceledException)
        {
            _log("BLE: notification timed out; reading latest STATUS value.");
            var raw = await ReadLatestStatusAsync(ct);
            if (raw is not null && IsCommandResponse(raw))
            {
                _log($"BLE read: {raw}");
                return ParseHwResponse(raw);
            }
            return new HwResponse(false, 0, "timeout waiting for dongle response");
        }
        finally
        {
            _pendingResponse = null;
            _pendingResponseFilter = null;
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
            _session?.Dispose();
            _device?.Dispose();
        }
        catch { /* best-effort */ }
        _session = null;
        _device = null;
        _authChar = _cmdChar = _statChar = null;
        _pendingResponse = null;
        _pendingResponseFilter = null;
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

        _log(reason + " Clearing cached id and retrying once via BLE name lookup.");
        _cfg.DeviceId = "";
        _persistConfig?.Invoke(_cfg);
        await ResetAsync(ct);
        return true;
    }

    /// <summary>
    /// Recover from a Windows GATT cache that stays stale (HRESULT 0x80070016)
    /// even after the device handle is reopened. This is the classic
    /// "firmware reflashed under a bonded peer" case: Windows keeps the old
    /// attribute table forever, so Uncached reads keep failing and Cached
    /// reads return dead handles that silently swallow writes. Unpairing
    /// forces Windows to forget the cached GATT database. The firmware uses
    /// token auth and does not require pairing, so we can immediately
    /// re-discover the device from a live advertisement and start clean.
    /// Returns true if a fresh device handle is ready for another Uncached
    /// service lookup.
    /// </summary>
    private async Task<bool> FlushGattCacheByUnpairAsync(CancellationToken ct)
    {
        if (_device is null) return false;
        var deviceName = _cfg.Name;

        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(_device.DeviceId).AsTask(ct);
            if (info?.Pairing is not null && info.Pairing.IsPaired)
            {
                var result = await info.Pairing.UnpairAsync().AsTask(ct);
                _log($"BLE: unpaired to flush stale GATT cache (status={result.Status}).");
            }
            else
            {
                _log("BLE: device is not paired; cannot flush GATT cache via unpair.");
                return false;
            }
        }
        catch (Exception ex)
        {
            _log($"BLE: unpair failed ({ex.GetType().Name} 0x{ex.HResult:X8}).");
            return false;
        }

        // Drop the stale handle (and any cached device id) and re-resolve the
        // dongle from a live advertisement so the next discovery is clean.
        try
        {
            _device.ConnectionStatusChanged -= OnConnectionChanged;
            _session?.Dispose();
            _session = null;
            _device.Dispose();
            _device = null;
        }
        catch { /* best-effort */ }

        if (!string.IsNullOrEmpty(_cfg.DeviceId) && _persistConfig is not null)
        {
            _cfg.DeviceId = "";
            _persistConfig(_cfg);
        }

        var fresh = await FindByNameAsync(deviceName, ct);
        if (fresh is null)
        {
            _log("BLE: device not re-found after unpair; power-cycle the dongle and retry while it is advertising.");
            return false;
        }

        _device = fresh;
        _device.ConnectionStatusChanged += OnConnectionChanged;
        await OpenGattSessionAsync(_device, ct);
        return true;
    }

    public void Dispose() => ResetAsync(CancellationToken.None).GetAwaiter().GetResult();

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private async Task LogPairingStateAsync(BluetoothLEDevice device, CancellationToken ct)
    {
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(device.DeviceId).AsTask(ct);
            if (info?.Pairing is null)
            {
                _log("BLE: pairing status unavailable; continuing with token auth.");
                return;
            }

            if (info.Pairing.IsPaired)
                _log($"BLE: paired (protection={info.Pairing.ProtectionLevel}); continuing with token auth.");
            else
                _log("BLE: not paired; continuing with token auth.");
        }
        catch (Exception ex)
        {
            _log($"BLE: pairing status check skipped ({ex.GetType().Name} 0x{ex.HResult:X8}).");
        }
    }

    private async Task OpenGattSessionAsync(BluetoothLEDevice device, CancellationToken ct)
    {
        try
        {
            _session?.Dispose();
            var bluetoothDeviceId = BluetoothDeviceId.FromId(device.DeviceId);
            _session = await GattSession.FromDeviceIdAsync(bluetoothDeviceId).AsTask(ct);
            if (_session is not null)
            {
                _session.MaintainConnection = true;
                _log("BLE: GATT session opened; maintaining connection.");
            }
        }
        catch (Exception ex)
        {
            _log($"BLE: GATT session setup skipped ({ex.GetType().Name} 0x{ex.HResult:X8}).");
        }
    }

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

    private async Task<string?> ReadLatestStatusAsync(CancellationToken ct)
    {
        if (_statChar is null) return null;

        foreach (var mode in new[] { BluetoothCacheMode.Uncached, BluetoothCacheMode.Cached })
        {
            try
            {
                var result = await _statChar.ReadValueAsync(mode).AsTask(ct);
                if (result.Status != GattCommunicationStatus.Success) continue;
                var reader = DataReader.FromBuffer(result.Value);
                var bytes = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(bytes);
                var payload = DecodeSingleFramedPayload(bytes);
                if (payload is not null) return payload;
            }
            catch (Exception ex)
            {
                _log($"BLE: STATUS read failed ({mode}, {ex.GetType().Name} 0x{ex.HResult:X8}).");
            }
        }

        return null;
    }

    private static HwResponse ParseHwResponse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
        int executed = root.TryGetProperty("executed", out var exEl) && exEl.TryGetInt32(out var n) ? n : 0;
        string? err = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
        return new HwResponse(ok, executed, err);
    }

    private static string? DecodeSingleFramedPayload(byte[] bytes)
    {
        if (bytes.Length < 2) return null;
        int expected = bytes[0] | (bytes[1] << 8);
        if (expected <= 0 || bytes.Length < expected + 2) return null;
        return Encoding.UTF8.GetString(bytes, 2, expected);
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
            if (_pendingResponse is not null
                && (_pendingResponseFilter is null || _pendingResponseFilter(completePayload)))
            {
                _pendingResponse.TrySetResult(completePayload);
            }
        }
    }

    private static bool IsCommandResponse(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return !doc.RootElement.TryGetProperty("firmware", out _);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// Find a visible BLE device whose name matches. The custom protocol is
    /// protected by the firmware token, so Windows pairing is optional and is
    /// not initiated from code.
    /// </summary>
    private static async Task<BluetoothLEDevice?> FindByNameAsync(string name, CancellationToken ct)
    {
        var fromAdvertisement = await FindByAdvertisementNameAsync(name, ct);
        if (fromAdvertisement is not null) return fromAdvertisement;

        var selectors = new[]
        {
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(false),
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
            BluetoothLEDevice.GetDeviceSelector()
        };

        foreach (var selector in selectors)
        {
            var devices = await DeviceInformation.FindAllAsync(selector).AsTask(ct);
            foreach (var di in devices)
            {
                if (!string.Equals(di.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var d = await BluetoothLEDevice.FromIdAsync(di.Id).AsTask(ct);
                if (d is not null) return d;
            }
        }
        return null;
    }

    private static async Task<BluetoothLEDevice?> FindByAdvertisementNameAsync(string name, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var localName = args.Advertisement.LocalName;
            if (string.Equals(localName, name, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(args.BluetoothAddress);
        }

        watcher.Received += OnReceived;
        try
        {
            using var registration = timeout.Token.Register(() => tcs.TrySetCanceled(timeout.Token));
            watcher.Start();
            var address = await tcs.Task;
            return await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            watcher.Received -= OnReceived;
            watcher.Stop();
        }
    }
}
