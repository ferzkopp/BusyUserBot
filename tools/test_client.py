"""Manual BLE test client for the Busy User Bot dongle.

Requires `bleak`:
    py -m pip install bleak

Usage examples (PowerShell):

    python tools/test_client.py --name BusyUserBot --token devtoken status
    python tools/test_client.py --name BusyUserBot --token devtoken type "hello"
    python tools/test_client.py --name BusyUserBot --token devtoken click 100 200
    python tools/test_client.py --name BusyUserBot --token devtoken key CTRL S
    python tools/test_client.py --name BusyUserBot --token devtoken display "ready"

The dongle is a real USB HID device, so whatever PC its USB-C plug is in will
receive the input — not necessarily the same PC running this script.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import struct
import sys
from typing import Any

from bleak import BleakClient, BleakScanner

SVC_UUID  = "6e601000-b5a3-f393-e0a9-e50e24dcca9e"
AUTH_UUID = "6e601001-b5a3-f393-e0a9-e50e24dcca9e"
CMD_UUID  = "6e601002-b5a3-f393-e0a9-e50e24dcca9e"
STAT_UUID = "6e601003-b5a3-f393-e0a9-e50e24dcca9e"


class Dongle:
    def __init__(self, client: BleakClient, token: str) -> None:
        self._client = client
        self._token = token
        self._rx_buf = bytearray()
        self._rx_expected = 0
        self._hello: asyncio.Future[str] | None = None

    async def __aenter__(self) -> "Dongle":
        await self._client.connect()
        await self._client.start_notify(STAT_UUID, self._on_notify)
        # Auth gates command execution. The hello notification is the only
        # message the dongle ever sends; commands afterwards are fire-and-
        # forget. Some Windows/Bleak stacks miss the hello after a fresh
        # no-pairing connection, so do not make it mandatory.
        loop = asyncio.get_running_loop()
        self._hello = loop.create_future()
        await self._client.write_gatt_char(AUTH_UUID, self._token.encode("utf-8"), response=True)
        try:
            hello = await asyncio.wait_for(asyncio.shield(self._hello), timeout=1.0)
        except asyncio.TimeoutError:
            pass
        else:
            if not json.loads(hello).get("ok"):
                raise RuntimeError(f"auth rejected: {hello}")
        finally:
            self._hello = None
        return self

    async def __aexit__(self, *_: Any) -> None:
        try:
            await self._client.stop_notify(STAT_UUID)
        except Exception:
            pass
        await self._client.disconnect()

    def _on_notify(self, _sender: Any, data: bytearray) -> None:
        i = 0
        while i < len(data):
            if self._rx_expected == 0:
                while len(self._rx_buf) < 2 and i < len(data):
                    self._rx_buf.append(data[i]); i += 1
                if len(self._rx_buf) < 2:
                    return
                self._rx_expected = struct.unpack("<H", bytes(self._rx_buf))[0]
                self._rx_buf.clear()
            need = self._rx_expected - len(self._rx_buf)
            take = min(need, len(data) - i)
            self._rx_buf.extend(data[i:i + take])
            i += take
            if len(self._rx_buf) == self._rx_expected:
                payload = bytes(self._rx_buf).decode("utf-8", errors="replace")
                self._rx_buf.clear()
                self._rx_expected = 0
                if self._hello and not self._hello.done():
                    self._hello.set_result(payload)

    async def send(self, actions: list[dict]) -> None:
        """Fire-and-forget: stream the framed command, expect no reply."""
        body = json.dumps({"actions": actions}).encode("utf-8")
        framed = struct.pack("<H", len(body)) + body
        # Chunk to a safe ATT payload; the firmware reassembles. Use
        # write-without-response (UDP style) — the dongle never answers.
        chunk = 180
        for off in range(0, len(framed), chunk):
            await self._client.write_gatt_char(CMD_UUID, framed[off:off + chunk], response=False)


async def find_device(name: str, address: str | None) -> str:
    if address:
        return address
    print(f"scanning for '{name}'...", file=sys.stderr)
    devs = await BleakScanner.discover(timeout=6.0)
    for d in devs:
        if d.name and d.name.lower() == name.lower():
            return d.address
    raise SystemExit(f"BLE device '{name}' not found. Make sure the dongle is powered and advertising.")


async def run(args: argparse.Namespace) -> int:
    address = await find_device(args.name, args.address)

    if args.cmd == "status":
        # No GATT 'status' read; service discovery proves BLE visibility.
        async with BleakClient(address) as raw:
            # `client.services` is the modern Bleak API (>= 0.20). The older
            # `get_services()` coroutine was removed.
            ok = any(s.uuid.lower() == SVC_UUID for s in raw.services)
        print(json.dumps({"ok": ok, "address": address}, indent=2))
        return 0 if ok else 1

    actions: list[dict]
    if args.cmd == "type":
        actions = [{"type": "type", "text": " ".join(args.text)}]
    elif args.cmd == "click":
        actions = [
            {"type": "move", "x": args.x, "y": args.y, "absolute": True},
            {"type": "click", "button": args.button},
        ]
    elif args.cmd == "key":
        actions = [{"type": "key", "keys": list(args.keys)}]
    elif args.cmd == "display":
        actions = [{"type": "display", "text": " ".join(args.text)}]
    else:
        raise SystemExit(f"unknown command {args.cmd}")

    async with Dongle(BleakClient(address), args.token) as d:
        await d.send(actions)
        print(json.dumps({"ok": True, "sent": len(actions)}, indent=2))
        return 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="Busy User Bot dongle BLE test client")
    p.add_argument("--name", default="BusyUserBot", help="advertised BLE name")
    p.add_argument("--address", help="optional explicit BLE MAC, skips scan")
    p.add_argument("--token", default="", help="device token written to AUTH characteristic")

    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("status")

    sp = sub.add_parser("type"); sp.add_argument("text", nargs="+")
    sp = sub.add_parser("click")
    sp.add_argument("x", type=int); sp.add_argument("y", type=int)
    sp.add_argument("--button", default="left", choices=["left", "right", "middle"])
    sp = sub.add_parser("key");     sp.add_argument("keys", nargs="+", help="e.g. CTRL S")
    sp = sub.add_parser("display"); sp.add_argument("text", nargs="+")

    args = p.parse_args(argv)
    try:
        return asyncio.run(run(args))
    except Exception as e:
        detail = str(e) or type(e).__name__
        print(f"error: {detail}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
