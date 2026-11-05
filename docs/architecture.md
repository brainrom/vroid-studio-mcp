# Plugin architecture

The plugin is split by responsibility. `Plugin` is a plain (not `partial`) entry class: the entry
point and wiring. Domain layers are standalone static classes (`Runtime/`, `Interop/Hooks`,
`Editor/`, `Commands/`, `MCP/`, `Tests/`), each in its own file. This keeps state and helpers
grouped, without dumping everything into one class.

## Tree

```
plugin/VroidStudioMcp/
  Plugin.cs                 // entry: Load(), patch registration, wiring, OnTick (once per frame)
  VroidTicker.cs            // MonoBehaviour → main thread → OnTick
  MiniJson.cs
  Runtime/
    Interop.cs              // Get/Set/Invoke/Enumerate/RealObj/RealType/TypeName/... (Il2Cpp)
    VroidState.cs           // captured VM/camera + readiness flags (_modelReady/_modelUpdating/_inEdit)
    Signals.cs              // Arm/Ready/Version — rebuild and model readiness signal
    CustomItemSignals.cs    // registry Save/Notify versions (import/export/rename completion)
    Registries.cs           // Selectors, ColorRegistry, Utf8
    Logging.cs              // WriteJson, TransferableGroupDisplayName
    DialogTrace.cs          // debug trace of every engine dialog (un-intercepted windows → dialog_trace.log)
    PresetLabels.cs         // overlay of preset labels (labels/*.json → resourceName→label)
    ServerSettings.cs       // config of addresses/flags (BepInEx/config) + live restart
    WinePaths.cs            // host↔app path translation (Wine)
    InteropLogFilter.cs     // log filter for Il2CppInterop ("Failed to init ... backend")
    Perf.cs, PluginLog.cs, NullablePolyfill.cs
  Interop/
    Hooks.cs                // Targets + patches + all static postfix/prefix (+ Patch* helpers)
  Editor/                   // facade of the private API (the only layer that knows internal types)
    Sections.cs             // section switching (EditMode)
    Categories.cs           // tabs, search, counters
    Presets.cs              // preset selection/reset, preset display name, custom-item (Custom tab) list
    CustomItems.cs          // custom-item import/export (*.vroidcustomitem) + import-dialog answer
    Parameters.cs           // ParamRef + assembly/serialization of category parameters and colors (enter payload)
    ParamCodec.cs           // color/numbers: Hex/TryParseColor/TryParseFinite/TryDbl/SafeNum
    Colors.cs               // color registry, universal setter
    Camera.cs               // AvatarCameraPosition + region and angle presets (frame)
    Screenshot.cs           // 3D-viewport RT (NormalLayerCamera), PNG, ShotHash
    Files.cs                // Open/Save/QuitClean + dirty flag/current model path
    Catalog.cs              // catalog dumps (dump_artifacts)
    MessagesI18n.cs         // overlay for engine string substitution + message table dump
    SettingsUi.cs           // injection of the MCP section into the standard settings dialog
    Icons.cs                // preset previews, texture conversion to PNG
  Commands/
    CommandServer.cs        // TCP 127.0.0.1:17890, string protocol, main-thread queue + response
    CommandDispatcher.cs    // thin router: queue, watchdog, method→handler registry, Test* hooks
    CommandUtil.cs          // common helpers: WaitUntil/Pause/Settle/Arg/ResolveSection/EnterSection
    ArgParse.cs             // strict shared argument parsing (int/float/bool flags/choices + errors)
    CoroutineDriver.cs      // drives a managed iterator (one step per frame), shared by server/tests
    Handlers/               // one file per domain; each command is a static method Context→IEnumerator
      SessionHandlers.cs    // state/open/create/save/exit/main_menu/test
      CategoryHandlers.cs   // categories/enter (+ auto-framing of the camera)
      PresetHandlers.cs     // presets/presets_full/select_preset/preset_preview_*
      CustomItemHandlers.cs // import_custom_preset/export_custom_preset
      ParamHandlers.cs      // set_param
      CameraHandlers.cs     // camera
      ScreenshotHandlers.cs // screenshot/ui_screenshot
      LabelHandlers.cs      // labels/label_set
      I18nHandlers.cs       // i18ndump/i18nreload/i18nlang
      SettingsHandlers.cs   // settingsprobe/dump/state/toggle/edit
      DebugHandlers.cs      // hook/unhook/hooks (runtime method logger)
  MCP/
    McpHost.cs              // Streamable HTTP JSON-RPC on HttpListener (127.0.0.1:17891/mcp)
  Tests/                    // one file per test; they call Editor + the shared harness
    VroidTest.cs            // base: Open/SwitchSection/EnterCategory/WaitUntil/Pause/Settle/Shot/Report
    TestRunner.cs           // drives the test iterator (one step per frame), name→factory registry
    TestSupport.cs          // test-only helpers over internal VMs (not part of the production Editor facade)
    SelfTest, ScopeTest, CatTest, CatWalk, PresetTest, PresetWalk, ParamWalk, ColorTests,
    BaseHairTest, EyeTest, CameraTests, IconTest, ExitTest, LabelTest, CreateTest, PreviewTest,
    RenderChangeTest, SidebarTest, ...
```

## Principles

- **Layers.** `Editor/` knows nothing about tests; tests call `Editor/` and the shared harness.
  `Interop/` attaches hooks and registers objects/signals (without domain logic), but its
  static callbacks write captured state into `Runtime/` (`VroidState`, `Colors`).
- **State.** Shared static fields are grouped in `Runtime/` (`Registries`/`VroidState`/
  `Signals`) and in test fields (inherited from `VroidTest`).
- **Synchronization.** Any change in the engine is accompanied by a signal
  (`Signals.Version` = `UpdateSelectedPresets` + model updates). Tests/commands call
  `Signals.Arm()` before mutation and wait for `Signals.Ready(...)`, and only then take a frame —
  otherwise the async rebuild is "choked".
- **Frame.** `OnTick` is called from several `Update` hooks, so it is protected by a guard on
  `Time.frameCount`: the coroutine driver steps exactly once per frame.
- **Exit.** `QuitClean()` resets the dirty flag (`MainActionHandler.IsUnSaved`) and exits without
  the Unsaved dialog.

## Tests

Tests are linear coroutines (their own driver, without Unity coroutines: `StartCoroutine` waits
for the Il2Cpp enumerator). A test describes a sequence, the base steps it one step per frame on
the main thread and calls `QuitClean` when done.

```csharp
public sealed class BaseHairTest : VroidTest
{
    protected override IEnumerator Run()
    {
        yield return Open();
        yield return SwitchSection("HairEditor");
        yield return EnterCategory("HairEditor", "Overall Hair");
        var before = Shot();
        Signals.Arm();
        Invoke(clear, "ClearTransferableGroup");
        yield return Settle(1.5f);
        Report("basehair", Shot() != before, ...);
    }
}
```

## How to add

- New internal API → a method in `Editor/<domain>.cs` (return simple values/DTOs).
- New test → a class `Tests/<Name>.cs : VroidTest` + registration in `TestRunner.Factories`
  (run: `test <name> <path>` via TCP).
- New external command → a handler method in `Commands/Handlers/<domain>Handlers.cs` (calls the `Editor` API)
  + a `name → methods` entry in `CommandDispatcher.BuildHandlers`.
- Do not patch anything in hot getters (see `docs/quirks.md`).
