# Hardware setup — LILYGO T-Dongle-S3

The dongle runs a single Arduino sketch
([`firmware/BusyUserBot/BusyUserBot.ino`](../firmware/BusyUserBot/BusyUserBot.ino))
that:

- advertises a custom GATT service over Bluetooth LE (see [protocol.md](protocol.md)),
- translates JSON commands into USB HID keyboard/mouse events, and
- shows status on the on-board 0.96" ST7735 display.

> ⚠️ Once flashed, the dongle is a **real HID keyboard + mouse** for whatever
> PC its USB-A plug is in. For first-time testing, plug it into a different
> PC than the one running the controller / test client.

Upstream board documentation:
[Xinyuan-LilyGO/T-Dongle-S3](https://github.com/Xinyuan-LilyGO/T-Dongle-S3)
([Quick Start](https://github.com/Xinyuan-LilyGO/T-Dongle-S3/blob/main/docs/quick_start.md) ·
[schematic](https://github.com/Xinyuan-LilyGO/T-Dongle-S3/tree/main/schematic) ·
[datasheet](https://github.com/Xinyuan-LilyGO/T-Dongle-S3/tree/main/datasheet) ·
[examples](https://github.com/Xinyuan-LilyGO/T-Dongle-S3/tree/main/examples)).

---

## User instructions

### Prerequisites

- A LILYGO T-Dongle-S3 (USB-A form factor with a fold-out USB-A plug).
- The dev environment installed via
  [`scripts/dev-env-setup.ps1`](../scripts/dev-env-setup.ps1) — installs
  `arduino-cli`, the ESP32 board core, and the firmware libraries
  (`NimBLE-Arduino`, `ArduinoJson`, `Adafruit GFX`, `Adafruit ST7735`).
  See [scripts.md](scripts.md).

### Quick start (recommended)

The guided flasher does autodetection, compile, upload, and smoke test in
one go:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/flash-dongle.ps1
```

It will:

1. Wait for you to plug the dongle in (with **BOOT** held the first time —
   see [Putting the dongle in download mode](#putting-the-dongle-in-download-mode)
   below) and detect the COM port by diffing `arduino-cli board list`.
2. Compile and upload `firmware/BusyUserBot` with the
   `esp32:esp32:lilygo_t_dongle_s3` FQBN, falling back to a generic ESP32-S3
   board with explicit build properties if the lilygo variant isn't
   recognised.
3. Run [`tools/test_client.py`](../tools/test_client.py) for a `status` →
   `display "ready"` → `type "hello"` smoke test.

The script reads `secrets.h` and the controller's `settings.json` for the
token and BLE name; it never modifies them. Full reference and flags are
in [scripts.md](scripts.md#flash-donglep1--interactive-flash--pair--smoke-test).

After it finishes, you're done with hardware — go to
[controller-setup.md](controller-setup.md).

### Putting the dongle in download mode

If this is a fresh dongle, or `arduino-cli` can't see the COM port: **hold
the BOOT button while plugging the dongle into the USB-A port**, then
release it once it's seated. Otherwise just plug it in. The dongle has a
single button (BOOT) — there is no separate RST button; to reset, unplug
and replug.

You can confirm download mode visually by watching the on-board display:

- **Plug in *with* BOOT held → screen stays dark.** The ESP32-S3 is
  sitting in the ROM bootloader; the sketch is not running. This is
  correct for flashing.
- **Plug in *without* BOOT → screen shows a status message.** The sketch
  is running normally; you are *not* in download mode.

### Windows Bluetooth pairing

Do not pair the dongle in Windows Bluetooth settings. The controller and
test client connect to the advertising BLE device by name, then authenticate
with `DEVICE_TOKEN`.

If Windows already shows `BusyUserBot` stuck on **Connecting...**, remove it
from Bluetooth settings, power-cycle the dongle, and run the controller or
test client directly.

---

## Reference

### Manual flashing (without `flash-dongle.ps1`)

Use these steps when the script falls over or when you want to know exactly
what's happening.

#### 1. Create `secrets.h`

**Already done if you ran `dev-env-setup.ps1`.** The bootstrap script
creates `firmware/BusyUserBot/secrets.h` from `secrets_example.h`,
generates a random `DEVICE_TOKEN`, and mirrors the same token into the
controller's `settings.json` so they stay in sync. `secrets.h` is
gitignored.

Manual variant:

```powershell
Copy-Item firmware/BusyUserBot/secrets_example.h firmware/BusyUserBot/secrets.h
notepad firmware/BusyUserBot/secrets.h
```

Set `DEVICE_TOKEN` to a long random string. Optionally change `DEVICE_NAME`
(default `BusyUserBot`). Make sure the same `DEVICE_TOKEN` is set in the
controller's `%APPDATA%\BusyUserBot\settings.json`.

#### 2. Find the COM port

In download mode the dongle exposes the ESP32-S3's native USB-CDC
interface. `arduino-cli` recognises the USB VID/PID but can't tell which
ESP32-S3 board it is, so it lists the same port twice with two candidate
FQBNs (`esp32_family` and `ozobot_circuit_kit`). The reliable way to
identify the port is a before/after diff:

1. **Before** plugging the dongle in, run:

   ```powershell
   arduino-cli board list
   ```

   Note the existing ports.

2. Plug the dongle in (holding **BOOT** as in [download mode](#putting-the-dongle-in-download-mode)
   if this is the first flash). Wait for any "Setting up a device" toast.

3. Run the same command **after** plugging in. The new entry is the dongle:

   ```text
   Port Protocol Type              Board Name          FQBN                           Core
   COM5 serial   Serial Port (USB) ESP32 Family Device esp32:esp32:esp32_family       esp32:esp32
                 Serial Port (USB) Ozobot circuit kit  esp32:esp32:ozobot_circuit_kit esp32:esp32
   ```

   Both candidate FQBNs are wrong; ignore them and use
   `esp32:esp32:lilygo_t_dongle_s3` (or the fallback below) when uploading.

Always do both runs — don't try to guess from a single `board list`, and
don't assume the highest-numbered port is the dongle.

#### 3. Compile and upload

```powershell
# If a Windows Application Control policy (WDAC / Smart App Control) blocks
# the ESP32 build tools from loading their bundled Python DLL out of %TEMP%
# ("An Application Control policy has blocked this file"), redirect Temp to a
# repo-local folder first. Harmless to set unconditionally.
$env:TEMP = "$PWD\.arduino-tmp"; $env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null

$fqbn = "esp32:esp32:lilygo_t_dongle_s3"
arduino-cli compile --fqbn $fqbn firmware/BusyUserBot
arduino-cli upload  --fqbn $fqbn --port COM7 firmware/BusyUserBot
```

If `lilygo_t_dongle_s3` isn't recognised, fall back to `esp32:esp32:esp32s3`
with explicit build properties:

```powershell
$fqbn = "esp32:esp32:esp32s3:USBMode=default,CDCOnBoot=cdc,PSRAM=disabled,FlashSize=16M,PartitionScheme=app3M_fat9M_16MB"
```

> `USBMode=default` selects USB-OTG / TinyUSB, which is **required** for
> the `USBHIDKeyboard` / `USBHIDMouse` classes to enumerate. The `hwcdc`
> option ("Hardware CDC and JTAG") makes the dongle appear as a *USB
> JTAG/serial debug unit* on the host PC and HID never comes up.

After upload, unplug and replug the dongle. The display should show
`BLE / advertising / BusyUserBot`.

#### 4. Smoke test

```powershell
python tools/test_client.py --name BusyUserBot --token <your-token> status
python tools/test_client.py --name BusyUserBot --token <your-token> display "ready"
python tools/test_client.py --name BusyUserBot --token <your-token> type "hello"
```

Keystrokes appear on whatever PC the dongle is plugged into.

### Arduino IDE board settings

If you prefer the Arduino IDE GUI to `arduino-cli`, select **ESP32S3 Dev
Module** with:

| Setting | Value |
| ------- | ----- |
| USB CDC On Boot | Enabled |
| USB Mode | USB-OTG (TinyUSB) |
| USB Firmware MSC On Boot | Disabled |
| USB DFU On Boot | Disabled |
| Upload Mode | UART0 / Hardware CDC |
| CPU Frequency | 240 MHz |
| Flash Mode | QIO 80 MHz |
| Flash Size | 16 MB |
| Partition Scheme | 16M Flash (3MB APP/9.9MB FATFS) |
| PSRAM | Disabled |

`USB Mode = USB-OTG (TinyUSB)` is required so TinyUSB owns the USB
interface and `USBHIDKeyboard` / `USBHIDMouse` enumerate correctly.

### VS Code IntelliSense for `.ino`

The Microsoft C/C++ extension alone will show `#include` squiggles. The
`dev-env-setup.ps1` script installs **Arduino Community Edition**
(`vscode-arduino-community.vscode-arduino-community`) which fixes this
automatically. Otherwise the squiggles are cosmetic — `arduino-cli` is the
source of truth.

### Troubleshooting

| Symptom | Fix |
| ------- | --- |
| `Failed to load Python DLL … An Application Control policy has blocked this file` (compile exits `0xffffffff`) | WDAC / Smart App Control is blocking DLLs in `%TEMP%`. Redirect Temp to a repo-local folder before compiling: `$env:TEMP = "$PWD\.arduino-tmp"; $env:TMP = $env:TEMP` (the `flash-dongle.ps1` script already does this). |
| `USBHIDKeyboard.h: No such file` | Wrong board package version. Use ESP32 core 3.x or newer (the bootstrap script installs this). |
| Compile error on `NimBLEServerCallbacks::onConnect` signature | NimBLE-Arduino version mismatch — install 2.x. |
| `arduino-cli` doesn't list a COM port | Hold **BOOT** while plugging in to force download mode. |
| New COM port doesn't appear at all | Plug directly into the PC, not into a hub / monitor / KVM / keyboard pass-through; reseat the fold-out USB-A plug; try a different port; if the screen also stays dark without BOOT, the dongle isn't getting power. |
| Dongle stuck on "boot" | BLE init crashed. Open a serial monitor on the COM port for the panic trace. |
| `BusyUserBot` not found by the controller/test client | Not advertising. Reset the dongle; check the display says `advertising`. Make sure no other PC is holding the connection. |
| Windows shows `BusyUserBot` stuck on `Connecting...` | Remove it from Bluetooth settings and do not pair it again; the app connects directly by BLE name and token. |
| Keys appear stuck | Disconnect or power-cycle — the firmware releases all keys/buttons on disconnect. |
| `auth rejected` | `DEVICE_TOKEN` in `secrets.h` doesn't match the controller's settings. |
