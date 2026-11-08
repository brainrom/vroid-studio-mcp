using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using static VroidStudioMcp.VroidState;
using static VroidStudioMcp.Hooks;

namespace VroidStudioMcp;

[BepInPlugin("com.kokokoshka.vroidstudiomcp", "VRoid Studio MCP", Plugin.Version)]
public class Plugin : BasePlugin
{
    public const string Version = "1.1.0";
    internal static Harmony Harmony;
    private static bool _tickSeen;
    private static int _lastTickFrame = -1;

    public override void Load()
    {
        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        PluginLog.Source = BepInEx.Logging.Logger.CreateLogSource("VroidStudioMcp");
        PluginLog.DebugEnabled = (Logger.ListenedLogLevels & LogLevel.Debug) != 0;
        InteropLogFilter.Install();
        // Settings are loaded before patching so the runtime flags are available to the patches.
        ServerSettings.Load();
        PresetLabels.Reload();
        PluginLog.LogInfo($"labels: {PresetLabels.Count} from {PresetLabels.Files.Count} file(s) in {PresetLabels.Dir}");
        PluginLog.LogInfo($"VroidStudioMcp v{Version} loading...");
        Harmony = new Harmony("com.kokokoshka.vroidstudiomcp");
        MessagesI18n.Install();
        RenderQuality.Install();

        var postfix = new HarmonyMethod(typeof(Hooks).GetMethod(nameof(OnHook), BindingFlags.Public | BindingFlags.Static));
        var prefix = new HarmonyMethod(typeof(Hooks).GetMethod(nameof(OnValuePre), BindingFlags.Public | BindingFlags.Static));
        foreach (var (type, method) in Hooks.Targets)
        {
            PatchByTypeName(type, method, postfix);
            if (Hooks.IsValueSetter(method))
                PatchByTypeName(type, method, prefix, prefix: true);
        }

        // refresh markers: UpdateValue() callers are engine refreshes, not user edits
        var refBegin = new HarmonyMethod(typeof(Hooks).GetMethod(nameof(OnRefreshBegin), BindingFlags.Public | BindingFlags.Static));
        var refEnd = new HarmonyMethod(typeof(Hooks).GetMethod(nameof(OnRefreshEnd), BindingFlags.Public | BindingFlags.Static));
        foreach (var t in new[]
        {
            "VRoid.Studio.ModelConfiguration.BoneTransformParameterViewModel",
            "VRoid.Studio.ModelConfiguration.BlendShapeTransformParameterViewModel",
            "VRoid.Studio.ModelConfiguration.CombinedBoneTransformParameterViewModel",
            "VRoid.Studio.ModelConfiguration.CombinedBlendShapeTransformParameterViewModel",
        })
        {
            PatchByTypeName(t, "UpdateValue", refBegin, prefix: true);
            PatchByTypeName(t, "UpdateValue", refEnd);
        }

        // preset selection / clear (all overloads)
        PatchAllByName("VRoidCore.Editing.Context", "LoadAndReplaceTransferableGroup", postfix);
        PatchAllByName("VRoid.Studio.ModelPresetsSelector.ClearOptionViewModel", "ClearTransferableGroup", postfix);

        // The import flow ends with an ImportCustomItemDialogViewModel ("Imported successfully" /
        // Wear now / Wear later). The import command supplies both the file and the wear answer, so
        // the plugin answers that dialog before it is displayed (never a modal window).
        PatchByTypeName(
            "VRoid.Studio.Dialog.ViewModel.DialogProviderViewModel",
            "Show",
            new HarmonyMethod(typeof(CustomItems).GetMethod(nameof(CustomItems.OnDialogShow), BindingFlags.Public | BindingFlags.Static)),
            prefix: true);

        // Custom-item registry signals: the import/export/save/rename flows are async and their
        // completion is not exposed as a Task on every path (SaveCustomItem/Rename are async void).
        // Arm these counters before a mutation and wait for them instead of a fixed pause.
        var ciSave = new HarmonyMethod(typeof(CustomItemSignals).GetMethod(nameof(CustomItemSignals.OnSave), BindingFlags.Public | BindingFlags.Static));
        var ciNotify = new HarmonyMethod(typeof(CustomItemSignals).GetMethod(nameof(CustomItemSignals.OnNotify), BindingFlags.Public | BindingFlags.Static));
        PatchByTypeName("VRoid.Studio.ModelPresetsSelector.CustomItemRegistry", "Save", ciSave);
        PatchByTypeName("VRoid.Studio.ModelPresetsSelector.CustomItemRegistry", "NotifyUpdateFiles", ciNotify);

        // Parameter registry (catalog_params.json) only. These ctor patches are pointless without the
        // dump artifacts, and each one logs an Il2CppInterop native-detour warning, so only do them
        // when dump_artifacts is on.
        if (ServerSettings.DumpArtifacts)
        {
            var ctorPost = new HarmonyMethod(typeof(Hooks).GetMethod(nameof(OnParamCtor), BindingFlags.Public | BindingFlags.Static));
            foreach (var t in new[]
            {
                "VRoid.Studio.HairEditor.DesignEditor.Parameter.ViewModel.ParameterViewModelBase",
                "VRoid.Studio.XWear.Edit.ModelEdit.MaterialParameter.MaterialParameterViewModelBase",
                "VRoid.Studio.TextureEditor.Color.ViewModel.ColorViewModelBase",
            })
                PatchDerivedCtors(t, ctorPost);
        }

        // capture the "simple color" module (VRoid.Studio.ColorSelection)
        var colorSel = new HarmonyMethod(typeof(Hooks).GetMethod(nameof(OnColorSel), BindingFlags.Public | BindingFlags.Static));
        PatchAllByName("VRoid.Studio.ColorSelection.ViewModel.SubColorSelection", "set_Color", colorSel);
        PatchAllByName("VRoid.Studio.ColorSelection.ViewModel.ColorItem", "set_Color", colorSel);
        PatchAllByName("VRoid.Studio.ColorSelection.ViewModel.ColorGroupEditor", "Confirm", colorSel);
        PatchAllByName("VRoid.Studio.ColorSelection.ViewModel.ColorGroup", "ConfirmChange", colorSel);
        PatchAllByName("VRoid.UI.View.UnifiedColorPickerTransform", "set_Color", colorSel);
        PatchAllByName("VRoid.Studio.HairEditor.DesignEditor.Material.ViewModel.MaterialColorNodeViewModel", "set_MaterialColor", colorSel);
        PatchAllByName("VRoid.Studio.HairEditor.DesignEditor.Material.ViewModel.BaseColorNodeViewModel", "set_MaterialColor", colorSel);
        PatchAllByName("VRoid.Studio.HairEditor.ViewModel", "set_HairMaterialTemplateColor", colorSel);
        PatchAllByName("VRoid.Studio.HairEditor.ViewModel", "set_HairMaterialTemplateEmissionColor", colorSel);
        // Ctor registration only matters for the two types Color.RefreshRegistry actually reads from
        // the registry: SubColorSelection (the colour leaves of a category) and HairEditor.ViewModel
        // (the global hair colours). The other colour VMs it never looks at — registering them just
        // added native-detour warnings, so they are dropped (runtime set_Color/Confirm hooks still
        // feed the registry when colours are actually edited).
        var colorSelCtor = new HarmonyMethod(typeof(Hooks).GetMethod(nameof(OnColorSelCtor), BindingFlags.Public | BindingFlags.Static));
        PatchOwnCtors("VRoid.Studio.ColorSelection.ViewModel.SubColorSelection", colorSelCtor);
        PatchOwnCtors("VRoid.Studio.HairEditor.ViewModel", colorSelCtor);

        // capture camera objects
        // REAL "model loaded" signal: VRoidCoreModelUpdater.IsUpdatingModel true -> false
        var isUpd = new HarmonyMethod(typeof(VroidState).GetMethod(nameof(OnIsUpdating), BindingFlags.Public | BindingFlags.Static));
        PatchAllByName("VRoid.Studio.Component.VRoidCoreModelUpdater", "set_IsUpdatingModel", isUpd);

        var modelUpd = new HarmonyMethod(typeof(VroidState).GetMethod(nameof(OnModelUpdate), BindingFlags.Public | BindingFlags.Static));
        PatchAllByName("VRoid.Studio.Engine.Model", "OnModelUpdateComplete", modelUpd);
        PatchAllByName("VRoid.Studio.Engine.ViewModel", "OnModelUpdateComplete", modelUpd);
        PatchAllByName("VRoid.Studio.Component.VRoidCoreModelUpdater", "OnModelUpdateComplete", modelUpd);

        PatchByTypeName("VRoid.Studio.VRMExportScreen.ViewModel", "OnScreenActivate", new HarmonyMethod(
            typeof(NativeAvatarExport).GetMethod(nameof(NativeAvatarExport.OnScreenActivate))));
        PatchByTypeName("VRoid.Studio.VRMExportScreen.ViewModel", "OnScreenDeactivate", new HarmonyMethod(
            typeof(NativeAvatarExport).GetMethod(nameof(NativeAvatarExport.OnScreenDeactivate))));

        var camCtrl = new HarmonyMethod(typeof(Hooks).GetMethod(nameof(OnCameraController), BindingFlags.Public | BindingFlags.Static));
        PatchByTypeName("VRoidStudio.GUI.UserControls.VRoidStudioCameraViewController", "OnEnable", camCtrl);
        var editorTick = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnEditorTick), BindingFlags.Public | BindingFlags.Static));
        PatchByTypeName("VRoidStudio.GUI.UserControls.VRoidStudioCameraViewController", "Update", editorTick);
        // persistent per-frame tick across all screens
        PatchAllByName("UnityEngine.EventSystems.EventSystem", "Update", editorTick);
        PatchByTypeName("VRoidStudio.GUI.AvatarEditor.AvatarEditor", "Update", editorTick);
        PatchByTypeName("VRoid.UI.Component.UICameraUpdater", "Update", editorTick);
        var camPos = new HarmonyMethod(typeof(Hooks).GetMethod(nameof(OnCameraPosition), BindingFlags.Public | BindingFlags.Static));
        PatchByTypeName("VRoid.UI.Component.AvatarCameraPosition", "Start", camPos);

        // capture MainViewModel as early as possible (created at startup)
        PatchOwnCtors("VRoid.Studio.MainViewModel", new HarmonyMethod(
            typeof(Hooks).GetMethod(nameof(OnMainViewModel), BindingFlags.Public | BindingFlags.Static)));

        // capture MainActionHandler (holds IsUnSaved) for clean exit without the unsaved dialog
        PatchOwnCtors("VRoid.Studio.MainActionHandler", new HarmonyMethod(
            typeof(VroidState).GetMethod(nameof(OnMainActionHandler), BindingFlags.Public | BindingFlags.Static)));
        // Capture the modal error dialog shown when a model fails to load. The generic
        // VRoid.Studio.Dialog.ViewModel.ErrorDialogViewModel is the one actually used for
        // "Failed to load the model" (key V1.Studio.StartScreen.ViewModel.ErrorDialog.OpenErrorDescription);
        // the beta-model one is kept for the beta/VRM import path.
        var loadErr = new HarmonyMethod(
            typeof(VroidState).GetMethod(nameof(OnLoadErrorDialog), BindingFlags.Public | BindingFlags.Static));
        PatchOwnCtors("VRoid.Studio.Dialog.ViewModel.ErrorDialogViewModel", loadErr);
        PatchOwnCtors("VRoid.Studio.StartScreen.BetaModelImportErrorDialog.ViewModel.BetaModelImportErrorDialogViewModel", loadErr);
        // The failed-load dialog is not necessarily an ErrorDialogViewModel: capture every dialog the
        // provider shows, so a failed open can be detected (and dismissed) instead of waiting 40 s.
        PatchAllByName("VRoid.Studio.Dialog.ViewModel.DialogProviderViewModel", "Show", new HarmonyMethod(
            typeof(VroidState).GetMethod(nameof(OnDialogShown), BindingFlags.Public | BindingFlags.Static)));
        // dialog_trace devtool (servers.json): the Show funnel above is already traced; install the
        // notification/dismiss hooks only when the trace is on, so the devtool adds no hooks otherwise.
        if (ServerSettings.DialogTrace)
        {
            PatchAllByName("VRoid.Studio.Dialog.ViewModel.DialogProviderViewModel", "ShowNotification", new HarmonyMethod(
                typeof(DialogTrace).GetMethod(nameof(DialogTrace.OnNotify), BindingFlags.Public | BindingFlags.Static)));
            PatchAllByName("VRoid.Studio.Dialog.ViewModel.DialogProviderViewModel", "Dismiss", new HarmonyMethod(
                typeof(DialogTrace).GetMethod(nameof(DialogTrace.OnDismiss), BindingFlags.Public | BindingFlags.Static)),
                prefix: true);
        }

        // independent per-frame ticker (active on every screen, unlike AvatarEditor.Update)
        try
        {
            var comp = AddComponent<VroidTicker>();
            UnityEngine.Object.DontDestroyOnLoad(comp.gameObject);
            PluginLog.LogInfo("ticker component injected");
        }
        catch (Exception e) { PluginLog.LogError($"ticker injection failed: {e.Message}"); }

        SettingsUi.Register();

        // Keep the player loop alive when the window is unfocused. The app forcibly disables
        // runInBackground (RunInBackgroundUtil), which otherwise freezes the main-thread command queue.
        // Postfix the app's toggles; OnTick also re-asserts it as a safety net.
        var runInBg = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(ForceRunInBackground), BindingFlags.Public | BindingFlags.Static));
        foreach (var (type, method) in new[]
        {
            ("VRoid.UI.Util.RunInBackgroundUtil", "Initialize"),
            ("VRoid.Studio.Util.RunInBackgroundUtil", "Enable"),
            ("VRoid.Studio.Util.RunInBackgroundUtil", "Restore"),
        })
            if (Interop.FindType(type) != null) PatchByTypeName(type, method, runInBg);

        // Background watchdog: logs the in-flight command if the main thread stops draining the queue.
        new Thread(() => CommandDispatcher.WatchdogLoop()) { IsBackground = true, Name = "vroid-studio-mcp-watchdog" }.Start();

        CommandServer.Start();
        McpHost.Start();
        ServerSettings.MarkApplied();

        Perf.Since("plugin load", loadSw);
        PluginLog.LogInfo("VroidStudioMcp loaded.");
    }

    public static void OnEditorTick() => OnTick();

    public static void ForceRunInBackground() => UnityEngine.Application.runInBackground = true;

    public static void OnTick()
    {
        // Several Update hooks feed this (EventSystem, screen controllers, the injected ticker), so
        // guard against advancing the command coroutine more than once per frame. Stepping the driver
        // (or accumulating Pause time) several times per frame would otherwise speed waits up and
        // re-run per-frame work.
        int frame = UnityEngine.Time.frameCount;
        if (frame == _lastTickFrame) return;
        _lastTickFrame = frame;
        // The app forcibly disables runInBackground (RunInBackgroundUtil), so an unfocused window
        // pauses the player loop and the command queue stops draining. Keep ticking in the background.
        // While minimized, WindowBackground switches off vsync so the minimize/restore cannot wedge
        // the renderer (see Runtime/WindowBackground.cs).
        WindowBackground.Tick();
        RenderQuality.Tick();
        if (!UnityEngine.Application.runInBackground) UnityEngine.Application.runInBackground = true;
        _refreshDepth = 0;
        if (!_tickSeen) { _tickSeen = true; PluginLog.LogInfo("main-thread ticker running"); }
        try
        {
            CommandServer.Tick();
            Catalog.Tick();
            SettingsUi.Tick();
        }
        catch (Exception e) { PluginLog.LogError($"tick error: {e.Message}"); }
    }

}
