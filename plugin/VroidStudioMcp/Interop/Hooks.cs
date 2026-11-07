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
using static VroidStudioMcp.Interop;
using static VroidStudioMcp.Registries;
using static VroidStudioMcp.VroidState;
using static VroidStudioMcp.Colors;

namespace VroidStudioMcp;

public static class Hooks
{
    private static string _lastSectionLog;
    private static string _lastCategoryLog;
    private static readonly string _eventPath = Path.Combine(BepInEx.Paths.GameRootPath, "ui_events.jsonl");
    private static readonly string _tracePath = Path.Combine(BepInEx.Paths.GameRootPath, "param_trace.log");
    private static readonly ConcurrentDictionary<string, DateTime> LastLog = new();
    private static readonly ConcurrentDictionary<string, string> Dedupe = new();
    private static readonly object _fileLock = new();

    public static bool IsValueSetter(string method) =>
        method is "set_Value" or "set_CombinedValue" or "set_NegativeValue" or "set_PositiveValue"
                or "set_Color" or "set_IsChecked";

    public static readonly (string Type, string Method)[] Targets =
    {
        ("VRoidStudio.GUI.AvatarEditor.AvatarEditor", "Awake"),
        ("VRoid.Studio.MainViewModel", "InitializeCurrentFileAndMoveToEditRoot"),
        ("VRoid.Studio.MainViewModel", "ImportFileAndMoveToEditRoot"),
        ("VRoid.Studio.EditRootScreen.ViewModel", "set_EditMode"),
        ("VRoid.Studio.EditRootScreen.ViewModel", "OnScreenActivate"),
        ("VRoid.Studio.Engine.Model", "OnHistoryStatusUpdated"),
        ("VRoid.Studio.EditRootScreen.ViewModel", "Save"),
        ("VRoid.Studio.EditRootScreen.ViewModel", "EnterToPhotoBooth"),
        ("VRoid.Studio.EditRootScreen.ViewModel", "EnterToVRMExport"),
        ("VRoid.Studio.EditRootScreen.ViewModel", "BackToModelSelect"),
        ("VRoid.Studio.ModelPresetsSelector.BasePresetsSelectorViewModel", "set_SelectedCategory"),
        ("VRoid.Studio.ModelPresetsSelector.BasePresetsSelectorViewModel", "UpdateSelectedPresets"),
        ("VRoid.Studio.ModelPresetsSelector.BasePresetsSelectorViewModel", "ShowSelectedItem"),
        ("VRoid.Studio.ModelPresetsSelector.BasePresetsSelectorViewModel", "EnterEditor"),
        ("VRoid.Studio.ModelPresetsSelector.CategoryViewModel", "UpdateIsSelectedCategory"),
        ("VRoid.Studio.ModelPresetsSelector.CategoryViewModel", "set_IsSelectedCategory"),
        ("VRoid.Studio.ModelPresetsSelector.ParameterCategoryViewModel", "UpdateParameters"),
        ("VRoid.Studio.ModelPresetsSelector.PresetCategoryViewModel", "UpdateParameters"),
        ("VRoid.Studio.ModelPresetsSelector.ParametricPresetCategoryViewModel", "UpdateParameters"),
        ("VRoidCore.Editing.Context", "ExecuteSyncCommand"),
        ("VRoidCore.Editing.Context", "SetBoneTransformParameter"),
        ("VRoid.UI.SingleParameterViewModel", "set_Value"),
        ("VRoid.UI.CombinedParameterViewModel", "set_CombinedValue"),
        ("VRoid.UI.CombinedParameterViewModel", "set_NegativeValue"),
        ("VRoid.UI.CombinedParameterViewModel", "set_PositiveValue"),
        ("VRoid.Studio.HairEditor.DesignEditor.Parameter.ViewModel.FloatParameterViewModel", "set_Value"),
        ("VRoid.Studio.HairEditor.DesignEditor.Parameter.ViewModel.IntParameterViewModel", "set_Value"),
        ("VRoid.Studio.TextureEditor.Color.ViewModel.ColorViewModelBase", "set_Color"),
        ("VRoid.Studio.TextureEditor.Color.ViewModel.ColorViewModelBase", "set_IsChecked"),
    };

    public static void OnRefreshBegin() => _refreshDepth++;

    public static object FindSelector(object instance)
    {
        if (instance == null) return null;
        var n = TypeName(instance) ?? "";
        return n.Contains("PresetsSelectorViewModel") ? instance : null;
    }
    public static bool DedupeIt(string key, string value)
    {
        if (Dedupe.TryGetValue(key, out var prev) && prev == value) return false;
        Dedupe[key] = value;
        return true;
    }
    public static void RecordEvent(string method, object instance, object[] args)
    {
        if (!ServerSettings.DumpArtifacts) return;
        try
        {
            Dictionary<string, object> data = null;
            switch (method)
            {
                case "ExecuteSyncCommand":
                    {
                        var cmd = FormatArg(First(args));
                        if (!DedupeIt($"cmd", cmd)) return;
                        data = new() { ["command"] = cmd };
                        break;
                    }
                case "set_EditMode":
                    {
                        var mode = FormatArg(First(args));
                        if (!DedupeIt("editmode", mode)) return;
                        data = new() { ["mode"] = mode };
                        break;
                    }
                case "set_SelectedCategory":
                    {
                        var cat = First(args);
                        var info = CategoryInfo(cat);
                        if (!DedupeIt("selcat", MiniJson.Serialize(info, false))) return;
                        data = new() { ["selector"] = TypeName(instance), ["category"] = info };
                        Catalog.RequestDump();
                        break;
                    }
                case "UpdateSelectedPresets":
                case "ShowSelectedItem":
                    {
                        var info = CategoryInfo(Get(instance, "SelectedCategory"));
                        if (!DedupeIt("usp", MiniJson.Serialize(info, false))) return;
                        data = new() { ["selector"] = TypeName(instance), ["selected"] = info };
                        break;
                    }
                case "UpdateIsSelectedCategory":
                    {
                        var info = CategoryInfo(instance);
                        if (!DedupeIt("uisc", MiniJson.Serialize(info, false))) return;
                        data = new() { ["category"] = info };
                        break;
                    }
                case "UpdateParameters":
                    {
                        var info = CategoryInfo(instance);
                        if (!DedupeIt("up", MiniJson.Serialize(info, false))) return;
                        data = new() { ["category"] = info };
                        Catalog.RequestDump();
                        break;
                    }
                case "LoadAndReplaceTransferableGroup":
                    data = new()
                    {
                        ["transferableGroupId"] = SafeStr(First(args)),
                        ["removingTransferableTypes"] = (args != null && args.Length > 1) ? SafeStr(args[1]) : null,
                    };
                    break;
                case "ClearTransferableGroup":
                    data = new() { ["cleared"] = true };
                    break;
                case "set_Value":
                case "set_CombinedValue":
                case "set_NegativeValue":
                case "set_PositiveValue":
                    {
                        var label = TryGetLabel(instance);
                        var val = FormatArg(First(args));
                        if (!DedupeIt($"p:{TypeName(instance)}:{label}", val)) return;
                        data = new() { ["vm"] = TypeName(instance), ["label"] = label, ["value"] = val, ["kind"] = _refreshDepth > 0 ? "refresh" : "edit" };
                        break;
                    }
                case "set_Color":
                    {
                        var label = TryGetLabel(instance);
                        var col = SafeStr(First(args));
                        if (!DedupeIt($"c:{TypeName(instance)}:{label}", col)) return;
                        data = new() { ["vm"] = TypeName(instance), ["label"] = label, ["color"] = col, ["kind"] = _refreshDepth > 0 ? "refresh" : "edit" };
                        break;
                    }
                case "set_IsChecked":
                    {
                        var label = TryGetLabel(instance);
                        var v = FormatArg(First(args));
                        if (!DedupeIt($"chk:{TypeName(instance)}:{label}", v)) return;
                        data = new() { ["vm"] = TypeName(instance), ["label"] = label, ["checked"] = v };
                        break;
                    }
                case "Save":
                case "EnterToPhotoBooth":
                case "EnterToVRMExport":
                case "BackToModelSelect":
                case "ImportFileAndMoveToEditRoot":
                case "InitializeCurrentFileAndMoveToEditRoot":
                    data = new() { ["args"] = (args ?? Array.Empty<object>()).Select(SafeStr).ToList() };
                    break;
            }
            if (data == null) return;
            data["event"] = method;
            data["t"] = DateTime.UtcNow.ToString("O");
            lock (_fileLock)
                File.AppendAllText(_eventPath, MiniJson.Serialize(data, false) + "\n", Utf8);
        }
        catch { }
    }
    public static void OnHook(MethodBase __originalMethod, object __instance, object[] __args)
    {
        try
        {
            var method = __originalMethod?.Name ?? "";
            var name = $"{__originalMethod?.DeclaringType?.FullName}.{method}";
            if (method == "UpdateSelectedPresets") Signals.PresetsUpdated();

            var sel = FindSelector(__instance);
            if (sel != null) Selectors.Track(sel, name);
            if (__instance != null)
            {
                var it = TypeName(__instance) ?? "";
                if (it.EndsWith("MainViewModel")) _mainViewModel = __instance;
                else if (it.EndsWith("EditRootScreen.ViewModel")) _editRootVm = __instance;
                else if (it == "VRoid.Studio.Engine.Model") _engineModel = __instance;
                else if (it.EndsWith("AvatarEditor"))
                {
                    var vm = Get(__instance, "_viewModel");
                    if (vm != null && (TypeName(vm) ?? "").EndsWith("MainViewModel")) _mainViewModel = vm;
                }
            }

            RecordEvent(method, __instance, __args);

            // current-section / current-category signals
            if (method == "set_EditMode")
            {
                var sec = FormatArg(First(__args));
                if (sec != _lastSectionLog) { _lastSectionLog = sec; PluginLog.LogTrace($">>> SECTION {sec}"); }
                return;
            }
            if (method == "set_IsSelectedCategory")
            {
                // fires for every category; the one set to true is the current one
                if (FormatArg(First(__args)) == "True")
                {
                    var cs = CategoryStr(__instance);
                    if (cs != _lastCategoryLog) { _lastCategoryLog = cs; PluginLog.LogTrace($">>> CATEGORY {cs}"); }
                }
                return;
            }
            if (method == "set_SelectedCategory")
            {
                var cs = CategoryStr(First(__args));
                if (cs != _lastCategoryLog) { _lastCategoryLog = cs; PluginLog.LogTrace($">>> CATEGORY {cs}"); }
                return;
            }
            // entering / leaving the edit root drives "a model is being edited"
            if (method == "OnScreenActivate" || method == "ImportFileAndMoveToEditRoot" || method == "InitializeCurrentFileAndMoveToEditRoot")
            {
                EnterEdit();
            }
            // returning to the model select: the edited model is gone, so clear readiness
            if (method == "BackToModelSelect")
            {
                LeaveEdit();
                // Selectors are only created while the edit root is alive; drop the stale ones so
                // `selectors` and the open/create readiness checks (Count >= 4) reflect the new session.
                Selectors.Clear();
                // The colour-VM registry roots per-session objects; reset it so they can be collected.
                ColorRegistry.Clear();
                Catalog.ResetDumps();
                PluginLog.LogTrace(">>> BACK TO MODEL SELECT");
                return;
            }
            // engine edit/command path (this is where combined-parameter edits actually happen)
            if (method == "ExecuteSyncCommand")
            {
                PluginLog.LogTrace($">>> COMMAND {FormatArg(First(__args))}");
                return;
            }
            // preset selection / clear
            if (method == "LoadAndReplaceTransferableGroup")
            {
                var rem = (__args != null && __args.Length > 1) ? SafeStr(__args[1]) : "";
                PluginLog.LogTrace($">>> PRESET load group={SafeStr(First(__args))} remove=[{rem}]");
                return;
            }
            if (method == "ClearTransferableGroup")
            {
                PluginLog.LogTrace(">>> PRESET clear");
                return;
            }
            // bulk update (called for every category) and value setters logged elsewhere
            if (method == "UpdateIsSelectedCategory" || IsValueSetter(method)) return;

            var now = DateTime.UtcNow;
            var last = LastLog.GetOrAdd(name, DateTime.MinValue);
            if ((now - last).TotalMilliseconds < 40) return;
            LastLog[name] = now;

            var sb = new StringBuilder(">>> HOOK ").Append(name);
            var label = TryGetLabel(__instance);
            if (label != null) sb.Append(" [").Append(label).Append(']');
            if (__args != null && __args.Length > 0)
                sb.Append('(').Append(string.Join(", ", __args.Select(FormatArg))).Append(')');
            PluginLog.LogTrace(sb.ToString());
        }
        catch (Exception e) { PluginLog.LogError($"hook log error: {e.Message}"); }
    }
    public static void OnMainViewModel(object __instance)
    {
        if (__instance == null) return;
        _mainViewModel = __instance;
        PluginLog.LogTrace($"MainViewModel captured: {TypeName(__instance)}");
    }
    public static string CurrentValue(object instance)
    {
        foreach (var m in new[] { "Value", "CombinedValue", "NegativeValue", "PositiveValue", "Color", "IsChecked" })
        {
            var v = Get(instance, m);
            if (v != null) return SafeStr(v);
        }
        return "?";
    }
    public static void OnValuePre(object __instance, object[] __args, MethodBase __originalMethod)
    {
        // Diagnostic-only (REFRESH/EDIT log + optional param_trace.log). It reflects on every value
        // setter, so skip it entirely unless debug is on. The file trace is a dump artifact only.
        if (!ServerSettings.DebugArtifacts) return;
        try
        {
            var isRefresh = _refreshDepth > 0;
            var label = TryGetLabel(__instance);
            var oldv = CurrentValue(__instance);
            var newv = SafeStr(First(__args));
            if (oldv == newv) return; // silent no-op (refresh or redundant edit)
            var shortType = (TypeName(__instance) ?? "?").Split('.').Last();
            if (ServerSettings.DumpArtifacts)
            {
                var kind = isRefresh ? "REFRESH" : "EDIT";
                try
                {
                    lock (_fileLock)
                        File.AppendAllText(_tracePath, $"{kind}\td={_refreshDepth}\t{shortType}\t[{label}]\t{oldv}->{newv}\t{StackTop()}\n", Utf8);
                }
                catch { }
            }
            if (isRefresh)
                PluginLog.LogDebug($">>> PARAM[REFRESH] {shortType} [{label}] {oldv} -> {newv}");
            else
                PluginLog.LogTrace($">>> PARAM[EDIT] {shortType} [{label}] {oldv} -> {newv}");
        }
        catch { }
    }
    public static void OnRefreshEnd() { if (_refreshDepth > 0) _refreshDepth--; }
    public static void OnCameraPosition(object __instance) { if (__instance != null) _cameraPosition = __instance; }
    public static void OnCameraController(object __instance)
    {
        if (__instance == null) return;
        if (_cameraController == null) PluginLog.LogTrace($"camera controller captured: {TypeName(__instance)}");
        _cameraController = __instance;
    }
    public static void OnColorSel(object __instance, object[] __args)
    {
        try
        {
            var t = TypeName(__instance) ?? "?";
            var label = Get(__instance, "TranslatedDisplayName") ?? TryGetLabel(__instance);
            var col = Get(__instance, "Color") ?? Get(__instance, "MaterialColor") ?? Get(__instance, "HairMaterialTemplateColor");
            var arg = First(__args);
            var tn = t.Split('.').Last();
            var ls = SafeStr(label); var cs = SafeStr(col); var as_ = SafeStr(arg);
            ColorRegistry.Register(__instance);
            // The live hair VM is whichever instance the editor just used; remember it so the
            // panel-active signal and the colour setter always refer to the same object.
            if (t.EndsWith("HairEditor.ViewModel")) _colorHairVm = __instance;
            PluginLog.LogTrace($"COLORSEL {tn} name={ls} color={cs} arg={as_}");
        }
        catch { }
    }
    public static void OnColorSelCtor(object __instance)
    {
        ColorRegistry.Register(__instance);
        if ((TypeName(__instance) ?? "").EndsWith("HairEditor.ViewModel")) _colorHairVm = __instance;
    }
    public static void OnParamCtor(object __instance)
    {
        Catalog.RegisterParam(__instance);
    }
    public static void PatchDerivedCtors(string baseTypeName, HarmonyMethod postfix)
    {
        var baseType = FindType(baseTypeName);
        if (baseType == null) { PluginLog.LogError($"type not found: {baseTypeName}"); return; }
        int n = 0;
        foreach (var t in AllTypesQuiet())
        {
            if (t == baseType || t.IsAbstract || !baseType.IsAssignableFrom(t)) continue;
            foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                try { Plugin.Harmony.Patch(c, postfix: postfix); n++; }
                catch { }
            }
        }
        PluginLog.LogInfo($"patched {n} ctor(s) derived from {baseType.FullName}");
    }
    // ---- runtime (on-demand) hooks: patch an arbitrary method to the same OnHook logger, so new
    // candidates can be discovered without rebuilding the plugin (dev/TCP only). ----
    private static HarmonyMethod _onHookMethod;
    private static readonly List<string> _dynamicHooks = new();
    public static HarmonyMethod OnHookMethod => _onHookMethod ??= new HarmonyMethod(
        typeof(Hooks).GetMethod(nameof(OnHook), BindingFlags.Public | BindingFlags.Static));
    public static List<string> DynamicHooks() => _dynamicHooks;
    public static void AddDynamicHook(string typeName, string methodName)
    {
        PatchAllByName(typeName, methodName, OnHookMethod);
        var key = $"{typeName}.{methodName}";
        if (!_dynamicHooks.Contains(key)) _dynamicHooks.Add(key);
    }
    public static int RemoveDynamicHook(string typeName, string methodName)
    {
        var type = FindType(typeName);
        if (type == null) return -1;
        int n = 0;
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (m.Name != methodName) continue;
            try { Plugin.Harmony.Unpatch(m, HarmonyPatchType.All, Plugin.Harmony.Id); n++; } catch { }
        }
        _dynamicHooks.RemoveAll(x => x == $"{typeName}.{methodName}");
        return n;
    }

    public static void PatchAllByName(string typeName, string methodName, HarmonyMethod patch, bool prefix = false)
    {
        var type = FindType(typeName);
        if (type == null) { PluginLog.LogError($"type not found: {typeName}"); return; }
        int n = 0;
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (m.Name != methodName) continue;
            try { if (prefix) Plugin.Harmony.Patch(m, prefix: patch); else Plugin.Harmony.Patch(m, postfix: patch); n++; }
            catch (Exception e) { PluginLog.LogError($"patch failed {typeName}.{methodName}: {e.Message}"); }
        }
        PluginLog.LogInfo($"patched {n} overload(s) of {type.FullName}.{methodName}");
    }
    public static void PatchOwnCtors(string typeName, HarmonyMethod postfix)
    {
        var type = FindType(typeName);
        if (type == null) { PluginLog.LogError($"type not found: {typeName}"); return; }
        int n = 0;
        foreach (var c in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            try { Plugin.Harmony.Patch(c, postfix: postfix); n++; }
            catch (Exception e) { PluginLog.LogError($"ctor patch failed {typeName}: {e.Message}"); }
        }
        PluginLog.LogInfo($"patched {n} ctor(s) of {type.FullName}");
    }
    public static void PatchByTypeName(string typeName, string methodName, HarmonyMethod patch, bool prefix = false)
    {
        var type = FindType(typeName);
        if (type == null) { PluginLog.LogError($"type not found: {typeName}"); return; }
        var method = AccessTools.Method(type, methodName);
        if (method == null) { PluginLog.LogError($"method not found: {typeName}.{methodName}"); return; }
        try
        {
            if (prefix) Plugin.Harmony.Patch(method, prefix: patch);
            else Plugin.Harmony.Patch(method, postfix: patch);
            PluginLog.LogInfo($"patched {type.FullName}.{methodName}");
        }
        catch (Exception e) { PluginLog.LogError($"patch failed {typeName}.{methodName}: {e.Message}"); }
    }
}
