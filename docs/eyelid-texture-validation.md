# Eyelid layer validation — VRoid Studio 2.16.0

Validated on Windows on 2026-11-08 against the installed 2.16.0 IL2CPP interop
assemblies. This extends the existing native custom-item route to Face/Eyelids
only and adds `eyelid_texture`; it does not imply general face-texture coverage.
Validation assets and captures are not distributed with the repository.

| Behavior | Status | Evidence |
| --- | --- | --- |
| Import and wear an eyelid custom item | VERIFIED | Native import resolved Level2.Eyelid and an editable image containing 40 raster layers; reused the one library item on three models |
| Inspect/export editable eyelid raster layers | VERIFIED | All 40 source layers exported; selected 1024x256 crease pixels matched exactly after each model was saved and reopened |
| Visibility and opacity | VERIFIED | Three different visible-layer selections and finite opacity values persisted after save/reopen |
| Missing item, invalid index and NaN | VERIFIED | Clear errors for no selected eyelid item, layer 999 and non-finite opacity |
| Face category and imported-root guards | VERIFIED | Skin category and a non-eyelid custom-item root rejected before library import; no unrelated library files created |
| Base-hair colour | VERIFIED | Existing `set_param` changed only Base Hair/Main Color; three chosen colours read back after save/reopen |
| Preservation | VERIFIED within inspected scope | All 92 exposed face parameters unchanged per model; hair group paths, material references and strand counts unchanged; iris RGBA pixels unchanged |
| Visual result | VERIFIED within front views | Actual comparable face and full-body renders inspected after reopening; hairline colours matched, distinct crease styles visible, outfits and hairstyle silhouettes intact |
| Face/Eyelids custom-item export | UNVERIFIED | Existing export route enabled for this category; no custom-item export check run |
| Undo/redo, other native layouts and platforms | UNVERIFIED | Native history command used, but replay and cross-version checks were not run |
| Asset-backed preparation, PNG replacement and blend/reorder/mask editing | SKIPPED / unsupported | Deliberately outside the eyelid tool; an already editable native eyelid item is required |
| Other Face custom-item categories | SKIPPED / unsupported | Category and root-type checks reject them |

The resolver reads native texture-editor metadata, requires a unique eyelid group,
descriptor and image, and checks that the image belongs to that group. Layer
changes use the native history command. A mutation response waits for a completed
native rebuild; `preview_verified:false` still requires inspecting an actual
render. Exports use a new destination and reject oversized native rasters.

Independent source review reported no actionable findings. Release compilation
completed with zero warnings/errors. Focused checks covered the behaviors listed
above; no broad regression suite was run. Original model hashes remained unchanged.

Final visual comparisons used matching camera state and viewport after native
rebuild completion. A mismatched initial capture was excluded from the comparison.

Expected negative-input errors are recorded separately from application issues.
The save watchdog logged 15/20-second warnings at error severity during a save
that completed in 22.61 seconds and reopened successfully. Application analytics
DNS failures and Xamarin binding warnings were also present. Native BuildModifier
disposal warnings have an **unverified cause**; the logs are not claimed clean.
The plugin ErrorLog contained only its UTF-8 marker.

See [the API contract](external-api.md#eyelid-texture-layers),
[iris validation](iris-texture-validation.md), and
[editor coverage](editor-coverage.md) for adjacent boundaries.
