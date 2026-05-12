# Helper scripts

Two PowerShell helpers live in [`scripts/`](../scripts/) at the repo root.
They are written in PowerShell 5.1+ syntax so they work with both the
in-box `powershell.exe` and PowerShell 7+ (`pwsh.exe`).

| Script | Purpose |
| ------ | ------- |
| [`dev-env-setup.ps1`](#dev-env-setupps1--bootstrap-the-dev-environment) | One-shot install of the full toolchain (winget + arduino-cli + libs + optional LM Studio). |
| [`flash-dongle.ps1`](#flash-donglep1--interactive-flash--pair--smoke-test) | Guided compile → upload → pair → smoke test of the dongle firmware. |

See also [hardware-setup.md](hardware-setup.md),
[controller-setup.md](controller-setup.md), and
[ai-setup.md](ai-setup.md).

---

## `dev-env-setup.ps1` — bootstrap the dev environment

Idempotent installer for everything needed to build the firmware, build
the controller, and run the manual BLE test client. Re-run any time; each
step checks for prior installation and skips work that is already done.

### Usage

```powershell
# From the repo root, in a regular (non-elevated) PowerShell window.
# Works with the in-box Windows PowerShell 5.1:
powershell -ExecutionPolicy Bypass -File scripts/dev-env-setup.ps1

# Or with PowerShell 7+ if you have it installed:
pwsh -ExecutionPolicy Bypass -File scripts/dev-env-setup.ps1
```

> Some `winget` packages auto-elevate via UAC. Accept the prompts.
> The script itself does not require an elevated shell.

### Flags

| Flag             | Effect |
| ---------------- | ------ |
| `-InstallLMStudio` | **Opt-in.** Also install [LM Studio](https://lmstudio.ai/) (a multi-GB download). Off by default; only pass this if you plan to run a local AI backend. When set, the script also downloads a vision model via the bundled `lms` CLI (see `-LMStudioModel`). |
| `-LMStudioModel` | Model identifier passed to `lms get`. Defaults to `lmstudio-community/Qwen3.5-9B-GGUF`. Pass an empty string (`-LMStudioModel ''`) to skip the model download (e.g. if you already have one locally, or want to pick one in the UI). Only relevant when `-InstallLMStudio` is also set. |
| `-SkipPython`    | Don't install Python or `bleak` (skip if you won't use [`tools/test_client.py`](../tools/test_client.py)). |

Examples:

```powershell
# Install LM Studio + default vision model.
powershell -ExecutionPolicy Bypass -File scripts/dev-env-setup.ps1 -InstallLMStudio

# Install LM Studio + a different model.
powershell -ExecutionPolicy Bypass -File scripts/dev-env-setup.ps1 -InstallLMStudio -LMStudioModel 'qwen2.5-vl-7b-instruct'

# Install LM Studio but skip the (large) automatic model download.
powershell -ExecutionPolicy Bypass -File scripts/dev-env-setup.ps1 -InstallLMStudio -LMStudioModel ''
```

> The `lms` CLI ships bundled with LM Studio at
> `%USERPROFILE%\.lmstudio\bin\lms.exe` after first launch / bootstrap.
> If the script can't find it right after install, it prints a warning
> and tells you to launch LM Studio once and re-run.

### What it installs

| Component | Source | Purpose |
| --------- | ------ | ------- |
| .NET 8 SDK | `winget Microsoft.DotNet.SDK.8` | Build the controller. |
| Visual Studio Code | `winget Microsoft.VisualStudioCode` | IDE. |
| VS Code: C# Dev Kit | `code --install-extension ms-dotnettools.csdevkit` | C# / .NET in VS Code. |
| VS Code: C/C++ | `code --install-extension ms-vscode.cpptools` | IntelliSense for the firmware sketch. |
| VS Code: Arduino *(optional)* | `code --install-extension` (best-effort, tries a few known marketplace IDs) | Nicer Arduino integration. The script warns and continues if no compatible extension is published; the C/C++ extension alone is enough via [`.vscode/c_cpp_properties.json`](../.vscode/c_cpp_properties.json). |
| Python 3.12 | `winget Python.Python.3.12` | Run [`tools/test_client.py`](../tools/test_client.py). |
| `bleak` | `pip install --user bleak` | BLE in the Python test client. |
| `arduino-cli` | `winget ArduinoSA.CLI` | Build & flash firmware from the command line. |
| ESP32 board package (`esp32:esp32`) | `arduino-cli core install` | Compile for the ESP32-S3. |
| `NimBLE-Arduino`, `ArduinoJson`, `Adafruit GFX Library`, `Adafruit ST7735 and ST7789 Library` | `arduino-cli lib install` | Firmware dependencies. |
| LM Studio *(opt-in, `-InstallLMStudio`)* | `winget ElementLabs.LMStudio` | Local AI engine. Skipped by default; pass `-InstallLMStudio` to install. |
| LM Studio model *(opt-in)* | `lms get <model>` | Vision model used by the controller. Defaults to `lmstudio-community/Qwen3.5-9B-GGUF`; override with `-LMStudioModel <id>` or skip with `-LMStudioModel ''`. |

### What it does **not** do

These steps are intentionally manual because they require choices or are
not safely scriptable:

- **Pairing the dongle in Windows** — done once via Settings → Bluetooth →
  Add device. See
  [hardware-setup.md](hardware-setup.md#first-time-pairing-on-windows).
- **Controller `appsettings.json`** — written automatically by the
  controller to `%APPDATA%\BusyUserBot\settings.json`.

> `firmware/BusyUserBot/secrets.h` is created automatically from
> `secrets_example.h` on first run, and the script generates a random
> `DEVICE_TOKEN` and mirrors it into the controller's `settings.json` so
> firmware and controller stay in sync. The file is gitignored. Edit
> `DEVICE_NAME` in `secrets.h` if you want a non-default BLE name.

### Re-running

Safe and recommended after major changes (e.g. when a new library version
is required). The script:

- Probes `dotnet --list-sdks`, `code --list-extensions`,
  `arduino-cli core list`, `arduino-cli lib list`, `pip show bleak`, and
  `winget list` before invoking installers.
- Refreshes `$env:Path` between steps so newly installed CLIs are visible
  in the current session.
- If a CLI was just installed but hasn't propagated to PATH yet, the
  script prints a warning and continues; re-run from a fresh terminal to
  complete any deferred steps.

### Exit codes

- `0` on success or "everything already installed".
- Non-zero if any installer fails. The script aborts at the first failure
  (it does not try to continue past a broken install).

---

## `flash-dongle.ps1` — interactive flash + pair + smoke test

Walks through the manual flashing steps in
[hardware-setup.md](hardware-setup.md#manual-flashing-without-flash-dongleps1):

1. **Detect the COM port** by diffing `arduino-cli board list` before and
   after you plug the dongle in.
2. **Compile and upload** `firmware/BusyUserBot` with
   `esp32:esp32:lilygo_t_dongle_s3`, falling back to the generic
   `esp32:esp32:esp32s3` board with explicit build properties if the
   lilygo variant isn't recognised.
3. **Open the Windows Bluetooth pairing dialog** (`ms-settings:bluetooth`)
   so you can pair the dongle once per PC.
4. **Run the Python test client** ([`tools/test_client.py`](../tools/test_client.py))
   for `status`, `display "ready"`, and `type "hello"`.

Each step pauses for confirmation; abort at any prompt with Ctrl+C. The
script only reads `firmware/BusyUserBot/secrets.h` and
`%APPDATA%\BusyUserBot\settings.json` (for the `DEVICE_TOKEN` and
`Dongle.Name`); it never modifies them. Use
[`dev-env-setup.ps1`](#dev-env-setupps1--bootstrap-the-dev-environment) to
generate / update those files.

### Usage

```powershell
# Full guided run from the repo root.
powershell -ExecutionPolicy Bypass -File scripts/flash-dongle.ps1

# Already know the COM port and the dongle is already paired:
powershell -ExecutionPolicy Bypass -File scripts/flash-dongle.ps1 -Port COM7 -SkipPair

# Just flash; no pairing prompt, no smoke test.
powershell -ExecutionPolicy Bypass -File scripts/flash-dongle.ps1 -Port COM7 -SkipPair -SkipSmokeTest
```

### Flags

| Flag             | Effect |
| ---------------- | ------ |
| `-Port <COMx>`   | Skip COM-port autodetection and use this port directly. Overrides any cached port. |
| `-NoCache`       | Don't read or write the cached COM port. Forces full autodetection even if a previous run remembered a port. |
| `-SkipPair`      | Don't open the Windows Bluetooth pairing dialog. |
| `-SkipSmokeTest` | Don't run [`tools/test_client.py`](../tools/test_client.py) at the end. |
| `-DeviceName`    | BLE name to scan for in the smoke test. Defaults to `Dongle.Name` from `settings.json`, then `DEVICE_NAME` from `secrets.h`, then `BusyUserBot`. |

### Port caching

After a successful upload the script writes the COM port to
`%LOCALAPPDATA%\BusyUserBot\flash-dongle.json`. Subsequent runs reuse that
port and skip the unplug/replug autodetection prompt. Override with
`-Port <COMx>` (one-off) or `-NoCache` (force fresh detection); delete the
JSON file to forget the cache permanently.
