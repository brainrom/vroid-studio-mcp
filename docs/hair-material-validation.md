# Hair material validation on Windows VRoid Studio 2.16.0

Validation uses BepInEx and the public HTTP MCP handlers. Validation assets and generated
native metadata are not distributed with the repository.
This is focused evidence, not full native editor coverage or a cross-version certification.

| Behavior | Result | Evidence and limits |
| --- | --- | --- |
| Release build | VERIFIED | Installed 2.16.0 interop references; zero warnings/errors |
| Catalog, material colors, slots, layers and group assignments | VERIFIED | Native reads against three existing hairstyles |
| Native material-only custom-item transfer | VERIFIED | Older source item, copied material plus dependencies; no source hairstyle worn or library item created |
| Assignment and model persistence | VERIFIED | Saved/reopened three separate trials; existing group paths and group/strand counts unchanged |
| Texture PNG export/import | VERIFIED | One 512x1024 layer, exact RGBA round trip, no vertical flip |
| Inline texture preview | VERIFIED | Public MCP preview returned a raster-layer image |
| Create, duplicate, rename and prepare | VERIFIED | New material inspected as asset-backed, prepared to editable layers, renamed and duplicated with readback |
| Supported float and layer visibility | VERIFIED | Normal-map weight and layer visibility changed with native readback on disposable unsaved data |
| Material color enable/value edits | VERIFIED | Saved/reopened base, outline and shade colors; disabled shared overlay/highlight flags preserved |
| Layer opacity and blend | VERIFIED | Native edits and restoration on disposable data; exhaustive blend rendering not tested |
| Invalid inputs | VERIFIED | Invalid material/group indices and non-finite float rejected without intended mutation |
| Recent-model cleanup/restoration | VERIFIED | Removed exact temporary entries and restored displaced entries without opening models; unrelated entries and model files preserved |
| Native preview after assignment | FAILED | Background 2.16.0 kept old rendered binding; explicit save/reopen produced correct appearance |
| Foreground-session rendering | UNVERIFIED | This pass exercised background rendering only |
| Native undo/redo of transferred material across model switches | UNVERIFIED | History integration and lifetime retention reviewed; replay not exercised |
| Other app versions/platforms | UNVERIFIED | Existing compatibility retained in source; no runtime checks outside Windows 2.16.0 |
| Brush painting, masks, layer reordering/merging, arbitrary shaders, VRM export dialogs | SKIPPED | Outside this bounded implementation; no unsafe internal-method tool exposed |

All trial images were actual native viewport captures at matching 2656x1920 resolution and
camera settings. Faces, outfits and visible hair silhouettes were inspected before/after.
Group IDs/counts and reference-only command semantics support geometry preservation; no
vertex-by-vertex mesh comparison was performed. Original model hashes were retained for
preservation checks. Overall artistic suitability remains separate from this verification.

Mutation results explicitly separate native update completion from visual acceptance.
The handlers now wait for a completed model rebuild, excluding unrelated preset-list signals.
After a timeout an edit may already have applied; inspect before retrying. Save/reopen and a
fresh screenshot are the supported workaround for the observed background preview cache.

Independent review found a detached import-probe retention leak and a possible readiness
attribution race; both were corrected. Expected input-validation errors are separate from
real implementation failures encountered and fixed during development. Native analytics DNS
and background-login errors remain app/network diagnostics, not proof of a material failure.
No new test framework, mocks or automated test suite was added.

Additional static review covered the asset-backed slot guard, import-probe cleanup,
rebuild-specific readiness and history restoration. Native history restoration was exercised
through the public tool. No full regression suite ran.
