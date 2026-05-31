# Logitech-G923-RPM-LED-controller
# G923 LED Plugin for SimHub

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
![SimHub Version](https://img.shields.io/badge/SimHub-9.x+-blue)
![.NET Framework](https://img.shields.io/badge/.NET-4.8+-purple)

**Control the RPM LEDs on your Logitech G923 racing wheel directly from SimHub.**

This plugin reads engine RPM data from any game supported by SimHub and lights up the five LEDs on the G923 wheel according to customisable thresholds. It works automatically with both the PlayStation/PC (PID `0xC266`) version of the wheel.

![G923 LEDs in action](https://i.imgur.com/placeholder-image.png) *(Add a screenshot or GIF here)*

---

## 🚀 Features

- ✅ **Automatic interface detection** – the plugin tests all HID interfaces of your G923 and picks the one that accepts LED commands. Works even if your wheel has a different firmware version.
- ✅ **Smooth RPM mapping** – LEDs start lighting at 65% of max RPM and reach full illumination at 95%.
- ✅ **Blink mode** – when RPM exceeds 95%, all five LEDs flash rapidly.
- ✅ **Zero configuration** – just install, close Logitech G HUB, and launch SimHub.
- ✅ **Game agnostic** – works with every title that provides RPM data to SimHub (Assetto Corsa, iRacing, BeamNG.drive, Forza, etc.).

---

## 📥 Download

1. **Plugin DLL** – [`G923LedPlugin.dll`](link-to-your-dll)  
2. **Required library** – [`hidapi.dll` (32-bit / x86)](https://github.com/libusb/hidapi/releases)  
   - Download `hidapi-win.zip` → extract → copy `x86/hidapi.dll`

> 💡 **Why x86?** SimHub runs as a 32-bit process, so you need the 32-bit version of `hidapi.dll`.

---

## 🔧 Installation

1. **Locate your SimHub folder**  
   Default path:  
   `C:\Program Files (x86)\SimHub\`

2. **Copy both files** into that folder:
   - `G923LedPlugin.dll`
   - `hidapi.dll`

3. **Prepare your G923 wheel**:
   - Connect the wheel to your PC.
   - Start Logitech G HUB once, wait for calibration, then **close G HUB completely** (right‑click the tray icon → *Quit*).  
     ⚠️ Make sure no `lghub.exe` or `lghub_agent.exe` processes remain in Task Manager.

4. **Launch SimHub** (optional: run as Administrator if you encounter write errors).

5. **Enable the plugin** in SimHub:
   - Go to **Settings** → **Plugins**.
   - Find **“G923 LED Plugin”** and ensure it is enabled.

That’s it! The LEDs will now work automatically when you start a race.

---

## 🎮 How It Works

- The plugin continuously reads `Rpms` and `MaxRpm` from SimHub’s `GameData`.
- It calculates the RPM percentage:  
  `pct = Rpms / MaxRpm`
- **LED mapping**:
  - `pct < 65%` → all LEDs off
  - `65% ≤ pct < 95%` → LEDs light progressively (1 to 5 LEDs)
  - `pct ≥ 95%` → all 5 LEDs blink at ~12 Hz
- The command sent to the wheel is:  
  `{ 0x00, 0xF8, 0x12, bitmask, 0x00, 0x00, 0x00, 0x01 }`  
  (where `bitmask` is a 5‑bit value controlling each LED)

---

## ⚙️ Customisation

You can easily change the RPM thresholds by editing the source code and recompiling the plugin:

| Constant | Default | Description |
|----------|---------|-------------|
| `LED_START_PCT` | 0.65 (65%) | RPM percentage when first LED turns on |
| `LED_SHIFT_PCT` | 0.95 (95%) | RPM percentage where blinking begins |

---

## 🐛 Troubleshooting

### ❌ `hid_write` returns -1, error 0x00000057 (Invalid parameter)

This usually means the plugin opened the wrong HID interface. **Your plugin version automatically tests all interfaces**, so it should recover. If the problem persists:

- **Run SimHub as Administrator** – some systems block `WriteFile` on HID devices.
- **Completely close Logitech G HUB** – kill any leftover processes.
- **Reinstall the wheel driver** – in Device Manager, uninstall the G923 under “Human Interface Devices” or “Game controllers”, then unplug and replug the wheel.
- **Try a different USB port** – preferably USB 2.0.

### 🔌 Plugin not working in a specific game

- Make sure SimHub receives RPM data from that game. Open the SimHub dashboard and check if the RPM gauge moves.
- Some games require additional configuration (e.g., enabling shared memory or UDP telemetry). Refer to SimHub’s game‑specific documentation.

### 🧪 Plugin was working, but stopped after a Windows update

- Re‑copy `hidapi.dll` (32‑bit) to the SimHub folder – it may have been removed by antivirus or a system cleanup.

---

## 📝 License

This project is licensed under the **MIT License** – feel free to use, modify, and distribute it.  
See the [LICENSE](LICENSE) file for details.

---

## 🤝 Acknowledgements

- [SimHub](https://www.simhubdash.com/) – the amazing sim racing dashboard software.
- [hidapi](https://github.com/libusb/hidapi) – cross‑platform HID library.
- Logitech – for making the G923 wheel.

---

## 💬 Support & Feedback

For bug reports or feature requests, please [open an issue](link-to-your-repo/issues) on GitHub.  
Pull requests are welcome!

---

**Happy racing!** 🏁
