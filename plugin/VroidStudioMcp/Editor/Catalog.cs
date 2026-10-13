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
using static VroidStudioMcp.Logging;

namespace VroidStudioMcp;

public static class Catalog
{
    private static bool _requested;
    private static DateTime _lastDump;
    private static DateTime _lastParamDump = DateTime.MinValue;
    private static int _lastParamCount;
    private static readonly ConcurrentBag<object> ParamRegistry = new();
    private static readonly HashSet<string> DumpedSelectors = new();

    /// <summary>Parameter VM captured by a ctor hook; dumped to catalog_params.json when debug artifacts are on.</summary>
    internal static void RegisterParam(object vm) { if (vm != null) ParamRegistry.Add(vm); }

    /// <summary>Drop the per-session dump bookkeeping (called when returning to the model select).</summary>
    internal static void ResetDumps()
    {
        DumpedSelectors.Clear();
        // Part-refs captured by the ctor hooks belong to the session that just ended; without this the
        // bag would grow for the lifetime of the process across every open/close cycle (debug only).
        ParamRegistry.Clear();
        _lastParamCount = 0;
        _lastParamDump = DateTime.MinValue;
    }

    /// <summary>Ask for a full catalog dump on the next tick (avoid dumping on every event).</summary>
    public static void RequestDump() => _requested = true;

    /// <summary>Per-frame: dump selectors/params once and honor a requested full dump.</summary>
    public static void Tick()
    {
        if (!ServerSettings.DumpArtifacts) return;
        if (_requested && (DateTime.UtcNow - _lastDump).TotalSeconds > 1.0)
        {
            _requested = false;
            _lastDump = DateTime.UtcNow;
            DumpCatalog();
        }
        foreach (var kv in Selectors.All)
        {
            if (DumpedSelectors.Contains(kv.Key)) continue;
            DumpedSelectors.Add(kv.Key);
            DumpSelectorFile(kv.Key, kv.Value);
        }
        int count = ParamRegistry.Count;
        if (count != _lastParamCount && (DateTime.UtcNow - _lastParamDump).TotalSeconds > 2.0)
        {
            _lastParamCount = count;
            _lastParamDump = DateTime.UtcNow;
            DumpParams();
        }
    }

    public static object DumpPresets(object opts)
    {
        var arr = new List<object>();
        foreach (var o in Enumerate(opts))
        {
            var d = new Dictionary<string, object> { ["type"] = TypeName(o) };
            d["transferableGroupId"] = Get(o, "TransferableGroupId") as string;
            d["isSelected"] = Get(o, "IsSelected");
            d["label"] = TryGetLabel(o);
            var model = Get(o, "Model");
            if (model != null)
            {
                var rn = Get(model, "resourceName") as string;
                d["resourceName"] = rn;
                d["itemId"] = Get(model, "itemId") as string;
                if (rn != null) d["displayName"] = TransferableGroupDisplayName(rn);
            }
            arr.Add(d);
        }
        return arr;
    }
    public static object DumpParameter(object p)
    {
        var d = new Dictionary<string, object> { ["type"] = TypeName(p), ["label"] = TryGetLabel(p) };
        var val = Get(p, "Value");
        if (val != null) { try { d["value"] = Convert.ToDouble(val); } catch { } }
        var cv = Get(p, "CombinedValue");
        if (cv != null) { try { d["combinedValue"] = Convert.ToDouble(cv); } catch { } }
        var col = Get(p, "Color");
        if (col != null) d["color"] = SafeStr(col);
        d["min"] = Get(p, "MinValue");
        d["max"] = Get(p, "MaxValue");
        d["isEditable"] = Get(p, "IsEditable");
        var key = Get(p, "_path") ?? Get(p, "_pathNegative") ?? Get(p, "_editableElementKey") ?? Get(p, "_parameterKey");
        if (key != null) d["key"] = SafeStr(key);
        return d;
    }
    public static Dictionary<string, object> DumpExtraMembers(object cat)
    {
        var result = new Dictionary<string, object>();
        try
        {
            var obj = RealObj(cat);
            var t = obj.GetType();
            if (!(t.Name.Contains("Expression") || t.Name.Contains("Part") || t.Name.Contains("Export")))
                return result;
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                if (v == null || v is string || v.GetType().IsPrimitive) continue;
                if (v is IEnumerable)
                {
                    var items = new List<object>();
                    foreach (var it in Enumerate(v)) items.Add(DumpParameter(it));
                    if (items.Count > 0) result[p.Name] = items;
                }
            }
        }
        catch { }
        return result;
    }
    public static object DumpCategory(object cat, int depth)
    {
        if (cat == null) return null;
        if (depth > 10) return new Dictionary<string, object> { ["type"] = TypeName(cat), ["truncated"] = true };
        var d = new Dictionary<string, object>
        {
            ["type"] = TypeName(cat),
            ["name"] = Get(cat, "Name") as string ?? Get(cat, "TranslatedDisplayName") as string,
            ["translationKey"] = Get(cat, "TranslationKey") as string,
            ["icon"] = Get(cat, "Icon") as string,
            ["tgtype"] = Get(cat, "TransferableGroupType") as string,
        };

        var children = new List<object>();
        foreach (var c in Enumerate(Get(cat, "Children"))) children.Add(DumpCategory(c, depth + 1));
        if (children.Count > 0) d["children"] = children;

        var pars = Get(cat, "Parameters");
        if (pars != null)
        {
            var list = new List<object>();
            foreach (var p in Enumerate(pars)) list.Add(DumpParameter(p));
            d["parameters"] = list;
        }

        var opts = Get(cat, "PresetOptions") ?? Get(cat, "_presets") ?? Get(cat, "_presetsNotSorted");
        if (opts != null)
        {
            d["presetOptionsType"] = TypeName(opts);
            d["presetCountProp"] = Get(opts, "Count") ?? Get(opts, "Length");
            d["presets"] = DumpPresets(opts);
        }

        var subParam = Get(cat, "ParameterCategoryViewModel");
        if (subParam != null) d["parameterCategory"] = DumpCategory(subParam, depth + 1);
        var subPreset = Get(cat, "PresetCategoryViewModel");
        if (subPreset != null) d["presetCategory"] = DumpCategory(subPreset, depth + 1);

        var extra = DumpExtraMembers(cat);
        if (extra.Count > 0) d["extra"] = extra;

        return d;
    }
    public static object DumpSelector(string typeName, object selector)
    {
        var d = new Dictionary<string, object> { ["selectorType"] = typeName };
        d["categories"] = DumpCategory(Get(selector, "Categories"), 0);
        return d;
    }
    public static void DumpCatalog()
    {
        try
        {
            var root = new Dictionary<string, object> { ["selectors"] = new List<object>() };
            var list = (List<object>)root["selectors"];
            foreach (var kv in Selectors.All)
            {
                try { list.Add(DumpSelector(kv.Key, kv.Value)); }
                catch (Exception e) { list.Add(new Dictionary<string, object> { ["selectorType"] = kv.Key, ["error"] = e.Message }); }
            }
            var json = MiniJson.Serialize(root, true);
            var path = Path.Combine(BepInEx.Paths.GameRootPath, "category_catalog.json");
            File.WriteAllText(path, json, Utf8);
            PluginLog.LogWarning($"catalog written: {path} ({Selectors.Count} selectors)");
        }
        catch (Exception e) { PluginLog.LogError($"catalog dump failed: {e}"); }
    }
    public static void DumpSelectorFile(string typeName, object selector)
    {
        try
        {
            var d = DumpSelector(typeName, selector);
            var safe = (typeName ?? "selector").Replace("`", "").Replace('.', '_');
            var path = Path.Combine(BepInEx.Paths.GameRootPath, $"catalog_{safe}.json");
            File.WriteAllText(path, MiniJson.Serialize(d, true), Utf8);
            PluginLog.LogWarning($"catalog written: {path}");
        }
        catch (Exception e) { PluginLog.LogError($"selector dump failed {typeName}: {e.Message}"); }
    }
    public static void DumpParams()
    {
        try
        {
            var seen = new HashSet<string>();
            var list = new List<object>();
            foreach (var o in ParamRegistry)
            {
                try
                {
                    var t = TypeName(o) ?? "?";
                    var label = TryGetLabel(o);
                    var key = t + "|" + label;
                    if (!seen.Add(key)) continue;
                    var d = new Dictionary<string, object> { ["type"] = t, ["label"] = label };
                    var v = Get(o, "Value");
                    if (v != null) { try { d["value"] = Convert.ToDouble(v); } catch { } }
                    var cv = Get(o, "CombinedValue");
                    if (cv != null) { try { d["combinedValue"] = Convert.ToDouble(cv); } catch { } }
                    var col = Get(o, "Color");
                    if (col != null) d["color"] = SafeStr(col);
                    d["min"] = Get(o, "MinValue");
                    d["max"] = Get(o, "MaxValue");
                    var tk = Get(o, "_translationKey") as string;
                    if (tk != null) d["translationKey"] = tk;
                    list.Add(d);
                }
                catch { }
            }
            var path = Path.Combine(BepInEx.Paths.GameRootPath, "catalog_params.json");
            File.WriteAllText(path, MiniJson.Serialize(new Dictionary<string, object> { ["params"] = list }, true), Utf8);
            PluginLog.LogWarning($"catalog_params written: {list.Count} params");
        }
        catch (Exception e) { PluginLog.LogError($"params dump failed: {e.Message}"); }
    }
}
