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
                 overrides + FontAwesome icon picker, OS secret stores
                 (Services/Secrets/), GitHub service (Services/GitHub/).
  LViz.Core/   → pure .NET — dtsi physical-layout parser, keyboard
                 profiles, .keymap parser (Keymap/Parser/) + Moergo
                 .json loader (MoergoJsonKeymapLoader), Raw HID
                 profile matching, settings, diagnostics, GitHub client,
                 login session and repo cache (GitHub/). No Avalonia.
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
  settings, exports, any future on-disk state. A save that writes several
  files (a `.layout.json` plus its `.dtsi`, in one or two folders) takes a
  `FileRollback.Capture` of every target first and calls `Restore()` in the
  catch, so a failure half-way leaves all files as they were.
- **Localization via `.resx` + `Loc` singleton.** Both `Strings.resx` (EN
  base) and `Strings.nl.resx` live in `App/Resources/`. Bind UI strings via
  `{Binding [Key_Name], Source={StaticResource Loc}}`. If you add a string,
  add it to both `.resx` files. No test compares the two key sets, and `Loc`
  shows `[Key]` for a missing key instead of failing, so diff the key lists
  yourself.
- **Diagnostics log at `ApplicationData/LViz/diagnostic.log`**
  (`~/Library/Application Support/LViz/` on macOS; confirm via
  `DiagnosticLog.GetLogFilePath()`).
  `DiagnosticLog.Info/Warn/Error("Tag", "message")`. Auto-rotates at 2MB.
  Tests write to the **same real log** — a `dotnet test` run leaves its
  warnings there, so clear the log before reading it for a manual test.
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
- **`Core/Keymap/Writer/` patches the original `.keymap` text, never
  regenerates it.** `KeymapSourceMap` maps every parsed binding and
  `display-name` to an exact span in the source and refuses (with a line
  number) any layer it can't match one-to-one; `KeymapWriter` replaces only
  the changed spans and re-parses the result as a bug guard. Key indices
  come from the parsed `KeyboardConfig`, never from counting cells in the
  text (unknown-arity behaviors and function-like macro calls make the two
  differ). A layer number is written as a define name only when the file
  already uses that define as a layer reference (`&mo NAME`, `&lt NAME …`):
  MoErgo exports define `KB_TYPE_GLOVE_80 1` ahead of `LAYER_Symbol 1`.
- **`Core/Keymap/Editing/KeymapEditSession` tracks layers by id, not
  position.** Baseline layer `i` is id `i`; an add takes a fresh id that is
  never reused. Each id has an origin (its baseline name and bindings, or what
  it was added with), and `IsKeyChanged`/`IsLayerRenamed`/`ChangeCount` compare
  `Current` with those origins, never with the undo stack. Keys that a
  structural edit only renumbered (`&mo 2` → `&mo 3`) deliberately count as
  changed. Steps are private nested records so they reach the id list
  directly. `ApplyStructure` stores the caller's `after` verbatim and redo
  replays it, so `after` must be derived from `Current`, with `Layer.Index` ==
  position. `PendingStructureEdits` is empty whenever the ids are back to the
  baseline list, even with structural steps still undoable.
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
- **Picking catalogs live in `Core/Keymap/Catalog/`, codes and kinds only.**
  `ZmkKeycodeCatalog` is the pickable keycode list from the embedded
  `Resources/zmk-keycodes.json`, generated from ZMK's MIT `keys.h` by the
  local-only `docs/tasks/gen_keycodes.py`. Regenerate it, never hand-edit it;
  a regeneration that changes counts must update `ZmkKeycodeCatalogTests`.
  `ZmkKeycodeLabel`/`ZmkKeycodeMapper` stay the display-glyph path.
  `ZmkBehaviorCatalog` (behaviors + parameter kinds) and `ZmkModifierWrap`
  (flat `["LC","LS","A"]` form ↔ flags) carry no display strings: friendly
  names and explanations are resx UI text.
- **Three layout importers, one result.** `Core/Layout/Import/` turns board
  files into `ImportedLayout`s (keys as `DtsiKeyPhysical` in ZMK units, in
  keymap binding order, plus warnings): `PhysicalLayoutImporter`
  (`zmk,physical-layout` dtsi), `InfoJsonImporter` (keymap-editor/QMK
  `info.json`, not to be confused with the Moergo `.json` keymap export) and
  `MatrixTransformImporter` (rough layout from the `map` text, RC grid when
  it is one line). Malformed input throws `LayoutImportException` with a
  1-based line; "nothing found" is an empty list. The dtsi-based ones share
  `Dtsi/DtsiText`: `MaskComments` keeps length and newlines, so offsets and
  line numbers found on masked text are valid in the original. Bundled
  board resources still go through `DtsiPhysicalLayoutParser.Parse`, which
  insists on exactly one physical-layout node. Test fixtures under
  `LViz.Tests/Layout/Fixtures/` are embedded by wildcard: drop a file in and
  read it with `LayoutFixtures.Read("<file>")`.
- **Layout editor: one geometry home, two-step drafts.** `UserLayoutGeometry`
  (Core/Layout) owns all `UserLayoutKey` rotation maths (centres, corners,
  hit-test, move/rotate/hands/normalise); don't re-derive rotations
  elsewhere. A draft goes `UserLayoutDraft.KeysFrom(ImportedLayout)` (hands
  from `ImportedLayout.Hands` when the count matches, else the midline) then
  `Create(keys, …)`, which normalises so the rotated bounds start at (0, 0).
  `LayoutEditorViewModel` (shared by the wizard's adjust step and the
  "Edit layout…" window) computes every drag from the snapshot taken on
  press, and its `Keys` are always hands-applied for the split line.
  `.dtsi` exports carry `ZmkPhysicalLayoutWriter.GeneratedMarker` on line 1;
  only a file with that marker is ever overwritten, and the scanner skips an
  export whose `<id>.layout.json` twin sits next to it. A saved-layout
  candidate (`ImportedLayout.SavedId`) is saved back under its own id.
- **Layout editor snapping and mirroring.** A move drag goes through
  `UserLayoutGeometry.SnapMove`: neighbour edges, centres and flush within the
  press tolerance, else the 0.25u grid, then a move that leaves an *unrotated*
  key overlapping another is pushed flush the shortest free way. Free
  (Option/Alt) skips all of it; the arrow-key nudge stays on the grid. So a
  test that drags a key sideways into a touching neighbour sees it pushed
  back: drag away from neighbours unless the push is what you test.
  `Mirror(keys, from)` flips about the middle between the halves' average key
  centres, not the split line (that only decides hands). The mirror toolbar
  shows only when `LayoutEditorViewModel.IsSplit`: the wizard's grid path
  passes its split answer, everything else falls back to `HasSplitGap`
  (innermost centres ≥ 1.25u apart). Bounding-box gaps don't work for that:
  Corne's angled inner thumbs overlap in X.
- **Wizard grid default from the keymap.** Keys per half defaults to the
  keymap until the user *changes* one of the three counts (a write of the
  same value, e.g. a binding echo, doesn't count); the split toggle re-runs
  the default rather than ending it.
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
  `Button.accent` (primary dialog button) and `Button.link` (text link) are
  defined there too. Fluent paints hover/pressed on
  `/template/ ContentPresenter#PART_ContentPresenter`, so state overrides go on
  that part. Palette brushes inside `Application.Styles` need `DynamicResource`:
  `Application.Resources` is parsed after the styles.
- **GitHub keymaps: one HTTP class, refs in the normal settings lists.**
  `Core/GitHub/GitHubClient` is the only code that talks HTTP; every call goes
  through its private `ApiRequest`/`SendAsync`/`SendForJsonAsync`, which own
  the status → `GitHubErrorKind` mapping. Exception messages are
  `GitHub {METHOD} {path} returned …` (path only) so a token never reaches
  the log; `GitHubTokens.ToString()` omits the tokens. `GitHubSession` owns
  the login (keychain JSON under `GitHubAppInfo.SecretKey`, refresh under 5
  minutes left, signs out on a rejected refresh); everything else asks it for
  a token per call. `ISecretStore` implementations never throw for platform
  failures (warn + "unavailable"), picked per OS by `SecretStoreFactory`.
  A GitHub keymap is stored as one `github:owner/repo:branch:path` string
  (`GitHubKeymapRef`) in the same `LayoutJsonPaths`/`RecentKeymapPaths` dicts
  as local paths; the file actually loaded is the cache copy under
  `ApplicationData/LViz/github/`, so `MainWindowViewModel` tracks the loaded
  *entry* in `_loadedEntry` (recents `IsCurrent`, wizard source), never
  `LoadedLayoutPath`. Startup, board switch and Refresh load the cache copy
  and then run a silent background check that reloads a newer commit or
  fetches a cold cache. A GitHub entry without a cache copy is kept, never
  pruned like a missing file (sign-out deletes the whole cache). The wizard
  books the already-open keymap for the board it saves.

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
- **`dotnet build` does NOT check `{Binding}` paths.** `LViz.App.csproj`
  doesn't set `AvaloniaUseCompiledBindingsByDefault`, so plain `{Binding}`s
  are reflection bindings even under `x:DataType`: a mistyped path builds
  clean and binds to nothing at runtime. The build only catches type and
  element names and missing `x:Name` fields. Check each new binding path
  against the view-model by hand, and each new `Loc` key against both
  `.resx` files. Still keep `x:DataType` on every new `DataTemplate`
  (needed for the cast-free bindings and for a later switch to compiled
  bindings).
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
  reorder. `DtsiBackedLayoutBuilder.Build` throws when a profile's
  `keyCount` differs from the dtsi entry count.
- **User layouts are `<id>.layout.json` files in
  `ApplicationData/LViz/layouts/`** (`UserLayoutStore`), keys in ZMK units
  with an explicit hand per key. A file loads only when its name matches its
  id; a broken file is skipped with a `Layouts` warning, never a crash.
  `App.axaml.cs` registers them via `KeyboardProfileRegistry.SetUserProfiles`
  as `UserLayoutProfile`s with ids `user:<id>`, listed after the bundled
  boards. Key-count auto-switch must go through
  `KeyboardProfileRegistry.FindByKeyCount` (user layouts win), never
  `All.FirstOrDefault`. `All` is a fresh array per read: don't rely on its
  identity.
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

- **Every new public property on `MainWindowViewModel` trips
  `SettingsViewModelMainPropertyCoverageTests`.** The test enforces that the
  Settings window doesn't silently miss main-VM changes: a new public property
  needs either an entry in `SettingsViewModel._mainPropagators` or its name in
  `IgnoredMainProperties` in that test, with a one-line reason. Main-window-only state (e.g.
  `HasRecentKeymaps`) belongs on the ignore list. Plan for this edit when a
  change adds a VM property; it is the one expected change to an existing test.

- **A member named like a Core static helper hides that helper inside the
  class.** `MainWindowViewModel.RecentKeymaps` (a property) shadows the Core
  type `LViz.Core.Settings.RecentKeymaps`, so calls to the helper inside the
  view-model fail to compile or bind to the wrong thing. Reach the helper
  through a `using` alias instead
  (`using RecentKeymapHistory = LViz.Core.Settings.RecentKeymaps;`, top of
  `MainWindowViewModel.cs`). Same applies to any new Core helper whose name
  matches a settings property (e.g. `HiddenLayers`).

- **`KeyBinding` record equality does not compare `Params` contents** (it
  compares the list reference). Compare `Behavior` plus
  `Params.SequenceEqual`, in code and in tests.

- **A C# local named `from` can't be the receiver of a `with` expression**
  (`from with { … }` parses as a LINQ query, even in parentheses). Rename the
  local or use the constructor.

- **xUnit analyzer warnings break the 0-warning build**: `Assert.Equal(1,
  x.Count)` must be `Assert.Single(x)` (xUnit2013).

- **Don't close an Avalonia flyout synchronously from a button inside it.**
  The button resolves and runs its `Command` *after* its `Click` handler; hiding
  the flyout in that handler detaches the content from the window first, so the
  command binding is gone and the click silently does nothing. Close it on the
  next dispatcher turn instead:
  `Dispatcher.UIThread.Post(() => SomeButton.Flyout?.Hide())`. See the
  load-layout flyout in `MainWindow.axaml.cs`.

- **`System.Text.Json` on user files: `JsonException.LineNumber` is 0-based,
  and `JsonElement.TryGetProperty` throws on a non-object.** Report
  `LineNumber + 1` to the user, and check `ValueKind == Object` before every
  `TryGetProperty` on untrusted JSON (root, each nested value). See
  `InfoJsonImporter`.

- **`ErrorText` is the white text on the red error toast, not an error
  colour.** Inline error text in a dialog uses `AccentRed`.

- **`ZmkKeymapLoader.Load` wraps every failure, file-read errors included, in
  `ZmkKeymapParseException`.** Catching that one type is complete; don't add
  extra catches for IO errors.

- **Replacing the active profile under the same id must go through
  `ReplaceUserProfile` + `ShowProfile`** (`MainWindowViewModel`), never
  `SelectKeyboard` directly: `SelectKeyboard` returns early on an identical
  id and would leave the old geometry on screen.

- **Never read an `[ObservableProperty]` backing field outside the
  constructor.** CommunityToolkit emits MVVMTK0034 per read and the build is
  0-warning. Use the generated property, and `partial void OnXChanged` for
  derived state. A value that must be clamped *before* it is stored (the
  wizard's grid steppers) needs a hand-written `SetProperty` setter, because
  the `On…Changing/Changed` hooks see the value already stored.

- **A command whose CanExecute reads a child view-model** (e.g.
  `Editor.IsModified`) can't use `[NotifyCanExecuteChangedFor]`; subscribe to
  the child's `PropertyChanged` and call `XCommand.NotifyCanExecuteChanged()`
  (see `LayoutEditWindowViewModel`).

- **Avalonia 11.3 custom-drawn controls** (`LayoutEditorCanvas`):
  `OnSizeChanged` isn't virtual on `Control`, so listen to the `SizeChanged`
  event to repaint on resize; the `propertyChanged:` argument of
  `AvaloniaProperty.Register` doesn't compile (use
  `Prop.Changed.AddClassHandler<T>` in a static ctor); `Matrix.CreateRotation`
  has no centre overload (use `new RotateTransform(deg, cx, cy).Value`); there
  is no `Cursors` class (use `new Cursor(StandardCursorType.X)`); text drawn
  with `FormattedText` needs `new Typeface(FontFamily.Default)`, because a bare
  `new Typeface()` has no family and throws inside `Render` on every frame,
  which freezes the window. Tests can't catch any of this; only a real render does.

- **A test that calls a `[SupportedOSPlatform]` App type trips CA1416** (the
  early `if (OperatingSystem.IsMacOS()) return;` doesn't count as a guard).
  Wrap the test class in `#pragma warning disable/restore CA1416` with a
  why-comment, as `MacKeychainSecretStoreTests` does.

- **xUnit `[InlineData]` can't take an implicitly typed array** (`new[] { "x" }`
  → CS0182). Use `new string[] { … }` or a `[Fact]`.

- **`Tmds.DBus.Protocol` 0.21.3 is not the API on GitHub's main branch.**
  There `MessageWriter` is a ref struct; in 0.21.3 it isn't, and `Variant` is
  obsolete (use `VariantValue`, `Dict<,>.AsVariantValue()`). Check members
  against the NuGet assembly, not main-branch samples. Idiom: see
  `LinuxSecretServiceStore`.

- **.NET 10 `TarEntry` is abstract.** Build test archives with
  `new GnuTarEntry(TarEntryType.RegularFile, name) { DataStream = … }`.

- **`FakeHttpHandler.Respond` is a synchronous func**: blocking in it blocks
  the test thread. A test that must hold a response open needs its own
  async handler (see `GatingHttpHandler` in `GitHubSessionTests`).

- **`MainWindowViewModel.InitializeAsync` starts the HID stack when
  `LiveKeyHighlighting` is true** (the default). Startup tests set it false.

- **A horizontal `StackPanel` measures its children unbounded, so
  `TextTrimming` inside it never trims.** Put an icon + trimmed text in a
  `DockPanel` instead (recents row in `MainWindow.axaml`). Avalonia's
  `SelectableTextBlock` centres with `TextAlignment`, not WPF's
  `HorizontalTextAlignment`.

- **FontAwesome icon names:** `icons.json` is embedded in the Projektanker
  FontAwesome assembly (`FontAwesomeCatalog` reads it); brand icons need the
  `fa-brands` prefix (`fa-brands fa-github`).
