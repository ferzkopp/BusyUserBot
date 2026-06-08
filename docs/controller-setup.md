# Controller setup

The controller is a .NET 8 WinForms application that captures the screen,
runs the Planner → Validator → Executor loop against an AI endpoint, and
sends HID actions to the dongle over Bluetooth LE. See
[control-flow.md](control-flow.md) for the loop architecture and
[protocol.md](protocol.md) for the wire protocol.

---

## User instructions

### 1. Prerequisites

Either run the bootstrap script (recommended) and skip to step 2:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/dev-env-setup.ps1
```

…or install manually:

- [.NET 8 SDK](https://dotnet.microsoft.com/download). Pick the architecture
  that matches your dev box (Windows x64 or Windows ARM64).
- Windows 10 build 17763 (1809) or newer — required for the
  `Windows.Devices.Bluetooth` WinRT APIs.
- A Bluetooth LE-capable radio on the PC (built-in on most laptops; for
  desktops a generic USB BT 4.0+ adapter works).
- VS Code with the **C# Dev Kit** extension, or Visual Studio 2022+.
- An AI backend — see [ai-setup.md](ai-setup.md).
- The dongle, flashed and advertising — see [hardware-setup.md](hardware-setup.md).

### 2. Build & run

```powershell
cd controller
dotnet build
dotnet run --project src/BusyUserBot
```

The controller's main window opens. Fill in the fields top-to-bottom; each
is persisted to `%APPDATA%\BusyUserBot\settings.json` automatically.

### 3. First-launch UI walkthrough

**Dongle**

- **BLE name** — must match `DEVICE_NAME` in `firmware/BusyUserBot/secrets.h`
  (default `BusyUserBot`).
- **Cached device id** — populated automatically after the first successful
  connection; leave blank initially.
- **Token** — must match `DEVICE_TOKEN` in `secrets.h`. The
  [bootstrap script](scripts.md#dev-env-setupps1--bootstrap-the-dev-environment)
  generates this once and writes it into both files for you.

**AI Engine**

- **Engine / Endpoint / Model / API key / Azure deployment** — see
  [ai-setup.md](ai-setup.md). For LM Studio, click **Refresh models** to
  populate the dropdown from the running server.

**Playbook**

- **File** — path to `playbook.json`. Defaults to
  `%APPDATA%\BusyUserBot\playbook.json`, which is auto-seeded from the
  bundled example on first launch.
- **Browse / Reload / Preview** — pick a different file, reload after
  editing, or open a read-only summary of the parsed playbook.
- **Interval (ms)** — sleep between consecutive plan iterations.
- **Max task runs** — safety stop on total iterations.
- **AI timeout (s)** — per-call budget for each Planner / Validator /
  Executor / step-validation request.

The playbook itself (scenarios, per-stage prompts, constraints) is edited
in your text editor of choice and reloaded with the **Reload** button. The
schema is documented in [control-flow.md](control-flow.md).

**Test buttons**

- **Test HW** — opens the BLE link, authenticates with the token, and logs
  the result. The link is intentionally kept open afterwards (dropping it
  too soon can corrupt the WinRT GATT cache).
- **Test AI** — sends a real Planner + Validator round-trip against the
  current screen and logs the goal, steps and verdict. No HID is sent.
- **Test mouse** — sends a small relative cursor wiggle (a ~60-pixel
  square plus two wheel ticks) so you can confirm the dongle is enumerated
  as a HID mouse on the target PC. Only enabled once **Test HW** has
  passed. Reuses that connection if available; otherwise it connects, runs
  the wiggle, then disconnects. Watch your cursor on the target PC.
- **AI Test Mouse** — end-to-end self-test of the whole pipeline: pops up
  a distinctive *AI MOUSE TEST* window with a single yellow **DISMISS**
  button, takes a screenshot, asks the AI for a click coordinate, sends
  the click via the dongle, and waits for the popup to be dismissed. Up
  to 3 attempts; each attempt is bounded by the configured **AI timeout**.
  Pass/fail is reported on the button label and in the log. Only enabled
  once both **Test HW** and **Test AI** have passed.

Both **Test HW** and **Test AI** must show ✅ before **Start** unlocks.
**Test mouse** and **AI Test Mouse** become available as their
prerequisites pass.

**Start / Stop**

- **Start** — runs the loop. By default the window hides to the system
  tray; click the tray icon (or right-click → *Stop and show*) to cancel
  the loop and bring the window back.
- **Keep window open** (checkbox next to Start) — when ticked, the window
  stays visible while the loop runs and the **Start** button turns into
  **Stop**, so you can watch the log live and cancel from the same
  button. Recommended for the first few runs while you're tuning the
  playbook; uncheck it once you're happy and want the bot to disappear
  into the tray.
- The firmware releases any held keys/buttons on disconnect, regardless
  of how the loop is stopped.

### 4. Quick safety check

Before letting the bot run unattended:

1. Read the **Active constraints** section in the **Preview** dialog and
   confirm it forbids anything destructive in your environment.
2. Run the controller for one or two iterations, watching the log, to
   confirm the executor's HID is reasonable.
3. Only then click into a non-foreground window and let it run. Stopping
   it from the tray is one click.

> The Planner refuses, the Validator double-checks, and the controller's
> hard constraint validator blocks forbidden text/chords as a third layer.
> Even so, **the controller is not a sandbox**. Run on a test account or
> VM, never on a machine with valuable unsaved work.

---

## Reference

### Command-line flags

| Flag | Effect |
| ---- | ------ |
| `--dry-run` | Don't open a BLE link. The fake hardware client logs every command instead of sending it. Combine with a real AI endpoint to develop / tune prompts without the dongle. |
| `--fake-ai` | Replace the AI engine with [`FakeAiEngine`](../controller/src/BusyUserBot/AI/FakeAiEngine.cs), which returns canned plans, verdicts and HID actions. Use to exercise the full pipeline with no LLM available. |

The flags are independent and can be combined:

```powershell
# Develop without the dongle:
dotnet run --project src/BusyUserBot -- --dry-run

# Develop without the dongle and without an LLM:
dotnet run --project src/BusyUserBot -- --dry-run --fake-ai
```

### Configuration file

Settings are persisted to `%APPDATA%\BusyUserBot\settings.json`. A
documented template is checked in at
[`appsettings.example.json`](../controller/src/BusyUserBot/appsettings.example.json):

```json
{
  "Dongle":  { "Name": "...", "DeviceId": "", "Token": "..." },
  "Ai":      { "Engine": "LMStudio | AzureOpenAI | Fake",
               "Endpoint": "...", "Model": "...", "ApiKey": "",
               "AzureDeployment": "", "AzureApiVersion": "2024-10-21" },
  "Loop":    { "PlaybookPath": "", "IntervalMs": 1500,
               "MaxIterations": 50, "AiTimeoutSeconds": 60 }
}
```

The playbook lives in a **separate** file pointed to by `Loop.PlaybookPath`
(default `%APPDATA%\BusyUserBot\playbook.json`). Its schema is documented
in [control-flow.md](control-flow.md). The bundled example
[`playbook.example.json`](../controller/src/BusyUserBot/playbook.example.json)
ships ~100 scenarios and is copied into place on first launch.

### Publishing release builds

```powershell
# x64
dotnet publish src/BusyUserBot -c Release -r win-x64 --self-contained false

# ARM64
dotnet publish src/BusyUserBot -c Release -r win-arm64 --self-contained false
```

Cross-publishing works in either direction (e.g. produce a `win-arm64`
build from an x64 dev box), but the resulting binary only *runs* on a
matching machine.

### Project structure

The controller's source lives in
[`controller/src/BusyUserBot/`](../controller/src/BusyUserBot/). Notable files:

| File | Role |
| ---- | ---- |
| `Program.cs` | Entry point; parses `--dry-run` / `--fake-ai`. |
| `MainForm.cs` | UI, configuration binding, test buttons, tray. |
| `BotLoop.cs` | Planner → Validator → Executor outer loop. |
| `AI/Prompts.cs` | Default system prompts and prompt builders. |
| `AI/OpenAiCompatibleEngine.cs` | LM Studio / Azure / OpenAI HTTP client. |
| `AI/FakeAiEngine.cs` | Canned responses for `--fake-ai`. |
| `AI/CommandParser.cs` | Tolerant JSON parsing for AI replies. |
| `AI/ConstraintValidator.cs` | Hard-rule check on HID before sending. |
| `HardwareClient.cs` | BLE GATT client (WinRT) + dry-run client. |
| `ScreenCapture.cs` | Full-screen capture and coordinate scaling. |
| `Models/` | `AppConfig`, `Playbook`, `HidAction` data models. |

### Stop semantics

The **Stop** path always:

1. Cancels the current loop iteration's `CancellationToken`.
2. Best-effort calls `HardwareClient.ResetAsync` to release any held
   keys / buttons.
3. Disposes the BLE client (drops the link); the firmware also releases
   keys/buttons on disconnect as a safety net.
4. Restores the visible window before re-enabling the **Start** button.

Closing the window with the X button cancels the loop too, then exits.
