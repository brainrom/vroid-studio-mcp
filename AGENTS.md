# AGENTS.md — working in the `vroid-studio-mcp` repository

Entry point for developers and AI agents working on this repository.

## What this is

`vroid-studio-mcp` is a BepInEx plugin that reverse-engineers and programmatically controls **VRoid Studio
2.14.0** (Unity 6, IL2CPP). It hooks the editor's internal API through BepInEx + Harmony +
Il2CppInterop and exposes it to clients as an MCP server (plus a raw line protocol for debugging and
scripting). We never rewrite or automate the GUI: we only call existing engine methods and read
state, which is also why it works with the window unfocused or minimized.

## Documentation

- [`README.md`](README.md) / [`README.ru.md`](README.ru.md) — end-user documentation (bilingual).
- [`docs/internal-api.md`](docs/internal-api.md) — reverse-engineered internal API: terminology, key
  classes, hook map (RVA), events, VRM-export entry points.
- [`docs/external-api.md`](docs/external-api.md) — the client-facing contract (MCP): sections →
  categories → presets/parameters, colors, camera, screenshots, batch, transports.
- [`docs/architecture.md`](docs/architecture.md) — plugin layers and how to add code/tests.
- [`docs/quirks.md`](docs/quirks.md) — engine behaviours that are easy to get wrong (read before
  editing the plugin).
- [`docs/macos.md`](docs/macos.md) — macOS status.
- [`docs/artist-agent-prompt.md`](docs/artist-agent-prompt.md) — an example agent system prompt.
- `output/il2cpp/index/` — generated dump index (`types.jsonl`, `methods.tsv`), not in git.

**Documentation language.** Internal/developer documentation (everything under `docs/` except the
READMEs, plus this file) is written in **English**. End-user documentation is bilingual:
`README.md` (English, default) and `README.ru.md` (Russian), cross-linked at the top. Do not add
Russian prose anywhere else.

## Repository layout

| Path | Contents |
|---|---|
| `plugin/VroidStudioMcp/` | plugin sources (C#, .NET 6, BepInEx 6 Unity.IL2CPP) |
| `config/BepInEx.cfg` | production BepInEx config with console/disk logging disabled |
| `data/i18n/`, `data/labels/` | shipped overlays: corrected labels and preset names |
| `tools/index_dump.py`, `tools/q.py` | dump indexing / search |
| `tools/api-tests/` | external protocol/queue/render test harness |
| `tools/vlm-labeler/` | standalone VLM preset-labeling harness |
| `run/` | local app sandbox (app + BepInEx + artifacts), not in git |
| `output/` | Il2CppDumper output, not in git |

The plugin is a regular (not `partial`) `Plugin` entry class; layers are standalone static classes.
See [`docs/architecture.md`](docs/architecture.md) for the full tree and layering rules.

## Build and run

Prerequisites: the .NET SDK, and a VRoid Studio installation with **BepInEx 6 (Unity.IL2CPP)** and
its generated `BepInEx/interop/` assemblies. The build takes its BepInEx references from a prepared
app directory (`GameDir`, default `run/game`; override with `-p:GameDir=<path>`).

```bash
dotnet build plugin/VroidStudioMcp/VroidStudioMcp.csproj -c Release
```

Deploy and run:

```bash
# deploy (paths relative to the app directory)
cp plugin/VroidStudioMcp/bin/Release/VroidStudioMcp.dll <app>/BepInEx/plugins/
cp config/BepInEx.cfg <app>/BepInEx/config/          # optional: keeps logs off

# run
#   Windows          -> launch VRoidStudio.exe
#   Linux/macOS Wine  -> wine VRoidStudio.exe
```

BepInEx does not reload plugins, so **restart the app after every build**. The shipped
`config/BepInEx.cfg` disables logging (`[Logging.Console]` and `[Logging.Disk]` `Enabled=false`) — no
log file, no extra output window.

**Background launch (Linux/Wine, for debugging and screenshots).** A plain `wine … &` from a
non-interactive session may block or be killed with the shell. Detach fully and redirect all fds:

```bash
setsid bash -c 'cd <app> && DISPLAY=<DISPLAY> XAUTHORITY=<XAUTH> \
  WINEDEBUG=-all exec wine VRoidStudio.exe' >/tmp/vroid_run.log 2>&1 </dev/null & disown
```

`DISPLAY`/`XAUTH` are host values (find them with `pgrep -a Xwayland` and the `/tmp/.X11-unix/X*`
socket). Without an X connection the engine still runs but the UI is not drawn; an invalid auth
crashes the process (`xcb_connection_has_error`). On Windows these are not needed.

## The control interface

- **MCP (Streamable HTTP): `http://127.0.0.1:17891/mcp`** — the primary client surface
  (`initialize` / `tools/list` / `tools/call`).
- **Raw line protocol: `127.0.0.1:17890`** — text / JSON-RPC 2.0, intended for debugging and
  scripting (e.g. `telnet`/`nc` or a small Python client).

Both sit on the same main-thread command queue. The complete command/tool reference, argument
semantics, formats and transport details are in [`docs/external-api.md`](docs/external-api.md) — do
not duplicate them elsewhere. Start from `state` / `sections` / `categories` / `enter`; section and
category names are case-sensitive, and `enter` returns a single list of numeric and color parameters
with stable `path`s.

Configuration lives in `<app>/BepInEx/config/com.kokokoshka.vroidstudiomcp.servers.json`: addresses,
`dev_tools`, `param_trace` (per-setter `param_trace.log` trace; also logs at `Debug`, expensive),
`dump_artifacts` (diagnostic file dumps; off unless re-verifying a new editor build), `dialog_trace`
(dialog-window tracing; see [`docs/quirks.md`](docs/quirks.md)), `max_batch_ops`, and preview-quality flags
(`msaa_samples`, `render_scale`, `aniso_filtering`). The address and `dev_tools` can also be changed
from the in-app Settings dialog, section **Programmatic Control (MCP)**.

## Testing

### In-plugin tests

Tests are classes `Tests/<Name>.cs : VroidTest` with a linear `IEnumerator Run()` driven on the main
thread by `TestRunner` (not Unity coroutines). Run them over the command channel:

```
test <name> <path.vroid>
```

They write their own `*_result.json` reports. Registered names include `selftest`, `scopetest`,
`cattest`/`catwalk`, `presettest`/`presetwalk`, `paramwalk`, `icons`, `colormap`/`colortest`/
`colortestall`, `haircolortest`, `basehairtest`, `eyetest`, `exittest`, `camtest`/`camwalk`/
`camstate`, `mcptest`, `colorparse`, `hairglobal`/`hairclear`/`hairrestore`, `renderchange`,
`sidebar`, `labeltest`, `createtest`, `previewtest`, `timescalefuzz`, `pathtest`, `apitest`
(the last four, plus `labeltest`, do not need a model), and
`customitemtest` (needs an open model).

`enumerationtest` needs no model and checks native array/list collection interfaces through both
the managed rewrap and the forced native fallback. `customcollectiontest <path.vroid>` checks
Add/Import discovery in the real custom libraries, once normally and once with the native fallback
forced, without creating or deleting items.

### Testing rules

- **Deterministic runs only.** Do not rely on external `sleep`; tests wait for engine signals and
  terminate the app themselves.
- **Go through production handlers**, not manual `Invoke`. `CommandDispatcher.TestEnter/
  TestSelectPreset/TestSetParam/TestPreview*` arm the signal (`Signals.Arm()`) and settle; a raw
  `Invoke(..., "set_IsSelected", true)` with a fixed pause is a race and can mask real bugs.
- **Model readiness** is `IsUpdatingModel true→false`, not a timer. Add timeouts and a non-empty
  render fallback (the engine is occasionally slow).
- **Screenshots:** compare hashes only after **3 consecutive identical frames** to avoid flicker
  false-positives. Confirm a parameter change by a hash difference before/after, then restore.
- **Never patch `.ctor` of proxies/base classes or async methods** — see `docs/quirks.md`.

The `renderchange` test drives the production handlers and requires that valid number/color/preset
changes alter the stable preview hash, while invalid commands error without changing it. `sidebar`
switches sections and compares fixed window-region hashes.

### External harness

`tools/api-tests/api_tests.py` (stdlib only) runs against an already-running app and covers what an
in-plugin test cannot (an in-process MCP call would deadlock on the shared queue): protocol
negotiation, concurrency/batch queue semantics, guards, the end-to-end "change → screenshot
changed" render check, argument stress and error handling.

```bash
python3 tools/api-tests/api_tests.py --model <path.vroid>   # all suites
python3 tools/api-tests/api_tests.py --suite protocol --list
```

`--model` is required (pass `''` to skip the render suite). Exit code is non-zero on failure.

## Nuances that most often break things

- **Selecting a preset** only via `PresetOptionViewModel.set_IsSelected(true)`; it invokes the stock
  `_presetSelectAction`. Do not call `Context.LoadAndReplaceTransferableGroup` manually.
- **Synchronization by event:** after any change (preset/parameter) wait for `UpdateSelectedPresets`
  or `IsUpdatingModel true→false` before taking a frame or moving on. Polling screenshots during
  application throttles the async rebuild. Make API setters resolve on a signal, not a timer.
- The preview is the RT of the 3D camera **`NormalLayerCamera`** (clean, size-stable viewport);
  cropping the center of `MainCamera.targetTexture` is only a fallback.
- `CategoryViewModel.UpdateIsSelectedCategory` is a bulk update — not a signal of the current
  category.
- Quitting blocks on the unsaved-changes dialog; save (or discard) first.

## Provenance

Most of the reverse engineering and the initial implementation were produced with AI coding agents
(**DeepSeek V4.1 Flash** in **OpenCode**). Every AI-generated change was manually reviewed and
verified against the running app (deterministic in-plugin tests and runtime checks) before it was
accepted.

## Conventions

- Comments only where they add value (the project keeps a minimum of comments).
- Command arguments are parsed through `Commands/ArgParse.cs` (strict, actionable errors); do not add a
  per-handler boolean/switch parser or a silent fallback.
- Refer to VRoid Studio as the **app** (a 3D editor), not a "game", in documentation and comments.
- **Documentation language:** internal docs in English; end-user docs (READMEs) bilingual EN + RU.
- Code and identifiers are English.
- Not committed to git: the app, BepInEx, dumps, `run/`, and binary tools (see `.gitignore`).
