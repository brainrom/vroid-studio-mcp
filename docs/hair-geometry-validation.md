# Bounded hair geometry validation — Windows 2.16.0

Validation covers bounded procedural-group addition, scalar edits and preservation.
Native command success and visual fitting are separate outcomes.

## VERIFIED

- Release build against the installed 2.16.0 interop assemblies: zero warnings/errors.
- Public MCP inspection reports group identity, type, material, guide validity and native
  scalar parameters. Signed guide offsets are accepted; non-finite data and negative raw
  radii are rejected by validation. Malformed-guide rejection is source-reviewed, not
  fault-injected at runtime.
- Add uses a deep-cloned existing native guide and the existing material reference. Native
  readback checks new identity, name, transform, transferable and material. It retains all
  previous group paths; it does not save a custom-library item.
- Count, length, width, position and interval setters completed native rebuilds. A disposable
  trial saved and reopened with one new group of three strands and values 0.5 / 0.3 /
  335 degrees / 18 degrees. Existing groups and strands, their material references,
  exposed hair/guide parameters and transforms were unchanged.
- Invalid group 999, duplicate group name, count 0, NaN width, interval 91 and mutation of
  a brush group returned actionable errors. Native default count is six; the caller must
  inspect and set it explicitly before visual acceptance.
- Separate validation of eyelid clearing and layer visibility preserved existing hair
  groups and strands. One eyelid item was cleared and two layers hidden. After reopen,
  92 face parameters per model, base-hair colors, hair catalogs and iris RGBA pixels were
  unchanged. Only the selected eyelid state changed.
- Actual face/full-body comparisons and native saved thumbnails were inspected. Original
  model hashes and the custom library were unchanged.

Focused checks covered save/reopen and invalid inputs; no full regression suite was run.

## FAILED visual fitting

The three hairline attempts produced visible crown intersections and stiff strips on the
chosen customized guide. Visual fitting was **FAILED**; fitting on a second model was
**SKIPPED** in this pass. Scalp-edge coverage was not established.
This tool is not an automatic hairstyle fitter or a substitute for individual guide/strand
sculpting. Reusing a valid guide does not establish visually suitable geometry.

## Review and remaining boundaries

Independent source review found two issues: missing partial-application details in MCP
errors and insufficient post-add identity/material readback. Both were corrected. The
small bounded position/interval extension follows documented editor semantics and runtime
readback; no additional independent review was run for those allowlist rows.

**UNVERIFIED:** forced post-mutation failures and readiness timeouts (including batch error
details), native undo/redo replay, other guide layouts, other versions/platforms, fitting on
the second model, and rendering in downstream engines. No guide-point editing, individual-strand
sculpting, deletion, brush painting or arbitrary native method invocation is exposed.

The initial guide validator rejected legitimate signed offsets before mutation; this was
fixed before successful addition. The final Release build succeeded. The save watchdog emitted error-level
15/20-second messages during a native save that completed in 21.62 seconds and reopened.
App analytics DNS failures, binding warnings and native BuildModifier disposal warnings
were inspected separately; the disposal warning's cause remains unverified. The plugin
ErrorLog contained only its UTF-8 marker. Logs are not claimed clean.

Validation assets and captures are not distributed with the repository.
