# Iris texture validation — VRoid Studio 2.16.0

Validated on Windows against the installed 2.16.0 IL2CPP interop assemblies on
2026-11-08. This is a bounded native-editor extension, not a claim of complete face
texture coverage. Validation assets are not distributed with the repository.

| Behavior | Status | Evidence |
| --- | --- | --- |
| Native iris discovery and layer inspection | VERIFIED | Three existing editable models; unique native iris target, three raster layers, disabled color overlay |
| Prepare on an already editable image | VERIFIED | No-op response, no model mutation |
| Prepare an asset-backed iris image | UNVERIFIED | The trial models already contained editable images |
| Replace a selected raster layer | VERIFIED | Native import of three 1024x512 iris PNGs; exact RGBA export comparison after save/reopen |
| Add a new raster layer | UNVERIFIED | Implemented native command, not exercised in this bounded run |
| Visibility and opacity | VERIFIED | Readback after false/true visibility and 0.75/1 opacity changes |
| Invalid index and non-finite opacity | VERIFIED | Layer 999 and NaN rejected; clean model remained clean |
| Save/reopen persistence | VERIFIED | All three trial files reopened; selected iris and 512x1024 hair layers exactly matched source pixels |
| Face, outfit and hair preservation | VERIFIED within inspected views | Comparable actual front, face and full-body renders inspected; hair group paths, material assignments and strand counts unchanged |
| Undo/redo and other native layouts/platforms | UNVERIFIED | No replay or cross-version suite run |
| Brows, eyeliner, lashes and eye-highlight texture editing | SKIPPED / unsupported | Deliberately outside this iris-only tool |

The iris resolver uses the native category registry and nested `IrisTextureInfo`
type. Early discovery failures were fixed by preserving declared nested array
element types, using native `TryCast`, and copying native enumerable keys into a
typed list. These failures returned errors before applying edits. Independent
review identified the nested-type and enumerable-key assumptions.

Release build completed with zero warnings/errors. Focused checks covered discovery,
pixel equality and preservation; no broad regression suite was run.

Diagnostics are not all failures: intentional invalid-input responses are expected;
the save watchdog logged error-level 16/21-second messages during successful native saves that
completed in approximately 23 seconds. Save/reopen receipts confirm persistence.
App logs also contained analytics DNS failures, pre-existing binding warnings and
native BuildModifier disposal warnings; the disposal warning's cause remains
unverified. The plugin error file was empty apart from its UTF-8 marker.

`preview_verified:false` remains intentional. A completed native rebuild is not
rendered acceptance; explicitly save/reopen and inspect actual renders. See
[the API contract](external-api.md#iris-texture-editing) and
[hair material validation](hair-material-validation.md) for the separate hair
binding refresh limitation.
