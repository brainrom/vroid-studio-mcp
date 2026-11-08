# Issue 1 follow-up validation

Scope: native height readout, global hair colour warnings, grouped parameter updates.

- Build against Windows VRoid Studio 2.16.0 interop: zero warnings/errors.
- Validation ran in a background test instance with application `-noaudio`.
- Disposable stock female model: native height 159.7701416015625, label 159.8cm, unit cm.
- Two iris-size parameters applied as 0.2 and 0.3 through set_params; readback matched.
- Batch containing a later NaN was rejected before all parameter setters; earlier values remained 0.2 and 0.3.
- Focused checks only; no broad regression suite was run.
- Hair-warning runtime check SKIPPED: stock model had no hairstyle selected. Warning source/schema reviewed.
- Independent review found readiness and error-detail gaps. Final source adds height readiness guard, pre-batch readiness gate, error on incomplete observed rebuild/idle state and applied-prefix details in error text. These corrections build but their new branches are runtime UNVERIFIED.
- No per-setter native completion token, native rebuild suppression or performance guarantee. Async update ordering, native exception/timeout paths, undo/redo, persistence and visible visual acceptance remain UNVERIFIED.
- Inner Top/Bottom removal remains unsupported: exact native layer ownership/navigation is unresolved.
- No model files were saved or custom items created during these checks.
- Logs include legacy missing-preferences fallback and analytics DNS failures; no claim that app logs are clean.

Validation artifacts are not distributed with the repository.
