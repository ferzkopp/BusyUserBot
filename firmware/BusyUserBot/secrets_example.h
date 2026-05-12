// secrets_example.h — copy to `secrets.h` and fill in.
// `secrets.h` is gitignored.

#pragma once

// Shared token. The controller must write this to the AUTH characteristic
// immediately after connecting. Wrong/missing token => the dongle disconnects.
// Pick something long and random.
#define DEVICE_TOKEN "change-me-to-a-long-random-string"

// BLE advertised name. Shows up in Windows' Bluetooth pairing dialog.
#define DEVICE_NAME "BusyUserBot"
