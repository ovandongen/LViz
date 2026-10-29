# AGENTS.md — project instructions

Conventions, commands and known gotchas for working on this codebase. Keep it
accurate: if something here is wrong or stale, fix it in the same change.
Links into `docs/` point at local-only planning docs; skip them if they don't
exist.

**Fresh clone or git worktree:** the `external/zmk-hid-protocol` submodule
starts empty and the build fails until you run
`git submodule update --init external/zmk-hid-protocol`. Run `dotnet test`
once before changing anything to confirm the baseline is green.

## What this project is

A Windows, macOS, and Linux Avalonia 11 / .NET 10 desktop overlay that
mirrors the active layer of a ZMK keyboard in real time over Raw HID.
Forked from [moergo-layer-viz](https://github.com/ovandongen/moergo-layer-viz)
and generalized so the engine can host arbitrary ZMK boards. **Seven
profiles ship today:** Corne 6-col, Glove80, GO60, Kyria, Lily58, Sofle,
A-Dux (`Core/Layout/*Profile.cs`, registered in `KeyboardProfileRegistry`).
Current release: v2.0.1 on `main`.

The HID transport lives in `external/zmk-hid-protocol` (git submodule).
Boards have to advertise the Raw HID interface (usage page `0xFF60`,
usage `0x61`, 32-byte reports) and emit layer-state (`0xFF`) plus
key-event (`0xF1`) reports. The simplest firmware path is
[zzeneg/zmk-raw-hid](https://github.com/zzeneg/zmk-raw-hid) +
[ovandongen/zmk-hid-viz](https://github.com/ovandongen/zmk-hid-viz)
as ZMK modules in the user's `west` manifest.

**Linux IS a shipped target.** We distribute a Linux binary on the same
footing as Windows and macOS. The only Linux-specific limitation is the
global show/hide hotkey, which requires per-WM cooperation (Wayland
portals, X11 grabs) that we don't ship today. The hotkey registry is
gated `!OperatingSystem.IsLinux()` in code (its Linux stub returns
failure), but the **HID layer source is a first-class Linux feature**
and the app runs there. Don't downgrade Linux to "build-only" in csproj,
release artifacts, or docs.

**No firmware flashing, no keymap editing on-device.** The app reads
layer-state / key-event reports from the keyboard and pushes
`SetLayerState` requests back for the app-focus and mouse-layer
features — that's the full set of side effects on the hardware.

## Repo layout

```
LViz.sln
src/
  LViz.App/    → Avalonia 11 UI, MVVM, HID pipeline, native show/hide
                 hotkey (Carbon on macOS, User32 on Windows), tray,
                 custom chrome, EN/NL localization, per-key label
                 overrides + FontAwesome icon picker.
  LViz.Core/   → pure .NET — dtsi physical-layout parser, keyboard
                 profiles, .keymap parser (Keymap/Parser/) + Moergo
                 .json loader (MoergoJsonKeymapLoader), Raw HID
                 profile matching, settings, diagnostics. No Avalonia.
  LViz.Tests/  → xUnit fixtures.
external/
  zmk-hid-protocol/   → submodule. Raw HID transport + report parser, and
                        the cross-device capability bus — `Capabilities/`
                        (manifest model + `ManifestReader`, `CapabilityCatalog`,
                        `ConfirmTracker`) and `Transport/RawHidDeviceBus`
                        (+ hot-plug). App-side wiring (`CapabilityRouter`,
                        `CapabilityControl`, `KeyboardCapabilityDevice`)
                        lives in `src/LViz.App/Services/`.
docs/                 → local-only roadmap + plans. Gitignored.
AGENTS.md             → this file: project conventions and gotchas.
```

## Common commands

```bash
git submodule update --init --recursive            # first-time clone, or a fresh git worktree
dotnet build                                       # full solution build
dotnet test                                        # run all xUnit tests
dotnet run --project src/LViz.App                  # launch the app
```

## Conventions

- **MVVM via CommunityToolkit.Mvvm.** Use `[ObservableProperty]` and
  `[RelayCommand]` attributes; don't hand-roll `INotifyPropertyChanged` or
  `ICommand` implementations.
- **Atomic writes for anything persisted.** `Core/Persistence/AtomicFile.cs`
  writes via `.tmp` + `Flush(flushToDisk:true)` + `File.Move`. Use it for
  settings, exports, any future on-disk state.
- **Localization via `.resx` + `Loc` singleton.** Both `Strings.resx` (EN
  base) and `Strings.nl.resx` live in `App/Resources/`. Bind UI strings via
  `{Binding [Key_Name], Source={StaticResource Loc}}`. If you add a string,
  add it to both `.resx` files.
- **Diagnostics log at `%APPDATA%/LViz/log.txt`** (path is product-name
  based; confirm via `DiagnosticLog` when in doubt).
  `DiagnosticLog.Info/Warn/Error("Tag", "message")`. Auto-rotates at 2MB.
- **Svalboard-ported chrome/transparency/bars-swap code lives in
  `MainWindow.axaml.cs`.** It works. Don't rewrite it without a concrete
  reason — the 3-tier transparency fallback, 8-way edge resize, and
  position-aware bar swap took real iteration to get right in the sister
  app.
- **`Core/Keymap/Parser/` is the `.keymap` pipeline.** Preprocess →
  devicetree lex/parse → two-pass interpret produces a `KeyboardConfig`.
  Lean v1 by design — function-like `#define`, `#include` resolution,
  recursive behavior resolution, and `/delete-node/` are out of scope
  (see [docs/roadmap.md](docs/roadmap.md) §1). Strict errors throw with
  line/column on malformed input — preserve that loudness; don't try to
  half-render a truncated file.
- **Two keymap loaders, one output.** `MoergoJsonKeymapLoader` parses the
  Moergo Layout Editor `.json` export (glove80.com / go60) into the *same*
  `KeyboardConfig` — recursive modifier params flatten depth-first to match
  the devicetree path (`LA(LC(N5))` → `["LA","LC","N5"]`), decoration
  label/background/icon map to `KeyBinding.Decoration*`. `.json` is **Moergo-
  only**: `KeymapStateService.Load` dispatches by extension and gates `.json`
  on a Moergo profile being active; the loader also rejects a non-Moergo
  `keyboard` field. `MoergoBoards` (Core/Layout) is the single source of truth
  for which profiles/keyboard-fields count as Moergo. Both loaders share
  `KeyboardConfigValidator` for the layer-count consistency check. Unmapped on
  purpose: `decoration.color`, `inputListeners`, `custom_devicetree`. This is
  the import format the fork originally dropped, re-added scoped to Moergo.
- **Per-key label overrides are scoped `(profileId, layerIndex, keyIndex)`**
  → `KeyLabelOverride` (main label, subscript, top-left badge, FA icon,
  font size, bold). Persisted via `UserSettings.KeyLabelOverrides` through
  the `PersistSetting → SettingsService.Save → AtomicFile` chain. Empty
  overrides auto-prune so absent and "all defaults" are indistinguishable
  on disk. Edit dialog: `Views/EditKeyLabelDialog.axaml`.
- **FontAwesome icon picker** reflects into the FA NuGet's embedded
  `icons.json` — see `App/Services/FontAwesomeCatalog.cs`. No extra
  assets shipped; the full ~1895-icon catalog is available with
  name/term search.
- **App-wide control styling lives in `App.axaml` `<Application.Styles>`.**
  The app ships **Inter** as the default font (`Avalonia.Fonts.Inter` +
  `.WithInterFont()` + `FontManagerOptions.DefaultFamilyName="fonts:Inter#Inter"`
  in `Program.cs`) — without it the OS fallback font renders text above-centre
  in Fluent controls on macOS. Global compact styles trim `Button` / `ComboBox` /
  `TextBox` / `CheckBox` heights to ~24px at 12px font with centred content;
  `NumericUpDown` is pinned to `Height=28` (a MinHeight floor doesn't tame a
  *disabled* stepper); CheckBox/RadioButton glyphs are shrunk via the Fluent
  template parts (`Border#NormalRectangle`, `Ellipse#OuterEllipse`/`CheckOuterEllipse`/`CheckGlyph`)
  reached with `/template/` selectors. Tune sizing *there*, not per-control —
  and a local property value still overrides these (Avalonia precedence), so a
  control that needs a different size just sets it inline.

## Before writing code

Cleanup-after-the-fact is more expensive than placement-up-front. Before the
first line of any non-trivial change, do this 30-second pass — out loud if
it forces a real decision, silently if the answer is obvious:

1. **Where does it belong?** What layer (`Core` / `App` / `Tests`), what
   folder, what file? Is there an existing type or utility that already
   does ~80% of this? Extend it instead of adding next to it. The naming
   split is load-bearing: ZMK-firmware concepts get `Zmk*`, our internal
   domain stays unprefixed.
2. **What does this replace?** If a new code path supersedes an old one,
   the same commit deletes the old path. No "keep it just in case"
   branches, no parallel implementations, no `// legacy` blocks. The
   "no backwards-compatibility hacks" rule from the global config is
   enforced here too.
3. **`/simplify` before commit, not after.** While context is fresh,
   scan the diff for dead branches, duplicated logic that already
   exists upstream, premature abstractions, and what-comments (delete)
   vs why-comments (keep). After-the-fact cleanup passes get skipped.

If any of these feels wrong or too expensive for a specific change, say so
and decide — don't silently skip.

## Known gotchas (preserve, don't re-learn)

Carried over from the upstream Moergo build and still apply verbatim:

- **Avalonia 11: `ItemContainerTheme` with `{Binding}` Setters doesn't
  rebind per item.** Use `ItemsControl.Styles` + `Style Selector="ContentPresenter"`
  instead. See `BoardView.axaml` for the correct pattern.
- **Avalonia 11: `xmlns:` prefix type casts (`((vm:T)DataContext).Prop`) don't
  resolve inside deferred DataTemplate content** (e.g. Flyout templates). The
  flyout opens and then crashes with "Unable to resolve type vm:...". Use
  `ElementName=...` bindings without the cast instead. See the picker flyout
  in `MainWindow.axaml`.
- **macOS Carbon hotkey events pump on Avalonia's main run loop** without
  extra wiring — Avalonia 11's AppKit-based loop sources Carbon events
  through `GetApplicationEventTarget`, so a single `InstallEventHandler`
  on that target catches `kEventHotKeyPressed` for every registration. If
  this ever breaks, install an explicit `RunLoopSource` on the main
  `NSRunLoop` before assuming Carbon is dead.
- **dtsi geometry is in centi-units (1u = 100), rotation in centi-degrees.**
  The per-board pixel conversion + (midline, canvas size, hidNameSubstring)
  lives on `DtsiKeyboardProfile`. New boards add `Resources/<board>.dtsi`,
  declare an `EmbeddedResource` in `LViz.Core.csproj`, and subclass
  `DtsiKeyboardProfile` (see `CorneProfile.cs` for the pattern). The
  builder assumes an identity dtsi-index → keymap-binding-index mapping.
  **If upstream dtsi key order doesn't match the keymap's binding-index
  order, reorder the local copy of the dtsi** (with an inline `// NB:
  local reorder` comment) rather than re-introducing a per-profile
  remap in C#. Resyncing from upstream then requires re-applying the
  reorder.
- **Per-key bounds must account for rotation about the explicit pivot.**
  Rotated keys (typical thumb clusters) can swing far outside the
  unrotated `(X, Y, X+W, Y+H)` rect. Call `KeyPosition.RotatedBounds()`
  for any code that needs a hand's bounding box (stacked-mode translation,
  screenshot framing, …) — never reach for the raw `(X, Y, Width, Height)`
  on rotated keys.

LViz-specific (extend this list as we hit more):

- **hidapi native binaries are vendored under
  `external/hidapi-binaries/runtimes/<rid>/native/` and copied to the
  output root** (not `runtimes/<rid>/native/`). The .NET native-library
  loader searches the exe directory, which works for both `dotnet run`
  and self-contained publish. The csproj has a `_HidapiHostRid` probe
  that picks the right RID when no `-r` is passed, so `dotnet run` works
  on a dev box without `-r osx-arm64`.
- **`Tmds.DBus.Protocol` is pinned to 0.21.3** in `LViz.App.csproj` to
  override `Avalonia.FreeDesktop`'s transitive 0.21.2 (CVE
  GHSA-xrw6-gwf8-vvr9). Don't downgrade. If Avalonia ever bumps its own
  pin past 0.21.3, drop the override.
- **HID profile matching is by name substring**
  (`DtsiKeyboardProfile.hidNameSubstring`), not VID/PID alone — multiple
  ZMK boards share the pid.codes VID/PID. When adding a board, pick a
  substring distinct enough not to collide with siblings.
- **macOS Velopack `.pkg` is unsigned → Gatekeeper blocks first install.**
  Expected behavior until we ship a Developer ID cert (deferred — see
  velopack plan). User bypass: System Settings → Privacy & Security →
  "Open Anyway" after the failed install, or
  `xattr -dr com.apple.quarantine ~/Downloads/LViz*.pkg && sudo installer -pkg … -target /`.
  First install always needs admin regardless of scope.
- **macOS: recommend "Install for me only" in the .pkg installer.**
  The Velopack `.pkg` exposes Change Install Location → "Install for
  me only", which puts `LViz.app` in `~/Applications` user-owned.
  Verified: subsequent in-app updates apply silently with no admin
  prompt. The default system-wide install (root-owned `/Applications/LViz.app`)
  re-prompts for admin on every content-changing update. Both notes
  belong in the README; keep in sync with `docs/roadmap.md` §5.

- **hidapi enumerate/open is NOT thread-safe on macOS — serialize it.**
  hidapi's macOS backend schedules an `IOHIDManager` on a run loop inside
  `hid_enumerate`/`hid_init`; two threads enumerating (or opening) at once
  abort the process — `IOHIDManagerScheduleWithRunLoop` → `"Schedule failed
  queue"` → SIGKILL, no managed stack. LViz runs two discovery loops (the
  keyboard `RawHidLayerSource` reconnect tick + the capability
  `RawHidDeviceBus` hot-plug poll), so every `Hid.Enumerate()` (materialized
  inside the lock) and every `ConnectToDevice()` is wrapped in
  `HidGlobalLock.Gate` (submodule, `Transport/HidGlobalLock.cs`). Reads/writes
  on an already-open handle stay lock-free. Any new hidapi enumerate/open call
  site MUST take this gate. Unit tests can't catch this — it needs real
  hardware + concurrent loops.

- **The keyboard must never be opened twice — the capability bus excludes
  it by matcher AND path.** The keyboard is owned by the HID pipeline's
  `RawHidLayerSource`; the capability `RawHidDeviceBus` enumerates the *same*
  USB devices and must NOT also open the keyboard, or two handles race the one
  interface. The bus excludes it two ways: the injected `IDeviceMatcher` (the
  live keyboard profile's matcher) drops matching rows, and
  `HidPipeline.OpenDevicePath` is passed as an explicit exclude-path. Any
  change to bus enumeration MUST preserve both. (Verified on hardware: a
  Glove80 resolved as the keyboard while a Ploopy Bean was the lone bus
  device — no double-open.)

- **macOS surfaces the Raw HID interface as interface number `-1`.** The
  `0xFF60`/`0x61` usage comes back on interface `-1` (not 0/1/…), and that is
  the handle that actually carries layer-state/manifest reports — reads/writes
  work normally on it. The bus dedup (`SelectBusDevices`, submodule
  `RawHidDeviceBus`) ranks `-1` *highest* when collapsing identical (vid,pid)
  rows. Don't treat `-1` as invalid or filter it out.

- **`CapabilityRegistry` is the single source of truth for hardcoded capability
  knowledge — every other capability is a forward-compatible string.** Manifests
  carry ids as opaque UTF-8 (`ManifestAssembler` decodes whatever a device sends),
  so an unknown id is discovered + inventoried with zero code changes — just inert.
  The host hardcodes only the wire-byte tables it must encode/decode itself, and
  those now live in `CapabilityRegistry` (+ `CapabilityDefinition`/`PayloadShape`/
  `ValueKind`/`CapabilityIds`, submodule): `CapabilityCatalog.ActionIdByByte`/
  `ValueKindByByte` are **projections** of the registry (the old literal dicts are
  deleted), and `external/zmk-hid-protocol/docs/capability-registry.md` is generated
  + staleness-tested from it. Routable = `WireByte != null && Roles contains
  Triggers` (keeps layer bytes / `signal.fire` out of forwarding). Consequence
  unchanged: a route for a non-routable capability can *display* but never
  *forward*. (Registry consolidation done 2026-06-14; see
  `docs/capability-registry-plan.md`.)

- **A device's stable key flips if its manifest read fails — so the router
  self-heals failed scans.** `CapabilityRouter.DeriveStableKey` keys a device by
  its manifest `configId`/name when the manifest read succeeds, but by its
  *transient transport name* (`vid:pid:displayName`) when it returns null. A
  startup scan that loses the HID race (manifest times out) therefore keys the
  device differently, so a persisted target (action-pipeline step, routing rule)
  resolves to nothing in `FindDevice` — the symptom was RGB/pointing pipeline
  steps silently no-op'ing until a Settings tab open forced a rescan. Fix:
  `ScanAsync` schedules a bounded backed-off rescan (3 attempts, 500/1000/2000ms)
  whenever any device comes back with no manifest; `RescanAsync`/external triggers
  reset the budget, a clean scan clears it. Don't reintroduce a code path that
  treats a one-shot scan as authoritative. (NB: the capability registry does
  **not** address this — it pins capability *ids*, not device-identity keys. The
  backed-off rescan is the actual fix; there's no registry change coming for it.)

- **App exit unconditionally forces the keyboard to base layer 0 — don't
  weaken it back to a conditional revert.** The board can't tell LViz has quit,
  so any layer an engine (auto-switch / mouse) or the action UI pushed would
  stay pinned after exit, leaving it stuck on a non-base layer with no overlay
  to explain it. `MainWindowViewModel.Shutdown` calls
  `StopKeyEventTracking(isAppExit: true)` →
  `LayerPushCoordinator.ForceBaseLayerOnExit` → `PushLayerSync(0, 500ms)` *before*
  `_hid.Stop()`, unconditionally (no `IsLastChangeAppControlled` gate), no-op only
  when no keyboard is connected. The *live-tracking toggle-off* path
  (`StopKeyEventTracking()` with no arg, from `ToggleLiveHighlighting`)
  deliberately keeps the old `RevertMouseLayerForShutdown` instead — it can be
  re-enabled (needs the mouse engine's pre-move state reset cleanly) and must not
  override a layer the user physically toggled while just hiding the overlay.
  Keep the two paths distinct.

- **The inbound layer-state decode validates payload shape — don't weaken it
  back to an opcode-only check.** `0xFF` (`core.layer.changed`) collides with
  VIA's `id_unhandled` sentinel: a VIA-enabled device on a shared raw-HID
  interface stomps `data[0]` to `0xFF` and echoes the rejected command back. So
  `RawHidProtocol.TryParseLayerStateBitmask` (submodule) requires `[1] ==
  HidConstants.Outbound.LayerStateFormat` (`0x04`, the viz-format marker) **and**
  a single-bit default-layer mask at `[2..5]` before reading the bitmask at
  `[6..9]`; on mismatch it drops the one report (returns null, no app back-off).
  This is the *only* inbound notify that validates shape today; the others stay
  id-byte-only until the registry-driven generalisation lands. Forwarding a
  VIA echo was already impossible (`0xFF` is `Notifies`-only, never in
  `ActionIdByByte`), so the guard only removes a one-frame *local* viz glitch.
  Wire assumption: the default-layer mask has exactly one bit — holds for the
  shipped firmware; revisit the guard + its test if a board emits it otherwise.
  See `docs/lviz-dotnet-inbound-validation.md`.
