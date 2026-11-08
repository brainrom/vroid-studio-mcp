# Bounded hair geometry validation — Windows 2.16.0

This follow-up requested fewer added lower eyelashes and a small hairline addition. The
API's native success and the artwork's visual acceptance are separate outcomes.

## VERIFIED

- Release build against the installed 2.16.0 interop assemblies: zero warnings/errors.
- Public MCP inspection reports group identity, type, material, guide validity and native
  scalar parameters. Signed guide offsets are accepted; non-finite data and negative raw
  radii are rejected by validation. Malformed-guide rejection is source-reviewed, not
  fault-injected at runtime.
- Add uses a deep-cloned existing native guide and the existing material reference. Native
  readback checks new identity, name, transform, transferable and material. It retains all
  previous group paths; it does not save a custom-library item.
- Count, length, width, position and interval setters completed native rebuilds. A private
  trial saved and reopened with one new group of three strands and values 0.5 / 0.3 /
  335 degrees / 18 degrees. The prior 12 groups and 56 strands, their material references,
  exposed hair/guide parameters and transforms were unchanged; trial totals were 13/59.
- Invalid group 999, duplicate group name, count 0, NaN width, interval 91 and mutation of
  a brush group returned actionable errors. Native default count is six; the caller must
  inspect and set it explicitly before visual acceptance.
- Separate final models retained their existing 9/83, 8/52 and 12/56 groups/strands. One
  optional eyelid item was cleared and two extra lower-lash layers hidden. After reopen,
  92 face parameters per model, base-hair colors, hair catalogs and iris RGBA pixels were
  unchanged. Only the requested eyelid state changed.
- Actual face/full-body comparisons and native saved thumbnails were inspected. Original
  model and backup hashes were preserved; the custom library was unchanged. The temporary
  hair trial and opened backup entries were removed from history, preserving its original
  20 members. The rejected temporary model was deleted after retaining render/JSON evidence.

Behavior verification used **109.21/180 seconds**, including focused
save/reopen and invalid-input checks. Screenshot work and production edits are separate.
No new test framework, mocks or full regression suite were added.

## FAILED visual fitting

The three hairline attempts produced visible crown intersections and stiff strips on the
chosen customized guide. After two corrective retries the geometry was rejected. The final
character models contain **no new hair group**. The analogous second-character fitting was
**SKIPPED** after this method failed, so scalp-edge coverage remains unfinished for both.
This tool is not an automatic hairstyle fitter or a substitute for individual guide/strand
sculpting. Reusing a valid guide does not establish visually suitable geometry.

## Review and remaining boundaries

Independent source review found two issues: missing partial-application details in MCP
errors and insufficient post-add identity/material readback. Both were corrected. The
small bounded position/interval extension follows documented editor semantics and runtime
readback; no additional independent review was run for those allowlist rows.

**UNVERIFIED:** forced post-mutation failures and readiness timeouts (including batch error
details), native undo/redo replay, other guide layouts, other versions/platforms, fitting on
the second character, and Unreal rendering. No guide-point editing, individual-strand
sculpting, deletion, brush painting or arbitrary native method invocation is exposed.

The initial guide validator rejected legitimate signed offsets before mutation; this was
fixed before successful addition. One build invocation used the wrong working directory
and was corrected; the final Release build succeeded. The save watchdog emitted error-level
15/20-second messages during a native save that completed in 21.62 seconds and reopened.
App analytics DNS failures, binding warnings and native BuildModifier disposal warnings
were inspected separately; the disposal warning's cause remains unverified. The plugin
ErrorLog contained only its UTF-8 marker. Logs are not claimed clean.

Private purchased assets, character files and screenshots are retained locally, not in Git.
