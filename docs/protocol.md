# Wire protocol: Controller ↔ Dongle (Bluetooth LE)

The dongle exposes a single custom GATT service over Bluetooth LE. The
controller pairs once via Windows, then connects by device id and exchanges
length-prefixed JSON over two characteristics.

This document is the source of truth for the wire format. The same JSON
schema is what the AI Executor stage is instructed to emit in
[control-flow.md](control-flow.md#stage-prompts), so the same record can be
deserialised from the model's output and re-serialised onto the wire.

## Connection lifecycle

```
   Controller (PC, WinRT BLE)                Dongle (ESP32-S3, NimBLE)
   ──────────────────────────                ─────────────────────────
                                                advertise "BusyUserBot"
            connect (encrypted, bonded)  ───►  accept
            ╔═══════════════════════════════════════════════════╗
            ║ AUTH (write):  "<DEVICE_TOKEN>"                   ║
            ║                                                   ║
            ║                  STATUS (notify):                 ║
            ║                  {"ok":true,"firmware":"0.2.1-ble"}║
            ║                  …or  {"ok":false,"error":"…"} + disconnect
            ╚═══════════════════════════════════════════════════╝
            ╔═══════════════════════════════════════════════════╗
            ║ COMMAND (write): {"actions":[ … ]}                ║
            ║                                                   ║
            ║                   STATUS (notify):                ║
            ║                   {"ok":true,"executed":N}        ║
            ║                       …or {"ok":false,"executed":n,"error":"…"}
            ╚═══════════════════════════════════════════════════╝
                              ⋮  (repeat per command)
            disconnect (controller drop)  ───►  release all keys/buttons,
                                                resume advertising
```

State resets on every disconnect: the dongle requires a fresh `AUTH` write
before it will accept any further `COMMAND`s, and it releases any held
keys / mouse buttons.

## Service and characteristics

| Role     | UUID                                   | Properties                  | Direction          |
| -------- | -------------------------------------- | --------------------------- | ------------------ |
| Service  | `6e601000-b5a3-f393-e0a9-e50e24dcca9e` | —                           | —                  |
| `AUTH`   | `6e601001-b5a3-f393-e0a9-e50e24dcca9e` | Write (encrypted)           | controller → dongle |
| `COMMAND`| `6e601002-b5a3-f393-e0a9-e50e24dcca9e` | Write / Write-No-Resp (enc) | controller → dongle |
| `STATUS` | `6e601003-b5a3-f393-e0a9-e50e24dcca9e` | Notify (encrypted)          | dongle → controller |

The encrypted properties force the link to be paired and bonded. The first
time the controller writes to `AUTH`, Windows pops a standard pairing
dialog; the bond persists and subsequent connections are silent.

## Authentication

Immediately after each connection the controller writes the UTF-8 device
token (matching `DEVICE_TOKEN` in `firmware/BusyUserBot/secrets.h`) to
`AUTH`. The dongle replies on `STATUS` with either:

```json
{ "ok": true, "firmware": "0.2.3-ble" }
```

…or, on a wrong token, `{"ok":false,"error":"bad token"}` followed by an
immediate disconnect. The dongle ignores writes to `COMMAND` until the
current connection is authenticated.

## Framing

`COMMAND` and `STATUS` payloads are length-prefixed JSON:

```
+--------+--------+-------------- ... --------------+
| len_lo | len_hi |   <len> bytes of UTF-8 JSON     |
+--------+--------+-------------- ... --------------+
```

`len` is a `uint16` little-endian; max payload is 8 KiB (firmware
enforced). A single payload may be split across multiple GATT
writes/notifications; both ends buffer until `len` bytes have been
received. Receivers must reset their reassembly state on disconnect.

## Commands (controller → dongle, on `COMMAND`)

```json
{
  "actions": [
    { "type": "move",  "x": 540, "y": 360, "absolute": true },
    { "type": "click", "button": "left" },
    { "type": "type",  "text": "hello world" },
    { "type": "key",   "keys": ["CTRL", "S"] },
    { "type": "wait",  "ms": 250 }
  ]
}
```

Supported action types:

| Type      | Fields                                                                | Notes |
| --------- | --------------------------------------------------------------------- | ----- |
| `move`    | `x`, `y` (int), `absolute` (bool, default `true`), `target` (string, optional, controller-only) | Coordinate system: (0,0) = top-left, (screen-max-x, screen-max-y) = bottom-right. Absolute is approximated by slamming to (0,0) first then walking to (x,y). The HID mouse report is relative and clamped to `int8_t` per packet, so the firmware chunks the move into ~120-pixel hops with ~8 ms pacing. Windows' "Enhance pointer precision" damps relative deltas non-linearly; for best accuracy, **disable** *Settings → Bluetooth & devices → Mouse → Additional mouse settings → Pointer Options → Enhance pointer precision* on the target PC. For true absolute positioning, swap the firmware mouse to a custom HID descriptor. The optional `target` string is consumed by the controller's iterative cursor-targeting refinement stage (a short natural-language description of the UI element being aimed at, e.g. *"the Start button on the taskbar"*); it is ignored by the dongle. |
| `click`   | `button` ∈ {`left`, `right`, `middle`}, `count` (int, default `1`)    |       |
| `down`    | `button`                                                              | Press and hold a mouse button. |
| `up`      | `button`                                                              | Release. |
| `scroll`  | `dy` (int, +up / −down, clamped to ±127 per report)                  |       |
| `type`    | `text` (string, ASCII only)                                           | Typed using the US layout. |
| `key`     | `keys` (array of key names, pressed together as a chord)              | See [Key names](#key-names). |
| `wait`    | `ms` (int, 0–10000)                                                   | Sleeps on the dongle, avoids round-trip jitter for short waits. |
| `display` | `text` (string)                                                       | Show a line on the dongle's screen for debugging. |

The Executor stage of the controller is constrained to the same schema —
see [control-flow.md](control-flow.md) — and the controller's hard
constraint validator runs over the parsed actions before they reach the
dongle.

## Responses (dongle → controller, on `STATUS`)

After every command payload, the dongle pushes exactly one response:

```json
{ "ok": true, "executed": 5 }
```

…or, on the first failing action:

```json
{ "ok": false, "executed": 2, "error": "unknown key: CMD" }
```

The dongle stops at the first failing action, releases all held
keys / buttons, and reports how many of the requested actions were
performed.

## Reset

There is no explicit "reset" command. The controller drops the BLE link to
reset; the firmware releases all keys and mouse buttons on disconnect and
returns to advertising. The controller's *Stop* path always exercises this
disconnect.

## Key names

Case-insensitive. Modifiers can appear with normal keys to form chords.
A..Z tokens represent the base letter key in chords (no implicit SHIFT).

```
A..Z  0..9
F1..F12
ENTER  ESC  TAB  SPACE  BACKSPACE  DELETE  INSERT
HOME  END  PAGEUP  PAGEDOWN
UP  DOWN  LEFT  RIGHT
CTRL  SHIFT  ALT  GUI         (GUI = Windows key; aliases: WIN, WINDOWS, CMD)
```

## Discovery model

The controller never initiates pairing from code. Workflow is:

1. User pairs the dongle once via **Windows Settings → Bluetooth → Add
   device** (the dongle advertises as `BusyUserBot` by default).
2. The controller scans the *paired* device list for the configured name
   on first launch, opens it via `BluetoothLEDevice.FromIdAsync`, and
   caches the resulting `DeviceId` in
   `%APPDATA%\BusyUserBot\settings.json`.
3. Subsequent launches skip the scan and connect by id.

If the user re-pairs the dongle on a different PC, the cached id becomes
invalid; the controller falls back to a name scan automatically and updates
the cache.

## Manual exercise

[`tools/test_client.py`](../tools/test_client.py) (Python +
[`bleak`](https://github.com/hbldh/bleak)) speaks this protocol directly
and is the easiest way to confirm the dongle works in isolation. See
[hardware-setup.md](hardware-setup.md) for the smoke-test commands and
[scripts.md](scripts.md#flash-donglep1--interactive-flash--pair--smoke-test)
for the guided flasher that runs them automatically after a successful
upload.
