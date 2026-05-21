# LViz — ZMK layer visualizer (preview)

Cross-platform desktop overlay (Windows, macOS, Linux) that mirrors the
active layer of a ZMK keyboard in real time over Raw HID. Forked from
[moergo-layer-viz](https://github.com/ovandongen/moergo-layer-viz) and
generalized so the engine can host arbitrary ZMK boards.

## Status

**Preview.** The HID layer source, per-app auto-switching engine,
mouse-idle layer push, cross-platform overlay chrome and native global
hotkeys all work — these are inherited from the upstream Moergo build.

The `.keymap` parser that produces on-screen key labels does **not**
exist yet. Until it lands, the app uses a placeholder loader that
labels each key with its position index (`"idx 0"`, `"idx 1"`, …). One
board profile ships out of the box: **Corne 6-column** (foostan).

Explicitly missing from this preview:

- `.keymap` parser
- Board profiles other than Corne (kyria, sofle, lily58, GO60, Glove80, …)
- User-editable key labels

## Firmware

LViz reads a custom **Raw HID** interface (usage page `0xFF60`, usage
`0x61`, 32-byte reports) — the same convention used by QMK Raw HID and
the upstream Moergo build. Your board has to advertise that endpoint
and emit layer-state (`0xFF`) plus key-event (`0xF1`) reports. The
protocol reference lives in
[external/zmk-hid-protocol](external/zmk-hid-protocol). The simplest
path is to add [zzeneg/zmk-raw-hid](https://github.com/zzeneg/zmk-raw-hid)
plus [ovandongen/zmk-hid-viz](https://github.com/ovandongen/zmk-hid-viz)
as ZMK modules in your `west` manifest.

## Build

.NET 10 SDK. Clone with submodules, build, run.

```bash
git clone --recurse-submodules https://github.com/<you>/LViz.git
cd LViz
dotnet build
dotnet test
dotnet run --project src/LViz.App
```

## Project layout

```
LViz.sln
src/
  LViz.Core/    pure .NET — dtsi physical-layout parser, keyboard profiles,
                Raw HID protocol parser, settings, diagnostics. No Avalonia.
  LViz.App/     Avalonia 11 UI, MVVM, HID pipeline, native show/hide hotkey
                (Carbon on macOS, User32 on Windows), custom chrome,
                EN/NL localization.
  LViz.Tests/   xUnit fixtures.
external/
  zmk-hid-protocol/   submodule — Raw HID transport + report parser.
```

## License

MIT — see [LICENSE](LICENSE).
