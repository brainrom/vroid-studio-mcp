# Hairline placement investigation (VRoid Studio 2.16.0)

This follows PR #5 at `4dfd9faff9cd06afe1a7d2e8a9b00eb717d1636f` and adds
bounded guide-origin translation. Native placement and persistence were verified,
but visual fitting failed overall. Translation alone does not repair arbitrary
hairstyles or shape individual strands.

## Findings and evidence boundaries

- **Confirmed:** `add` deep-copies a selected group's Guide and reuses its transform
  and transferable. It calls `AddProceduralHairGroupCommand` with a material and name,
  but does not copy source strands or parameter sets. The inspected source was a
  brush guide with eight raw/offset points. Structural validity does not establish
  suitability for procedural generation.
- **Confirmed:** inspection showed procedural defaults including `GrowFrom=0`,
  guide Height=0.4 and Offset=0.01. The source guide had the same Height and Offset,
  so a mismatch in those defaults was not established as the cause. Native addition
  starts with six hairs. Count edits and the new group/material persisted through
  save/reopen; pre-existing inspected groups and strands were preserved.
- **Confirmed limitation:** the earlier writable surface exposed count, length,
  width and two angles, but no guide placement or strand shaping. Three initial
  fitting attempts showed crown seams/intersections or straight strips despite
  successful numeric readback and completed rebuilds.
- **Inference:** a customized brush guide plus default procedural roots/shape may
  be unsuitable for the attempted settings. Guide suitability and parameter choices
  remain plausible contributors. There is no evidence of a transport failure or
  proof that the native add command omitted required initialization.
- **Unresolved:** native UI creation sequence and default-guide construction,
  procedural root mapping, physical coordinate calibration, and the need for
  guide-point or individual-strand shaping.

## Native API trace

The inspected 2.16.0 interop assemblies were:

| Assembly | SHA-256 |
| --- | --- |
| VRoidCore.dll | `785734b3346292b152ed423d547bf94e4b7affc94815b2ed2d86ab2e92aa3ce3` |
| VRoid.Studio.dll | `ed95fad4be05cebad8e3016dd10cdc8da4f1a6e17d895820c31642f783e70f69` |

`AddProceduralHairGroupCommand` accepts transferable ID, transform ID, localized
name, shared material path and Guide. `ProceduralHairGroupPropertyViewModel` is
bound to a procedural group path and Guide reference. Its `SetGuide` accepts a
GuideParameterSet modifier. UI parameter closures dispatch opaque native tokens;
their labels and slider domains cannot be recovered from these wrappers alone.

`SetGuideOriginTransformCommand(IHairGroupPath, Transform)` stores a target group
path and transform and provides native rollback/roll-forward history methods.
`Guide.Origin` uses `VRoidCore.Common.Types.Transform`, whose fields are Position
(Vector3), Rotation (Quaternion) and Scale (Vector3). The implementation passes an
updated Position through that history command, preserving Rotation/Scale.
It does not write raw guide buffers or call model-internal methods.

These are **signature/layout observations**, not recovered native implementations.
The control uses raw stored coordinates; world-space directions and physical units
are uncalibrated. The 0.02 per-call axis limit is tool policy, not a measured native
safety guarantee.

The [official hair editor guide](https://vroid.pixiv.help/hc/en-us/articles/360012339194-Hair-Editor)
and [freehand guide](https://vroid.pixiv.help/hc/en-us/articles/360013258473-How-to-draw-stray-hair-with-freehand-groups)
describe guide/strand control points. They do not establish the 2.16.0 history-command wiring.

## Build and runtime validation

The tested implementation is `7dc218b7e0c3a041c034cc21f41954d12111117b`, on
Windows VRoid Studio 2.16.0. Release compilation completed with zero warnings/errors;
whitespace checks passed and independent source review found no actionable issues.
The loaded module identity was confirmed. Runtime checks used disposable models.
Validation assets and captures are not distributed with the repository.

| Check | Result and boundary |
| --- | --- |
| Invalid origin inputs | **VERIFIED:** NaN and a 0.03 axis displacement were rejected, with origin readback unchanged. This is not an exhaustive invalid-input matrix. |
| Position axes and readiness | **VERIFIED:** X, Y and Z writes returned the requested stored coordinates and ready completion. Rotation and scale were preserved. Delayed/timeout cases remain unverified. |
| Group/material isolation | **VERIFIED:** existing group inspection records matched after reopening, except their list indices; existing group/strand counts and material inventories were preserved. Each test added one group with two strands using an existing material. This is not a raw mesh/vertex audit. |
| Save/reopen | **VERIFIED:** inspected group state, including translated origins, persisted through native save/reopen on two models. |
| Scalp-edge coverage | **VERIFIED in limited captured views:** one trial covered the exposed edge in front and both three-quarter views, with the outfit and hairstyle silhouette retained. This narrow observation did not establish acceptable overall fitting. |
| Overall visual fitting | **FAILED:** both evaluated fits were ultimately unsuitable. One retained stiff tips/crown transitions across three attempts. Native command success and local coverage do not establish a usable hairstyle. |
| Eye preservation | **VERIFIED on one model:** iris RGBA pixels and inspected eyelid state were unchanged after reopening. Face-parameter equality was not re-read in this pass. |

Earlier scalar-control validation is recorded in
[hair geometry validation](hair-geometry-validation.md). No broad regression suite
was run. This documentation cleanup does not add new runtime evidence.

## Diagnostics and remaining limits

Open/save watchdog messages at 16/19 seconds accompanied operations that completed
in 16.846/21.397 seconds. An earlier save logged watchdog messages at 15/20 seconds
and completed in 21.62 seconds. These messages did not establish a persistence
failure. Analytics DNS failures, legacy-preference fallback and binding warnings
were also observed. Native BuildModifier disposal warnings remain unexplained;
logs are not claimed clean, and none of these warnings establishes the fitting cause.

**UNVERIFIED:** native undo/redo replay, forced partial-application/timeouts,
delayed-command behavior, other platforms and rendering in downstream engines.
Immediate readback precedes readiness; delayed native application could return a
partial-mutation error. Inspect state before retrying.

**Unsupported:** guide/strand control-point sculpting, root placement mapping,
physical/world-axis calibration, rotation/scale writes and `GrowFrom` writes.
Guide-origin translation cannot change individual root or tip shapes. The native
freehand/strand control-point command path remains a possible investigation, not
a verified fitting solution.
