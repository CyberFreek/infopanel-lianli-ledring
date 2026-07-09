# v1.0.0 — Lian Li LED Ring for InfoPanel

First release. Controls the addressable LED ring on the **Lian Li Universal Screen 8.8"**
(`VID 0x0416 / PID 0x8050`) from InfoPanel.

## Highlights

- **All 19 L-Connect effects** — Rainbow, Wave, Static Color, Breathing, Rainbow Morph,
  Paint, Runway, Tide, Blow Up, Meteor, Snooker, Mixing, Ping-Pong, Bullet Stack, Twinkle,
  River, Hourglass, Electric Current, Rainbow Wave.
- **Graphical color picker** — the "Configure Colors…" button opens a dark-themed picker
  window; each of the six colors shows as a swatch with its hex code and a full
  spectrum/hex picker. A "Colors used" control sets how many colors multi-color effects cycle.
- **Live controls** — speed (1–10), brightness (0–100), reverse direction. Changes apply
  to the ring immediately.
- **Robust** — auto-reconnects if the ring is unplugged; settings persist across restarts.

## Install

1. Download `InfoPanel.LianLiLedRing.zip` below.
2. Extract into `%ProgramData%\InfoPanel\plugins\` so you have
   `…\plugins\InfoPanel.LianLiLedRing\InfoPanel.LianLiLedRing.dll`.
3. Start InfoPanel → **Plugins** page → enable **Lian Li LED Ring**.

## Notes

- This plugin controls the **LED ring only**. The 8.8" **LCD** is a separate USB device
  and is handled by InfoPanel's built-in USB panel support.
- Requires InfoPanel 1.4.x (plugin host with `IPluginConfigurable` / `PluginAction`).
- Not affiliated with Lian Li. Protocol details determined by reverse engineering for
  interoperability.

**Full changelog:** initial release.
