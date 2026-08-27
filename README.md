# CaYaCooler

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform: Windows 11](https://img.shields.io/badge/Platform-Windows%2011-0078D6)]()
[![MCU: ESP32-S2%2FS3](https://img.shields.io/badge/MCU-ESP32--S2%20%2F%20S3-E7352C)]()

**English | [Türkçe](README.tr.md)**

An ESP32-S2/S3-based smart cooler controller:

- Native Windows 11 **Dynamic Lighting** (HID LampArray) — device name **CaYaLight**
- **2× 4-pin PWM fan** control (25 kHz) with tachometer **RPM** readback
- **Companion PC app** (system tray + GUI): CPU/GPU temperature, fan RPM/mode, lighting mode, music-reactive lighting
- **Pure HID** device: both Dynamic Lighting (LampArray) and PC communication (vendor HID reports) go over the same HID interface. **No CDC/serial** (enabling CDC made Windows stop recognizing the LampArray device).

```
firmware/CaYaCooler/CaYaCooler.ino  -> ESP32-S2/S3 firmware (HID-only)
pc-app/                             -> C# .NET 8 WinForms app (CaYaControl)
CaYaLight/CaYaLight.ino             -> Dynamic Lighting only (simplified test build)
```

> **Settings** are auto-saved to `cayacontrol.settings.json` next to the app executable (lighting mode, fan, brightness, all music settings, selected GPU).

---

## 0) Hardware used (reference)

Parts tested in this project — you don't need the exact same ones, equivalents work fine:

| Part | Note |
|---|---|
| ESP32-S2 Mini dev board | Native USB OTG required — the original ESP32 (without an S2/S3 suffix) is **not supported**. The same firmware also runs unmodified on ESP32-S3 boards. |
| 2× 4-pin PWM case fan (12V) | Standard Intel 4-pin pinout (GND / +12V / Tach / PWM). |
| WS2812/NeoPixel LED strip (30 LEDs) | `LED_COUNT` is adjustable in firmware. |
| Logic-level N-channel MOSFET + 100Ω gate resistor + 100kΩ pull-down resistor | For the "cut fan power when the PC is off" circuit below. |

---

## 1) Hardware / Wiring

### 4-pin PWM fan connector (standard pinout)

| Pin | Wire | Function | Connects to |
|-----|------|----------|-------------|
| 1 | Black | GND | 12V supply GND **+** ESP32 GND (common ground required) |
| 2 | Yellow | +12V | 12V supply + |
| 3 | Green | Tach (RPM) | ESP32 tach pin, 10k pull-up to 3.3V |
| 4 | Blue | PWM (speed) | ESP32 PWM pin (3.3V works; a 5V level-shifter is ideal) |

### ESP32-S2/S3 pin map

| Function | GPIO |
|----------|------|
| LED DIN (WS2812) | 4 |
| Fan 1 PWM | 5 |
| Fan 2 PWM | 6 |
| Fan 1 Tach | 7 |
| Fan 2 Tach | 15 |
| Fan power MOSFET gate (FAN_EN, optional) | 16 |

> If your board's pinout differs (GPIO16 in particular may be reserved for flash/PSRAM on some clone boards), update the numbers in firmware to match your specific board's pinout diagram.

### Important notes

- **Don't power the fans from USB.** Use a separate 12V supply (or a PC PSU Molex connector: yellow=12V, black=GND). The 12V GND and ESP32 GND **must be common**.
- Tach lines are open-collector; the code uses `INPUT_PULLUP`, but an external 10k pull-up is more reliable.
- LED strip: put a ~330Ω series resistor on DIN; 30 LEDs at full white draw ~1.8A → power the 5V rail separately, keep GND common. A 3.3V→5V level shifter on DIN improves stability.
- If driving both fans at the same speed is fine, you can parallel the PWM lines to a single GPIO — but separate RPM readback still needs separate tach lines.

### "Cut the fan when the PC is off" — MOSFET power switch

The fans' 12V rail is switched through an **N-channel logic-level MOSFET** whose gate is driven from the ESP32. Safety is entirely **hardware-based**: the ESP32 only runs while the PC/motherboard is supplying USB power, so when the PC turns off, the ESP32 turns off too → the gate's **100kΩ pull-down resistor** pulls the gate LOW → the MOSFET turns off → the fan is cut **in every scenario** (even if the firmware crashes or never boots).

```
                +12V (separate supply) ────────────┬─────────────┐
                                                    │             │
                                              Fan 1 || Fan 2      │
                                             (4-pin PWM, parallel)│
                                                    │             │
                     ESP32 GPIO5 ── PWM ────────────┤             │
                     ESP32 GPIO7 ── tach ───────────┤             │
                                                    │             │
                                              fan(-) / GND         │
                                                    │             │
                                                    ▼             │
                                           ┌─────────────────┐    │
                     ESP32 GPIO16 ──100Ω───┤ G   N-MOSFET  D ├────┘
                                           │                 │
                                           │        S        │
                                           └────────┬────────┘
                                                    │
                    GPIO16 ──100kΩ pull-down──┐     │
                                               ├─────┘
                                              GND (ESP32 + 12V supply, common)
```

- The 100Ω gate resistor limits the MOSFET's switching inrush (optional but recommended).
- The 100kΩ pull-down holds the gate firmly LOW whenever the ESP32 pin is high-impedance (during reset/boot, or with no power at all) — **the fan defaults to off**, and only turns on once the ESP32 explicitly drives the pin HIGH.
- On boot, the firmware sets this pin (`FAN_EN_PIN`, default GPIO16) HIGH in `setup()`. If you're not using this circuit, you can leave the pin unconnected — no code changes needed.
- If you'd rather switch the fan on the 12V side (high-side, with a P-MOSFET) instead of tying fan(-) to the low-side N-MOSFET drain as shown, you'll need to adapt the circuit accordingly — the low-side N-MOSFET setup above is the simplest and most common approach.

---

## 2) Firmware (Arduino IDE)

1. **Boards Manager** → "esp32" by Espressif, **version 3.x** (the `ledcAttach` API is required).
2. **Library Manager** → "Adafruit NeoPixel".
3. Board: your ESP32-S2 or ESP32-S3 module (both have native USB OTG, same firmware runs on either). Settings:
   - **USB Mode: USB-OTG (TinyUSB)**
   - **USB CDC On Boot: Disabled**  ← required (with CDC enabled, Windows won't see the Dynamic Lighting device)
4. `firmware/CaYaCooler/CaYaCooler.ino` → Compile/Upload.

> If you previously plugged the device in with a broken descriptor, remove it from Device Manager and re-plug it (to clear Windows' USB descriptor cache).

---

## 3) PC App (CaYaControl)

Requires: **.NET 8 SDK** (or later). NuGet packages restore automatically.

```powershell
cd pc-app
dotnet build -c Release
# Run (requires admin rights - needed for CPU temperature access):
.\bin\Release\net8.0-windows\CaYaControl.exe
```

Single-file, portable build:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# output: pc-app\bin\Release\net8.0-windows\win-x64\publish\CaYaControl.exe
```

Prebuilt binaries are available on the **[Releases](../../releases)** page.

### Usage

- On launch, the app automatically finds the device over **HID** (VID 0x303A / PID 0x4343, vendor usage 0xFF00). No COM port. Use **Reconnect** if it can't find it.
- **CPU / GPU** temperature cards; a **GPU selector** on the right (NVIDIA prioritized, all GPUs listed). **Fan RPM** and **power %** come live from the device.
- **Lighting mode**: Off / Static Color / Temperature / Rainbow / **Music (audio-reactive)** / **Windows Dynamic Lighting**.
  - Choosing any mode other than "Windows Dynamic Lighting" makes the app take over the lighting.
  - **Music Settings...**: visual mode (spectrum/single color), color scheme (rainbow / frequency-to-color / static / bass-mid-treble / two-color gradient), spectrum style (mirrored/from-center), brightness, sensitivity/AGC, bass emphasis, attack/decay, gamma, **noise gate** (skip silence), **frequency range** (min/max Hz), **idle behavior** (off / static / breathing / hold-last / slow rainbow).
- **Fan**: Automatic (temperature curve) or Manual (slider).
- **Start with Windows**: sets up a highest-privilege Scheduled Task.
- Closing the window minimizes the app to the **tray**; use the tray menu → Exit to quit.
- All settings auto-save to `cayacontrol.settings.json`.

---

## 4) HID protocol (reference)

Communication uses vendor HID reports (no CDC/serial):

**PC → device** — Feature report `0x10` (48 bytes): `[cmd][payload...]`
```
0x01 TEMP   cpu, gpu
0x02 MODE   mode (0..5)
0x03 COLOR  r, g, b
0x04 FAN    mode(0=auto/1=manual), duty%
0x05 BRIGHT 0..255
0x06 FRAME  chunked: [start][count][RGB...] (sent in 15-LED chunks)
```

**device → PC** — Input report `0x11` (16 bytes):
```
cpu, gpu, rpm1(LE), rpm2(LE), duty, fanmode, mode, host, fanManualDuty, brightness, staticColor(r,g,b)
```

---

## 5) Safety / failsafe

- If no PC data arrives for 5s, fans automatically ramp to **80%** (fails high, not low).
- The fan curve is hysteretic (EMA) with a smooth ramp — no sudden RPM jumps.
- Thanks to the MOSFET power switch, fans are cut **at the hardware level** whenever the PC/ESP32 is off (see [§1 "Cut the fan when the PC is off"](#cut-the-fan-when-the-pc-is-off--mosfet-power-switch)).
- `LampArrayKind = Peripheral (4)` → gives a colored grid icon in Dynamic Lighting. Change the `k` value in firmware to alter it.

---

## Contributing

Issues and PRs are welcome — especially test results on other ESP32-S2/S3 board variants, new lighting modes, or fixes.

## License

[MIT](LICENSE) © 2026 Çağan Turgut (CaYatur)

This project uses the following third-party libraries (under their own licenses, as separate NuGet/Arduino packages): [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0), [NAudio](https://github.com/naudio/NAudio) (MIT), [HidSharp](https://github.com/IntergatedCircuits/HidSharp) (Apache-2.0/MIT), [Adafruit NeoPixel](https://github.com/adafruit/Adafruit_NeoPixel) (LGPL-3.0).
