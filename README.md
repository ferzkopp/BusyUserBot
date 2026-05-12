# Busy User Bot

A closed-loop "busy user" simulator for Windows. A small ESP32-S3 USB dongle
plugs into the PC under test and pretends to be a real keyboard and mouse.
A separate .NET controller — typically running on the same PC, but
optionally on a second one — takes screenshots, asks a vision-capable AI
model what a normal user might do next, validates the plan, and sends the
resulting HID actions back to the dongle over Bluetooth LE. The dongle
injects them as USB HID events, so to the OS under test the input is
indistinguishable from a real human at the keyboard.

The intended use is **automated, long-running, hands-off desktop activity**
for testing, demos, and stress scenarios where you want a machine to "look
busy" with realistic, varied user behaviour rather than a fixed script.

## How it works

```
                   ┌────────────────────────────────────────────┐
                   │                  PC under test             │
                   │                                            │
                   │  ┌───────────┐                             │
                   │  │ Screenshot│                             │
                   │  └─────┬─────┘                             │
                   │        │                                   │
                   │        ▼                                   │
                   │  ┌─────────────────────────────────────┐   │
                   │  │           Controller (.NET)         │   │
                   │  │                                     │   │
                   │  │   Planner ──▶ Validator ──▶ Executor│   │
                   │  │      ▲                          │   │   │
                   │  │      └── replan on rejection ◀──┘   │   │
                   │  └─────────┬───────────────┬───────────┘   │
                   │            │ HTTP          │ BLE GATT      │
                   │            ▼               ▼               │
                   │   ┌─────────────────┐    ┌─────────────┐   │
                   │   │ AI engine       │    │  Dongle     │   │
                   │   │ (LM Studio /    │    │  (BLE)      │   │
                   │   │  Azure OpenAI)  │    └──────┬──────┘   │
                   │   └─────────────────┘           │          │
                   └─────────────────────────────────┼──────────┘
                                                     │ USB HID
                                                     ▼
                                               keyboard + mouse
                                               input on this PC
```

Three AI stages drive the loop:

1. **Planner** — picks one small goal from a random subset of seeded
   "scenarios" (e.g. *"Open Notepad and jot down a short reminder"*) and
   proposes a few steps to reach it.
2. **Validator** — inspects the plan and rejects anything that would
   destroy data, shut down or lock the PC, or otherwise prevent the bot
   from continuing to run unattended.
3. **Executor** — turns each plan step into HID actions, one screenshot at
   a time, with a per-step success check and small retry budget.

A second hard safety layer enforced inside the controller blocks forbidden
text substrings (e.g. `rm -rf`) and forbidden key chords (e.g. `Win+L`,
`Ctrl+Alt+Delete`) before any action reaches the dongle.

The PC never injects input itself — every keystroke and click goes through
the physical USB dongle, which makes the activity observable in any normal
input pipeline (security, accessibility, anti-cheat, telemetry, …).

## Hardware

Built around the **LILYGO T-Dongle-S3**: a USB-A stick form-factor
ESP32-S3 board with native USB OTG, Bluetooth LE, and a small on-board
0.96" ST7735 status display.
[Product page](https://www.lilygo.cc/products/t-dongle-s3) ·
[upstream repo](https://github.com/Xinyuan-LilyGO/T-Dongle-S3).

- The ESP32-S3's native USB-OTG lets it enumerate as a real USB HID
  keyboard + mouse — no extra chips, no special host drivers.
- Wi-Fi and BLE are both available; this project uses **BLE GATT** for the
  controller link (low overhead, encrypted/bonded, no IP setup).
- The on-board display shows status (advertising / connected / last
  command) and accepts an optional `display` action for debugging.

## Repository layout

| Path | Purpose |
| ---- | ------- |
| [README.md](README.md) | This file. |
| [docs/](docs/) | Setup, protocol and architecture documentation. |
| [docs/control-flow.md](docs/control-flow.md) | Planner → Validator → Executor open-loop architecture and playbook schema. |
| [docs/ai-setup.md](docs/ai-setup.md) | LM Studio and Azure OpenAI setup, with model recommendations per hardware tier. |
| [docs/hardware-setup.md](docs/hardware-setup.md) | Flashing and pairing the T-Dongle-S3. |
| [docs/controller-setup.md](docs/controller-setup.md) | Building, configuring and running the .NET controller. |
| [docs/protocol.md](docs/protocol.md) | BLE GATT wire protocol between controller and dongle. |
| [docs/scripts.md](docs/scripts.md) | Reference for `dev-env-setup.ps1` and `flash-dongle.ps1`. |
| [firmware/](firmware/) | Arduino + NimBLE firmware for the T-Dongle-S3. |
| [controller/](controller/) | .NET 8 WinForms controller (BLE via WinRT). |
| [tools/](tools/) | Python helper script for manual BLE testing of the dongle. |
| [scripts/](scripts/) | PowerShell bootstrap and flashing helpers. |

## Quick start

A single command installs the toolchain (winget-based, idempotent — see
[docs/scripts.md](docs/scripts.md) for the full reference and flags):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/dev-env-setup.ps1
```

Then, in order:

1. **Flash the dongle and pair it once with Windows.**
   Walk through [docs/hardware-setup.md](docs/hardware-setup.md), or run the
   guided flasher script — see [docs/scripts.md](docs/scripts.md#flash-donglep1--interactive-flash--pair--smoke-test).
2. **Set up an AI backend.**
   Pick LM Studio (local, default) or Azure OpenAI — see
   [docs/ai-setup.md](docs/ai-setup.md). Model recommendations cover everything
   from a CPU-only laptop up to a 24 GB GPU.
3. **Build and run the controller.**
   See [docs/controller-setup.md](docs/controller-setup.md). Use `--dry-run`
   to develop without the dongle and `--fake-ai` to develop without an LLM.
4. **Customise the playbook.**
   Edit `%APPDATA%\BusyUserBot\playbook.json` to change the scenario list,
   constraints, or per-stage prompts. The schema and prompt-tuning notes
   are in [docs/control-flow.md](docs/control-flow.md).

## Safety notes

- The Validator stage and the controller's hard constraint check both
  refuse destructive actions, but **the controller is not a sandbox**. Run
  it on a test account, throwaway VM, or dedicated test machine — never on
  a workstation with valuable unsaved work.
- The BLE link is encrypted and bonded; the dongle additionally requires a
  shared `DEVICE_TOKEN` on every fresh connection (see
  [docs/protocol.md](docs/protocol.md)).
- Stopping the controller (tray-click → *Stop and show*) cancels the
  current iteration, drops the BLE link, and the firmware releases any
  held keys/buttons on disconnect.

## Status

This repository contains **sample / scaffold code** that builds clean and
exercises the full pipeline end-to-end with `--dry-run --fake-ai`. The
firmware compiles and uploads, and the controller's BLE client has been
exercised against the dongle, but no large-scale unattended run on real
hardware has been published yet. Bug reports and pull requests welcome.

## Licence

See the repository licence file. Third-party components (LM Studio, the
ESP32 Arduino core, NimBLE-Arduino, ArduinoJson, Adafruit GFX, etc.) are
distributed under their own licences.
