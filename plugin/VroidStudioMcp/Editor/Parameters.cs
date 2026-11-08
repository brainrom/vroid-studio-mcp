using System;
using System.Collections.Generic;
using System.Linq;
using static VroidStudioMcp.Interop;
using static VroidStudioMcp.VroidState;

namespace VroidStudioMcp;

/// <summary>Builds the editable parameter list (numeric + colour) of a category tab and serializes the
/// command payload. This is the layer that knows the internal VM shapes for parameters/colours.</summary>
public static class Parameters
{
    public sealed class ParamRef
    {
        public int Index; public string Kind; public string Label;
        public string Name; public string Group; public string Id; public string Path;
        public object Target; public object Value; public object Min; public object Max;
        public string ColorId; public UnityEngine.Color? Color; public string Setter;
        public string Key; public string Groups; public bool Global;
    }

    public static List<ParamRef> Build(string section, string tab, object category)
    {
        var list = new List<ParamRef>();
        var paramNode = Get(category, "ParameterCategoryViewModel");
        var pars = paramNode != null ? Get(paramNode, "Parameters") : Get(category, "Parameters");
        if (pars != null)
            foreach (var p in Enumerate(pars))
            {
                bool combined = Get(p, "CombinedValue") != null;
                var label = TryGetLabel(p);
                list.Add(new ParamRef
                {
                    Index = list.Count,
                    Kind = combined ? "combined" : "float",
                    Label = label,
                    Path = $"{Sections.SectionLabel(section)}/{tab}/{label}",
                    Target = p,
                    Value = combined ? Get(p, "CombinedValue") : Get(p, "Value"),
                    Min = Get(p, "MinValue"),
                    Max = Get(p, "MaxValue"),
                    Key = ParamKey(p),
                });
            }

        // Clothing (Outfit) has no ParameterCategoryViewModel: its sliders live on the currently
        // selected item instance (the fitting/property panel), exposed as BlendShapeParameters.
        if (section == "ClothingEditor")
        {
            var selector = Categories.FindSelectorByShort(section + ".PresetsSelectorViewModel");
            var editorVm = Get(Get(selector, "_editRootScreen"), "ClothingEditor");
            var instance = Get(editorVm, "SelectedInstance");
            var bsp = Get(instance, "BlendShapeParameters");
            if (bsp != null)
            {
                var raw = Enumerate(bsp).ToList();
                // The same slider label can occur once per mesh part (e.g. dress body vs sleeves);
                // keep the label but disambiguate the path so set_param by path stays unambiguous.
                var totals = new Dictionary<string, int>();
                foreach (var p in raw)
                {
                    var l = TryGetLabel(p);
                    if (!string.IsNullOrEmpty(l)) totals[l] = totals.TryGetValue(l, out var c) ? c + 1 : 1;
                }
                var seen = new Dictionary<string, int>();
                foreach (var p in raw)
                {
                    var label = TryGetLabel(p);
                    if (string.IsNullOrEmpty(label)) continue;
                    int n = seen.TryGetValue(label, out var c0) ? c0 + 1 : 1;
                    seen[label] = n;
                    bool combined = Get(p, "CombinedValue") != null;
                    var path = $"{Sections.SectionLabel(section)}/{tab}/{label}" + (totals[label] > 1 ? $" #{n}" : "");
                    list.Add(new ParamRef
                    {
                        Index = list.Count,
                        Kind = combined ? "combined" : "float",
                        Label = label,
                        Path = path,
                        Target = p,
                        Value = combined ? Get(p, "CombinedValue") : Get(p, "Value"),
                        Min = Get(p, "MinValue"),
                        Max = Get(p, "MaxValue"),
                        Key = ParamKey(p),
                    });
                }
            }
        }

        Colors.RefreshRegistry();
        var catType = Get(category, "TransferableGroupType")?.ToString();
        bool hairSelected = section == "HairEditor" && HairBaseSelected(category);
        var colors = Colors.Entries
            .Where(e => ColorMatches(catType, section, tab, e.Id, hairSelected))
            .OrderBy(e => e.Id)
            .ToList();
        var sharedSeen = new HashSet<string>();
        foreach (var e in colors)
        {
            var id = e.Id;
            var (group, name) = SplitColorId(id);
            var sig = e.Sig;
            // A colour whose accessor writes several transferable groups is one shared value (skin
            // base colour). Expose a single canonical entry under a section-less global path and skip
            // its per-group aliases; the group's own Dark/Light colours stay.
            bool shared = !id.StartsWith("Hair|") && Colors.IsSharedSig(sig);
            string path; bool global; string outGroup = group;
            if (shared)
            {
                if (!sharedSeen.Add(sig)) continue;
                outGroup = SharedFamily(e.Group);
                path = $"{outGroup}/{name}";
                global = true;
            }
            else
            {
                // Global hair colours use a stable section-level path (no tab): shared by every
                // Hairstyle tab. All other colours keep the Section/Category/Name form.
                global = id.StartsWith("Hair|");
                path = global ? $"{Sections.SectionLabel(section)}/{name}" : $"{Sections.SectionLabel(section)}/{tab}/{name}";
            }
            // Hair has two colors on the same VM: read the matching getter (Color is the plain
            // ColorSelection case; the hair template needs Color vs EmissionColor distinguished).
            object raw = id == "Hair|Emission"
                ? Get(e.Obj, "HairMaterialTemplateEmissionColor")
                : (Get(e.Obj, "Color") ?? Get(e.Obj, "HairMaterialTemplateColor"));
            list.Add(new ParamRef
            {
                Index = list.Count,
                Kind = "color",
                Label = name,
                Name = name,
                Group = outGroup,
                Id = id,
                Path = path,
                Target = e.Obj,
                ColorId = id,
                Setter = e.SetMethod,
                Value = ParamCodec.Hex(raw),
                Color = raw is UnityEngine.Color rc ? rc : (UnityEngine.Color?)null,
                Groups = e.Group,
                Global = global,
            });
        }
        Disambiguate(list);
        MarkMixedColorAggregates(list);
        // The clear slot removes the element entirely: the engine still holds a stale default colour
        // (e.g. lips #000000) and would report editable params. Suppress them while the right panel
        // is hidden, the same way Hair uses IsRightPanelActive. See docs/quirks.md.
        if (SuppressedByClear(section, category)) list.Clear();
        return list;
    }

    // A symmetric color like Iris "Both Eyes" mirrors one side when read. When the two sides differ,
    // a single readout is misleading, so report value=null (the client can still set it by name).
    private static void MarkMixedColorAggregates(List<ParamRef> list)
    {
        var colors = list.Where(p => p.Kind == "color" && !string.IsNullOrEmpty(p.Name)).ToList();
        foreach (var c in colors)
        {
            if (!c.Name.StartsWith("Both", StringComparison.OrdinalIgnoreCase)) continue;
            var key = SideKey(c.Name);
            var sides = colors.Where(o => o != c && o.Group == c.Group
                && (o.Name.StartsWith("L ", StringComparison.OrdinalIgnoreCase) || o.Name.StartsWith("R ", StringComparison.OrdinalIgnoreCase))
                && SideKey(o.Name) == key).ToList();
            if (sides.Count >= 2 && sides.Select(s => s.Value as string).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            { c.Value = null; c.Color = null; }
        }
    }

    private static string SideKey(string name)
    {
        var s = (name ?? "").Trim();
        if (s.StartsWith("L ", StringComparison.OrdinalIgnoreCase) || s.StartsWith("R ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        else if (s.StartsWith("Both ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5);
        return s.Trim().TrimEnd('s').ToLowerInvariant();
    }

    // System id of a parameter: the transferable key(s) it is bound to. Lets a client key off a
    // value that (unlike the label) is stable when the UI renames two sliders to the same label.
    public static string ParamKey(object p)
    {
        try
        {
            if (Get(p, "CombinedValue") != null)
            {
                var negPath = Get(p, "_pathNegative");
                var posPath = Get(p, "_pathPositive");
                var neg = negPath != null ? Invoke(negPath, "get_Key") as string : null;
                var pos = posPath != null ? Invoke(posPath, "get_Key") as string : null;
                var parts = new[] { neg, pos }.Where(x => !string.IsNullOrEmpty(x)).ToArray();
                return parts.Length > 0 ? string.Join("|", parts) : null;
            }
            var path = Get(p, "_path") ?? Get(p, "Path");
            var k = path != null ? Invoke(path, "get_Key") as string : null;
            return k ?? (Get(p, "_boneElementKey") as string);
        }
        catch { return null; }
    }

    // Two sliders in the same tab can share a label (e.g. dress body vs sleeves). Append #N in
    // display order so the stable path stays addressable; only when the label actually collides.
    private static void Disambiguate(List<ParamRef> list)
    {
        var totals = list.GroupBy(p => p.Path).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>();
        foreach (var p in list)
        {
            if (!totals.TryGetValue(p.Path, out var total) || total <= 1) continue;
            int n = seen.TryGetValue(p.Path, out var c) ? c + 1 : 1;
            seen[p.Path] = n;
            p.Path = $"{p.Path} #{n}";
        }
    }

    // Canonical family for a shared colour: prefer the skin family, else the shortest group name.
    private static string SharedFamily(string groupsCsv)
    {
        if (string.IsNullOrEmpty(groupsCsv)) return "Shared";
        var groups = groupsCsv.Split(',');
        foreach (var g in groups) if (g.IndexOf("Skin", StringComparison.OrdinalIgnoreCase) >= 0) return "Skin";
        return groups.OrderBy(g => g.Length).First();
    }

    // True when the category has a Clear slot that is currently active: the element is gone, so its
    // parameters must not be reported. Face overlays expose IsRightPanelActive (same signal Hair uses).
    private static bool SuppressedByClear(string section, object category)
    {
        try
        {
            if (!HasClearPreset(category)) return false;
            if (section == "FaceEditor")
            {
                var fe = Get(_editRootVm, "FaceEditor");
                if (Get(fe, "IsRightPanelActive") is bool active) return !active;
            }
        }
        catch { }
        return false;
    }

    public static bool ColorMatches(string catType, string section, string tab, string id, bool hairSelected)
    {
        // Hair colors are global (not tied to a tab's group type), and the editor only shows them
        // once a base preset is applied -> expose them on any hair tab only when a base is selected.
        // Base Hair is the exception: it has its own single colour (group "Main Color"); the global
        // Main/Highlight dials do not apply to it, so do not report them there.
        if (id.StartsWith("Hair|")) return section == "HairEditor" && hairSelected && tab != "Base Hair";
        return catType != null && id.StartsWith(catType + "|");
    }

    // True when the editor's hair colour controls are actually shown (a hair template is active).
    // `HairEditor.ViewModel.IsRightPanelActive` is the exact UI signal (unlike
    // SelectedHairMaterialTemplate, which stays non-null even on the clear slot). Falls back to the
    // per-tab selected group id if the hair VM was not captured.
    public static bool HairBaseSelected(object category)
    {
        try
        {
            var vm = Colors._colorHairVm;
            if (vm != null && Get(vm, "IsRightPanelActive") is bool active) return active;
        }
        catch { }
        try
        {
            var presetNode = Get(category, "PresetCategoryViewModel");
            var src = (object)presetNode ?? category;
            var gid = Get(src, "_selectedTransferableGroupId") as string;
            if (!string.IsNullOrEmpty(gid)) return true;
            return Enumerate(Get(src, "PresetOptions")).Any(o =>
                !(TypeName(o) ?? "").Contains("ClearOption") && Get(o, "IsSelected") is bool b && b);
        }
        catch { return false; }
    }

    /// <summary>Number of color parameters a tab exposes (colors are not counted by `params`).</summary>
    public static int CountColors(string section, string tab, object category)
    {
        var catType = Get(category, "TransferableGroupType")?.ToString();
        bool hairSelected = section == "HairEditor" && HairBaseSelected(category);
        int n = 0;
        foreach (var e in Colors.Entries)
            if (ColorMatches(catType, section, tab, e.Id, hairSelected)) n++;
        return n;
    }

    /// <summary>True when the tab has a Clear slot: selecting it removes the element
    /// entirely, so while it is active there is nothing to edit. (Distinct from `needs_preset`,
    /// which is true only when the tab is currently empty.)</summary>
    public static bool HasClearPreset(object tab)
    {
        var presetNode = Get(tab, "PresetCategoryViewModel");
        var src = (object)presetNode ?? tab;
        return Enumerate(Get(src, "PresetOptions")).Any(o => (TypeName(o) ?? "").Contains("ClearOption"));
    }

    // Consistent with `categories`: whether a tab has its OWN editable parameters. Hair tabs (except
    // Base Hair, which owns its colour) are preset-only — their Main/Emission colours are global;
    // Outfit tabs are preset-only because item sliders appear only after an item is applied. Face and
    // Body are parametric whenever they expose any parameter/colour.
    public static string TabKind(string section, string tab, bool hasOwnParams)
    {
        switch (section)
        {
            case "HairEditor": return tab == "Base Hair" ? "parametric" : "preset-only";
            case "ClothingEditor": return "preset-only";
            default: return hasOwnParams ? "parametric" : "preset-only";
        }
    }

    // Color registry ids look like "<TransferableGroupType>|<group>|<name>" (e.g. "...Iris|Color|L Eye")
    // or the short hair form "Hair|Main". The leaf name is the human label the UI shows.
    private static (string group, string name) SplitColorId(string id)
    {
        var parts = id.Split('|');
        if (parts.Length >= 3) return (parts[parts.Length - 2], parts[parts.Length - 1]);
        if (parts.Length == 2) return (parts[0], parts[1]);
        return (null, id);
    }

    public static object Serialize(List<ParamRef> list) =>
        list.Select(p =>
        {
            var d = new Dictionary<string, object>
            {
                ["index"] = p.Index,
                ["label"] = p.Label,
                ["path"] = p.Path,
                ["kind"] = p.Kind,
                ["value"] = p.Kind == "color" ? p.Value : ParamCodec.SafeNum(p.Value),
                ["min"] = p.Min,
                ["max"] = p.Max,
            };
            if (p.Kind == "color") { d["name"] = p.Name; d["group"] = p.Group; d["id"] = p.Id; }
            if (p.ColorId == "Hair|Main" || p.ColorId == "Hair|Emission")
                d["warning"] = ParamHandlers.HairGlobalWarning;
            if (!string.IsNullOrEmpty(p.Key)) d["key"] = p.Key;
            // Global colours (hair Main/Emission, the shared skin base) use a section-less path that
            // any relevant tab can address; flag it so a client knows the path is portable.
            if (p.Kind == "color" && p.Global) d["global"] = true;
            return (object)d;
        }).ToList();

    // Parameter payload shared by `enter` and `select_preset(..., params)`: the tab's editable set
    // plus readiness flags. Outfit sliders belong to the applied item, hence the section special-case.
    public static Dictionary<string, object> Payload(string section, string tab, object category)
    {
        var list = Build(section, tab, category);
        bool hasClear = HasClearPreset(category);
        var kind = section == "ClothingEditor"
            ? (list.Count > 0 ? "parametric" : "preset-only")
            : TabKind(section, tab, list.Count > 0);
        var result = new Dictionary<string, object>
        {
            ["category"] = tab,
            ["kind"] = kind,
            ["has_clear_preset"] = hasClear,
            ["parameters"] = Serialize(list.Where(p => !(p.Kind == "color" && p.Global)).ToList()),
        };
        // Shared colours (hair Main/Emission, the skin base) are one value addressed from several
        // tabs; list them separately so they are not mistaken for the category's own parameters.
        // Indices are preserved, so set_param still accepts them by index or by path.
        var globals = list.Where(p => p.Kind == "color" && p.Global).ToList();
        if (globals.Count > 0) result["globals"] = Serialize(globals);
        // A tab with a Clear slot is empty until a real preset is chosen; until then there is
        // nothing to edit (the parameter set is identical for every real preset).
        if (list.Count == 0 && hasClear) result["needs_preset"] = true;
        // Clothing sliders belong to the currently applied item, not to the tab; report which item
        // they describe (human label when known) so the client can tell whose parameters these are.
        if (section == "ClothingEditor")
        {
            var item = ClothingItemName(SelectedClothingInstance(section));
            if (!string.IsNullOrEmpty(item)) result["item"] = item;
        }
        return result;
    }

    private static object SelectedClothingInstance(string section)
    {
        var selector = Categories.FindSelectorByShort(section + ".PresetsSelectorViewModel");
        return Get(Get(Get(selector, "_editRootScreen"), "ClothingEditor"), "SelectedInstance");
    }

    // Readable name of an applied clothing item: overlay label for its resource, else the VM name.
    private static string ClothingItemName(object instance)
    {
        if (instance == null) return null;
        var info = Get(instance, "transferableGroupInfo");
        var resourceName = info != null ? Get(info, "ResourceName") as string : null;
        return PresetLabels.Get(resourceName)
            ?? (Get(instance, "Name") as string)
            ?? resourceName;
    }
}
