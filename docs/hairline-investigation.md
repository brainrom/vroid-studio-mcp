# Hairline placement investigation (VRoid Studio 2.16.0)

> **Owner acceptance update (2026-11-08): FAILED.** The owner rejected the Gothic
> appearance too and requested reverting the added hair. Its separate trial was
> archived outside the character outputs; the three pre-hairline model files were
> verified unchanged. Idol's rejected addition had already been removed. Earlier
> visual inspection results below are historical and do not establish owner
> acceptance. Native placement/persistence evidence is unchanged; no further
> fitting is planned because the owner will edit manually.

This follows PR #5 at `4dfd9faff9cd06afe1a7d2e8a9b00eb717d1636f`.
The earlier addition succeeded technically and failed visually. This change adds
a bounded guide-origin placement control; it does not claim the rejected hairstyle
has been repaired or that translation alone can shape the required strands.

## Findings and evidence boundaries

- **Confirmed:** `add` deep-copies a selected group's Guide and reuses its transform
  and transferable. It calls `AddProceduralHairGroupCommand` with a material and name,
  but does not copy source strands or parameter sets. The retained trial used brush
  group 0, `[Front]_Hair_1`, with eight raw/offset guide points. Structural validity
  does not establish a suitable procedural guide.
- **Confirmed:** retained candidate 1/2/3 JSON shows procedural defaults including
  `GrowFrom=0`, guide Height=0.4 and Offset=0.01. The source brush guide parameter
  readback also has Height=0.4 and Offset=0.01. A mismatch in these two defaults is
  therefore not established as the cause. Native addition starts with six hairs;
  the retained saved trial was reduced to three. The existing 12 groups/56 strands
  were preserved; its new group and material survived save/reopen.
- **Confirmed limitation:** the previous writable surface had count, length, width
  and two angles, but no guide placement or strand shaping. All three retained front
  renders visibly contain crown seams/intersections or straight strips. Their
  numeric readbacks and completed rebuilds were not visual acceptance.
- **Inference:** a customized brush guide plus default procedural roots/shape is
  unsuitable for these attempted settings. Both guide suitability and artistic
  choices remain plausible contributors. There is no evidence of a transport failure,
  nor proof that the native add command omitted required initialization.
- **Unresolved:** exact native UI creation sequence and default-guide construction,
  procedural root scalar mapping, physical coordinate calibration, and whether a
  translated guide can achieve this particular fringe without point/strand shaping.

## Native API trace

Installed 2.16.0 interop assemblies inspected with the existing ilspycmd:

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
(Vector3), Rotation (Quaternion) and Scale (Vector3). The new surface passes an
updated Position through that public history command, preserving Rotation/Scale.
It does not write raw guide buffers or call model-internal methods.

These are **signature/layout observations**, not recovered native implementations
or execution evidence. The control uses explicitly raw stored coordinates; it
does not promise world-space directions or millimetres. Conservative displacement
bounds are tool policy, not a measured native safety guarantee.

The [official hair editor guide](https://vroid.pixiv.help/hc/en-us/articles/360012339194-Hair-Editor)
describes brush retouch and control points for shaping hair. The
[official freehand guide](https://vroid.pixiv.help/hc/en-us/articles/360013258473-How-to-draw-stray-hair-with-freehand-groups)
also uses guide and strand control points. These describe authoring workflows,
not proof of 2.16.0 history-command wiring. The older manual referenced in
`quirks.md` is absent from this checkout.

## Initial source/build validation (before the authorized retry)

Retained local evidence: `delivery-report.md`, `geometry-preservation.json`,
`Idol-geometry-candidate1/2/3.json`, `Idol-hairline-attempt1/2/3.png`,
`operations.jsonl`, `BepInEx-final.log`, and `Player-final.log` from the original
Lash-Hairline-20261108 task. Purchased models/images remain private.

- **VERIFIED (retained):** previous addition, scalar setters, material reference,
  group preservation and save/reopen. See `hair-geometry-validation.md` for limits.
- **VERIFIED (current build):** Release compilation against installed 2.16.0 interop,
  zero warnings/errors; whitespace check passed. Build success is not native execution.
- **VERIFIED (source review):** independent review found no actionable correctness
  issue in the diff and confirmed the installed constructor/field signatures and
  reflection compatibility. Immediate readback still precedes readiness: delayed
  native application could return a partial-mutation error and must not be retried
  blindly. This timing remains untested.
- **FAILED (retained):** Idol visual fitting; two corrective retries exhausted.
- **SKIPPED:** Military Gothic fitting and further visual attempts. Casual Girl
  and both final EX Hair/Mia Eyes trials are unchanged.
- **UNVERIFIED:** new origin setter invalid-input behavior, native rebuild signal,
  target isolation, material preservation, undo/redo and save/reopen persistence.
  Actual comparable front and three-quarter acceptance remains pending.

The previous ledger used 109.20611/180 seconds, leaving 70.79389 seconds. No fresh
behavior tests were run in this source/build investigation; no visual retry allowance
was inferred. Only the owner's 2.10 editor was running at preflight. A second editor
shares its profile, so this pass did not launch an app or open a model.
No new test framework was created. Before a later runtime pass, use backed-up
disposable models, record current history/library state, and verify invalid values,
one-axis changes with other transform components preserved, group/material isolation,
and persistence through native save/reopen. Reconcile the shared allowance and
obtain an explicit new visual attempt allowance before another fitting attempt.

Retained logs contain save watchdog messages at 15/20 seconds followed by a
successful 21.62-second save. Analytics/network and binding warnings are separate;
native BuildModifier disposal warnings remain unexplained. Logs are not claimed
clean, and none of these messages establishes the geometric cause.

## Authorized PR #6 runtime retry (2026-11-08)

The owner subsequently authorized another visual fitting round using PR #6. The
existing behavior allowance continued; it was not reset. This section supersedes
the initial runtime-unverified and fitting-skipped statuses above for the checks
explicitly listed here. The tested code is `7dc218b7e0c3a041c034cc21f41954d12111117b`.

The installed Release DLL was loaded in an agent-owned 2.16.0 instance and its
module identity confirmed. All edits used separate disposable copies. The owner's
running 2.10 editor was preserved; the task instance was closed after cleanup.

| Check | Result and boundary |
| --- | --- |
| Invalid origin inputs | **VERIFIED:** NaN and a 0.03 axis displacement were rejected, with origin readback unchanged. This is not an exhaustive invalid-input matrix. |
| Position axes and readiness | **VERIFIED:** X, Y and Z writes returned the requested stored coordinates and ready completion in the exercised workflows. Rotation and scale were preserved. Delayed/timeout cases remain unverified. |
| Group/material isolation | **VERIFIED:** all existing group inspection records matched after reopening, except their list indices; group/strand counts and material inventories were preserved. Each candidate added one group with two strands using an existing material. This is not a raw mesh/vertex audit. |
| Save/reopen | **VERIFIED:** both candidates' inspected group state, including translated origins, persisted through native save/reopen. |
| Gothic visual fitting | **VERIFIED in captured views:** candidate 2 covered the exposed scalp edge in matched front and both three-quarter views. Full-body comparisons retained the outfit and hairstyle silhouette. A separate editable trial was retained; owner selection is pending. |
| Idol visual fitting | **FAILED:** the initial attempt and two corrective retries retained unsuitable stiff tips/crown transitions. The candidate was rejected and its disposable file removed. The original model was unchanged. |
| Eye preservation | **VERIFIED for Gothic:** iris RGBA pixels and the inspected eyelid state were unchanged after reopening. Face-parameter equality was not re-read in this pass. |

The Gothic trial retains 8 original groups/52 strands and adds one group/two
strands (9/54 total). The rejected Idol trial retained 12 original groups/56 strands
and added one group/two strands (13/58 total). Source models, approved hairstyles,
original models and purchased-library inventory were preserved. All 20 pre-existing
recent-list members were restored; the separate new trial was not registered in
that capacity-limited list. Private assets, models and screenshots are not published.

Shared behavior verification used **173.96118/180 seconds**, leaving **6.03882
seconds**. The retry used 63.94070 seconds of runtime checks and 0.81437 seconds of
offline receipt checks, in addition to the prior 109.20611 seconds. Production
fitting and screenshot work are separate. No new test framework was added.
Local receipts include `preservation.json`, `verification-budget.json`,
`installation-final.json`, `operations.jsonl`, the three-angle Before/After renders,
and rejected-attempt renders under the private PR6-Hairline-20261108 evidence folder.

The plugin log contains open/save watchdog messages at 16/19 seconds; those
operations completed in 16.846/21.397 seconds. Analytics DNS failures are separate
from model commands. The app also logged a missing legacy BetaDocuments preference
file and explicitly fell back to defaults. Binding warnings and native
BuildModifier disposal warnings remain; the latter's cause is **UNVERIFIED**.
ErrorLog contains only a UTF-8 marker. Logs are not claimed clean.

**Still unsupported or deferred:** guide/strand control-point sculpting, root
placement mapping, physical/world-axis calibration, rotation/scale writes, undo/redo
replay, forced partial-application/timeouts, other platforms and Unreal appearance.
PR #6 provides useful translation, demonstrated on Gothic; it is insufficient to
claim general hairline fitting. For Idol, the next proposed investigation is the
native freehand/strand control-point command path, followed by a separately
authorized fitting round on a disposable copy. Translation alone cannot change
the shape of individual roots or tips.
