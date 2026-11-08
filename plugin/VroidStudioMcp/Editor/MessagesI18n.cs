using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BepInEx;
using HarmonyLib;

namespace VroidStudioMcp;

/// <summary>
/// Overlay for the engine's message table (VRoid.UI.Messages). The app resolves every UI label from
/// a localization key (e.g. Main.ModelEditor.Category.ParameterModel.name.NoseWingScaleX) through a
/// key→string lookup; we postfix that lookup and return our corrected text for the current Crowdin
/// language, falling back to the engine value. Curated overrides live in &lt;GameRoot&gt;/i18n/&lt;lang&gt;.json
/// (flat key→text), so the UI itself shows the fixed strings without touching the app files.
/// </summary>
public static class MessagesI18n
{
    private static readonly Dictionary<string, Dictionary<string, string>> Overlay = new(StringComparer.OrdinalIgnoreCase);
    private static bool _installed;

    public static string Dir => Path.Combine(BepInEx.Paths.GameRootPath, "i18n");
    public static int Count => Overlay.Sum(kv => kv.Value.Count);

    public static void Reload()
    {
        Overlay.Clear();
        try
        {
            if (!Directory.Exists(Dir)) { PluginLog.LogWarning($"i18n: no dir {Dir}"); return; }
            foreach (var f in Directory.EnumerateFiles(Dir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                var body = JsonDocument.Parse(File.ReadAllText(f)).RootElement;
                if (body.ValueKind != JsonValueKind.Object) continue;
                var lang = Path.GetFileNameWithoutExtension(f);
                // allow {"lang":"..","messages":{...}} or a flat map
                if (body.TryGetProperty("lang", out var l) && body.TryGetProperty("messages", out var m))
                    lang = l.GetString() ?? lang;
                var map = (body.TryGetProperty("messages", out var mm) && mm.ValueKind == JsonValueKind.Object)
                    ? mm : body;
                if (!Overlay.TryGetValue(lang, out var dict)) Overlay[lang] = dict = new(StringComparer.Ordinal);
                foreach (var p in map.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) dict[p.Name] = p.Value.GetString();
            }
            PluginLog.LogInfo($"i18n: {Overlay.Sum(kv => kv.Value.Count)} override(s) in {Overlay.Count} language(s) from {Dir}");
        }
        catch (Exception e) { PluginLog.LogError($"i18n: reload failed: {e.Message}"); }
    }

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Reload();
        var post = new HarmonyMethod(typeof(MessagesI18n).GetMethod(nameof(Funnel), BindingFlags.Public | BindingFlags.Static));
        // The exact funnel is not documented; patch the known key→string entry points and see.
        Patch("VRoid.UI.MessagesMap", "CurrentDictionary", post);
        Patch("VRoidStudio.UICulture.Translator", "Translate", post);
        try
        {
            var mapType = Interop.FindType("VRoid.UI.MessagesMap");
            var mapping = mapType != null ? AccessTools.Inner(mapType, "Mapping") : null;
            if (mapping != null) Patch(mapping, "get_Item", post);
            else PluginLog.LogWarning("i18n: Mapping type not found");
        }
        catch (Exception e) { PluginLog.LogWarning($"i18n: mapping patch: {e.Message}"); }
    }

    private static void Patch(string typeName, string method, HarmonyMethod post)
        => Patch(Interop.FindType(typeName), method, post);

    private static void Patch(Type t, string method, HarmonyMethod post)
    {
        var m = t != null ? AccessTools.Method(t, method) : null;
        if (m == null) { PluginLog.LogWarning($"i18n: funnel not found: {t?.FullName ?? "?"}.{method}"); return; }
        try { Plugin.Harmony.Patch(m, postfix: post); PluginLog.LogDebug($"i18n: patched {t.FullName}.{method}"); }
        catch (Exception e) { PluginLog.LogError($"i18n: patch {t.FullName}.{method}: {e.Message}"); }
    }

    // Harmony postfix for a string-returning key→string lookup. __0 binds the first argument whatever
    // its declared name is. Unknown keys keep the engine value.
    public static void Funnel(string __0, ref string __result)
    {
        try
        {
            if (__0 == null || Overlay.Count == 0) return;
            var lang = CurrentLang();
            if (lang != null && Overlay.TryGetValue(lang, out var dict) && dict.TryGetValue(__0, out var v))
                __result = v;
        }
        catch { }
    }

    // Funnel runs on every UI string; resolving this every time used to be the single hottest path
    // in the plugin. Cache the property and read its value per call.
    private static PropertyInfo _langProp;

    private static PropertyInfo LangProp()
    {
        if (_langProp != null) return _langProp;
        var t = Interop.FindType("VRoid.UI.Messages.Messages");
        return _langProp = t != null ? AccessTools.Property(t, "CurrentCrowdinLanguageCode") : null;
    }

    private static string CurrentLang()
    {
        try { return LangProp()?.GetValue(null) as string; }
        catch { return null; }
    }

    /// <summary>Get/set the engine's current Crowdin language code (dev/testing). Null code reads it.</summary>
    public static string Language(string code)
    {
        try
        {
            var p = LangProp();
            if (!string.IsNullOrEmpty(code)) p?.SetValue(null, code);
            return p?.GetValue(null) as string;
        }
        catch { return null; }
    }

    // Dev-only: read the engine's message table (VRoid.UI.Messages) and dump one language to
    // messages_<code>.json. When `lang` is null, lists the language codes and their key counts. The
    // table is the engine's source of truth (strings live in s_localeStringDictionary, keyed by Crowdin
    // language code); reading it lets us overlay a corrected translation without touching the app.
    public static object Dump(string lang, out string error)
    {
        error = null;
        var type = Interop.FindType("VRoid.UI.Messages.Messages");
        if (type == null) { error = "Messages type not found"; return null; }
        var propDict = AccessTools.Property(type, "CurrentDictionary");
        var propCode = AccessTools.Property(type, "CurrentCrowdinLanguageCode");
        if (propDict == null || propCode == null) { error = "Messages properties not found"; return null; }
        string orig = null;
        try { orig = propCode.GetValue(null) as string; } catch { }
        var codes = DiscoverLanguages();
        if (lang == null)
        {
            var counts = new Dictionary<string, object>();
            foreach (var c in codes)
            {
                try { propCode.SetValue(null, c); counts[c] = DictPairs(propDict.GetValue(null)).Count; }
                catch (Exception e) { counts[c] = "err:" + e.Message; }
            }
            if (orig != null) { try { propCode.SetValue(null, orig); } catch { } }
            return new Dictionary<string, object> { ["languages"] = codes, ["counts"] = counts, ["current"] = orig };
        }
        var map = new Dictionary<string, object>();
        var propCurrent = AccessTools.Property(type, "Current");
        try
        {
            if (!string.Equals(orig, lang, StringComparison.OrdinalIgnoreCase)) propCode.SetValue(null, lang);
            var dict = propDict.GetValue(null);
            foreach (var kv in DictPairs(dict)) map[kv.Key] = kv.Value;
            // Full UI text lives in the strongly-typed Messages tree; each leaf string is a message
            // keyed by its property path. Walk it (bounded) to collect every string for this language.
            var current = propCurrent?.GetValue(null);
            if (current != null) WalkMessages(current, "", map, 0);
        }
        finally { if (orig != null) { try { propCode.SetValue(null, orig); } catch { } } }
        var path = Path.Combine(BepInEx.Paths.GameRootPath, $"messages_{lang}.json");
        try { File.WriteAllText(path, MiniJson.Serialize(map, true)); }
        catch (Exception e) { error = e.Message; return null; }
        return new Dictionary<string, object>
        {
            ["lang"] = lang,
            ["count"] = map.Count,
            ["file"] = path,
            ["sample"] = map.Keys.Take(8).Select(k => $"{k}={map[k]}").ToList(),
        };
    }

    // Recurse the VRoid.UI.Messages object graph, collecting non-empty string leaf properties keyed by
    // their dotted property path (which is the engine's message key).
    private static int WalkMessages(object node, string prefix, Dictionary<string, object> map, int depth)
    {
        if (node == null || depth > 14) return 0;
        var real = Interop.RealObj(node);
        if (real == null) return 0;
        int n = 0;
        foreach (var p in real.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.GetGetMethod() == null) continue;
            // Il2CppInterop also surfaces compiler-generated backing fields as properties; skip them so
            // keys use the declared property names (e.g. ...ParameterModel.name.NoseSize).
            if (p.Name.Contains("k__BackingField")) continue;
            var pt = p.PropertyType;
            if (pt == typeof(string))
            {
                string v = null;
                try { v = p.GetValue(real) as string; } catch { }
                if (!string.IsNullOrEmpty(v)) { map[prefix + p.Name] = v; n++; }
            }
            else if (pt.IsClass && pt != typeof(string) && (pt.FullName ?? "").StartsWith("VRoid.UI.Messages"))
            {
                object child = null;
                try { child = p.GetValue(real); } catch { }
                if (child != null) n += WalkMessages(child, prefix + p.Name + ".", map, depth + 1);
            }
        }
        return n;
    }

    // Language codes the engine knows, from LanguageModeMap.LanguageEntry.CrowdinLanguageCode.
    private static List<string> DiscoverLanguages()
    {
        var res = new List<string>();
        try
        {
            var mapType = Interop.FindType("VRoid.UI.EditorOption.LanguageModeMap");
            var entries = AccessTools.Field(mapType, "s_languageEntries")?.GetValue(null);
            foreach (var e in Interop.Enumerate(entries))
            {
                var c = Interop.Get(e, "CrowdinLanguageCode") as string;
                if (!string.IsNullOrEmpty(c) && !res.Contains(c)) res.Add(c);
            }
        }
        catch { }
        if (res.Count == 0) res.AddRange(new[] { "en", "ja", "ko", "zh-CN", "zh-TW" });
        return res;
    }

    // Enumerate an Il2Cpp Dictionary<string,string> proxy. Il2CppInterop collections usually expose
    // only the generic IEnumerable<KeyValuePair<,>>, so walk that interface via reflection.
    private static List<KeyValuePair<string, string>> DictPairs(object dict)
    {
        var res = new List<KeyValuePair<string, string>>();
        if (dict == null) return res;
        try
        {
            // Preferred: Keys/Values CopyTo into Il2Cpp string arrays (works even when the proxy
            // implements no IEnumerable interface).
            var keys = Interop.Get(dict, "Keys");
            var vals = Interop.Get(dict, "Values");
            int n = 0;
            try { n = Convert.ToInt32(Interop.Get(dict, "Count")); } catch { }
            if (n > 0 && keys != null && vals != null)
            {
                var ka = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(n);
                var va = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(n);
                Interop.Invoke(keys, "CopyTo", ka, 0);
                Interop.Invoke(vals, "CopyTo", va, 0);
                for (int i = 0; i < n; i++)
                {
                    var k = ka[i]; var v = va[i];
                    if (k != null && v != null) res.Add(new KeyValuePair<string, string>(k, v));
                }
                if (res.Count > 0) return res;
            }
            var t = dict.GetType();
            var en = (object)(dict as System.Collections.IEnumerable);
            if (en != null)
            {
                foreach (var kv in (System.Collections.IEnumerable)en)
                {
                    var k = Interop.Get(kv, "Key"); var v = Interop.Get(kv, "Value");
                    if (k is string ks && v is string vs) res.Add(new KeyValuePair<string, string>(ks, vs));
                }
                return res;
            }
            foreach (var iface in t.GetInterfaces())
            {
                if (!iface.IsGenericType || iface.GetGenericTypeDefinition() != typeof(IEnumerable<>)) continue;
                var m = iface.GetMethod("GetEnumerator");
                var e = m.Invoke(dict, null);
                var et = e.GetType();
                var mv = et.GetMethod("MoveNext");
                var cur = et.GetProperty("Current");
                while (mv != null && (bool)mv.Invoke(e, null))
                {
                    var kv = cur.GetValue(e);
                    var k = Interop.Get(kv, "Key"); var v = Interop.Get(kv, "Value");
                    if (k is string ks && v is string vs) res.Add(new KeyValuePair<string, string>(ks, vs));
                }
                if (res.Count > 0) break;
            }
        }
        catch (Exception e) { PluginLog.LogWarning($"i18n dict enum failed: {e.Message}"); }
        return res;
    }
}
