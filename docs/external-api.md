# External API

This document defines how VRoid Studio is exposed to clients. It is the client-facing contract: the
model of the editor (sections → categories → presets/parameters), the semantics of each operation,
and the transport details a client must respect.

## Transports

Editor control is offered over MCP (Streamable HTTP), with a raw line protocol as a
debugging/scripting companion. Both dispatch to the same `CommandDispatcher` on the Unity main
thread; command and tool names are identical.

| Transport | Address | Purpose |
|---|---|---|
| MCP (Streamable HTTP) | `http://<host>:17891/mcp` | primary client interface (`initialize` / `tools/list` / `tools/call`) |
| Raw line protocol | `tcp://<host>:17890` | debugging/scripting; text line or JSON-RPC 2.0 |

Configuration is stored in `<BepInEx>/config/com.kokokoshka.vroidstudiomcp.servers.json`; the address and
the `dev_tools` flag are also editable from the in-app Settings dialog, section **Programmatic
Control (MCP)**.

## Design principles

- The interface mirrors the editor's UI structure rather than exposing a flat list of every editable
  parameter: a client navigates section → category → (presets / parameters).
- Only stable `id`/`label`/`value` leave the plugin; Il2Cpp handles, RVAs, marshaling and
  event-based synchronization stay internal.
- Every mutating call is **blocking for the client**: the request enters the main-thread queue, the
  client waits, and the operation resolves on an engine event (`UpdateSelectedPresets` /
  `IsUpdatingModel true→false`), with a timeout as fallback. Screenshots are captured only after
  resolution (capturing during a rebuild throttles it).
- `initialize.instructions` returns the workflow, semantics and formats, and section labels are
  listed in the tool descriptions, so `sections` is not required as the first call.

## Threat model

The service targets a **local, honest but careless client** on loopback; it does not defend against
attackers.

- For any invalid, missing or wrong-typed argument the client receives a clear error; the server does
  not return HTTP 500 and does not leave the session ambiguous — `ping` (MCP, bypassing the queue)
  and `state` must keep responding. This is covered by the `misuse` test suite.
- Deliberately **outside the threat model**: huge requests (batches of tens of thousands of
  operations, megabyte strings/labels). This is a DoS, not carelessness; the effect is a temporary
  main-thread stall, cured by a restart.
- Guards against honest mistakes: `open` rejects non-files and non-regular files (directories,
  special files); the camera rejects non-finite and absurd magnitudes (`|v| > 1e6`); numbers, colors,
  indexes and preview ranges are range-checked.
- MCP enforces a loopback check on `RemoteEndPoint`; the TCP front does not. Non-loopback addresses
  are therefore not supported (file commands operate on paths of the machine running the app).

## Session state

`state` → `{ready, updating, selectors, in_edit, section}`.

- `in_edit` — a model is being edited (true after `open` or activation of the edit root; false on the
  model-select screen and before the first open). Tracked by the `EditRootScreen.OnScreenActivate` /
  `BackToModelSelect` hooks with a plugin-owned `_inEdit` flag, because `IsEditModelActive` is not
  reset when returning to the menu.
- `ready = modelReady && in_edit`; `updating` — a rebuild is in progress (`IsUpdatingModel
  true→false`); `section` — the current section, or `null` outside the editor.
- `open` returns once `in_edit` and `ready` are true (model built, camera reset).

## Sections

- Exposed sections: `Face`, `Hairstyle`, `Body`, `Outfit` (UI labels; the enum mapping stays internal,
  `*Editor` is neither returned nor accepted).
- **Case-sensitive** (`Ordinal`): section and category must match the labels returned by
  `sections`/`categories` (`face`, `irises` → `unknown section`, `no tab`). The errors are
  actionable: `unknown section` lists the valid labels, `no tab` lists the tabs of the section.
  Parameter `label`/`key`/`path` matching is case-insensitive.
- `Accessories` is not exposed yet (no preset selector; not critical).
- `Look` (light/shadow) is not exposed (subjective, few parameters).
- `sections` → `[{id, title}]`; available offline.

## Categories

`categories(section)` → `{section, categories:[…]}`. The call switches the editor to `section` and
returns it; each entry carries a kind and counters:

- `kind` — `parametric` if the category has its own adjustable parameters (numeric/colors), otherwise
  `preset-only`. Rules: Face/Body — by the presence of parameters; Hair — all tabs are `preset-only`
  except `Base Hair` (which has its own color); Outfit — `preset-only` at the tab level, because the
  sliders belong to the applied item (`enter` after selecting an item reports `parametric`).
- `params` / `colors` — the number of numeric / color parameters.
- `presets` — the number of presets; `has_clear_preset` — whether the tab has an empty (Clear) slot
  that removes the item entirely.

`has_clear_preset` semantics:

- `true` — the tab has a Clear slot selecting "nothing". While the tab is empty, `enter` returns an
  empty list and `needs_preset:true`; after any real preset is selected the parameter set is the
  same. This flag is not equivalent to `needs_preset` (a tab with a Clear slot may already be filled).
- `false` — no Clear slot; a preset is always applied and **replaces** all parameter values.

Kind and counters are read from the eager VM collection and are correct immediately, without warming
up by entering. Outfit is the exception: its sliders belong to the applied item, so its tabs always
report `preset-only`/`0` and the authoritative source is `enter` after selecting an item.

**`Expression Editor` is masked.** The node exists in the engine tree but is not exposed: it is not
listed by `categories`, and `enter`/`presets` respond `no tab`. Its data lives in
`FaceEditor.ViewModel`, not in the category tree (see `internal-api.md` §7).

**Preset names.** VRoid provides no human-readable names. `presets` returns a single `name` field per
preset: a human-readable label when known, otherwise the internal identifier; the empty/Clear preset
is named `"(clear)"`. The label source and maintenance are internal (see `internal-api.md`).

### Layers and overlap

- **Face: one key — one parameter.** `enter` returns a stable `key` (transferable key) for numeric
  parameters. The same `key` in different tabs is the same parameter: `Face Sets` aggregates
  eye-shape/contour/ears and shares keys with `Eyeliner`/`Eyebrows`/`Eyelids`/`Skin`. Selecting a
  preset in any such tab overwrites all keys it owns, including those visible in other tabs. There is
  no per-tab isolation; use the specific tab when editing a specific element, and expect a preset to
  overwrite shared keys.
- **Whole Sets (Outfit)** is `preset-only` with no own parameters: selecting a set applies items into
  other slots (`Tops`/`Bottoms`/`Dresses`/…). Fit is adjusted with sliders in those slot tabs
  afterwards.
- Clothing: `Tops` and `Bottoms` coexist; `Dresses` (Onepiece) is mutually exclusive with them —
  selecting a dress clears the top/bottom, and selecting a top or bottom removes the dress. Each slot
  remembers its item; no hidden/overlapped element is left behind.
- Parameters/colors of an overlapped or invisible element still apply, but are not visible until the
  element is uncovered (the upper layer removed). Likewise for an iris hidden by hair: the effect
  cannot be judged from the frame while the eye is not visible. This is correct engine behavior, not a
  plugin bug.

## Category and parameters

`enter(section, category)` → `{kind, parameters[], globals?, item?, needs_preset?}`.

- `parameters` is a **single list** of the category's own parameters, numeric and color alike. Each
  entry is `{index, path, kind: "float"|"combined"|"color", label, value, min?, max?, key?, global?}`.
- `path` is stable across repeated `enter` calls (`Section/Category/Name`; for global colors
  `Section/Name`); `index` is short-lived and valid only until the next `enter` or model change.
- `label` and the category/path strings are **language-bound** (they mirror the editor UI, so the
  agent's terminology matches the user's); `key` (and the color `id`) are language-independent.
  Prefer `key` for language-independent addressing.
- `key` is the stable system id (transferable key) when available. Numeric parameters are
  `SingleParameterViewModel` / combined variants (`Value` / `CombinedValue`).
- Color is a parameter too (`kind="color"`, value `#RRGGBB`).
- When two parameters collide on a label within a tab, `path` is disambiguated with ` #1`, ` #2`, …
  (the full `path` is unambiguous).
- `globals` holds the section's **shared** colors (hair Main/Emission, skin base). These are single
  values addressed from several tabs via a section-level path (`Skin/Color`, `Hairstyle/Main`) and
  are therefore not duplicated into `parameters`. Their entries still carry `global:true`.

Setting: `set_param(section, category, <path|label|key|index>, value)`, dispatched by `kind`
(number or `#RRGGBB` / `r,g,b`). `path` is preferred; `label`/`name`, `key` and `index` are also
accepted. `r,g,b` must be in 0..1; `NaN`/`Infinity` are rejected (they send the engine into an
infinite rebuild). Numeric read-back is rounded to 3 digits as in the UI; writes are clamped to the
union of `[min,max]` and the current value; the color echo is normalized to `#RRGGBB`.

### Colors as parameters

- **Eyes (Iris)** — three separate color parameters (`Both`, `R Eye`, `L Eye`). `Both` changes both;
  `R`/`L` change one side (enables heterochromia). Verified by `eyetest`.
- **Hair** — two global colors, `Main` and `Highlight` (Emission), on Hairstyle tabs except
  `Base Hair`. The value is shared, so the path is tab-agnostic: `Hairstyle/Main`,
  `Hairstyle/Emission` (returned in `globals`), settable from any non-Base tab. The color panel
  requires the Level-0 preset `Hairstyle Sets`; while it is active the globals are available on any
  non-Base tab. The section has no numeric parameters. `Base Hair` has its own single color,
  `Hairstyle/Base Hair/Main Color`; the globals do not apply to it.
- **Skin** — the base color (Color/Dark/Light) is one value shared across `FaceSkin`/`BodySkin`/
  `NoseLighting`/`Eyelid`; it is returned once in `globals` as `Skin/Color`, `Skin/Dark Color`,
  `Skin/Light Color`, addressable from any of those tabs, with no per-group duplicates.

**Custom presets.** The user's saved items (the editor's Custom sub-tab) are part of the same preset
list, not a separate entity: `presets` returns one continuous list where custom items come **first**
(kind `custom`), then the stock presets (kind `preset`) and the Clear slot (kind `clear`). `id`
(equal to `index`) is the continuous value used everywhere, so both kinds can be listed, previewed
and selected through the same commands. Custom presets are global (saved on disk) and appear in every
model.

## Actions

- `presets(section, category, [source])` → `[{id, index, name, kind, selected}]` (`name`: label, else
  resource id; Clear: `"(clear)"`, kind `clear`; custom items kind `custom`). `id == index` is the
  continuous position. `source` (optional, default `all`) is a stateless filter: `all` (custom +
  stock), `presets` (stock only), `custom` (custom only). `selected` (`true`/`false`, `null` on the
  Clear slot) is the read-only applied-preset marker - the engine's `PresetOptionViewModel.IsSelected`.
  The label machinery is internal.
- `select_preset(section, category, id, [params])` — by the `id` from `presets` (custom and stock share
  one id space). The Clear preset has kind `clear` (`ClearTransferableGroup()`). Returns the applied
  preset's `name` (label, else resource id) or `"cleared"`; `resourceName` is never exposed. With
  `params=true` it returns an object `{name, category, kind, has_clear_preset, parameters, item?}`
  (the same payload as `enter`), which lets an Outfit client obtain the new item's sliders without a
  second `enter`.
- `set_param(...)` — the single setter for numeric/combined and color values; the global `path` from
  `enter` is preferred, `index` is short-lived.
- `create_custom_preset(section, category, name)` — create a named custom preset (a Custom sub-tab
  item) in one call; `name` is applied through the dialog-free registry rename, so it gets a
  meaningful identifier immediately. Returns `{section, category, id, name, kind:"custom", renamed}`
  where `id` is the preset's continuous id in `presets` (custom items come first). A fresh item is an
  empty template (no strand geometry) until it is sculpted in the design editor. Creating does not
  apply the preset; use `select_preset` for that. Blocking.
- `import_custom_preset(section, category, path, name?, wear?)` — import a reusable custom item
  (`*.vroidcustomitem`) from a host path into the Custom tab of the matching Hairstyle or Outfit
  category. The target category comes from the file, so a file imported from another tab in the same
  section lands in its own category (`category` in the result, plus `requested_category` when they
  differ). `name` optionally renames the new item. `wear` chooses whether the imported item is applied
  to the model (`"now"`) or only added to the library (`"later"`, the default). Returns `{imported,
  section, category, id, name, file, applied, wear_requested, type, count, renamed}`. Blocking.
- `export_custom_preset(section, category, index, path, save?, overwrite?)` — export the Custom-tab
  item with the `id` from `presets` to a `*.vroidcustomitem` file (the extension is appended when the
  path has none). Hairstyle and Outfit only, and the item may be worn or not. `save=true` (default)
  includes the worn item's current design in the exported file; for an unworn item that step is
  skipped (`saved:false`) and the stored file is exported as-is. `save=false` exports the stored file
  without touching the model. `overwrite=false` refuses to replace an existing file. Returns
  `{exported, section, category, id, name, path, bytes, worn, saved, save_requested}`. Blocking.
- `save [path]` — without a path, saves in place (`saved`); with a path, performs save-as
  (`MainActionHandler.SaveSync(path)`, no dialog) and returns the resolved path. Both variants are
  confirmed by a signal rather than a fixed pause (save-as: the file appears; in-place: the dirty flag
  `IsUnSaved` clears); otherwise `{ok:false,error}`. After save-as the path is registered on the
  engine, so a later `save` without a path writes to the new file.
- `create <female|male>` — a new character from a built-in template (the same choice as the stock
  "Create New Model" dialog). Blocking, like `open`: on return the model is built and
  `state.ready=true`. Valid only from the start screen; with a model open it returns
  "first save/main_menu", forcing the caller to resolve pending changes explicitly (`save` →
  `main_menu`, or `main_menu` to discard). Returns `{gender, variant}`.
- `preset_preview_list(section, category, ids)` and
  `preset_preview_range(section, category, start, count)` — visual selection of presets by the same
  continuous `id` (custom items included). The list variant takes 1..12 explicit ids; the range
  variant walks consecutive ids (`count` 1..12; out-of-range is an error, not trimmed). Each result
  element carries `{id, index, name, kind}` (the same as in `presets`), so no manual join is needed.
  In MCP the images arrive as inline image blocks; `mode` controls writing `preset_<i>.png`. `b64`
  (range only) returns base64 PNG inline for the VLM harness. Caveats: some Face overlays
  (lips/lashes/cheeks) render on a generic head rather than the current model;
  clothing/hairstyle/eyes reflect the real preset. All previews carry the editor's fixed
  light/self-shadowing (e.g. a pink shadow on the neck), which is not a parameter and cannot be
  removed (the Look section is outside the API).
- `screenshot(mode)` — preview-only PNG. `inline` (MCP default) returns a base64 image without writing
  to disk; `file` writes `screenshot.png` (raw-protocol default); `both` does both. Requires an open
  model; `ui_screenshot` is the exception and works without one.
- `main_menu()` — return to the main menu (resets the dirty flag, no dialog).
- **`batch`** — an array of operations executed sequentially on the main thread with a per-element
  result: `{method, arguments}` objects (MCP) or an array of commands (raw protocol). MCP `batch` takes
  **named arguments only**: `arguments` is an object with exactly the same keys as that tool's
  `inputSchema` (e.g. `{"method":"enter","arguments":{"section":"Face","category":"Irises"}}`,
  `{"method":"hair_group_add","arguments":{"category":"Front","from":"look/fringe","name":"Bangs"}}`).
  A positional array in `arguments` is rejected, an unknown key or a missing required argument is an
  error, so an operation can never silently land on the wrong parameter. `method` is any tool name,
  including `help`; array-valued arguments (e.g. `preset_preview_list` `indexes`) stay JSON arrays.
  For image-returning operations (`screenshot`, `ui_screenshot`, `preset_preview_*` with
  `inline`/`both`) it appends inline image blocks, each preceded by an `[op N] <method>` text marker,
  capped at 12 images / 8 MB per batch (excess dropped). The raw protocol returns text only. Batch
  size is bounded by `max_batch_ops` (`servers.json`, default 200, 0 = unlimited); exceeding it is an
  error before the batch starts. Raw-protocol operation arguments also accept `args`/`params`.
- **`status`** (MCP) — a liveness/queue probe answered **outside the main-thread queue** (like the
  JSON-RPC `ping`), so it works during a mutation or batch. Returns `{inflight?, inflight_seconds?,
  queued, batch?}`: the current command (if running > 3 s), the queue length, and the batch position
  `{index,total,method}`. Useful to recognize that a timed-out batch is still applying and avoid
  repeating it. No model required.

## Camera

Subcommands: `reset`, `frame`, `angles`, `zoom`, `pan`, `projection`, `rotate`, `view`, `probe`.
Each call is blocking, waits for application, and returns
`{angles:{yaw,pitch}, distance, position, rotation, projection, fov}`; an unknown subcommand or a
missing/non-numeric argument yields `{"ok":false,"error":…}`.

- **`frame <region> [angle]`** — the primary inspection command. `region` sets the framing and orbit
  center: `body|face|head|upper|legs|feet`. `angle` (default `front`):
  `front|back|left|right|threequarter_left|threequarter_right`. E.g. `frame face right` (profile),
  `frame legs threequarter_left`.
- **`angles x y`** rotates around the current center (`x=yaw`, `y=pitch`; model front is `yaw 180`)
  and preserves the current distance and pan — it does not reframe. For a new region or angle use
  `frame`.
- **`projection`** toggles the projection; it is "sticky" — only `projection` changes it, `reset`
  restores the projection captured at open, and `frame`/`angles`/`zoom`/`pan`/`view` leave it alone.
- `frame` and an explicit `<region>` on `enter` re-aim from the front (yaw returns to front); pass an
  `angle` or follow with `camera angles` for other views.
- `rotate` takes a `CameraRotationMode` index; the valid values are listed in the tool schema and by
  `help('camera')` (resolved from the enum at runtime). `view` (numeric) is manual calibration and
  `probe` is diagnostics.
- Requires an open model (`no model open` otherwise).

### Camera policy

The plugin uses its own category→view policy. `ThumbnailCameraConfigResolver.Resolve(tgtype)` returns
the default for every category outside preview generation, so it cannot be reused. The policy maps:
Face → `face`; back hair → `head:back`, side hair → `head:threequarter_*`;
Body/Tops/Bottoms/Dresses → `body`; Socks/Shoes → `feet`; neck/hand accessories → `upper`.

Framing uses `SetCustomView(pos, center)`, where the center (and orbit center) is a fraction of the
avatar height from the renderer bounds and the distance is a fraction of the default; `frame` also
sets the angle. Each camera command waits for the rotation coroutine and one frame
(`WaitCameraIdle`), so `camera …` followed by `screenshot` in one `batch` yields a fresh frame.

## Exit

The agent must not be left with the "unsaved changes" dialog.

- `SaveAndExit()` — save, wait, quit.
- `ExitWithoutSave()` — reset dirty (`MainActionHandler.IsUnSaved=false`), then `Application.Quit()`.
- `BackToModelSelect()` — return to the main menu, also resetting dirty.
- Plugin surface: `QuitClean()`, the `exit` command/tool (`save`/`discard`), and `main_menu`.
  `exit save` waits for the in-place save to finish (with a timeout) before quitting; `discard`
  resets dirty and quits.

## Developer and debug surface

The raw line protocol additionally exposes commands not published over MCP. They are intended for
reverse-engineering and tooling (label overlays, i18n dumps, render-quality probes, the settings
dialog, full-window screenshots) and are documented in [`internal-api.md`](internal-api.md). Preset
icons are exported by the `icons` command and keyed by `resourceName`; raw `resourceName`/`itemId`
stay on the developer surface (identifier parsing — `internal-api.md` §5).

## Implementation notes

- **Network:** `HttpListener` binds `0.0.0.0:17891`, so the MCP handler explicitly rejects
  non-loopback clients (`RemoteEndPoint` → `403`). The raw command server binds `127.0.0.1:17890`
  directly. Remote clients are not supported.
- **HTTP framing:** every MCP response is sent with an explicit `Content-Length` and
  `Connection: close` (`KeepAlive=false`). Otherwise the runtime uses `Transfer-Encoding: chunked`,
  whose terminator is not flushed under Wine on non-keep-alive connections — the client hangs,
  connections leak, and the listener stops accepting after roughly 140 sequential requests
  (covered by `protocol` → `sequential-300`).
- **Undo/redo** exists in the engine history (`Context`) but is not exposed yet.
- `IsChecked` (`ColorViewModelBase`) toggles an overlay (`*_OverlayColorDisableFlag`); the UI has
  almost no explicit checkboxes, and it is not needed for the current surface.


## Native height and grouped parameter updates

`body_height()` reads the body editor's native `BodyHeightValue`, `BodyHeightLabel`,
`UnitLabel` and property label. It requires a ready model and refuses a read during an
update. `value` retains the native unit; consult `unit_label` rather than assuming units.
Windows 2.16.0 runtime returned value `159.7701416015625`, label `159.8cm`, unit `cm`
for a disposable stock female model. This is not a camera/mesh-bounds estimate.

`set_params(section, category, changes_json)` applies 1–64 changes in one category.
For example, `changes_json` can contain:

```json
[{"parameter":"Face/Irises/Iris Size (X)","value":0.2},
 {"parameter":"Face/Irises/Iris Size (Y)","value":0.3}]
```

All entries, duplicate targets, finite values and available setters are checked before
parameter setters run. Navigation to the specified category may still occur on rejected
input. The tool uses ordinary native setters in sequence and one final readiness wait;
it does **not** suppress native rebuilds, provide an atomic transaction or promise a speedup.
Results report returned setter calls, `native_rebuilds_suppressed:false`, observed rebuild
and updating state. An observed rebuild/idle state is not a per-setter completion token.
A deadline without observed rebuild/idle state returns an error with the applied prefix;
do not blindly replay after partial failure. A failed setter itself may already have applied.
No-op inputs may produce no rebuild and therefore an incomplete-readiness error.

Global hair Main/Emission entries include a warning during discovery and setter responses:
these operations can overwrite distinct custom hair material colours. This preserves the
native global-colour semantics. Use individual-material tools when installed (separate PR #5)
to preserve distinct colours. It does not claim to detect all material configurations.

### Remaining issue #1 boundaries

Inner Top/Bottom removal is not added. The inspected native texture editor exposes layer
visibility toggling, but reliable category-to-layer ownership was not established and entry
can trigger colour-baking/save prompts. Arbitrarily toggling layers would risk unrelated
textures. A future implementation needs exact clothing ownership and reversible layer-state
capture before it can safely expose this operation. True native rebuild suspension remains
unimplemented; the grouped setter is only an intermediate improvement.
