# ESP32-C3 Ambient Monitor Backlight Controller

Firmware for the **Seeed Studio XIAO ESP32-C3** that drives a WS2812B LED strip and exposes a browser-based configuration interface with a binary WebSocket protocol for low-latency colour streaming from a desktop helper app.

## Hardware

| Component | Details |
|-----------|---------|
| Board | Seeed Studio XIAO ESP32-C3 |
| LED strip | WS2812B (external 5V supply) |
| Data pin | GPIO 4 (D2) — change via `LED_DATA_PIN` in `platformio.ini` |

## Features

- Wi-Fi station mode with credentials stored in NVS
- Access Point + captive portal when Wi-Fi is not configured
- REST JSON API (`/api/config`, `/api/status`, `/api/reboot`, `/api/testpattern`)
- Binary WebSocket on `/ws` for 30–60 FPS RGB streaming
- Idle sleep: fades LEDs off after ~10s without stream frames
- JSON WebSocket commands for debugging
- Responsive web UI (vanilla JS)
- LED layout preview with index numbering
- Test patterns including edge identification
- Config import/export
- OTA firmware updates (web UI + PlatformIO wireless upload)

## Build & Flash

Requires [PlatformIO](https://platformio.org/).

```bash
# Install dependencies and build firmware
pio run

# Upload firmware
pio run -t upload

# Upload web assets to LittleFS
pio run -t uploadfs

# Serial monitor
pio device monitor
```

First boot without Wi-Fi credentials starts AP **`Backlight-Setup`**. Connect and open `http://192.168.4.1` to configure Wi-Fi and LED layout.

When connected to your network, use `http://backlight.local` (mDNS) or the device IP.

### Wi-Fi credentials (optional, local only)

To bake Wi-Fi into firmware for recovery flashes without re-entering credentials in the web UI:

```bash
cp wifi_secrets.ini.example wifi_secrets.ini
# Edit [wifi] ssid and password in wifi_secrets.ini
```

`wifi_secrets.ini` is **gitignored** and read at build time by `scripts/load_wifi_secrets.py`. It is never committed. If the file is missing, builds still work — configure Wi-Fi through the web UI or AP setup portal instead.

## OTA Updates

The partition table uses dual OTA slots (`partitions.csv`). After the first USB flash, you can update over Wi-Fi.

### Web UI

Open the config page → **Firmware Update (OTA)** → choose `.pio/build/seeed_xiao_esp32c3/firmware.bin` → Upload.

### PlatformIO (ArduinoOTA)

```bash
python -m platformio run -e seeed_xiao_esp32c3_ota -t upload
```

This uses `upload_protocol = espota` and `upload_port = backlight.local`. Change the port to the device IP if mDNS is unavailable.

**Note:** Switching to the OTA partition layout requires one USB flash. LittleFS (`uploadfs`) must still be updated over USB when web assets change.

## LED Layout

Each edge is defined by **strip indices** (0-based positions on the physical WS2812 chain):

| Field | Description |
|-------|-------------|
| `leftStart` / `leftEnd` | Strip indices for the left edge |
| `rightStart` / `rightEnd` | Right edge |
| `topStart` / `topEnd` | Top edge |
| `bottomStart` / `bottomEnd` | Bottom edge |

If `start > end`, LEDs run backward along the strip for that edge. The **reverse** checkboxes invert direction again.

**Stream order** (colours from the desktop app): Left → Top → Right → Bottom.  
`layoutTotal` = sum of all edge spans — this is the `ledCount` in WebSocket frames.

## WebSocket Binary Protocol

Endpoint: `ws://<device-ip>/ws`

| Offset | Size | Field |
|--------|------|-------|
| 0 | 2 | `frameNumber` (uint16, little-endian) |
| 2 | 2 | `ledCount` (uint16, little-endian) |
| 4 | 3×N | RGB bytes per LED |

Total payload: `4 + (3 × ledCount)` bytes.

Invalid packets (wrong length, count > configured strip length) are ignored.

### JSON debug commands

```json
{"cmd":"brightness","value":180}
{"cmd":"off"}
{"cmd":"test","pattern":"rainbow"}
```

## LED Index Order

The desktop app sends colours in **stream order** (not necessarily sequential strip indices):

**Left → Top → Right → Bottom**

Each edge maps to its configured strip index range. The preview UI labels show physical strip indices.

Use the **Edge ID** test pattern to verify physical wiring:

- Left = Red
- Top = Green
- Right = Blue
- Bottom = White

## Desktop Streamer (Windows)

The **BacklightStreamer** app captures your monitor edges via DXGI desktop duplication, averages colour in small boxes per LED sample point, and streams binary WebSocket frames to the ESP32.

### Build locally

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
cd desktop/BacklightStreamer
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ../../publish/desktop
```

Run `publish/desktop/BacklightStreamer.exe`.

### Configuration

- **Device host** — IP or hostname of the ESP32 (e.g. `192.168.3.180` or `backlight.local`)
- **Monitor** — which display to capture
- **Custom capture region** — optional crop within the monitor (useful for ultrawide or letterboxed content)
- **Border inset / sample radius** — inset from the screen edge, and how far inward each LED averages
- **Target FPS / blend FPS** — capture rate vs interpolated send rate to the ESP32
- **Brightness** — live device brightness control (0–255)
- **Live preview** — capture thumbnail with inset/radius guides and edge colour glows
- **Sync layout** — pulls LED edge indices from `GET /api/config` so sample points match the web UI layout
- **Start on boot** — adds a registry Run entry (`--minimized` to tray)

Settings are stored in `%AppData%\\BacklightStreamer\\settings.json`.

### CI builds

Pushes to `main` that touch `desktop/` trigger [.github/workflows/desktop.yml](.github/workflows/desktop.yml), which publishes `BacklightStreamer.exe` as a workflow artifact.

## Project Structure

```
src/                 — ESP32 firmware
data/                — Web UI (LittleFS)
desktop/BacklightStreamer/  — Windows streamer app
platformio.ini
```

## Configuration Keys (NVS)

Wi-Fi SSID/password, total LED count, per-edge start/end indices, brightness, color order, gamma correction, max FPS, per-edge reverse flags.

## License

MIT
