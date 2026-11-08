# vroid-studio-mcp

[English](README.md) | [Русский](README.ru.md)

![vroid-studio-mcp promo](docs/media/promo.gif)

`vroid-studio-mcp` is an adapter between **VRoid Studio 2.14.0** and AI agents. It lets an MCP client
(Claude Desktop, OpenCode, …) create and edit characters in the real editor: pick presets, move
sliders, change colors, frame the camera, inspect the result, and save — all under the agent's
control, while you keep working in VRoid Studio as usual.

## Features

- **Character creation and editing** — create or open a character, let the agent build it, and save
  it (`.vroid`).
- **Core editor sections** — sections (`Face`,
  `Hairstyle`, `Body`, `Outfit`), categories, presets, and individual sliders/colors.
- **Hair materials and layers** — inspect and edit supported material properties and PNG layers,
  and assign materials to existing hair groups without replacing their geometry. See the
  [tool contract and limitations](docs/external-api.md#hair-material-editing).
- **Iris textures** — import/export native iris PNG layers and control their visibility/opacity
  without replacing the face. See [iris texture editing](docs/external-api.md#iris-texture-editing).
- **Eyelid layers** — import native eyelid custom items and select their visible raster layers.
  See [eyelid texture layers](docs/external-api.md#eyelid-texture-layers).
- **Session diagnostics** — identify the loaded process/build, model path and unsaved state before
  working with multiple editor instances; clean exact recent-history entries without deleting models.
- **Live visual feedback** — the agent receives rendered previews of the model and preset
  thumbnails, so it decides by looking instead of guessing.
- **High-quality preview render** — configurable MSAA and supersampling (SSAA) plus anisotropic
  filtering keep the preview clean and crisp for the agent to judge.
- **Readable preset names** — VRoid ships no human-friendly names; the plugin supplies them, so
  presets are told apart by meaning rather than an internal id.
- **Corrected English labels** — the editor's English translation gives different sliders the same
  name; the plugin fixes these in the interface.

## How it works

- The plugin attaches to VRoid Studio's **own internal API** and calls it directly. It does **not**
  simulate the mouse or keyboard and does **not** read the screen, so it is precise and reliable.
- Because it never touches the GUI, it **keeps working while the window is in the background or
  minimized** — start a long edit, switch to another app, and the agent keeps going.
- **Wine is supported out of the box.** If you run VRoid Studio through Wine on Linux, paths are
  translated in both directions automatically, so agents and scripts on your computer can just use
  normal Linux paths for open/save — there is nothing to configure.

## Requirements

- **VRoid Studio 2.14.0** (Windows, or run through Wine) — [official website](https://vroid.com/en/studio).
- **BepInEx 6 for Unity IL2CPP** — download
  [BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788](https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip).
  BepInEx bundles everything else the plugin needs.

## Installation

1. Install the BepInEx build from **Requirements** into your VRoid Studio folder, then launch the app
   once so BepInEx finishes setting itself up.
2. Copy the plugin file into BepInEx's plugin folder, so you end up with:
   ```
   <VRoidStudio>/BepInEx/plugins/VroidStudioMcp.dll
   ```
3. Copy the two data folders into the VRoid Studio folder (the one containing `VRoidStudio.exe`):
   ```
   <VRoidStudio>/i18n/      # corrected English labels
   <VRoidStudio>/labels/    # readable preset names
   ```
4. Optional: copy the included BepInEx config to keep log files off:
   ```
   <VRoidStudio>/BepInEx/config/BepInEx.cfg
   ```
5. Start VRoid Studio. The plugin is now serving an MCP endpoint at
   `http://127.0.0.1:17891/mcp`.

### What's in the release

The archive ships **only this plugin and its data** — never VRoid Studio itself and never BepInEx
(both are things you install separately):

```
BepInEx/plugins/VroidStudioMcp.dll   the plugin
BepInEx/config/BepInEx.cfg      BepInEx config with logging turned off
i18n/                           corrected English labels
labels/                         readable preset names
README.md  README.ru.md  LICENSE
```

## Getting started

Point your MCP client at the local endpoint. For example:

```json
{ "mcpServers": { "vroid-studio-mcp": { "url": "http://127.0.0.1:17891/mcp", "type": "http" } } }
```

Then just ask the agent for what you want, for instance: *"Create a female character, give her a
short bob and green eyes, then save it to my Documents."* The agent inspects the current sections,
presets and parameters, applies changes, and checks the result on the rendered preview.

## Configuration

Settings live in `<VRoidStudio>/BepInEx/config/com.kokokoshka.vroidstudiomcp.servers.json`. The defaults
are sensible; the ones you might care about:

| Setting | Default | What it does |
|---|---|---|
| `enabled` | `true` | turn the control server on/off |
| `msaa_samples` | `4` | smooths the preview (applied on restart) |
| `render_scale` | `2` | renders the preview at higher resolution for a crisper image |
| `aniso_filtering` | `true` | sharper textures at an angle |

If the preview feels slow, set `render_scale` to `1`.

The address (and these toggles) can also be edited inside VRoid Studio: open **Settings** from the
account icon and look for the **Programmatic Control (MCP)** section at the top.

## Documentation for developers

- [docs/internal-api.md](docs/internal-api.md) — the reverse-engineered internal API of VRoid Studio.
- [docs/external-api.md](docs/external-api.md) — the control surface exposed to clients (MCP).
- [docs/architecture.md](docs/architecture.md) — how the plugin is built and how to extend it.
- [docs/quirks.md](docs/quirks.md) — engine behaviours that are easy to get wrong.
- [docs/macos.md](docs/macos.md) — macOS status.
- [AGENTS.md](AGENTS.md) — build/test workflow and conventions.

[docs/artist-agent-prompt.md](docs/artist-agent-prompt.md) is an example system prompt for an agent
that acts as a character artist.

## macOS

macOS is **still under research** and not part of this release. On Apple Silicon it is achievable
(the plugin itself is platform-independent), but it needs BepInEx/Il2CppInterop builds that are not
yet part of BepInEx upstream. See [docs/macos.md](docs/macos.md).

## AI disclosure

Most of the reverse engineering and the initial implementation were produced with AI coding agents:
**DeepSeek V4.1 Flash** running in **OpenCode**. Every AI-generated result was manually reviewed and
verified against the running app (deterministic in-plugin tests and runtime checks); nothing was
merged without validation.

## Legal

This is an interoperability and automation project. VRoid Studio, BepInEx and other third-party
components belong to their owners and are not included. Use at your own risk.

## License

[MIT](LICENSE) © Ilya Chelyadin and vroid-studio-mcp contributors.
