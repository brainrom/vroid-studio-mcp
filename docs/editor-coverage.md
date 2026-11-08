# Bounded native editor coverage audit

This audit compares the existing public MCP surface with the installed Windows VRoid Studio
2.16.0 interop metadata. Method availability is static evidence; it is not runtime verification.
It is not a screen-by-screen claim of full editor coverage. Runtime outcomes belong in the
PR validation record. Generated interop files, purchased assets and private character models
are not distributed with this repository.

| Area | Existing coverage | This change | Remaining boundary |
| --- | --- | --- | --- |
| Hair | Presets, custom-item libraries, shared colors | Native material catalog, properties and group assignments | Strand/guide geometry editing, bones and gizmos are not added |
| Irises | Presets and left/right colors | Native iris raster PNG import/export, preparation and layer visibility/opacity | Brows, eyeliner, lashes, highlights and other face texture channels are not exposed by this tool |
| Eyelids | Stock presets | Native eyelid custom-item import/export; raster inspection/export, visibility and opacity | No other Face custom-item categories, raster import, preparation or tree restructuring |
| Materials | Global colors | Create, duplicate, rename, supported material colors/floats | Arbitrary Unity shader properties and unsafe reflection are not public tools |
| Textures | Model/preset screenshots | Native editable raster-layer PNG import/export and inline layer previews | No brush painting, masks, layer tree restructuring or composited UV preview |
| Layers | None | Native visibility, opacity and supported composition modes | No destructive merge/reorder/delete operations |
| Custom items | Full-item import/export | Source material inspection and bounded material-only transfer | Source hairstyle is never worn as a fallback |
| Camera | Reset, frame, angles, zoom, pan, projection, custom view, probe | Existing controls reused | No evidence requiring a new camera operation for this task |
| Model files | Open/create/save and explicit menu/exit | Model path and unsaved state in diagnostics | No new whole-avatar VRM export/dialog automation |
| Diagnostics | Queue status, opt-in traces and development hooks | Process ID, app version, loaded assembly ID | Public arbitrary internal-method invocation remains excluded |
| History | Native edit history, no public undo/redo | Exact recent-entry removal and capacity-guarded restoration, never file deletion | Recent models are separate from undo history; undo/redo MCP tools deferred |

## Native evidence and design constraints

- `GetAllMaterialsQuery`, `AdvancedEdit.GetHairMaterialQuery` and
  `GetEditableImageRasterLayersQuery` expose material/slot/layer information.
- `CreateMaterialCommand`, `DuplicateMaterialCommand`, `SetHairMaterialNameCommand`,
  `SetMaterialColorCommand` and `SetMaterialFloatCommand` provide bounded native edits.
- `AdvancedEdit.BeginHairMaterialAdvancedEditCommand` initializes advanced editable layers.
  `LoadImageToEditableImageRasterLayerCommand` and `LoadImageAsNewRasterLayerCommand` keep
  edits in native model/history data. A texture assigned only to a Unity renderer would not
  establish persistence and is not used.
- Group material assignment changes the native `HairMaterialReference` through the group
  parameter command. No geometry-generation or preset-replacement command is used.
- Layer properties use `ModifyEditableImageLayerTreeParameterSetCommand`; values are checked
  against known types and returned native blend names.
- `SharedMaterialDuplicateUtil.DuplicateSharedHairMaterial` supports dependency-aware copying
  between collections. The transfer implementation must also preserve history and model events;
  calling the utility directly against the live collection is insufficient.
- `ModelFileAccessHistory.GetPaths/Remove` supplies exact-entry cleanup. Model files and other
  history entries must remain intact.

All indices are temporary native catalog positions. Refresh lists after editing. Readiness
requires a completed native model-rebuild signal for mutations; an elapsed timeout alone is not
completion. A command may have applied before a readiness timeout, and clients must inspect
before retrying. Multi-instance callers should bind operations to `state.process_id` and
`state.plugin_build`, and preserve unsaved work before reloading BepInEx.

The tested background 2.16.0 renderer retained old group materials until explicit save/reopen.
An update-complete event is not visual proof. A blanket refresh did not fix this; targeted
presentation-cache invalidation is deferred pending bounded runtime diagnosis. See the
[validation record](hair-material-validation.md) for tested and unverified behaviors.
