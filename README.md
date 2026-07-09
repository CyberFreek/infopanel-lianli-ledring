# InfoPanel — Lian Li LED Ring Plugin

An [InfoPanel](https://github.com/habibrehmansg/infopanel) plugin that controls the
addressable LED ring on the **Lian Li Universal Screen 8.8"** (`VID 0x0416 / PID 0x8050`).
It streams all 19 L-Connect lighting effects to the ring from the host, with multi-color
support, speed / brightness / direction control, and a graphical color picker.

> This plugin drives **only the LED ring**. The 8.8" LCD itself is a separate USB device
> (`VID 0x1CBE / PID 0xA088`) handled by InfoPanel's built-in Turing/USB panel support.

## Features

- All 19 effects, matching L-Connect: Rainbow, Wave, Static Color, Breathing, Rainbow Morph,
  Paint, Runway, Tide, Blow Up, Meteor, Snooker, Mixing, Ping-Pong, Bullet Stack, Twinkle,
  River, Hourglass, Electric Current, Rainbow Wave.
- Up to 6 user colors for multi-color effects, with a live count control.
- Speed (1-10), brightness (0-100), and reverse-direction options.
- A bundled graphical **color picker** (`LedRingColorPicker.exe`) - a small dark-themed
  WPF window that shows each color as a swatch and opens a full spectrum/hex picker.
- Auto-reconnect if the ring is unplugged/replugged.

## Install

1. Download the latest `InfoPanel.LianLiLedRing.zip` from [Releases](../../releases),
   or build it yourself (below).
2. Extract it into `%ProgramData%\InfoPanel\plugins\` so you have:
   `%ProgramData%\InfoPanel\plugins\InfoPanel.LianLiLedRing\InfoPanel.LianLiLedRing.dll`
   (plus `LedRingColorPicker.exe`, `MahApps.Metro.dll`, `ControlzEx.dll`, `PluginInfo.ini`, ...).
3. Start InfoPanel and enable **Lian Li LED Ring** on the **Plugins** page.

## Usage

On the Plugins page, the plugin card exposes:

| Setting | Description |
| --- | --- |
| LED Ring Enabled | Master on/off |
| Effect | One of the 19 effects |
| Speed | 1 (slow) - 10 (fast) |
| Brightness | 0 - 100 % |
| Reverse Direction | Flip animation direction |
| **Configure Colors...** | Opens the graphical color picker |

**Configure Colors...** launches a window with six MahApps color fields (each shows the
actual color plus its hex code) and a "Colors used" control. Single-color effects use
Color 1; multi-color effects cycle through the active colors. Changes apply to the ring
immediately. Colors are stored in `%LOCALAPPDATA%\InfoPanel\plugins\lianli-led-ring.colors.txt`.

## Build

Requires the .NET 8 SDK on Windows.

```
dotnet build InfoPanel.LianLiLedRing/InfoPanel.LianLiLedRing.csproj -c Release
```

The output folder `InfoPanel.LianLiLedRing/bin/Release/net8.0-windows/` is the complete,
shippable plugin folder. Building the plugin also builds and bundles the color picker exe
automatically.

### About the SDK reference

The plugin compiles against `InfoPanel.Plugins` (the InfoPanel plugin SDK). A copy of that
assembly is bundled under `lib/` so the plugin builds without the full InfoPanel source
tree. It is referenced with `Private=false` and is **not** shipped in the plugin output -
InfoPanel provides it at runtime. To update it, drop a newer `InfoPanel.Plugins.dll` into `lib/`.

## How it works

The LED ring is a vendor-class USB device. Frames are sent as 64-byte bulk packets on
endpoint `0x01` (`0x11` = set-LED-chunk; 3 packets paint all 60 LEDs). There is no hardware
effect engine - every effect is rendered on the host and streamed, pacing each frame at the
vendor's `speed x interval` cadence. Effect implementations are original, written from an
understanding of the observed effect behavior.

## Credits

- [InfoPanel](https://github.com/habibrehmansg/infopanel) by habibrehmansg - the host app and plugin SDK.
- Protocol understanding aided by [sgtaziz/lian-li-linux](https://github.com/sgtaziz/lian-li-linux).

Not affiliated with Lian Li Industrial Co., Ltd. 

## License

MIT - see [LICENSE](LICENSE).
.
