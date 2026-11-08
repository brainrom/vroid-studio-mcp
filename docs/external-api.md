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
  (`*.vroidcustomitem`) from a host path into the Custom tab of the matching Hairstyle, Outfit or Face/Eyelids
  category. The target category comes from the file, so a file imported from another tab in the same
  section lands in its own category (`category` in the result, plus `requested_category` when they
  differ). `name` optionally renames the new item. `wear` chooses whether the imported item is applied
  to the model (`"now"`) or only added to the library (`"later"`, the default). Returns `{imported,
  section, category, id, name, file, applied, wear_requested, type, count, renamed}`. Blocking.
- `export_custom_preset(section, category, index, path, save?, overwrite?)` — export the Custom-tab
  item with the `id` from `presets` to a `*.vroidcustomitem` file (the extension is appended when the
  path has none). Hairstyle, Outfit and Face/Eyelids are supported; the item may be worn or not. `save=true` (default)
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

## Hair material editing

`hair_material` operates on native model data and history commands, not Unity renderer overrides.
It does not select a preset or generate/replace hair geometry. Start with `action:list`, then
`action:inspect, material:<index>`. Material, slot, layer and group indices are zero-based,
session-local and must be refreshed after structural edits. Work on a separately saved model
when evaluating a source texture pack.

| Action | Arguments in addition to action | Meaning |
| --- | --- | --- |
| `list` | none | Materials and existing group assignments |
| `inspect` | `material` | Native colors, supported float properties, slots and raster layers |
| `create` | optional `name` | Create a native hair material |
| `duplicate` | `material` | Duplicate the material without assigning it |
| `rename` | `material`, `name` | Rename using native history |
| `assign` | `material`, optional `group` | Assign to the indexed group, or the current selection |
| `prepare` | `material` | Explicitly initialize advanced editable texture slots |
| `import_texture` | `material`, `slot`, `path`, optional `layer` | Replace one raster layer, or add a new layer if omitted |
| `export_texture` | `material`, `slot`, `layer`, `path` | Export one raster layer to a new PNG file |
| `preview_texture` | `material`, `slot`, `layer` | Return that raster layer as an MCP image |
| `set_color` | `material`, `key`, `value` | Set an inspected native color property |
| `set_color_enabled` | `material`, `key`, `value` | Enable/disable an inspected toggleable color with `true`/`false` |
| `set_float` | `material`, `key`, `value` | Set an inspected supported shader parameter |
| `set_layer` | `material`, `slot`, `layer`, `key`, `value` | Supported visibility, opacity or blend control |

`key` must be a supported key from inspection, not a method name. Numeric and color values
are strings. Import uses PNG files; exports refuse an existing target. Layer previews are
individual raster contents, not a promise of a composited material or UV preview. Use the
avatar `screenshot` to assess the rendered result. Layer stacks retain their native editability.
Color entries report `enabled`, `disabled` and `can_set_enabled`. Material-owned base overlay
and emission colors, when available, use `base:overlay` and `base:emission`; arbitrary global
base-color IDs are not accepted. Changing a disabled color does not enable it automatically.

```json
{"name":"hair_material","arguments":{"action":"list"}}
{"name":"hair_material","arguments":{"action":"inspect","material":0}}
{"name":"hair_material","arguments":{"action":"prepare","material":0}}
{"name":"hair_material","arguments":{"action":"import_texture","material":0,"slot":0,"layer":0,"path":"C:/art/hair-base.png"}}
{"name":"hair_material","arguments":{"action":"assign","material":0,"group":0}}
{"name":"hair_material","arguments":{"action":"preview_texture","material":0,"slot":0,"layer":0}}
```

After a mutation, refresh inspection before using indices again. A rendered edit waits up to
12 seconds for a completed native model rebuild and model readiness (preset-list events do
not satisfy this check). A timeout is an error, not success;
the command may already have applied, so inspect before retrying. Material creation, duplication
and renaming also await native readiness. No automatic save occurs.

**VRoid 2.16 background-session preview limitation:** group material assignments persist in the
model, but the tested hidden/background renderer kept the old material until reopening. This
has not been reproduced in a foreground session. Mutation results report
`preview_verified:false`; `ready:true` confirms a completed model update, not visual acceptance.
Save to a separate trial file, explicitly return to the main menu, reopen it and inspect a
`screenshot`. Save once more after reopening to refresh a stale saved thumbnail. The plugin
never reloads an open model automatically. Blanket native refresh was tested and did not
invalidate this cache, so it is not exposed as a public action.

`hair_material_source` reads a `.vroidcustomitem` through the native loader. Its `list` action
takes `path` and returns source material IDs. `import` additionally takes `material_id` and an
optional `name`; supported material-only transfer is separate from assignment. It never wears
the source item or adds it to the reusable custom-item library. Unsupported native transfer
paths produce an explicit error rather than falling back to hairstyle replacement.
The native compatibility updater resolves source asset references in the detached destination
before committing the material copy. Arbitrary archive rewriting is not used. Transfer sources
are retained for native edit-history replay; undo/redo across model switches remains unverified.

## Iris texture editing

`iris_texture` edits the current model's iris image through native advanced-edit and raster
history commands. It does not select a face preset, import a whole model, or modify face
geometry, eyebrows, eyeliner, lashes or eye highlights. The iris image contains both eyes;
separate left/right texture uploads are not supported.

| Action | Arguments | Meaning |
| --- | --- | --- |
| `inspect` | none | Resolve the native iris image, preparation state, raster layers and overlay colors |
| `prepare` | none | Make the iris image editable without baking its current overlay colors |
| `import_texture` | `path`, optional `layer` | Replace the indexed layer, or add a new layer when omitted |
| `export_texture` | `layer`, `path` | Export a raster layer to a new PNG file |
| `set_layer` | `layer`, `key`, `value` | Change `visibility` (`true`/`false`) or `opacity` (`0`..`1`) |

Start with `inspect`; use `prepare` only when necessary. Layer indices are zero-based and
must be refreshed after structural edits. Import requires a prepared image and a valid PNG
up to 4096 pixels per side / 16 megapixels / 64 MiB. Export refuses existing files.
Import replaces only the selected layer, so inspect other visible layers that could cover it.
For a PNG containing authored colors, inspect the iris color overlay: the existing
`set_param` tool can set both iris colors to white when an enabled overlay would tint the PNG.
These are explicit editing decisions; importing does not silently change colors or hide layers.

```json
{"name":"iris_texture","arguments":{"action":"inspect"}}
{"name":"iris_texture","arguments":{"action":"prepare"}}
{"name":"iris_texture","arguments":{"action":"import_texture","layer":0,"path":"C:/art/iris-teal.png"}}
{"name":"iris_texture","arguments":{"action":"export_texture","layer":0,"path":"C:/art/iris-layer-check.png"}}
```

Mutations await a completed native rebuild, with a 12-second deadline. A readiness timeout
can follow an applied edit: inspect before retrying. `preview_verified:false` requires the
caller to inspect actual renders. Save to a separate trial and explicitly reopen it to verify
persistence. Unsupported or ambiguous native iris layouts are rejected instead of guessing
internal image IDs. The tool never rewrites a `.vroid` archive.

See [iris texture validation](iris-texture-validation.md) for tested and unverified paths.

## Eyelid texture layers

`eyelid_texture` supports `inspect`, `export_texture` (`layer`, `path`) and
`set_layer` (`layer`, `key`, `value`). Layer keys are `visibility` and `opacity`
(finite `0`..`1`); exports require a new PNG path. Inspect returns the exact native
eyelid group/image, editable state and raster layer names, visibility, opacity and
blend mode. Other face channels and geometry are outside this tool.

Use `import_custom_preset` with `section:"Face"`, `category:"Eyelids"` to import an
eyelid custom item through VRoid's native registry. Face imports must have the exact
native `Level2.Eyelid` root type; other Face categories and item types are rejected.
`wear:"later"` only adds the library item. `wear:"now"` applies its eyelid group;
inspect the item and back up the model before replacing an existing group.
This does not import a complete face, sample model, iris or clothing set.

```json
{"name":"import_custom_preset","arguments":{"section":"Face","category":"Eyelids","path":"C:/art/eyelids.vroidcustomitem","wear":"now"}}
{"name":"eyelid_texture","arguments":{"action":"inspect"}}
{"name":"eyelid_texture","arguments":{"action":"set_layer","layer":0,"key":"visibility","value":"true"}}
{"name":"eyelid_texture","arguments":{"action":"set_layer","layer":0,"key":"opacity","value":"0.7"}}
{"name":"eyelid_texture","arguments":{"action":"export_texture","layer":0,"path":"C:/art/eyelid-layer.png"}}
```

Layer editing requires an already editable native image, such as a layered custom
item. PNG import, asset-backed preparation, blending changes and layer restructuring
are not exposed here. Refresh indices after item changes. Mutations wait for a
completed native rebuild for up to 12 seconds; a timeout can follow an applied edit,
so inspect before retrying. `preview_verified:false` is not visual acceptance.
Save/reopen and inspect actual renders separately. Shared skin colours are not
changed automatically.

See [eyelid layer validation](eyelid-texture-validation.md) for tested and
unverified paths, including the untested Face/Eyelids custom-item export route.

## Session identity and history

`state` additionally returns `process_id`, `app_version`, `plugin_build` (loaded assembly module
ID), `model_path`, and `unsaved` (null if unavailable). Verify this identity before controlling
an editor when multiple instances exist. A DLL copied on disk is not proof that an existing
process loaded it; BepInEx requires an app restart.

`recent_models` defaults to `action:list`. `action:remove` requires an exact `path` returned by
the list and refuses the current open model. It removes only the recent-history entry, never
the model file. Use only for explicitly requested cleanup; preserve unrelated history entries.
`action:restore` appends an absolute path to an existing `.vroid` file without opening it.
It refuses a full native history so restoration cannot evict another entry. Use it to restore
entries displaced by the app's recent-history limit after removing known temporary entries.

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


## Bounded hair geometry

`hair_geometry(action, group?, material?, name?, key?, value?)` is separate from the
geometry-preserving material tools. Use it only when geometry changes are intended.
Enter an editable non-base Hairstyle category first.

- `inspect`: list groups and material indices; optional `group` returns one group, its
  path, name, transform, material ID, strand count, guide validity and parameter values.
- `add`: requires source `group`, existing `material` and a unique `name` (1..128 characters).
  Deep-clone the source's native guide; retain its transform and transferable scope; execute
  `AddProceduralHairGroupCommand`. Existing groups and library files are not replaced.
  The response identifies the new group/path, source kind and `parameter_initialization`.
  Only the guide and transform are reused: source strands, brush root positions and source
  parameter sets are not copied. Native defaults may include six strands:
  explicitly inspect and adjust the new group before saving or accepting its appearance.
- `set`: requires a procedural `group`, allowlisted `key`, and numeric string `value`.
  Brush groups are read-only. Native modification uses `reuseStrands=false` to regenerate
  the target group's strands. Other groups are not selected for modification.

| Key | Tool bounds | Native property |
| --- | --- | --- |
| `count` | integer 1..4 | ProceduralFormParameterSet.HairCount |
| `length` | 0.05..1 | ProceduralFormParameterSet.Length |
| `width` | 0.001..1 | HairParameterSet.WidthScale |
| `position` | 0..360 degrees | ProceduralFormParameterSet.AngleFrom |
| `interval` | 1..90 degrees | ProceduralFormParameterSet.AngleWidth |
| `origin_x`, `origin_y`, `origin_z` | absolute stored coordinate; maximum change 0.02 native units per call | Guide.Origin.Position.x / y / z |

These are conservative tool limits, not the full editor ranges. Length is relative to the
source guide. Position starts the fan around it (0 front, 90 model left); interval extends
from that edge. Customized guide shapes can produce clipping or unexpected placement.
All other raw parameters are inspection-only. Arch of Hair Length is never writable.

`guide.valid_for_copy` validates native structure and finite data only. A customized brush
guide can pass this check and still produce crown intersections or stiff strips when used
with procedural defaults. This add operation is not equivalent to the editor creating a
fresh default procedural guide. Inspect `procedural_parameters.GrowFrom` along with the
other raw defaults before diagnosing root placement; the current surface cannot change
that field. Its mapping, units and supported range have not been established here, so do
not infer that changing length, count or width can move roots to the source brush hairline.

The **experimental** origin keys translate the entire selected procedural group's guide
through `SetGuideOriginTransformCommand(IHairGroupPath, Transform)`. They accept an absolute
component of the stored native guide-origin position, preserving the other two components,
rotation and scale. `inspect.guide_origin` reports position `[x,y,z]`, rotation `[x,y,z,w]`,
scale and validity; `settable` reports current per-call bounds. These coordinates are not
calibrated metres, world axes, or an individual-root height. The 0.02 displacement cap is a
tool limit, not an editor range or safety guarantee; repeated calls accumulate. Missing or
non-finite transform data, degenerate rotation/scale, malformed guides and larger changes
are rejected before the history call. Exact transform readback is required afterward, and
the existing handler waits for native rebuild readiness. These guards and the native
command signature are source-supported; runtime placement, rebuild signaling, persistence
and undo/redo for this new route are **UNVERIFIED**. Brush groups remain read-only.

```json
{"name":"hair_geometry","arguments":{"action":"inspect"}}
{"name":"hair_geometry","arguments":{"action":"add","group":0,"material":0,"name":"Fringe trial"}}
```

Refresh inspection and use the **returned new group index**, rather than assuming an index:

```json
{"name":"hair_geometry","arguments":{"action":"set","group":7,"key":"count","value":"2"}}
{"name":"hair_geometry","arguments":{"action":"set","group":7,"key":"length","value":"0.4"}}
```

For a disposable placement trial, inspect group 7 first. **Only if its current stored X
coordinate is 0**, this example requests a 0.01 native-unit translation on that axis:

```json
{"name":"hair_geometry","arguments":{"action":"set","group":7,"key":"origin_x","value":"0.01"}}
```

Use the inspected coordinate to choose a nearby absolute value for another group. Do not
assume that increasing X, Y or Z moves the hair in a particular screen/world direction.

Indices can change after an addition or model/category switch. Inspect before every new
editing sequence. Keep a recoverable model backup and use separate trials. This surface does
not expose deletion, undo/redo, arbitrary reflection, guide sculpting or library saving.
On a disposable trial, compare the source and added group's guide/parameter inspection,
then inspect matching front, side and crown renders after readiness. Check both root
placement and strand shape; confirm prior groups are unchanged. Save/reopen only after the
trial is visually accepted. A failed fit needs a proven root/guide editing route rather than
further blind length or width adjustments.

Mutation success returns `applied`, `ready`, `preview_verified:false` and `result`.
Readiness requires a completed native rebuild, with a 12-second timeout. A post-mutation
failure retains JSON `{error,details}` in the MCP error text (also in batch results).
`details.applied` may be null if application cannot be established, or true after a confirmed
native edit whose readiness timed out. Inspect state and group paths before retrying; do not
blindly repeat an add. Native readiness never certifies a good-looking hairstyle.

See [hair geometry validation](hair-geometry-validation.md) for the rejected visual trial
and verified persistence, separately from successful API calls.
