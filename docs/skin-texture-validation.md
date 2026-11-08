# Native skin texture layers

`skin_texture` targets `face` or `body` independently. Inspect first; prepare only if the image is asset-backed. Imports without a layer index add a raster layer. Existing layers are retained. Refresh inspection after adding layers, then set `name`, `visibility`, or `opacity`. Export writes a single layer to a new PNG.

Preparation uses the native skin-specific advanced-edit key with `applyColors=false`; shared skin colors and geometry are not changed. PNG imports are bounded to 64 MiB and 4096 pixels per side. Layer indexes are session-local.

## Validation

Release compilation succeeded without warnings. Independent source review identified action whitespace normalization, which was corrected.

In VRoid Studio 2.16.0, face and body inspection, adding 15 PNG raster layers, naming them, toggling visibility, and saving/reopening an editable model succeeded. Actual before/after renders were inspected. Original layers remained present and hidden; the selected base layer remained visible after reopening.

Asset-backed preparation, undo/redo, opacity changes, PNG export, other versions/platforms, and invalid-input runtime checks remain unverified. Existing editable images were used for the runtime check. Native command readiness is not visual acceptance; inspect a render separately.
