# VRoid Studio 2.14.0 — internal API (semantics)

This document records the results of reverse engineering so that the discovered semantics are not lost.
Method addresses are given for **this specific build**; after updating the app they must be
re-dumped (`Il2CppDumper`), since RVAs change.

## 1. Environment

| Parameter | Value |
|---|---|
| Product | VRoid Studio 2.14.0 (pixiv) |
| Unity | `6000.0.62f1` |
| Scripting backend | IL2CPP, metadata **v31.1** |
| Bitness | x64 |
| `GameAssembly.dll` image base | `0x180000000` (VA = RVA + `0x180000000`) |
| Mod loader | BepInEx **6.0.0-be.788** (`Unity.IL2CPP`) |
| Plugin runtime | .NET 6 (`BasePlugin` from `BepInEx.Unity.IL2CPP`) |

The plugin is host-agnostic: the same sources build and run on Windows (native) and under Wine on
Linux (Wine 11.18 in this build), and, with a separate BepInEx/Il2CppInterop stack, on macOS arm64
(see [`macos.md`](macos.md)). Only path handling is host-dependent (§14).

Main assemblies (namespaces):

- `VRoid.Studio.dll` — `VRoid.Studio.*`, `VRoid.Studio.ModelPresetsSelector.*`, `VRoid.Studio.ModelConfiguration.*`, `VRoid.Studio.ExpressionParameter.*`
- `VRoid.UI.dll` — `VRoid.UI`, `VRoid.UI.View`
- `VRoidCore.dll` — `VRoidCore.Editing.*`, `VRoidCore.Common.*`, `VRoidCore.Protobuf.*`
- `VRoidStudio.dll` — `VRoidStudio.GUI.*`, `VRoidStudio.Application.*`, `VRoidStudio.PhotoBooth.*`

## 2. Terminology

| Concept | Code | Note |
|---|---|---|
| Section (top tabs) | `VRoid.Studio.EditRootScreen.EditMode` | Face/Hairstyle/Body/Outfit/Accessories/Look |
| Category tree | `CategoryModel` / `CategoryViewModel` | `Name`, `TranslationKey`, `Icon`, `TransferableGroupType`, `Children` |
| Parameter category | `ParameterCategoryModel` / `ParameterCategoryViewModel` | `.Parameters` (`IParameterViewModel`) |
| Preset category | `PresetCategoryModel` / `PresetCategoryViewModel` | `.PresetOptions` (`IOptionViewModel`) |
| Parameters+presets | `ParametricPresetCategoryModel` / `ParametricPresetCategoryViewModel` | contains both sub-VMs |
| Expression category | `ExpressionCategoryModel` / `ExpressionCategoryViewModel` | for the special logic see §7 |
| Parameter section | `ParameterSection` | `transferableGroupType` + `ParameterModel[]` |
| Parameter (description) | `ParameterModel` → `SingleParameterModel` (`key`), `CombinedParameterModel` (`negativeKey`+`positiveKey`), `SingleBlendShapeParameterModel` | `displayName`, `translationKey` |
| Parameter (live) | `VRoid.Studio.ModelConfiguration.IParameterViewModel` → `BoneTransformParameterViewModel`, `BlendShapeTransformParameterViewModel`, `Combined*` | inherit `VRoid.UI.SingleParameterViewModel` / `CombinedParameterViewModel` |
| Preset (description) | `PresetModel` | `transferableGroupId`, `itemId`, `resourceName`, `priceInCoin`, … |
| Preset (live) | `PresetOptionViewModel` / `ClearOptionViewModel` / `CustomItemOptionViewModel` / `AddCustomItemOptionViewModel` | implement `IOptionViewModel` |
| Group type | `transferableGroupType` (string) | e.g. `TransferableGroupType.N00.Level1.EyeSet` — stable ID |
| Selector | `BasePresetsSelectorViewModel` (subclasses `FaceEditor/HairEditor/BodyEditor/ClothingEditor.PresetsSelectorViewModel`) | root of the left panel |
| Engine | `VRoidCore.Editing.Context` | commands/queries, edit core |

`EditMode` (enum values): `FaceEditor=0`, `HairEditor=1`, `BodyEditor=2`,
`ClothingEditor=3`, `AccessoryEditor=4`, `GeneralEditor=5`.

UI tab correspondence: Face→`FaceEditor`, Hairstyle→`HairEditor`, Body→`BodyEditor`,
Outfit→`ClothingEditor`, Accessories→`AccessoryEditor`, Look→`GeneralEditor`.

## 3. Category tree

`BasePresetsSelectorViewModel.Categories` — the root (`CategoryViewModel`). Traversal:
`Children` → nested `CategoryViewModel`. Each category has:
`Name`, `TranslationKey`, `Icon`, `TransferableGroupType`, `IsCustomable`, `Parent`.
Selection: `BasePresetsSelectorViewModel.set_SelectedCategory(CategoryViewModel)`.

`CategoryModel` (description) additionally: `transferableGroupType`, `children[]`, `isCustomable`.

This build has four tree selectors: `FaceEditor`, `HairEditor`, `BodyEditor`, `ClothingEditor`.
`AccessoryEditor`/`GeneralEditor` use a different UI (`ParameterUtil`, `InstanceItemViewModel`,
`PropertyPanel`).

## 4. Parameters

Live parameters (`IParameterViewModel`) are inheritors of:

- `VRoid.UI.SingleParameterViewModel` — `Label`, `Value` (float), `MinValue`, `MaxValue`, `IsEditable`;
  protected `ModelValue` get/set.
- `VRoid.UI.CombinedParameterViewModel` — bidirectional slider: `Label`, `CombinedValue`,
  `NegativeValue`, `PositiveValue`, `MinValue`, `MaxValue`; protected `NegativeModelValue` /
  `PositiveModelValue`.
- `VRoid.Studio.TextureEditor.Color.ViewModel.ColorViewModelBase` — color: `TranslatedDisplayName`,
  `Color`, `IsChecked`, `IsCheckEnabled`, `IsValid`.

Concrete classes (`VRoid.Studio.ModelConfiguration`):

| Class | Key path | Value |
|---|---|---|
| `BoneTransformParameterViewModel` | `IBoneTransformPath` (+ `_boneElementKey`) | `Value` |
| `BlendShapeTransformParameterViewModel` | `IBlendShapeTransformPath` | `Value` |
| `CombinedBoneTransformParameterViewModel` | two `BoneTransformPath` | `CombinedValue` |
| `CombinedBlendShapeTransformParameterViewModel` | two blend-shape paths | `CombinedValue` |

Changing a value goes through VM setters → `ModelValue` → `Context` → history command.

Discovered scope (this build, default N00 model):

| Section | Categories with parameters | Total parameters | Presets |
|---|---|---|---|
| Face (`FaceEditor`) | 8 (`Face Sets` 92, `Eyes Sets` 30, `Irises` 4, `Eyebrows` 9, `Eyeliner` 17, `Nose` 12, `Mouth Inside` 29, `Skin` 21) | 214 | 613 |
| Body (`BodyEditor`) | 1 (`Whole Body` 23) | 23 | 6 |
| Hair (`HairEditor`) | 0 (parameters in Design Editor) | 0 | 393 |
| Clothing (`ClothingEditor`) | 0 in `ParameterCategoryViewModel`; sliders are on the selected item (`InstanceItemViewModel.BlendShapeParameters`, e.g. `Skirt Flare`/`Hem Flare`), served by `enter` | per item (Onepiece 88, Socks 98, Shoes 38, …) | 379 |

## 5. Presets

`PresetCategoryViewModel.PresetOptions` — `IReadOnlyCollection<IOptionViewModel>`.
Items — `PresetOptionViewModel` (normal), `ClearOptionViewModel` (empty/reset),
`AddCustomItemOptionViewModel` (add), `CustomItemOptionViewModel` (user-defined).

On `PresetOptionViewModel`, useful: `TransferableGroupId`, `IsSelected`, `Model` (`PresetModel`
with `resourceName`, `itemId`, `priceInCoin`, flags), `Preview`.

Preset selection: `PresetOptionViewModel` → `PresetSelectAction` → `Context.LoadAndReplaceTransferableGroup(...)`.

### Preset identifiers

`PresetModel` (ctor `(transferableGroupId, itemId, resourceName)`) carries several different ids —
they are easy to confuse:

| id | What it is | Where (ctor arg) | Format | Purpose |
|---|---|---|---|---|
| `idx` | position in the current `PresetCategoryViewModel.PresetOptions` | not stored, render order | `int` | `select_preset`, preview; short-lived (VM rebuild, section/model change) |
| `resourceName` | path to the group/icon asset | `PresetModel` 3rd; live → `TransferableGroupInfo.ResourceName` | `pixiv/VRoid/.../TransferableGroupType.N00.Level1.Tops.transferablegroup` | preview (`Thumbnails.Get`), `label_set`, fallback name |
| `itemId` | id of the **catalog** entry (product/license) | `PresetModel` 2nd | string (Booth upstream — `long`) | purchase/ownership/bundles; fallback key for labels |
| `transferableGroupId` | id of the transferable group itself | `PresetModel` 1st | string | key `PlayFabUserModel.transferableGroupIdToPreset` |

**Where `itemId` comes from.** From PlayFab — `PlayFabUserModel.LoadPresetModelsWithPlayfab(CatalogItem)`
(`itemId = CatalogItem.ItemId`, `resourceName = GetResourceName(item)`); offline —
`UnsafeLoadPresetModelsWithoutPlayfab()` from `EditableAssetCatalog.TransferableGroupEntry` (fields
`id` + `resourceName`). Attributes of `PresetModel` hang on itemId (`priceInCoin`, `isPurchased`/
`isOwned`/`isTakeFree`/`isSpecial`/`isSetItem`/`setCount`/`isNewItem`) and the dictionaries of `PlayFabUserModel`
(`_itemIdTo*`, `_inventoryItemIds`, `_itemIdToBundledItemIds`). Marketplace prefixes
`pixiv/VRoidMobile/`, debug `pixiv/dummy/`. **For the built-in stock `id == resourceName`** ⇒ for stock
presets itemId duplicates resourceName and isn't needed externally; it diverges only for purchased/downloaded ones.

**Do not confuse** `transferableGroupId` with `transferableGroupType` (`CategoryModel.TransferableGroupType`) —
this is the *type* of the group (`...Level1.Tops`), not a concrete id.

**Externally** (`presets`/`preset_preview_*`/`select_preset`) only `idx` and `name` go out; the raw
`resourceName`/`itemId` remain on dev/TCP (`presets_full`). The label overlay is keyed by
`resourceName` with a fallback to `itemId` (`PresetLabels.Get`).

**Groundwork for custom assets.** User/purchased groups live not in `PresetModel`, but in
`CustomItemOptionViewModel`/`AddCustomItemOptionViewModel` (own `ThumbnailSource`/`DisplayName`); their
identity is in `TransferableGroupInfo` (`VRoidCore.Editing`): `Id`, `Type`, `ResourceName`,
`OriginalResourceName`, `OriginalId`, `Revision`, `UpdatedSinceLastRevision`, `UserDefinedString`.
`OriginalId`/`Revision` give the source and version of the updatable group, `UpdatedSinceLastRevision` — the
update flag; the asset catalog — `EditableAssetCatalog` (`EntryBase.{resourceName,id,address}`,
`TransferableGroupEntry.{transferableGroupType,thumbnailAddress}`).

## 6. Engine `VRoidCore.Editing.Context`

Key methods (public):

- `ExecuteSyncCommand(ISyncCommand<HistoryManagerContext, Model>)`
- `ExecuteSyncQuery<T>(ISyncQuery<T>)`
- `ExecuteAsyncCommand(...)`
- `SetBoneTransformParameter(string transferableGroupId, IEnumerable<KeyValuePair<IBoneTransformPath,float>>)`
- `GetActiveTransferableGroupsWithCustomItemIndex()`, `GetParametersForTransferableGroup(...)`
- `GetColorParameter(...)`, `TryGetColorParameter(...)`
- `LoadAndReplaceTransferableGroup(...)`, `RemoveTransferablesWithType(...)`
- `SerializeActive()`, `DuplicateActive()`, `BeginEditing()`
- `AddEventListener/RemoveEventListener`, `IsCommandExecutable`, `IsQueryExecutable`

History: `VRoid.Studio.EditRootScreen.ViewModel.Undo()/Redo()/Save()/SaveAs()/EnterToVRMExport()/EnterToPhotoBooth()`.

## 7. Expression Editor (special case)

Belongs to `VRoid.Studio.FaceEditor.ViewModel` (not to the category tree). Structure:

```
FaceEditor.ViewModel
 ├─ PresetsSelector : FaceEditor.PresetsSelectorViewModel
 ├─ SelectedCategory : CategoryViewModel
 ├─ IsExpression / IsNotExpression : bool
 ├─ ExpressionParameterCategories : IReadOnlyCollection<ExpressionParameterCategoryViewModel>
 └─ SelectedExpressionParameterCategory

ExpressionParameterCategoryViewModel
 └─ ParameterParts : IEnumerable<ExpressionParameterPartViewModel>
      └─ Parameters : IEnumerable<ExpressionParameterViewModel>
```

In the category tree there is an `Expression Editor` node of type `ExpressionCategoryViewModel`
(without children/parameters) — it is only an "entry button"; the real data lies in `FaceEditor.ViewModel`.
It is not exposed externally: `categories` does not list it, `enter`/`presets` answer `no tab`
(`Categories.IsUnsupportedTab` + `FindTab`).

## 8. Hook map (RVA)

VA = RVA + `0x180000000`.

| Method | RVA |
|---|---|
| `VRoid.Studio.MainViewModel.InitializeCurrentFileAndMoveToEditRoot` | `0x30D6580` |
| `VRoid.Studio.MainViewModel.ImportFileAndMoveToEditRoot` | `0x30D6A70` |
| `VRoidStudio.GUI.AvatarEditor.AvatarEditor.Awake` | `0x3F6DA80` |
| `VRoidStudio.GUI.AvatarEditor.AvatarEditor.Update` | `0x3F6DC50` |
| `VRoid.Studio.EditRootScreen.ViewModel.set_EditMode` | `0x34189D0` |
| `VRoid.Studio.EditRootScreen.ViewModel.Save` | `0x341C440` |
| `VRoid.Studio.ModelPresetsSelector.BasePresetsSelectorViewModel.set_SelectedCategory` | `0x33147B0` |
| `...BasePresetsSelectorViewModel.UpdateSelectedPresets` | `0x3315770` |
| `...BasePresetsSelectorViewModel.ShowSelectedItem` | `0x33159A0` |
| `...CategoryViewModel.UpdateIsSelectedCategory` | `0x3320BC0` |
| `...ParameterCategoryViewModel.UpdateParameters` | `0x3324880` |
| `...PresetCategoryViewModel.UpdateParameters` | `0x3323070` |
| `VRoid.UI.SingleParameterViewModel.set_Value` | `0x37CD120` |
| `VRoid.UI.CombinedParameterViewModel.set_CombinedValue` | `0x37C9770` |
| `VRoid.Studio.TextureEditor.Color.ViewModel.ColorViewModelBase.set_Color` | `0x324D4A0` |
| `VRoidCore.Editing.Context.SetBoneTransformParameter` | `0x3C64760` |
| `VRoidCore.Editing.Context.ExecuteSyncCommand` | `0x3C648F0` |

`BasePresetsSelectorViewModel.EnterEditor` — abstract (no RVA);
`Context.ExecuteSyncQuery<T>` is generic, a Harmony patch won't install (IL Compile Error), use `ExecuteSyncCommand`.

Notes on resolution/safety:

- `AvatarCameraPosition.CameraRotationMode` — a **nested** enum (it has an empty namespace), so
  resolving by name via `Assembly.GetType` gives `null`; take it via
  `Interop.FindType("VRoid.UI.Component.AvatarCameraPosition").GetNestedType("CameraRotationMode", ...)`.
- Color: `SubColorSelection.get_Accessor()` → `EditableElementColorAccessor._keysToSet`
  (`TransferableGroupTypeAndKey[]`) — which transferable groups the color actually writes. One accessor
  can cover several groups (base skin color) — this is the "shared" color.
- The engine does **not** validate the values of `set_Value`/`set_CombinedValue`/`set_Color`: `NaN`/`Infinity`
  lead to an infinite rebuild. Validate at the plugin's input.

## 9. Event/live model

The plugin (with `dump_artifacts: true` in `servers.json`; off by default) writes JSONL to
`<app root>/ui_events.jsonl`. Event types:

| `event` | Source | Data |
|---|---|---|
| `set_EditMode` | `EditRootScreen.ViewModel` | `mode` |
| `set_SelectedCategory` | `BasePresetsSelectorViewModel` | `selector`, `category{type,name,tgtype}` |
| `UpdateSelectedPresets` / `ShowSelectedItem` | `BasePresetsSelectorViewModel` | `selected{...}` |
| `UpdateIsSelectedCategory` | `CategoryViewModel` | `category{...}` |
| `UpdateParameters` | `*CategoryViewModel` | `category{...}` |
| `set_Value` / `set_CombinedValue` / `set_NegativeValue` / `set_PositiveValue` | parameter VMs | `vm`, `label`, `value` |
| `set_Color` / `set_IsChecked` | `ColorViewModelBase` | `vm`, `label`, `color`/`checked` |
| `ExecuteSyncCommand` | `Context` | `command` (concrete type) |

Plus a main-thread ticker — our own `MonoBehaviour` (`VroidTicker.Update`) — the point of execution of
commands on the Unity main thread.

Exploration dump (only with `dump_artifacts: true`): `<app root>/catalog_<Full.Type.Name>.json`
(categories, parameters, presets) and the combined `category_catalog.json`.

`param_trace` (off by default) enables the expensive per-setter instrumentation: it writes
`<app root>/param_trace.log` (`EDIT`/`REFRESH` lines) and, when `Debug` is listed in the BepInEx
`LogLevels`, logs the same lines at `Debug`. It reflects on every value setter, so keep it off unless
diagnosing.
`dump_artifacts` (off by default) writes the catalog/event files (`ui_events.jsonl`,
`catalog_*.json`, `category_catalog.json`, `catalog_params.json`) — those are only
needed when the editor build changes and the catalog has to be re-verified, not for normal work.
`dialog_trace` (off by default) is the independent dialog-tracking devtool: it writes
`dialog_trace.log` — every `DialogProviderViewModel` Show/ShowNotification/Dismiss, plus every
custom-item save prompt that was **not** intercepted, each with the in-flight command and the
pre-armed-answer state (see [`quirks.md`](quirks.md) §Hair).

**Command servers** (both processed on the main thread via the ticker
`BepInEx AddComponent<VroidTicker>` + public `Update`, shared `CommandDispatcher`):

- TCP `127.0.0.1:17890` — request string → JSON string; strings with `{` are treated as JSON-RPC 2.0.
  Convenient via `telnet`/`nc`.
- MCP (Streamable HTTP) `http://127.0.0.1:17891/mcp` — `initialize`/`tools/list`/`tools/call`.

Methods (text and MCP tools): `state`, `sections`, `open <path>`, `create <female|male>` (new
character from a template; `MainViewModel.InitializeCurrentFileAndMoveToEditRoot`, only from the start
screen), `categories <section>` → `{section, categories:[...]}` (switching the section is a side effect,
`section` returns it), `enter <section> <tab>` (a single list of parameters: numbers + colors),
`set_param <section> <tab> <index> <value>`, `presets <section> <tab>` → `[{index,name}]`,
`preset_preview_list <section> <tab> <idx,idx,...> [mode]`,
`preset_preview_range <section> <tab> <start> [count] [mode]` (`mode` includes `b64`),
`select_preset <section> <tab> <idx> [params]` (idx 0 = clear; `params=true` adds a payload
of parameters, like `enter`), `camera …`, `screenshot`, `save`,
`main_menu`, `exit save|discard`, `test <name> <path>`.

Dev commands (**TCP only**, not exposed in MCP, the client doesn't know about them): `labels` (re-read
overlay, `{dir,count,files}`), `label_set <section> <tab> <idx|resourceName> <text>`,
`presets_full <section> <tab>` (like `presets`, but with `resourceName`/`itemId` — for the VLM harness),
`haircustom list <section> <tab>` / `select <section> <tab> <index>` / `create <section> <tab>` /
`save|saveasnew <section> <tab> <index>` / `import <section> <tab> <path> [name] [wear=now|later]` /
`export <section> <tab> <index> <path> [save=true|false] [overwrite=true|false]` /
`rename <section> <tab> <index> <name>` /
`tab <section> <tab> <presets|custom>` (saved custom presets of a category's Custom sub-tab — see §12),
Base64 previews for the VLM harness are served by the public `preset_preview_range … b64` (inline in JSON,
without writing files; count ≤ 12).

General debug command (domain-independent): `hook <Full.Type.Name> <Method>` / `unhook …` / `hooks` —
attaches the same method logger to arbitrary engine methods at runtime, so unknown call paths can be
explored without rebuilding. Avoid hot getters (see §10).

**Preset labels** (`PresetLabels`, `Runtime/PresetLabels.cs`): VRoid does not provide human-readable
names, so `presets` puts into `name` a label from an external overlay, otherwise the resource id; Clear —
`"(clear)"`. For the client this is just `name`; the source and mechanics are not exposed externally. The
source — `<app root>/labels/*.json`, a flat map `{resourceId: label}` (resourceName, fallback itemId;
a wrapper `{"labels": {...}}` is allowed). Files are merged in ascending name order (numeric prefixes,
Linux conf.d), the last one wins; `label_set` (addressing by index or resourceName) writes
`99-user.json`, an empty `text` — a tombstone. `labels` re-reads the directory.

**VLM labeling of labels** (`tools/vlm-labeler/`): a standalone .NET console, talks over TCP
JSON-RPC (`presets_full` → `preset_preview_range … b64`) for unlabeled presets, sends the previews to an
OpenAI-compatible vision API and writes the result to a per-category file
`labels/10-<section>-<category>.json` (prefix `10` — curated/`99-user` override it). VLM —
single-threaded; the source of truth for automatic labels is these files.

Screenshots are saved in variants: `screenshot.png` (canonical), `screenshot_raw.png`,
`screenshot_vflip.png` (for debugging orientation).

## 10. Observations / pitfalls

1. **`UpdateIsSelectedCategory` is mass.** It is called for *every* category in order to
   update the selection flag. The current category cannot be determined from it. Use
   `BasePresetsSelectorViewModel.set_SelectedCategory` (or `SelectedCategory`).
2. **The catalog is written immediately upon opening a character** — the editor creates all 4
   `PresetsSelectorViewModel` in advance; presets and parameters are initialized immediately, not lazily.
3. **Presets in Hair/Clothing change other presets, not parameters.** A Hairstyle Set
   switches several presets at once in other categories. `Overall Hair` and `Front`+`Back`
   are mutually exclusive. Hair color parameters are available **only with a selected preset**
   (an empty preset = "absence", encoded by `ClearOptionViewModel`).
4. **Two different "edit" paths.**
   - **Single parameters** (`BlendShapeTransformParameterViewModel`, `BoneTransformParameterViewModel`)
     and **colors** (`ColorViewModelBase`) are changed by the UI directly through VM setters:
     `SingleParameterViewModel.set_Value` / `ColorViewModelBase.set_Color` → this is `EDIT`.
   - **Combined parameters** (`Combined*ParameterViewModel`: Eye Size X, Nose Size, …) are changed by
     the UI not through the VM, but by an engine command: `VRoidCore.Editing.Context.ExecuteSyncCommand(SetBoneTransformParameterCommand)`.
     After that the engine calls `UpdateValue()` on the VM, which internally invokes
     `set_NegativeValue`/`set_PositiveValue` → this is `REFRESH`, not an edit.
   - Conclusion: detect the fact of an edit on `Context.ExecuteSyncCommand` (the concrete command type) /
     `Context.SetBoneTransformParameter`; VM setters are suitable for single/color. The field
     `ui_events.jsonl.kind` (`edit`/`refresh`) distinguishes this; `param_trace.log` writes the stack
     (both files — `ui_events.jsonl` with `dump_artifacts`, `param_trace.log` with `param_trace`).
5. **The Il2CppInterop proxy** returns the base/interface type in `GetType()`. The concrete type
   is obtained via the native class:
   `IL2CPP.il2cpp_object_get_class(ptr)` + `il2cpp_class_get_name/namespace` →
   rewrap via `Il2CppObjectBase.TryCast<T>()`. Traversal of il2cpp collections should be done via
   `Count`+`get_Item`; do NOT use `IEnumerator.Current` (there was an `AccessViolation`).

## 11. Reproduction

```bash
# BepInEx 6 be.788 (Unity.IL2CPP win-x64) with the .NET 6 runtime bundled (dotnet/ folder).
# Unity base libs: https://unity.bepinex.dev/libraries/6000.0.62.zip

# 1. Install BepInEx into the app directory and launch the app once so that
#    BepInEx generates <app>/BepInEx/interop/*.dll:
#      Windows        -> launch VRoidStudio.exe
#      Linux/macOS    -> wine VRoidStudio.exe

# 2. Build the plugin (BepInEx references come from the app directory, GameDir, default run/game):
dotnet build plugin/VroidStudioMcp/VroidStudioMcp.csproj -c Release -p:GameDir=<app>

# 3. Deploy:
cp plugin/VroidStudioMcp/bin/Release/VroidStudioMcp.dll <app>/BepInEx/plugins/
```

Useful utilities:

- `tools/index_dump.py output/il2cpp/dump.cs output/il2cpp/index` — indexes types/methods.
- `tools/q.py ns|find|type|members ...` — search over the index.

## 12. Separate editors: Hairstyle and Outfit

Unlike Face, presets here are not combinations of parameter values.

### Hairstyle

- Observation: `Hairstyle Set` switches several presets at once in other categories
  (`Front`, `Back`, `Overall Hair`, …), rather than setting parameter values.
- `Overall Hair` is mutually exclusive with `Front`+`Back`. An empty preset = "absence",
  encoded by `ClearOptionViewModel` (in `PresetOptions`).
- Hair color parameters are available only when a preset is selected in the category.
- Design editor: `VRoid.Studio.HairEditor.DesignEditor.ViewModel`.
  - Property tree: `HairEditor.DesignEditor.Property.ViewModel.PropertyViewModelBase.Groups`
    (`GroupViewModel`) → parameters.
  - Parameters: `HairEditor.DesignEditor.Parameter.ViewModel.ParameterViewModelBase` →
    `FloatParameterViewModel`, `IntParameterViewModel`, `EnumParameterViewModel<T>`,
    `CurveParameterViewModel`, `Overriding*`, `MaterialParameterViewModel`.
    Fields: `TranslatedDisplayName`, `Value`, `MinValue`, `MaxValue`.
  - Materials/colors: `HairEditor.DesignEditor.Material.ViewModel.MaterialNodeViewModel`,
    `MaterialColorNodeViewModel`, `SubGroupNodeViewModel`; color — `ColorViewModelBase`.
  - Material parameter: `MaterialParameterViewModel.MaterialNodes` / `SelectedMaterial`.

### Outfit

- `VRoid.Studio.ClothingEditor.ViewModel` + `InstanceItem.ViewModel.InstanceItemViewModel`.
- Path to the VM: `ClothingEditor.PresetsSelectorViewModel._editRootScreen` (`EditRootScreen.ViewModel`)
  → property `ClothingEditor` → `SelectedInstance` (`InstanceItemViewModel`) →
  `BlendShapeParameters` (`BlendShapeTransformParameterViewModel[]`). `SelectedInstance` is switched
  when the tab changes (`set_SelectedCategory`); empty if the model has no item of this category.
- The same slider label may repeat across mesh parts (body/sleeves) — `enter` separates
  such `path`s with a ` #N` suffix; addressing by short name is then ambiguous (need `path`/`index`).
- After selecting a preset, a property/fitting panel opens with the material and parameters.
- Material: `VRoid.Studio.XWear.Edit.ModelEdit.MaterialProperty`;
  material parameters: `XWear.Edit.ModelEdit.MaterialParameter.MaterialParameterViewModelBase`
  (`MaterialInfoViewModel`, `MaterialTextureParameterViewModel`).
- Other properties: `ContentPropertyBase` (`BlendShapeProperty`, `MaterialProperty`,
  `BoneControlPropertyBase`, `AutoFittingProperty`, …).
- Example of a real float clothing parameter: `Skirt Flare` (`BlendShapeTransformParameterViewModel`)
  → changed via `SingleParameterViewModel.set_Value` (`EDIT`).

The plugin maintains a registry of parametric VMs (hooks of concrete constructors of inheritors of
`ParameterViewModelBase` / `MaterialParameterViewModelBase` / `ColorViewModelBase`) and
(with `dump_artifacts: true`) dumps it to `<app root>/catalog_params.json`.

## 13. Settings dialog (Mux UI) and the Programmatic Control section

The stock settings dialog (account icon → Settings) is an XAML/Mux tree, not uGUI-by-hand.

- View classes (all `: RectTransform` from `Mux.Markup`):
  `VRoid.Studio.AccountIcon.SettingDialog.SettingDialogTransform`
  (+ `LanguageSettingTransform`, `ThemeSettingTransform`, `PreviewQualitySettingTransform`,
  `TextureResolutionSettingTransform`, `UIScalingTransform`, `CameraSettingsTransform`,
  `PalmRejectionTransform`, `ManageAdConsentSettingsTransform`).
- VM: `VRoid.Studio.AccountIcon.SettingDialog.ViewModel.SettingDialogViewModel`
  (`DialogViewModelBase`); opened via the private
  `VRoid.Studio.AccountIcon.ViewModel.OpenSettings()` (also `SetupAccountMenuItemDataSources` there).
- The XAML is compiled (`[XamlImport] InitializeComponent`), not loaded as text; there are no reference
  fields in `SettingDialogTransform` — the tree is built in code.
- **Injection point:** `SettingDialogTransform` inherits `TitledScrollableDialogTransform`,
  which has the property `ScrollableDialogContent` (`Mux.Markup.RectTransform`). Its `Body` is
  the content `UnityEngine.RectTransform`; on it hangs `UnityEngine.UI.VerticalLayoutGroup`,
  and child Mux rows are positioned by this layout (Mux sets their RectTransforms).
  Therefore one's own row is an ordinary uGUI child with a `LayoutElement`; `SetAsFirstSibling()` puts it
  at the top of the list, `SetSiblingIndex(n)` — after any other section.
- **Native controls** (for cloning, to match the style): `VRoid.UI.View.CheckToggleTransform`
  (radio), `VRoid.UI.View.CheckBoxTransform` (square checkbox — present in the dialog but inactive),
  `VRoid.UI.View.LabelTransform`/`Text`, `TextFieldTransform`/`NumberInput`. Cloning is easiest
  via `UnityEngine.Object.Instantiate(go, parent)`; runtime listeners (`onValueChanged`/`onEndEdit`)
  are **not** copied on `Instantiate`, so the clone is "clean".
- **UI screenshot:** the regular `screenshot` takes the RT of the 3D camera `NormalLayerCamera` (preview without UI,
  size-resilient; if absent — a crop of the center of `MainCamera`). The full window frame (with UI) is given by
  `ScreenCapture.CaptureScreenshotIntoRenderTexture` (in Wine/DXVK — with a Y flip). The dev command
  `uiscreenshot [inline|file|both]` (TCP only) returns base64/the file `ui_screenshot.png`.

**Service config:** a separate JSON `BepInEx/config/com.kokokoshka.vroidstudiomcp.servers.json`
(`{enabled,tcp,mcp}`), not via Unity settings. Stock settings are fixed XML schemas
(`VRoidStudio.Application.UserPreferences.ApplicationPreferences` + `VRoid.UI.EditorOption.Preference`,
all fields `[XmlElement]`), and they cannot be extended with plugin fields without rebuilding the app; a separate
file is more correct and does not conflict with their saving.

## 14. Host ↔ app paths (Wine)

On Windows the app process uses Windows paths natively and no translation is needed. Under Wine the
plugin runs in a Windows process but the host (and the MCP client) uses POSIX paths. To keep file
operations transparent, `CommandDispatcher` accepts both host-POSIX (`/home/...`) and Windows
(`Z:\...`) paths, and the file commands (`open`/`save`) return the host path.

- Conversion — `wine_get_dos_file_name` / `wine_get_unix_file_name` (exports of `kernel32.dll`).
  The symbols are taken **dynamically** (`GetModuleHandleW` + `GetProcAddress` +
  `Marshal.GetDelegateForFunctionPointer`): on real Windows they are absent, so a static
  `DllImport` is unacceptable. Detecting Wine — by these exports or `ntdll!wine_get_version`;
  outside Wine `IsWine=false` and both functions are no-ops.
- The returned Wine buffers are freed with `HeapFree(GetProcessHeap(), 0, ptr)`.
- Fallback without the API — the heuristic "`Z:` = host root" (`/x` ⇄ `Z:\x`).
- Implementation: `Runtime/WinePaths.cs`; check — `test pathtest`.
- The MCP client and the app must be on the same machine: `initialize.instructions` report the style of
  host paths (Wine → POSIX, otherwise Windows). A remote client is formally possible (the address is configurable),
  but the paths diverge and file commands are meaningless — this is not supported.

## 15. VRM export (experimental; runtime UNVERIFIED)

`export_avatar` wraps the existing native export-screen coroutine, scoped to Windows app 2.16.0.
Signatures were inspected in the installed IL2CPP interop assemblies; build success is not runtime
acceptance. No app restart, source save or native export was used to validate this implementation.

- Transition to the export screen: `VRoid.Studio.EditRootScreen.ViewModel.EnterToVRMExport()` (opens
  `VRMExportScreen`).
- Screen: `VRoid.Studio.VRMExportScreen.ViewModel`.
- Export engine: `VRoidStudio.Exporter.VRoidExporter`, `VRoidStudio.Exporter.VRoidStudioExportUtil`,
  `VRoidStudio.Exporter.ExportAvatarContainer`.
- Settings: `VRoidStudio.PhotoBooth.ExportSettings` (atlas, texture compression, reduction…).

The concrete adapter is `VRoidStudio.GUI.AvatarEditor.PhotoBooth.PhotoBoothViewModel.ExportVRM0/1`:

```text
IEnumerator ExportVRM0/1(string exportPath, glTF_VRM_Meta/VRM10ObjectMeta meta,
    bool isEnableHairCombination, bool shrinkTexture, MaterialReductionMode materialReductionMode,
    bool runMeshOptimization, Action onSuccess, Action onFailure, Action<double> onProgress)
```

Run the returned native enumerator on the existing `PhotoBoothCoroutineExecutor` with Unity's own
`StartCoroutine`; do not manually tick it (nested coroutines/render waits must retain native semantics).
The native success/failure callbacks are the completion evidence. `VRoidStudioExportUtil` provides
an export-avatar container/clone pipeline; calling the lower-level `VRoidExporter` against the edited
avatar directly could mutate skeleton/material state and is deliberately avoided.

The tool captures `VRMExportScreen.ViewModel.OnScreenActivate/Deactivate`, requires initialized
`ObsoleteLegacyPhotoBoothViewModel.V1PhotoBooth`, rejects rebuilding previews and native dialogs,
and does not navigate or modify `ExportSettings`. Metadata is newly constructed from explicit client
fields; permission and license decisions have no plugin defaults. Export uses combined hair with
no texture shrinking, mesh optimization or material reduction. Native thumbnail, eye and spring
handling remain owned by the existing screen context.

Export writes a unique same-directory temporary `.vrm`; only native success plus a non-empty regular
file allows publication to the requested path. Overwrite defaults false. A callback timeout never
publishes late output: the operation is marked abandoned and its temporary output is removed on a
late callback. Native work is not canceled; the command queue rejects everything except `state` and
`ping` while it remains active. MCP `status.native_export_busy` also works outside that queue. A
native coroutine that dies without a callback leaves the guard closed; safe cancellation/recovery
is unresolved. Source preservation, coroutine cleanup, all failure paths and actual VRM consumer
acceptance require runtime checks on disposable data before production use.
