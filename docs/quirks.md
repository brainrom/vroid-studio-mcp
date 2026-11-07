# Quirks of the internal API (engine facts)

This document lists engine behaviours that are **not** obvious from the `Editor/` code. For how to
call the API, see `plugin/VroidStudioMcp/Editor/` and `docs/architecture.md`.

## Runtime diagnostics

Routine hook, category, command, color-selection, preview-RT and screenshot traces are emitted
at Info level only when `debug_artifacts=true` in `servers.json`. Keep Info enabled in the
BepInEx log levels to collect them. These messages describe successful operations; actual
warnings and errors keep their existing levels and remain independent of this flag.

The external render suite deliberately opens a malformed model and attempts an invalid save.
Those negative tests can produce Unity exceptions even when the suite passes; distinguish
them from failures during normal editing when reviewing a test-session log.

## Readiness and synchronization

- **"Model loaded"** = `VRoidCoreModelUpdater.set_IsUpdatingModel` true→false.
  `Engine.Model/ViewModel.OnModelUpdateComplete` is **not** called on open/presets.
- **Any change is asynchronous.** After selecting a preset/parameter/color you need to wait for
  `UpdateSelectedPresets` or `IsUpdatingModel true→false`. The sign that "the change is applied" is
  not setting a flag, but this event.
- **Polling with screenshots during application throttles the rebuild.** A heavy `ShotHash()` on every tick
  (ReadPixels+EncodeToPNG+PNG write) prevents the job from completing. Take a frame — **once**
  after the signal.
- The software is non-deterministic in time (open takes ~6–32 s); occasionally the model does not
  appear. Rely on signals + timeouts, not fixed sleeps.
- **A modal can set `Time.timeScale = 0`** (for example, when loading a heavy model). Any
  `WaitUntil`/`Pause` that counts by scaled `Time.deltaTime` then does not complete and **hangs the whole
  command queue** (the channel is alive, but `_current` is not released). Therefore all plugin waits —
  `CommandDispatcher`/`VroidTest` — use `Time.unscaledDeltaTime`. The regression is caught by the test
  `timescalefuzz` (does not require a model): it manually drives waits under `timeScale=0` with a watchdog.

## Background/minimized window

- **Windows console selection can pause the app.** If the BepInEx console title starts with
  `Select`, press Escape in that console to leave text selection before diagnosing a stalled
  command. Keep disk logging enabled when collecting evidence; the optional production config
  disables the console.

- **The plugin forcibly keeps `Application.runInBackground = true`** (the app itself turns it off via
  `RunInBackgroundUtil`), so the main-thread command queue does not stall when the window is out of focus or
  minimized. The tick is confirmed by `ping` even in the minimized state.
- **A minimized window breaks restore under Wine/DXVK.** A minimized window has no valid
  swapchain surface, but the player keeps drawing into it. With vsync enabled (FIFO), present on
  unminimizing blocks on swapchain recreation and hangs **the whole player loop**: `frameCount`
  freezes (with `rb=true` and `focused=true`), `ping`/commands pile up in the queue, and only a window
  message (click) wakes it. Confirmed by native stacks: the main Unity thread is stuck in
  `WaitForMultipleObjectsEx` (`MsgWaitForMultipleObjects`) of the main loop and waits for the render thread,
  which is stuck in DXVK (`dxgi`/`d3d11`/`winevulkan`) until the swapchain is resolved.
- **`Runtime/WindowBackground.cs`** on a minimized window (`IsIconic`) switches
  `QualitySettings.vSyncCount = 0` and, if `Application.targetFrameRate <= 0`, sets `60`. With
  vsync off, present does not block, and the target frame rate makes Unity wait for a frame with a
  timeout, so the queue ticks and restore does not hang. 2 s after the window appears the original
  settings are restored. Minimization is detected via `user32!IsIconic` (by the hwnd of the process's
  main window); on non-Windows (macOS) the P/Invoke fails — the handling is disabled.

## Render and camera

- **The preview is taken from the layer's 3D camera.** `NormalLayerCamera` renders the 3D viewport of the avatar into its
  `targetTexture` — without UI and exactly the viewport size, so `screenshot` reads this RT in full
  (no crop needed; size-stable). `MainCamera` is the UI orthographic camera for the whole window; if you take its RT,
  you would have to cut out the center with fixed fractions (`0.153/0.030/0.692/0.940`) — this is only a fallback,
  and such fractions "leak" UI / cut the model at a different window size. `ScreenCapture.*` under Wine
  gives a Y-inversion, the 3D-RT `NormalLayerCamera` does not.
- **Full window frame (with UI):** `ScreenCapture.CaptureScreenshotIntoRenderTexture` is suitable only
  from the main thread (dev command `uiscreenshot`/MCP `ui_screenshot`). Under Wine/DXVK the result is cached
  (it may return not the freshest frame) and requires a flip along Y.
- **Camera** = `VRoid.UI.Component.AvatarCameraPosition`: `ChangeAngles(Vector2)`, `Zoom(float)`,
  `Pan(Vector2)`, `RotateAxis(...)`, `SwitchCameraProjection()`, `ResetPosition()`, `SetCustomView`.
  Per-category angles in the plugin are its own preset-regions (`Editor/Camera.cs`), plus the angle axis
  (`front/back/left/right/threequarter_*`): `ThumbnailCameraConfigResolver.Resolve` returns the
  default outside preview generation.
- **`ChangeAngles` orbits around the current center and keeps the current orbital distance.**
  The vector is `(yaw, pitch)`; the model front = `yaw 180` (not 0). Verified at runtime: after
  `camera frame feet` the call `camera angles 150 -10` keeps the distance and the feet center.
  The rotation coroutine overwrites a **subsequent** `Zoom`/`Pan` (called right after `angles`), so
  the chain `ChangeAngles → Zoom` does not work — wait for the coroutine to finish. For a new
  region/angle use `SetCustomView` via `camera frame <region> [angle]`; the region preset also
  carries a camera **pitch** (0 = level, +90 = straight down) — `top` = 80, the crown-from-above
  view used to judge hair coverage.
  Calibration: `camera view <c> <v>`, diagnostics: `camera probe`.
- **`ResetPosition()` resets angles and pan, but not the distance** (the zoom is encoded into the position). Therefore
  `camera reset` after `ResetPosition()` delivers the initial view via
  `SetCustomView(_initialPosition, _initialRotationCenter)`.
- **Directional views** (back/side) are a rotation of the direction vector in `SetFocus` + `SetCustomView`,
  and **not** `ChangeAngles` (the latter's coroutine overwrites the framing). `enter` by default thus
  auto-frames the category (`view`: face → face, `Hairstyle/Back` → back view, Shoes → feet,
  Tops → upper, Body/Bottoms/Dresses → torso; `keep` — do not touch, or an explicit `<region>[:<angle>]`).
- **Preset previews — from the asset-store, not from the current model.** Some Face overlays (lips/eyelashes/
  cheeks) are drawn on a generic mannequin; on all thumbnails (and on the live frame) there is a
  fixed editor light/self-shadowing — a pink shadow on the neck; this is **not a parameter**, it cannot be removed
  (the Look section is outside the API).
- **`WaitCameraIdle()`** (waits for `_isRunningCameraRotateCoroutine=false` + one frame) is mandatory
  before `GetState()`/screenshot: `ChangeAngles`/`RotateAxis` are coroutines, and `SetCustomView` is visible
  in `targetTexture` only from the next render. Without it `camera …` + `screenshot` in one
  batch captures an already stale frame.
- **`GetAngles()` — the engine's raw angles:** pitch comes in `[0,360)` (e.g. `354.3` instead of `−5.7`),
  `position/rotation` — as is. There is no normalization.
- **Preview antialiasing (AA) — only via RT recreation.** The engine builds the preview `RenderTexture`
  in `VRoid.UI.CameraTargetTextureProvider.CreateRenderTexture` as single-sampled (`antiAliasing=1`), and
  `RenderTexture.antiAliasing` in this build is **get-only**. Therefore `Runtime/RenderQuality.cs`
  patches the factory (postfix): takes `rt.descriptor`, sets `msaaSamples`/an enlarged size and
  returns a **new** `RenderTexture`. The `descriptor` of an already-created RT must not be changed — Unity
  logs `Setting render texture desc of already created render texture is not supported!` and the change
  has no effect. Consequence: `msaa_samples`
  and `render_scale` from `servers.json` are applied **only by restart**; `rendermaasa`/`renderscale` —
  only save the config.
- **SSAA instead of/together with MSAA.** With `render_scale=2` the factory returns an RT at 2× (the compositor
  `CompositionRenderer` stretches it onto the panel → downscale = SSAA). This fixes the thin
  MToon-inverted outline (~1 px) that MSAA4 does not smooth (verified: the outline shimmers with
  MSAA4, is smooth with SSAA2). The hard area limit is 8 Mpix.
- **`screenshot` and MSAA.** `Texture2D.ReadPixels` does not read a multisampled RT directly — the plugin
  first does `Graphics.Blit` of the MSAA-RT into a single-sampled temp and reads it (`Screenshot.ResolveForRead`).
  `NormalLayerCamera.targetTexture` with SSAA has size 2×, so MCP screenshots come out at 2×.

## Categories and parameters

- **UI tabs = direct `Children` of the `Categories` node.** Inside a tab lie `ParameterCategoryViewModel`
  and `PresetCategoryViewModel` with **the same names** — do not look up a category by name.
- **The highlight is given by selecting a direct tab** (`set_SelectedCategory`); selecting a sub-node changes the data,
  but not the highlight. `UpdateIsSelectedCategory` is bulk, not a signal of the current category.
- **Switching a section reloads the categories asynchronously** (~1s) — wait for it to populate.
- Combined parameter: `set_CombinedValue` actually executes `SetBoneTransformParameterCommand`.
- **`Face Sets ⊃ Eyes Sets`**: Eyes Sets has no unique parameters (all are in Face Sets).
  `Face Sets` is an aggregate: its sliders share a `key` with the separate tabs (`Face/Face Sets/Iris Size (X)`
  and `Face/Irises/Iris Size (X)` — **the same** parameter); editing one is visible in the other.
- **`Expression Editor` — just an "entry button"** (`ExpressionCategoryViewModel` without
  children/parameters; the data is in `FaceEditor.ViewModel`). It is not editable via the API: it is excluded from
  `categories` and `FindTab` (for an external client it is as if it does not exist).
- **Section/category names are strict externally** (`Ordinal`): `ResolveSection` and `FindTab` do not
  normalize case; the client must send exact labels from `sections`/`categories`.
- **`has_clear_preset` = the tab has an empty (Clear) slot**, selecting which **removes the element
  entirely** (nothing applied). This is not the same as `needs_preset`: the flag only reports
  the presence of a slot, while `needs_preset:true` is set only when the tab is currently empty (`enter`
  returned an empty list in the presence of a Clear slot). After selecting **any** real preset the set
  of parameters is the same. Hair (Front/Back/…), part of the face overlays
  (Eyelids/Lips/Cheeks/Nose/Mouth) and clothing behave this way (see below). Without a Clear slot (`has_clear_preset:false`)
  the preset is always applied and replaces all parameter values.
- **`min`/`max` — the slider range, not the model domain.** Factory preset values are sometimes
  outside it (clothing has `-10` with `min:0`, `120` with `max:100`). On write the plugin
  clamps to the **union of `[min,max]` and the current value** (read-modify-write does not move an already
  existing out-of-range default, but outright garbage is still cut off), in the response it sets
  `clamped:true`; reading returns values rounded to 3 digits (like the UI).
- **`NaN`/`Infinity` are hazardous.** `float.TryParse` accepts them, and `set_CombinedValue(NaN)` drives
  the engine into an infinite rebuild and **hangs the command queue** (main thread busy, the process
  unresponsive). All numeric/color parsers (`EnterHandler`… `SetParamHandler`, `TryParseColor`,
  camera) reject non-finite values (`TryParseFinite`) with an error, before calling the setter.
- **The stable parameter id is `key`.** Combined parameters give keys `_pathNegative`/`_pathPositive`
  (`BoneTransformPath.get_Key()`), single ones — `_path`/`BoneTransformKey`. In `enter` this is the field `key`
  (for some parameters it is empty). The label of two sliders may coincide, the `key` does not; `set_param`
  accepts `key` on par with `path`/the label.
- **Duplicate labels are disambiguated by `#N` in all tabs** (not only Outfit): if two parameters give one
  `path`, in display order ` #1`, ` #2`, … are appended; `set_param` by the full `path` from
  `enter` is unambiguous, by a short label — the error `ambiguous`.
- **The contract is deliberately tied to the UI language.** `label`, the category name and `path` are taken from
  the engine's `TranslatedDisplayName`/`Name`, therefore they change together with the editor language
  (`Face/Nose/Nose Width` ↔ `Face/鼻/鼻の横幅`), so that the terminology of the user, the client and in the
  prompt match. Only `key` (and the color `id`) is stable between calls/restarts —
  for a "language-independent" address use it.
- **VRoid's English localization is inconsistent: different parameters share the same EN label.**
  Example: `鼻の高さ` (nose height) and `小鼻の横幅` (width of the wings/nostrils) in EN are both `Nose Size`
  (correctly *Nose Height* / *Nostril Width*). In Japanese the labels are unique. This is a defect of the app's
  strings, not of the plugin; it is handled by `#N`/`key`; the strings cannot be rewritten. **Real**
  duplicates also occur: in clothing the same label (e.g. `スカートを広げる`/`Skirt Flare`) appears
  several times (different mesh parts), and in JP they also duplicate — here `#N` is appropriate rather
  than a workaround for translation.
- **Hair: two global knobs** (`Hair|Main` / `Hair|Emission`, in the UI — Main/Highlight
  Color) on the Hairstyle tabs **except `Base Hair`**, but the color panel is visible only when a base
  preset is actually selected (see `has_clear_preset`). Therefore `enter` returns these colors only when a
  preset is selected; on Clear — `needs_preset:true`. The exact signal is `HairEditor.ViewModel.IsRightPanelActive`
  (not `SelectedHairMaterialTemplate`: it remains non-empty on Clear too).
  The value is **shared** for all tabs, so the path is made tab-agnostic:
  `Hairstyle/Main` / `Hairstyle/Emission` (in `enter` such colors have `global:true`); you can address it
  from any non-Base tab — `set_param Hairstyle <tab> "Hairstyle/Main"`.
  The color panel is enabled precisely by the **Level-0 preset `Hairstyle Sets`** (the shared set): the group
  presets (`Overall Hair`/`Front`/`Back`/…) are insufficient without it. While the set is selected, the global colors
  are returned and accepted on any non-Base tab; after clearing **all** tabs the section is empty again
  (`needs_preset`). `HairEditor.ViewModel` can exist in several instances (model
  reloads), so `_colorHairVm` is pinned by hooks to the **last** created/used
  instance, and it is the same one that goes into the panel signal (`IsRightPanelActive`) and into the setter. If you take the signal and the
  setter from different instances — they diverge, and `enter` is inconsistently empty (this was a source of flakiness).
- **`Base Hair` — the exception:** it has its own single color (group `Main Color`,
  path `Hairstyle/Base Hair/Main Color`); the global `Main`/`Emission` do not apply to it and are not
  output. In the entire Hairstyle section there are no numeric parameters: the shape sliders live in a separate
  editor (`VRoid.Studio.HairEditor.DesignEditor...ParameterViewModel`) and are not output externally.

## Clothing (Outfit)

- **The parameters belong to the applied item, not to the tab.** A tab has no scheme of its own:
  `ClothingEditor.ViewModel.SelectedInstance.BlendShapeParameters` are the current item's sliders.
  Different items give different sets; the sizes range from units to hundreds (measurements: Tops 4/5/32/98/128,
  Bottoms 4/98, Dresses 8/58/81/108, Shoes 19/38). Items on the same model give an
  **identical** set (all shoes — 38, two kinds of footwear — 19), i.e. the signature is tied to the item's
  model. Therefore `categories` for Outfit reports `preset-only`, and `enter` is authoritative.
  `enter` returns the field `item` (the readable name of the applied item); an empty slot → `needs_preset:true`.
- **Body/bodysuit sliders are part of the same list.** For some items the panel mixes in figure-fitting
  parameters (`Neck Width`, `Neck Thickness`, `Expand Bodysuit`, `Increase Body Bulk`,
  `Tighten Crotch`…) and combined parameters — hence the large counters (98/108/128).
- **Duplicate labels** within a single item (different mesh parts) are disambiguated by the suffix `#N`
  (`Volume #1..#3`, `Skirt Flare`×2).
- **Clothing slots.** `Tops` and `Bottoms` coexist; `Dresses` (Onepiece) is **mutually exclusive** with them —
  selecting a dress clears the top/bottom, and selecting a top or bottom removes the dress. Each slot remembers its item:
  `enter Tops` after selecting Bottoms still returns the top.

## Presets

- Selection — `PresetOptionViewModel.set_IsSelected(true)` (it itself calls `_presetSelectAction` with
  fallbacks/colorOverwrites/callbacks). A manual `Context.LoadAndReplaceTransferableGroup` is not needed.
- **Empty preset** = `ClearOptionViewModel` (usually `PresetOptions[0]`), but it has **no**
  `set_IsSelected` → call `ClearTransferableGroup()`.
- It appears to apply immediately, but the result is asynchronous (see synchronization). `IsSelected` after
  application is often reset by the engine — not a sign of success.
- "Set" presets and hair change several groups at once; hair — a single shared Main Color.
- There are no human-readable names (`resourceName`/`itemId` + preview).

## Hair design editor (Hairstyle sub-screen)

- **The guide overlay (fitting mesh + handles + width curve) is drawn iff a group is selected.** It
  does not depend on the active tool: both `Hand` (Select) and `ControlPoint` show it while a group
  is selected. `DesignEditor.ViewModel` exposes `CurrentTool` (`Tool` = Hand/Brush/Retouch/ControlPoint)
  and `Mode` (`Select`/`Brush`/`Retouch`/`ControlPoint` mode VM); switching the tool switches the mode.
  Nothing else removes the overlay — hiding a group hides its hair, not the guide.
- **Deselect = `set_SelectedItem(null)`** (what a click on the empty viewport does). It runs
  `Mode.OnSelectedItemChanged(null)` → `UpdateHairGuides(null)` → the guides/overlay are dropped
  (and the engine also calls `Model.ClearSelectedHairs()`). Verified: after it
  `hair_guides == 0` and a screenshot has no overlay.
- **Never Harmony-patch `DesignEditor.ViewModel.set_SelectedItem`.** With an observation postfix on it
  (**even a no-op one**), the engine's own `set_SelectedItem(null)` throws (NRE from the patched
  trampoline) and the UI can no longer deselect / remove the overlay. The plugin therefore does not
  hook it (see `Interop/Hooks.cs`); the mode `OnSelectedItemChanged` hooks are safe and still report
  selection changes. This was the root cause of the "clean render is impossible" symptom.
- **`SelectedIndex` by reference is unreliable after a rebuild**: `set_SelectedCategory` (and other
  group-list rebuilds) replaces the `HairGroupNodeViewModel` objects, so a comparison against the
  still-selected engine node yields "nothing selected" while the engine (and its overlay) still holds
  one. Decide "selected" from `get_SelectedItem != null`, not from a reference scan.
- **A pristine entry is unselected.** `MoveToHairDesignEditor` arrives with no selection, i.e. a clean
  render is the default; it is the *first* mutation (`hairgroup add`/`set <index>` selects a group)
  that turns the overlay on.
- **A group's name is its durable identity.** The name is stored in the preset and is what the left
  panel shows, so it survives a rebuild (unlike the list index, which shifts on add/delete/category
  switch) and is what the plugin addresses groups by (`hairgroup <cmd> <index|name>`); an unknown
  name is an error (never a silent fallback to the selected group) and a name matching several groups
  is an "ambiguous name" error (never a first-match). No synthetic session id is needed.
- **Group visibility** is `ActionHandler.ToggleHairGroupVisibility(IHairGroupPath)` (a *toggle* —
  compare `IsVisible` first to force a state); "solo" = hide every group but one.
- **Procedural groups have no engine flip.** `ActionHandler.CloneProceduralHairGroup(ProceduralHairGroupPath)`
  duplicates a group (the copy keeps the parameters and is **not** selected; it keeps the same name);
  `FlipBrushHairGroup` exists for freehand only. Symmetry must be built as clone + negated
  `Position`/`Center (X)`.
- **Freehand (brush) strands: the (u,v) on the guide is the invariant, not the 3D point.**
  `BrushHair.ControlPoints` are `ControlPoint{ Point, Normal, Uv }` in the guide's **local** frame.
  Changing a guide parameter (`Guide Parameters/Height`, `Fit to Head`, …) **re-derives `Point`/
  `Normal` from `Uv`** (measured: `Height 0.4→0.8` moved the points *and* the normals, left `Uv`
  byte-identical), so a guide edit silently rewrites every strand's geometry — re-read after any guide
  change. `v` is the length-normalized position along the guide axis (0 root, 1 tip; scales with
  `Height`); `u` is the azimuth around the axis **in turns** (`u ≈ frac(1 − atan2(dx, dz)/360°)` in the
  local `XZ` frame, `u=0` at local `+Z`; mirroring flips it; the engine normalizes it with
  `BrushHair.NormalizeAngle`). The strand's parameter set is the same families as a procedural group
  but every field is a **nullable override** (null = inherit from the group). Full model:
  [`hair-freehand.md`](hair-freehand.md).
- **Freehand mirroring is recorded only as a paired grouplet.** A strand drawn with `Mirror` on is a
  `PairedGrouplet` (`Hair0`/`Hair1`); the UI never shows the pairing. Editing one side with `Mirror`
  off **breaks the pair permanently** (`BreakIntoUnpairedGrouplets`) - it does not re-pair when Mirror
  is turned back on; only flip/clone recreates a mirror.
- **A freehand group's category is localization, not group type.** Brush groups live in the same
  Front/Back/Side/Overall/Extra categories as procedural ones and the category does not restrict the
  kind. `Base Hair` is listed by `hairgroup categories` but is **not** a strand-design entity (no
  groups; the editor's precondition is `IsCustomizableNonBaseHair`) — base hair is a fixed preset worn
  over the head, only re-textured.
- **`save` while a design session is open does not include the session's uncommitted edits.** The
  `.vroid` is written (the file exists and is valid), but the in-session changes (a moved control
  point, a `Guide Parameters` edit, …) are not in it: re-opening reads the previously committed value.
  Leaving the editor with Save (Overwrite/New) applies the design to the model, and only then does
  `save` serialize it; `close_design(discard)` likewise drops the pending edits. (A design that *was*
  committed is saved and reads back fine.)
- **Entering/leaving the design editor repeatedly leaks memory.** Measured ~121 MB per entry before the
  fix; a batch that applied ~129 hairstyles OOM-killed the app (~14 GB RSS). Root cause (fixed): the
  plugin's `ColorRegistry` rooted *every* colour-setter instance, while consumers only read
  `SubColorSelection` and `HairEditor.ViewModel`; registration is now restricted to those two types and
  the registry is cleared when the edit root is left → growth drops to ~48 MB/entry (~30 with a periodic
  GC). The **residual ~30 MB/entry is editor/native-side** and not reclaimable by GC, so long sweeps run
  chunked (restart between chunks; the harness calls `memgc` every `--gc-every` exports). Diagnose with
  the `memstat` / `memgc` / `memclear` dev commands. Details: [`hair-export.md`](hair-export.md).
- **A group's `Children` (`BrushHairGrouplet`) is a compiler-generated iterator and cannot be walked
  by `Interop.Enumerate`** — read the backing `List<BrushHairGrouplet> _hairs`, or use
  `GetBrushHairGroupChildrenQuery(groupPath).Execute(model)` → `BrushHairStrandPath[]`. Also,
  `Model.GetBrushHairGroup`/`GetBrushHairStrand` need the **concrete** `BrushHairGroupPath`/
  `BrushHairStrandPath`, not the `IHairGroupPath`/`IHairStrandPath` proxy (which silently yields
  null); the `GetHairGuide*` methods are the opposite (they take the interface).
- **A "Saving temporary items" modal is raised by the section switch, not only by editor entry, and
  refers to the category's *unsaved temporary design*.**
  `ConfirmCustomItemSavingDialogUtil` exposes two awaited prompts, both "Save / Back / Start editing
  without saving" (mapped to `CustomItemSavingResult` = SaveAndEdit / DiscardAndEdit / Cancel):
  `CheckStashedButNotWornExistsAndConfirm` (fires when
  `MultiCustomItemSavingModel.CheckStashedButNotWornExists()`) and the
  `CheckStashedAndSpecifiedWornTemporaryAsNewItems(globalBus, multi, wornTg, ...)` variant (fires when
  `CheckStashedOrSpecifiedWornTemporaryExists(wornTg)` - the category being entered still carries a
  **worn temporary** item, i.e. a design applied to the model but not saved as a reusable custom item).
  The reproduced prompt was the second kind: a `close_design(discard)` on Front left the sculpted Front
  design as a worn temporary, so the next `create_custom_preset` (whose `CommandUtil.EnterSection`
  switches into Hairstyle) asked about `TransferableGroupType.N00.Level1.HairFront`. The prompt is
  reached both by `MoveToHairDesignEditor` (`open_design`; unanswered it never returns, no session
  appears, and every later call looks like "no design editor open") **and by the section switch itself**
  (`Sections.SwitchSectionForCat`, performed by `CommandUtil.EnterSection` at the start of
  `create_custom_preset`, `save_custom_preset`, `enter`, ...). Like the exit notice it is **awaited**, so
  it BLOCKS its caller: on the section-switch path the `create_custom_preset` 20 s "did the item
  appear?" wait expired, the command failed with *"the new item did not appear (creation failed)"* and
  the modal stayed on the whole window. The plugin answers it "start editing without saving"
  (true = proceed; no custom item is written behind the caller's back) whenever a plugin command is
  driving the engine (`CommandDispatcher.TryGetInFlight`) or for the edit-entry transition
  (`HairDesign._enterDialogAnswer`), so a human-driven Edit/Export/Rename keeps the stock dialog.
  `dialog_trace.log` (written when `dialog_trace` is on) records every dialog the plugin did *not*
  intercept, with the in-flight command that reached it; a `STASH` line names the worn/stashed category
  and the predicate (`wornTemporaryExists` / `stashedButNotWornExists`) - the probe used to pin this one.
- **The Base Hair colour's *panel* is not exposed while the design editor is open**, but the colour
  itself is: the same `enter Hairstyle "Base Hair"` that reports `Main Color` outside a session returns
  `needs_preset` with a session open, yet the engine-side `IColorAccessor` (`Get/Set(Model)`) in the
  eager `CategoryColorSelectionCollection` on the selector still reads and writes it. `HairBase`
  (below) uses that handle, so the chromakey coverage check runs inside a design session too
  (this is what the coverage check uses).
- **`hair_coverage` must not judge "no base hair" from a colour value.** After a Clear a colour VM
  (black) can linger in the `Colors` registry, so "a colour exists" would make the check pass
  vacuously against an invisible base hair. The accessor's `GetIsAvailable(Model)` (and the
  preset node's `IsSelected`) is the reliable signal; with nothing worn, a stock base-hair preset is
  applied temporarily and the previous selection is put back afterwards.
- **A successful import raises a modal `ImportCustomItemDialogViewModel`, and it is not awaited.** After
  `ImportCustomItemByTransferableType` the engine shows "Imported successfully" with **Wear now** (invokes
  the import confirmation callback and applies the item) and **Wear later** (just dismisses). The import
  itself has already happened; the window only chooses whether to wear. It is a normal dialog (it does
  not block the import task), but it blocks the editor and any following UI work, so a programmatic
  import must answer it: the plugin prefixes `DialogProviderViewModel.Show` and consumes it before it is
  displayed (`import_custom_preset` arms the answer; `wear` = Wear now, otherwise Wear later).
- **A renamed custom item's row can briefly revert to the old name.** `RenameNotSelectedCustomItem`
  rewrites the file, the registry `Save` notifies the Custom tab, and the file-system observer then
  re-reads the file and notifies again; between those two notifications the row may show the previous
  `DisplayName` (observed after an import + rename). The registry file's `CustomItemMetaData` is the
  stable engine truth, so the plugin reads the name/type back through
  `LoadCustomItemContentFromPath` instead of trusting the row VM. Completion waits use the
  `CustomItemSignals.Save`/`NotifyUpdateFiles` postfixes, not sleeps.
- **Export is not restricted to the worn item.** The UI's right-click action is "Save and export", and
  its save half (`SaveCustomItem`) only makes sense for the item currently on the model, which is where
  the "worn only" appearance comes from. `CustomItemRegistry.ExportCustomItem(model, ...)` takes any
  `FileCustomItemModel` and re-writes that item's stored contents to the requested path, so a stored,
  unworn custom item exports correctly (verified for Hairstyle Back and Outfit Bottoms).
- **Custom-preset name is reset by an Overwrite.** The reusable `*.vroidcustomitem` takes its name from
  the in-place model item (the category name, e.g. "Front"), so `OverwriteCustomItem`/`SaveAsNewCustomItem`
  after a design session writes the wrong name; re-apply the rename *after* the write. `save_custom_preset`
  / `haircustom save` therefore always re-apply a name — the requested one, else the preset's current
  display name read before the write (`Presets.CustomItemName`). Without it an overwrite would silently
  rename the user's preset to the category name. (An earlier version kept a per-CustomItemId map of the
  name given at creation and preferred it; the item's own name is the single source of truth and avoids
  resurrecting a name the user has since changed.)
- **The engine's "newly saved" notice BLOCKS the editor close.** After the save prompt,
  `NewlySavedCustomItemDialogUtil.NotifyNewlySaved` **awaits** a modal info dialog
  (`"{0} will be saved as a new item."`). It is not a cosmetic popup: the `Exit` continuation — and so
  a programmatic close — waits for a human to dismiss it, and the close reports `closed:false`. The
  plugin answers it with `Task.CompletedTask` for a save it drove itself (see
  `HairDesign.OnNotifyNewlySaved`) and reports the content as `close_design.save_notice`; a user-driven
  save keeps the stock notice. Never leave this unpatched when closing programmatically.
- **Closing must save only the edited category.** With "all" categories the engine calls
  `SaveStashedAndSpecifiedWornTemporaryAsNewItems` and creates a **new** custom item for every category
  it considers to have temporary updates (observed: Back/Extensions/Side/Ahoge junk presets, announced
  by the notice above). `close_design`/`hairclose` therefore default to the current category's
  `TransferableGroupType`; pass an explicit list to widen it.
- **The design editor's category selector is the Hairstyle section's selector**
  (`HairEditor.PresetsSelectorViewModel`). Switching the Hairstyle category while a session is open
  drags the session onto that category (and can leave it with no editable groups). Hence the coverage
  check — which needs the Base Hair tab — requires the session to be closed, and `save_custom_preset`
  refuses while a session is open. A session also survives its close as pinned references, so "is a
  session open" must be read from the screen's `IsActive`, not from the presence of the sub-editor VM.
- **`IsEditable` is false for a merely hidden group** (it follows `ToggleHairGroupVisibility`), so a
  hidden procedural group is still editable through the API even though the engine flag says otherwise.
- **A curve's `SelectedCurveTemplateIndex` is stale after a custom `set_Curve`** (it is not cleared),
  so it cannot be used for readback: match the current control points against each template's own curve
  and report a template only on an exact match.
- **`Procedural Parameters` semantics** (needed to design anything): `Position`/`Interval` are degrees
  around the head seen from above — **`Position` 0 = front, 90 = the model's left, 180 = back,
  270 = the model's right**, increasing towards the model's left. `Position` is the fan's *start*
  edge and `Interval` sweeps from it towards the left, so the fan covers
  **`[Position, Position + Interval]`** (a centred fringe of width *W* needs `Position = 360 - W/2`).
  The frame is **global** (the same in every category). `Length` is a fraction of the guide measured
  from the roots near the crown; it grows the strand forward/down, so a front group reaches the
  forehead hairline at ≈0.3, the eye at ≈0.6 and below the chin at 1.0. `Center (X)`/`Center (Z)`
  *translate* the whole fan laterally (+X = model's left) / front-to-back (+Z = forward); both are
  ~head-sized, so ±1 is already off the head. One group is one fan of at most 20 hairs, so a full
  element is 2-4 groups; a single wide fan is sparse and lets the underlying hair show through.
  Full measured reference: [`hairstyle-design-manual.md`](hairstyle-design-manual.md). NB
  `Arch of Hair Length = 0.5` exactly degenerates the geometry (bounds blow up, viewport empties).
- **Coverage check via the Base Hair chromakey.** `Base Hair` is a *separate* section
  (`Hairstyle/Base Hair/Main Color`, its own single colour, no hairstyle editing) that a non-base
  hairstyle is supposed to cover - and it can be painted any colour. So painting it a key colour
  (e.g. `#00FF00`) and counting key pixels from several angles is a direct "is the hairstyle
  finished?" test. `haircoverage [threshold] [hex]` does it: set the key colour on `Base Hair`, shoot
  front / three-quarter L+R / left / right / back / top, count key pixels, restore the colour.
  Calibration: a full stock hairstyle ≈0.09% of the frame at its worst view, one custom Front over
  the bare base hair ≈6.5% - so a threshold of ~0.3-0.5% cleanly separates finished from unfinished.
- **`open_design` must SELECT the category**, not merely find it: the engine's
  `IsCustomizableNonBaseHair` precondition follows the *current* category, so a stale current tab
  makes the guard misfire ("not customizable" for a perfectly valid custom preset).
- **The template's empty freehand guide** (0 strands) is removed automatically on `open_design` entry
  (`HairGroups.RemoveEmptyFreehand`; `hairgroup cleanup` for a manual run): it is never procedurally
  editable and only inflates the group list/counts.
- **Per-group material (own colour).** A group's `Materials` parameter is a
  `MaterialParameterViewModel` (`MaterialNodes`, `SelectedMaterial`); the engine's
  `ActionHandler.DuplicateMaterial(SharedHairMaterialPath)` copies the shared material, and
  `set_SelectedMaterial(node)` assigns the copy to the group, so one group can have its own colour.
  The material's **own** colours are `MaterialColorNodeViewModel` nodes found by walking the material
  node subtree (the base colour sits under a `Shader Color` subgroup; `Main Color`/`Highlight Color`
  are `BaseColorNodeViewModel`s = the shared global ones), set with `set_MaterialColor(Color)`.
  `hairgroup material [index] <hex>` does duplicate + assign + tint; verified that only that group's
  strands change colour (`hairgroup material [index]` lists the materials).
- **`CloneProceduralHairGroup` keeps the source's name and does not select the copy**, so a clone is
  indistinguishable in the list; clone with an explicit new name (`hairgroup clone <index> <name>`).
- **Categories are per part** (Front/Back/Side/Overall Hair/…); a group belongs to one category, so a
  single "whole hairstyle" is edited in `Overall Hair` (or comes from a `Hairstyle Sets` preset),
  while Front/Back/Side hold combinable parts.

## Color

- The module is `VRoid.Studio.ColorSelection.*` (not TextureEditor). Category colors:
  `CategoryColorSelection` → `ColorSelection` → `SubColorSelection` (ids of the form
  `<TransferableGroupType>|<group>|<name>`), setter `SubColorSelection.set_Color`.
- **Iris** = 3 sub-colors `Both Eyes` / `R Eye` / `L Eye` (Both applies to both; the Both value
  matches Right). **Hair** = the global `HairMaterialTemplateColor` (main) and
  `...EmissionColor` (highlight) on `VRoid.Studio.HairEditor.ViewModel`.
- **The base skin color is one for several groups.** The accessor `SubColorSelection` (`_model.get_Accessor()`
  → `EditableElementColorAccessor._keysToSet`) writes at once to `Level1.FaceSkin`/`Level1.BodySkin`/
  `Level1.NoseLighting`/`Level2.Eyelid`, therefore `Color`/`Dark Color`/`Light Color` on the tabs
  Skin/Nose/Eyelids/Whole Body are **the same value** (setting in one changes all). The plugin
  detects this by the accessor signature (several groups per one key set) and returns one
  canonical color with the tab-agnostic path `Skin/Color`, `Skin/Dark Color`, `Skin/Light Color`
  and `global:true`; per-group aliases are not duplicated. It is addressed from any of these tabs.
  (The fact that `Face/Skin` has no VMs of its own in the dump is a consequence: the skin color sits on NoseLighting/Eyelid.)
- **A symmetric color with a side mismatch** is returned in `enter` with `value:null` (for example,
  `Both Eyes`, when L and R differ): a single value cannot be read, but setting `Both` is still
  possible — it will apply to both.
- **Base hair** color is available when the base hairstyle is active and the category `Base Hair` is selected
  (otherwise it is overridden by the hair template). It is output as the single `Main Color` (`SubColorSelection`);
  the global Main/Emission do not apply to Base Hair.
- **Do not patch hot getters** (`get_HairMaterialTemplateColor` and the like) — loading hangs
  (reflection+log in a loop). Only setters, and instances — via the ctor.

## i18n / localization

- **Its own message system, not Unity Localization.** The UI text is a strongly typed tree
  `VRoid.UI.Messages.*` (generated classes; a leaf is a `string` property, e.g.
  `VRoid.UI.Messages.Main.ModelEditor.Category.ParameterModel.name.NoseWingScaleX`). The values lie
  in `VRoid.UI.Messages.Messages.s_localeStringDictionary` (`crowdinLanguageCode → key → string`),
  fallback — `"en"`; the runtime facade — `VRoidStudio.UICulture.Translator.Translate`,
  `VRoid.UI.Messages.CurrentDictionary` (public, but a **small** subset), `Locale.GetText`.
  A message key = the path through the properties (`Main.ModelEditor.Category.ParameterModel.name.NoseSize`).
- **Languages and translation via Crowdin.** `VRoid.UI.EditorOption.LanguageMode` = En/Ja/Ko/ZhCn/ZhTw;
  `LanguageModeMap.LanguageEntry.CrowdinLanguageCode` (+ `SystemLanguage`). That is, the strings are translated in
  Crowdin, `ja` is the original language, `en` is one of the translations.
- **The full table is extracted at runtime:** switch `Messages.CurrentCrowdinLanguageCode` and
  walk the `Messages.Current` tree by reflection (the leaves are strings). The TCP dev command `i18ndump [lang]`
  writes `messages_<lang>.json` (canonical keys ~4.4k: ja=4395, en=4387; the backing fields
  of the properties `*_k__BackingField` are filtered out). Ready dumps: `<app root>/messages_{ja,en}.json`.
  Models/assets may carry their own `LocalizedString` protobuf (map locale→text + fallback) —
  these are the names of user/resource elements, separate from the UI table.
- **String override directly in the UI works.** `Editor/MessagesI18n.cs` puts a Harmony postfix on
  `VRoid.UI.MessagesMap.CurrentDictionary` (which is also `Translator.Translate`/`Mapping.get_Item`) —
  a **single** key→string point. The overlay `i18n/<crowdinLang>.json` (a flat key→text map) overrides
  the text for the current `CurrentCrowdinLanguageCode`, otherwise the engine string is taken; it affects **the whole**
  interface, not only the parameters. The directory `<GameRoot>/i18n` — a symlink to `data/i18n` (like
  `labels`); the TCP dev commands `i18nreload` (re-read) and `i18nlang [code]` (read/change
  `CurrentCrowdinLanguageCode`).
- **The EN translation is defective:** 188 EN strings correspond to >1 different JA strings (in parameter names
  — 7 collisions: `Nose Size` ← `鼻の高さ`/`小鼻の横幅`, `Nose Width` ← `鼻の幅`/`鼻の横幅`,
  `Height`, `Tail Base Thickness`, `Tail Tip Length`, `Tuft Size/Width`). The plugin mirrors the UI,
  so it is fixed only by its own overlay by keys (see `docs/external-api.md`), not by editing the app.
  The directory `data/i18n/en.json` — the corrected EN for collisions in the editor labels
  (parameters/categories/parts/blend-shapes; 280 keys), with uniqueness within a section.

## Il2CppInterop / Harmony

- `GetType()` on a proxy gives the base/interface type; the real one — via the native class
  (`IL2CPP.il2cpp_object_get_class`) + `TryCast`.
- **`Nullable<T>` model fields are misread and miswritten by the generated proxies.** A strand
  `WidthScale` of ≈0.03 reads as `0` (and `HasValue` can read false when the native flag is 1); the
  setter does not reach the native field. `il2cpp_field_get_value_object` throws for these value-type
  fields, and the `IL2CPP.il2cpp_field_get_value` wrapper takes a `void*`. `Runtime/Il2CppNullable.cs`
  therefore resolves `il2cpp_field_get_value`/`il2cpp_field_set_value` from `GameAssembly.dll` with
  `NativeLibrary.TryGetExport` and calls them through an `IntPtr` delegate with a `Marshal` buffer
  (layout: `bool hasValue` @0, `T value` @4; only 4-byte numeric/enum types are supported), so **no
  `unsafe` is needed**. Use `Il2CppNullable.TryRead` (`NotNullable` / `Ok` / `Error`) and `TrySet` for
  any nullable model field; the plain managed getter is not trustworthy, and a failed read must not
  silently fall back to it. (`null` vs explicit `0` is a real distinction: most strand overrides are
  null, but an explicit 0 is stored with `hasValue = 1`.)
- **A freehand strand's `PositionOffset` does not keep an unset state through a rebuild.** The UI uses a
  dedicated `ModifyBrushHairStrandPositionOffsetParameterCommand`; after the model rebuild an unset
  value is resolved to an explicit `0`. The corpus can therefore contain `null` or `0` for
  `PositionOffsetX/Y`; the writer treats `null` as inherit, and the round-trip comparator treats the two
  as the same state for this field only.
- Iterate collections via `Count`/`Item`/`ToArray` (not `IEnumerator.Current` → AV).
- **The `.ctor` of proxies/base classes is not patched** ("Derived classes must provide an
  implementation"); patch the own ctors of concrete types or methods. The expected Il2CppInterop warning
  (`Failed to init IL2CPP patch backend … using normal patch handlers`) is selectively
  silenced by `InteropLogFilter` — this is not an error, Harmony keeps working with managed handlers.
- **Do not use `HarmonyLib.AccessTools.TypeByName`.** On failure it scans the types of
  *all* assemblies twice and for each broken IL2CPP assembly prints `ReflectionTypeLoadException` with a list
  of types — this is tens of MB of log per launch and a noticeable delay (at runtime — on every UI translation).
  Resolve types via `Interop.FindType` (cache + `Assembly.GetType`), and the full quiet walk — via
  `Interop.AllTypesQuiet`.
- Even the concrete `SettingDialogTransform..ctor` and `AccountIcon.ViewModel..ctor` could not be
  patched (the same warning) — their instances are obtained by patching methods (`InitializeComponent`,
  `SetupAccountMenuItemDataSources`), not the constructor.
- A reliable per-frame tick — your own `MonoBehaviour` (`VroidTicker`) with a **public** `Update`.

## Exit

- **The unsaved dialog** blocks `Application.Quit`/`BackToModelSelect` when there are changes. The dirty flag —
  `VRoid.Studio.MainActionHandler.IsUnSaved` (obtained from `EditRootScreen.ViewModel._mainActionHandler`).
  Reset it → exit without the dialog (`QuitClean`).
- **`save` without a path for a model without a file** (created via `create`) opens the native Save-As
  and at the same time "successfully" completes. The plugin forbids this: the current file path is empty → error,
  an explicit path is needed (save-as). The path is read from `EditRoot.ViewModel._mainViewModel._mainModel.currentFile.path`.
- **`save <path>` (save-as) does not always update `currentFile.path`.** After a successful `SaveSync`
  the plugin itself writes `currentFile.path` to the engine and keeps the fallback `_lastSavePath` (open/menu),
  so the next `save` without a path already works (`saved`) instead of failing with "no file".
- **`open` — a separate editor session**, available only from the model selection screen (like `create`);
  with a model open it returns an error until `main_menu`.
- **`state.selectors`** is zeroed outside edit, and the `Selectors` registry is cleared on `BackToModelSelect`
  (otherwise "sticky" 4 remain and break the readiness of `open`/`create`).
- **Accessories** (no presets selector) and **Look** — outside scope; **TextureEditor** (2D),
  the hairstyle editor, Custom presets — also outside scope.
