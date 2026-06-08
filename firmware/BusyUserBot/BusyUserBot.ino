// BusyUserBot.ino - LILYGO T-Dongle-S3 firmware (BLE transport)
//
// Roles:
//   * Advertise a custom GATT service over Bluetooth LE.
//   * Receive framed JSON command payloads from the PC controller.
//   * Inject the resulting keystrokes/mouse events into the host PC over
//     native USB HID (the dongle's USB-C plug acts as a real keyboard/mouse).
//   * Show a tiny status line on the on-board 0.96" ST7735 display.
//
// Protocol (full spec: docs/protocol.md):
//
//   Service UUID: 6e601000-b5a3-f393-e0a9-e50e24dcca9e
//   Characteristics:
//     6e601001-...  AUTH    (write)            UTF-8 device token; required
//                                              once per connection.
//     6e601002-...  COMMAND (write/wnr)        Length-prefixed JSON. First 2
//                                              bytes of a payload are a uint16
//                                              little-endian total length;
//                                              the JSON body follows in any
//                                              number of writes until len is
//                                              reached.
//     6e601003-...  STATUS  (notify)           Length-prefixed JSON responses
//                                              and async log lines, chunked
//                                              to fit MTU.
//
// Build:
//   * Arduino IDE 2.x (or arduino-cli).
//   * Board package: "esp32 by Espressif Systems" 3.x.
//   * Library:       "NimBLE-Arduino" by h2zero (>= 1.4).
//   * Board:         "LilyGo T-Dongle S3" (or "ESP32S3 Dev Module" with
//                    USB CDC On Boot = Enabled, USB Mode = USB-OTG (TinyUSB),
//                    Upload Mode = UART0/Hardware CDC).
//   * Tools menu MUST have "USB Mode: USB-OTG (TinyUSB)" so that the
//     TinyUSB HID classes below enumerate as a HID keyboard + mouse on the
//     host PC. With "Hardware CDC and JTAG" the dongle instead appears as a
//     "USB JTAG/serial debug unit" and HID never comes up.

#include <Arduino.h>
#include <ArduinoJson.h>
#include <NimBLEDevice.h>
#include <USB.h>
#include <USBHIDKeyboard.h>
#include <USBHIDMouse.h>
#include <SPI.h>
#include <Adafruit_GFX.h>
#include <Adafruit_ST7735.h>

#include "secrets.h"

// LILYGO T-Dongle-S3 ST7735 0.96" 80x160 panel pin map (see upstream
// factory_screen example: https://github.com/Xinyuan-LilyGO/T-Dongle-S3).
// MISO is unused (SPI write-only).
//
// We use Adafruit_ST7735 with INITR_MINI160x80 instead of TFT_eSPI because
// TFT_eSPI 2.5.43 crashes (StoreProhibited) inside init() on Arduino-ESP32
// core 3.x due to SPI HAL changes.

// USB-OTG / TinyUSB mode is required for HID enumeration. With
// `ARDUINO_USB_MODE == 1` ("Hardware CDC and JTAG") the dongle would expose
// the ESP32-S3 native USB-Serial/JTAG controller and *no* HID device, so
// fail the build loudly rather than silently producing a useless dongle.
// (Same guard as LilyGo's own usb_hid_keyboard example.)
#if ARDUINO_USB_MODE == 1
#error "BusyUserBot requires USBMode = USB-OTG (TinyUSB). Re-build with FQBN option USBMode=default (or Tools -> USB Mode -> USB-OTG (TinyUSB) in the Arduino IDE). See docs/hardware-setup.md."
#endif

// ---------------------------------------------------------------------------
// UUIDs
// ---------------------------------------------------------------------------
static constexpr const char* SVC_UUID  = "6e601000-b5a3-f393-e0a9-e50e24dcca9e";
static constexpr const char* AUTH_UUID = "6e601001-b5a3-f393-e0a9-e50e24dcca9e";
static constexpr const char* CMD_UUID  = "6e601002-b5a3-f393-e0a9-e50e24dcca9e";
static constexpr const char* STAT_UUID = "6e601003-b5a3-f393-e0a9-e50e24dcca9e";

static constexpr const char* FIRMWARE_VERSION = "0.2.7-ble";

// ---------------------------------------------------------------------------
// HID + display
// ---------------------------------------------------------------------------
USBHIDKeyboard Keyboard;
USBHIDMouse    Mouse;

// ST7735 panel + backlight pins.
static constexpr int TFT_MOSI_PIN      = 3;
static constexpr int TFT_SCLK_PIN      = 5;
static constexpr int TFT_CS_PIN        = 4;
static constexpr int TFT_DC_PIN        = 2;
static constexpr int TFT_RST_PIN       = 1;
// Backlight is active-LOW; driving the pin LOW turns the LED on.
static constexpr int TFT_BACKLIGHT_PIN = 38;

SPIClass tftSpi(FSPI);

class BusyUserDisplay : public Adafruit_ST7735 {
public:
    BusyUserDisplay(SPIClass* spi, int8_t cs, int8_t dc, int8_t rst)
        : Adafruit_ST7735(spi, cs, dc, rst) {}

    void setPanelOffset(int8_t col, int8_t row) {
        setColRowStart(col, row);
    }
};

BusyUserDisplay tft(&tftSpi, TFT_CS_PIN, TFT_DC_PIN, TFT_RST_PIN);

// The T-Dongle-S3's 160x80 ST7735 glass is offset slightly from the
// Adafruit MINI160x80 default; without this, the last physical column and
// bottom rows can retain power-on garbage.
static constexpr int8_t TFT_COL_START = 25;
static constexpr int8_t TFT_ROW_START = 3;

// ---------------------------------------------------------------------------
// Per-connection state
// ---------------------------------------------------------------------------
struct ConnState {
    bool authed = false;
    // Reassembly buffer for the length-prefixed command stream.
    std::vector<uint8_t> rxBuf;
    uint32_t expected = 0;  // 0 = waiting for length prefix
};
static ConnState g_conn;
static NimBLECharacteristic* g_statChar = nullptr;
static NimBLEServer*         g_server   = nullptr;
static uint16_t              g_mtu      = 23;  // BLE default until negotiated

// ---------------------------------------------------------------------------
// Display helpers
// ---------------------------------------------------------------------------
static String g_topLine = "boot";
static String g_midLine = "";
static String g_botLine = "";

static void clearDisplay() {
    tft.fillScreen(ST77XX_BLACK);
}

static void redraw() {
    clearDisplay();
    tft.setTextSize(1);
    tft.setTextWrap(false);
    tft.setTextColor(ST77XX_WHITE);
    tft.setCursor(2, 2);  tft.print(g_topLine);
    tft.setTextColor(ST77XX_GREEN);
    tft.setCursor(2, 18); tft.print(g_midLine);
    tft.setTextColor(0xC618 /* light grey 565 */);
    tft.setCursor(2, 34); tft.print(g_botLine);
}

static void showStatus(const String& top, const String& mid, const String& bot) {
    g_topLine = top; g_midLine = mid; g_botLine = bot;
    redraw();
}
static void showLast(const String& bot) { g_botLine = bot; redraw(); }

// ---------------------------------------------------------------------------
// Key-name table (matches docs/protocol.md)
// ---------------------------------------------------------------------------
struct KeyEntry { const char* name; uint8_t code; };
// We use the USBHIDKeyboard "consumer" key codes for letters/digits via
// .write()/.press() with the ASCII value; modifiers and named keys use the
// constants from USBHIDKeyboard.h.
static int resolveKey(const String& nameIn, uint8_t& outCode) {
    String n = nameIn; n.toUpperCase();
    // Letters
    // IMPORTANT: for chord actions we want the base key without implicit
    // SHIFT. The USBHIDKeyboard helpers treat uppercase ASCII as shifted
    // characters, so map A..Z tokens to lowercase key codes.
    if (n.length() == 1 && n[0] >= 'A' && n[0] <= 'Z') {
        outCode = (uint8_t)(n[0] - 'A' + 'a');
        return 1;
    }
    // Digits
    if (n.length() == 1 && n[0] >= '0' && n[0] <= '9') { outCode = n[0]; return 1; }
    // Function keys
    if (n.length() >= 2 && n[0] == 'F') {
        int num = n.substring(1).toInt();
        if (num >= 1 && num <= 12) { outCode = KEY_F1 + (num - 1); return 1; }
    }
    struct M { const char* k; uint8_t v; };
    static const M map[] = {
        {"ENTER", KEY_RETURN}, {"RETURN", KEY_RETURN},
        {"ESC", KEY_ESC}, {"ESCAPE", KEY_ESC},
        {"TAB", KEY_TAB}, {"SPACE", ' '},
        {"BACKSPACE", KEY_BACKSPACE}, {"DELETE", KEY_DELETE},
        {"INSERT", KEY_INSERT},
        {"HOME", KEY_HOME}, {"END", KEY_END},
        {"PAGEUP", KEY_PAGE_UP}, {"PAGEDOWN", KEY_PAGE_DOWN},
        {"UP", KEY_UP_ARROW}, {"DOWN", KEY_DOWN_ARROW},
        {"LEFT", KEY_LEFT_ARROW}, {"RIGHT", KEY_RIGHT_ARROW},
        {"CTRL", KEY_LEFT_CTRL}, {"CONTROL", KEY_LEFT_CTRL},
        {"SHIFT", KEY_LEFT_SHIFT},
        {"ALT", KEY_LEFT_ALT}, {"OPTION", KEY_LEFT_ALT},
        {"GUI", KEY_LEFT_GUI}, {"WIN", KEY_LEFT_GUI},
        {"WINDOWS", KEY_LEFT_GUI}, {"CMD", KEY_LEFT_GUI},
    };
    for (const auto& e : map) if (n == e.k) { outCode = e.v; return 1; }
    return 0;
}

static uint8_t resolveButton(const String& bIn) {
    String b = bIn; b.toLowerCase();
    if (b == "right")  return MOUSE_RIGHT;
    if (b == "middle") return MOUSE_MIDDLE;
    return MOUSE_LEFT;
}

// Compact action label for the tiny LCD bottom line.
static String actionLabel(JsonObjectConst a, int index1) {
    String t = a["type"].as<const char*>() ? String(a["type"].as<const char*>()) : "?";
    t.toLowerCase();

    String label = String("a") + index1 + ":" + t;
    if (t == "key" && a["keys"].is<JsonArrayConst>()) {
        label += " ";
        bool first = true;
        for (JsonVariantConst v : a["keys"].as<JsonArrayConst>()) {
            if (!first) label += "+";
            first = false;
            const char* k = v.as<const char*>();
            label += (k ? k : "?");
            if (label.length() > 26) break;
        }
    } else if (t == "type") {
        const char* txt = a["text"].as<const char*>();
        if (txt && *txt) {
            label += " \"";
            for (int i = 0; txt[i] && i < 10; i++) label += txt[i];
            if (strlen(txt) > 10) label += "...";
            label += "\"";
        }
    } else if (t == "wait") {
        int ms = a["ms"].is<int>() ? a["ms"].as<int>() : 0;
        label += " " + String(ms) + "ms";
    }

    if (label.length() > 30) label = label.substring(0, 30);
    return label;
}

// ---------------------------------------------------------------------------
// HID release on disconnect / error
// ---------------------------------------------------------------------------
static void releaseAllInput() {
    Keyboard.releaseAll();
    Mouse.release(MOUSE_LEFT);
    Mouse.release(MOUSE_RIGHT);
    Mouse.release(MOUSE_MIDDLE);
}

// ---------------------------------------------------------------------------
// Action execution
// ---------------------------------------------------------------------------
// USBHIDMouse::move() takes int8_t for x/y/wheel (range -127..127). Anything
// larger gets silently truncated. Chunk a possibly-large delta into
// successive ~120-pixel hops so the host sees a smooth (multi-report) move
// instead of a tiny garbled one. The per-hop delay is sized to comfortably
// exceed the typical 8 ms HID polling interval so every report is actually
// consumed by the host driver under load.
static void mouseMoveChunked(int dx, int dy) {
    const int STEP = 120; // safe margin under int8_t max (127)
    while (dx != 0 || dy != 0) {
        int sx = dx;
        if (sx >  STEP) sx =  STEP;
        if (sx < -STEP) sx = -STEP;
        int sy = dy;
        if (sy >  STEP) sy =  STEP;
        if (sy < -STEP) sy = -STEP;
        Mouse.move((int8_t)sx, (int8_t)sy, 0);
        dx -= sx;
        dy -= sy;
        delay(8);
    }
}

// Slam the cursor to the top-left corner. The standard HID mouse is
// relative-only and clamped to int8_t per report, and Windows further damps
// medium-magnitude relative deltas when "Enhance pointer precision" is on,
// so a too-tight margin lands well short of (0,0) and the subsequent
// absolute move ends up offset. We over-shoot generously: 80 max-negative
// hops = 10160 raw pixels (covers any single screen up to ~8K even at
// ~50% acceleration damping). Once the cursor hits the screen edge the OS
// silently clamps further deltas, so over-shooting is harmless.
static void mouseSlamToOrigin() {
    for (int i = 0; i < 80; i++) {
        Mouse.move(-127, -127, 0);
        delay(8);
    }
}

// Returns empty string on success, otherwise an error message.
static String executeAction(JsonObjectConst a) {
    String t = a["type"].as<const char*>() ? String(a["type"].as<const char*>()) : "";
    if (t == "move") {
        if (a["x"].isNull() || a["y"].isNull()) return "move requires x,y";
        int x = a["x"].as<int>();
        int y = a["y"].as<int>();
        bool absolute = a["absolute"].is<bool>() ? a["absolute"].as<bool>() : true;
        if (absolute) {
            // The standard HID mouse is relative-only. We approximate
            // absolute by snapping to (0,0) first (chunked, see above) and
            // then walking to (x,y) in chunks. For real absolute positioning
            // swap USBHIDMouse for a custom HID descriptor (see firmware
            // README). Coordinate system: (0,0) = top-left, (max-x, max-y)
            // = bottom-right. The OS clamps at screen edges.
            mouseSlamToOrigin();
            // Let the OS settle / cursor latch to the corner before we
            // start the absolute walk; without this, fast back-to-back
            // reports can be coalesced and undercount the move.
            delay(20);
            mouseMoveChunked(x, y);
        } else {
            mouseMoveChunked(x, y);
        }
    } else if (t == "click") {
        uint8_t btn = resolveButton(a["button"].as<const char*>() ? a["button"].as<const char*>() : "left");
        int count = a["count"].is<int>() ? a["count"].as<int>() : 1;
        for (int i = 0; i < count; i++) Mouse.click(btn);
    } else if (t == "down") {
        Mouse.press(resolveButton(a["button"].as<const char*>() ? a["button"].as<const char*>() : "left"));
    } else if (t == "up") {
        Mouse.release(resolveButton(a["button"].as<const char*>() ? a["button"].as<const char*>() : "left"));
    } else if (t == "scroll") {
        int dy = a["dy"].is<int>() ? a["dy"].as<int>() : 0;
        if (dy >  127) dy =  127;
        if (dy < -127) dy = -127;
        Mouse.move(0, 0, (int8_t)dy);
    } else if (t == "type") {
        const char* s = a["text"].as<const char*>();
        if (!s) return "type requires text";
        Keyboard.print(s);
    } else if (t == "key") {
        if (!a["keys"].is<JsonArrayConst>()) return "key requires keys[]";
        JsonArrayConst arr = a["keys"].as<JsonArrayConst>();
        if (arr.size() == 0) return "keys[] empty";
        // Press all then release all => chord
        std::vector<uint8_t> codes;
        for (JsonVariantConst v : arr) {
            uint8_t c;
            if (!resolveKey(String(v.as<const char*>() ? v.as<const char*>() : ""), c))
                return String("unknown key: ") + (v.as<const char*>() ? v.as<const char*>() : "?");
            codes.push_back(c);
        }
        for (uint8_t c : codes) Keyboard.press(c);
        delay(15);
        for (auto it = codes.rbegin(); it != codes.rend(); ++it) Keyboard.release(*it);
    } else if (t == "wait") {
        int ms = a["ms"].is<int>() ? a["ms"].as<int>() : 0;
        if (ms < 0) ms = 0; if (ms > 10000) ms = 10000;
        delay(ms);
    } else if (t == "display") {
        showLast(String(a["text"].as<const char*>() ? a["text"].as<const char*>() : ""));
    } else {
        return String("unknown action type: ") + t;
    }
    return "";
}

// ---------------------------------------------------------------------------
// Notify (status) helper - chunked, length-prefixed
// ---------------------------------------------------------------------------
static void notifyStatus(const String& json) {
    if (!g_statChar) return;
    uint32_t total = json.length();
    // 2-byte little-endian length prefix, then payload.
    std::vector<uint8_t> buf;
    buf.reserve(2 + total);
    buf.push_back(total & 0xff);
    buf.push_back((total >> 8) & 0xff);
    for (size_t i = 0; i < total; i++) buf.push_back((uint8_t)json[i]);

    // Chunk to (MTU - 3) per ATT notification (3 bytes ATT overhead).
    uint16_t chunk = (g_mtu > 3) ? (g_mtu - 3) : 20;
    for (size_t i = 0; i < buf.size(); i += chunk) {
        size_t n = std::min((size_t)chunk, buf.size() - i);
        g_statChar->setValue(buf.data() + i, n);
        g_statChar->notify();
        delay(2);
    }
}

// ---------------------------------------------------------------------------
// Command-stream handler
// ---------------------------------------------------------------------------
static void handleCommandBytes(const uint8_t* data, size_t len) {
    if (!g_conn.authed) {
        notifyStatus("{\"ok\":false,\"error\":\"unauthorized\"}");
        if (g_server && g_server->getConnectedCount() > 0) {
            // Close the connection on auth failure.
            auto peers = g_server->getPeerDevices();
            for (auto& id : peers) g_server->disconnect(id);
        }
        return;
    }

    size_t off = 0;
    while (off < len) {
        if (g_conn.expected == 0) {
            // Need 2-byte length prefix; may straddle writes.
            while (g_conn.rxBuf.size() < 2 && off < len) g_conn.rxBuf.push_back(data[off++]);
            if (g_conn.rxBuf.size() < 2) return;
            g_conn.expected = (uint32_t)g_conn.rxBuf[0] | ((uint32_t)g_conn.rxBuf[1] << 8);
            g_conn.rxBuf.clear();
            if (g_conn.expected == 0 || g_conn.expected > 8192) {
                notifyStatus("{\"ok\":false,\"error\":\"bad length\"}");
                g_conn.expected = 0;
                return;
            }
        }
        size_t need = g_conn.expected - g_conn.rxBuf.size();
        size_t take = std::min(need, len - off);
        g_conn.rxBuf.insert(g_conn.rxBuf.end(), data + off, data + off + take);
        off += take;

        if (g_conn.rxBuf.size() == g_conn.expected) {
            // Complete payload — parse and execute.
            StaticJsonDocument<4096> doc;
            DeserializationError err = deserializeJson(doc, g_conn.rxBuf.data(), g_conn.rxBuf.size());
            g_conn.rxBuf.clear();
            g_conn.expected = 0;

            if (err) {
                String e = String("{\"ok\":false,\"error\":\"bad json: ") + err.c_str() + "\"}";
                notifyStatus(e);
                continue;
            }
            JsonArrayConst actions = doc["actions"].as<JsonArrayConst>();
            int executed = 0;
            String errMsg;
            for (JsonVariantConst v : actions) {
                if (!v.is<JsonObjectConst>()) { errMsg = "action not an object"; break; }
                showLast(actionLabel(v.as<JsonObjectConst>(), executed + 1));
                String r = executeAction(v.as<JsonObjectConst>());
                if (r.length()) { errMsg = r; break; }
                executed++;
            }
            if (errMsg.length()) {
                releaseAllInput();
                String s = String("{\"ok\":false,\"executed\":") + executed +
                           ",\"error\":\"" + errMsg + "\"}";
                notifyStatus(s);
                showLast("ERR");
            } else {
                String s = String("{\"ok\":true,\"executed\":") + executed + "}";
                notifyStatus(s);
                showLast(String("ok x") + executed);
            }
        }
    }
}

// ---------------------------------------------------------------------------
// NimBLE callbacks
// ---------------------------------------------------------------------------
class ServerCb : public NimBLEServerCallbacks {
    void onConnect(NimBLEServer* /*s*/, NimBLEConnInfo& info) override {
        g_conn = ConnState{};
        g_mtu = 23;
        showStatus("BLE", "connected", "auth?");
    }
    void onDisconnect(NimBLEServer* s, NimBLEConnInfo& /*info*/, int /*reason*/) override {
        releaseAllInput();
        g_conn = ConnState{};
        showStatus("advertising", String(DEVICE_NAME), String("v") + FIRMWARE_VERSION);
        NimBLEDevice::startAdvertising();
    }
    void onMTUChange(uint16_t mtu, NimBLEConnInfo& /*info*/) override {
        g_mtu = mtu;
    }
};

class AuthCb : public NimBLECharacteristicCallbacks {
    void onWrite(NimBLECharacteristic* c, NimBLEConnInfo& info) override {
        std::string v = c->getValue();
        if (v == DEVICE_TOKEN) {
            g_conn.authed = true;
            showStatus("BLE", "authed", "ready");
            String hello = String("{\"ok\":true,\"firmware\":\"") + FIRMWARE_VERSION + "\"}";
            notifyStatus(hello);
        } else {
            g_conn.authed = false;
            notifyStatus("{\"ok\":false,\"error\":\"bad token\"}");
            // Drop the link.
            if (g_server) g_server->disconnect(info.getConnHandle());
        }
    }
};

class CmdCb : public NimBLECharacteristicCallbacks {
    void onWrite(NimBLECharacteristic* c, NimBLEConnInfo& /*info*/) override {
        std::string v = c->getValue();
        handleCommandBytes((const uint8_t*)v.data(), v.size());
    }
};

// ---------------------------------------------------------------------------
// setup() / loop()
// ---------------------------------------------------------------------------
//
// setup() is structured as an explicit sequence of named "stages". Before each
// stage runs we display its label on the LCD (and print over Serial). If the
// firmware hangs or panics, the last label still on the screen / printed over
// USB-CDC tells you exactly which stage was the culprit. This costs ~50 bytes
// of flash and is invaluable when debugging on hardware that has no easy way
// to attach JTAG.
//
// Order is also important:
//   1. Serial / USB CDC must come up early so the user has a chance to
//      attach a serial monitor before any heavy init runs.
//   2. The display is initialised next so we have visible diagnostics even
//      without a host PC.
//   3. USB HID classes are registered, then USB.begin() finalises the
//      TinyUSB descriptors and triggers host enumeration.
//   4. BLE is initialised last because it is the most likely thing to hang
//      (NimBLE pulls in a lot of code and depends on the radio coming up).
static void stageBegin(const char* label) {
    Serial.print(F("[stage] "));
    Serial.println(label);
    Serial.flush();
    showStatus(label, "...", "");
    // Small delay so a fast hang still leaves the label visible on the LCD.
    delay(50);
}
static void stageOk(const char* label) {
    Serial.print(F("[ok]    "));
    Serial.println(label);
    Serial.flush();
}

void setup() {
    // ---- 1. USB-CDC serial console ---------------------------------------
    // With USBMode=default + CDCOnBoot=cdc, `Serial` is the TinyUSB CDC
    // class. Calling Serial.begin() before USB.begin() just buffers writes
    // until the device enumerates; that's fine -- the buffered messages
    // come out as soon as the host opens the port.
    Serial.begin(115200);
    Serial.println();
    Serial.print(F("BusyUserBot firmware "));
    Serial.println(FIRMWARE_VERSION);

    // ---- 2. Display ------------------------------------------------------
    // Backlight off until the panel is initialised so we don't flash garbage.
    pinMode(TFT_BACKLIGHT_PIN, OUTPUT);
    digitalWrite(TFT_BACKLIGHT_PIN, HIGH);
    tftSpi.begin(TFT_SCLK_PIN, /*MISO*/ -1, TFT_MOSI_PIN, TFT_CS_PIN);
    tft.initR(INITR_MINI160x80);
    tft.setSPISpeed(20000000);
    tft.setRotation(1);          // 160 wide x 80 tall
    tft.setPanelOffset(TFT_COL_START, TFT_ROW_START);
    tft.invertDisplay(true);     // GREENTAB160x80 panel needs inversion ON
    clearDisplay();
    digitalWrite(TFT_BACKLIGHT_PIN, LOW);  // backlight ON
    showStatus("boot", FIRMWARE_VERSION, "");
    delay(200);

    // ---- 3. USB HID ------------------------------------------------------
    stageBegin("HID");
    Keyboard.begin();
    Mouse.begin();
    // USB.begin() finalises TinyUSB descriptors and triggers host
    // enumeration. After this returns, Windows should see the dongle as a
    // composite USB device (CDC + HID Keyboard + HID Mouse).
    USB.begin();
    stageOk("HID");

    // Give the host a moment to enumerate so the very first Serial.println
    // after this point is more likely to be captured by a serial monitor.
    delay(300);

    // ---- 4. BLE ----------------------------------------------------------
    stageBegin("BLE init");
    NimBLEDevice::init(DEVICE_NAME);
    NimBLEDevice::setPower(ESP_PWR_LVL_P9);
    NimBLEDevice::setMTU(247);
    // The custom protocol is guarded by DEVICE_TOKEN. Do not enable BLE
    // bonding/security here: some Windows ARM Bluetooth stacks get stuck in
    // the OS pairing flow even though the GATT attributes are plain.
    stageOk("BLE init");

    stageBegin("BLE svc");
    g_server = NimBLEDevice::createServer();
    g_server->setCallbacks(new ServerCb());

    NimBLEService* svc = g_server->createService(SVC_UUID);
    auto* authChar = svc->createCharacteristic(AUTH_UUID,
        NIMBLE_PROPERTY::WRITE);
    authChar->setCallbacks(new AuthCb());

    auto* cmdChar = svc->createCharacteristic(CMD_UUID,
        NIMBLE_PROPERTY::WRITE | NIMBLE_PROPERTY::WRITE_NR);
    cmdChar->setCallbacks(new CmdCb());

    g_statChar = svc->createCharacteristic(STAT_UUID,
        NIMBLE_PROPERTY::NOTIFY | NIMBLE_PROPERTY::READ);

    svc->start();
    stageOk("BLE svc");

    stageBegin("BLE adv");
    NimBLEAdvertising* adv = NimBLEDevice::getAdvertising();
    // The 31-byte BLE advertising packet can't fit a 128-bit service UUID
    // (18 bytes incl. header) AND the flags field (3 bytes) AND the name
    // ("BusyUserBot" = 13 bytes incl. header). NimBLE silently drops the
    // name in that case, and Windows shows the device as "Unknown Device"
    // in the pairing dialog. Split the data: service UUID + flags in the
    // primary advertisement, name in the scan response (a second 31-byte
    // packet returned to active scanners like Windows).
    NimBLEAdvertisementData advData;
    advData.setFlags(BLE_HS_ADV_F_DISC_GEN | BLE_HS_ADV_F_BREDR_UNSUP);
    advData.setCompleteServices(NimBLEUUID(SVC_UUID));
    adv->setAdvertisementData(advData);

    NimBLEAdvertisementData scanData;
    scanData.setName(DEVICE_NAME);
    adv->setScanResponseData(scanData);
    adv->enableScanResponse(true);

    NimBLEDevice::startAdvertising();
    stageOk("BLE adv");

    showStatus("advertising", String(DEVICE_NAME), String("v") + FIRMWARE_VERSION);
    Serial.println(F("[ready] advertising"));
}

void loop() {
    // Everything is event-driven via NimBLE callbacks; nothing to poll.
    delay(100);
}
