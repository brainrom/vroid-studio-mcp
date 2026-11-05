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

namespace VroidStudioMcp;

public static class Categories
{
    public static object FindSelectorByShort(string shortName) => Selectors.BySuffix(shortName);

    // Tabs that exist in the editor tree but are not editable through this API: `Expression Editor`
    // is only an entry button (ExpressionCategoryViewModel with no children/parameters; its data
    // lives in FaceEditor.ViewModel). Treated as nonexistent everywhere (listing and lookup).
    private static readonly HashSet<string> UnsupportedTabs = new(StringComparer.Ordinal)
    {
        "Expression Editor",
    };

    public static bool IsUnsupportedTab(string name) => name != null && UnsupportedTabs.Contains(name);
    public static int PresetCount(object cat)
    {
        var opts = Get(cat, "PresetOptions");
        if (opts == null) return 0;
        int n = 0;
        foreach (var _ in Enumerate(opts)) n++;
        return n;
    }
    public static int ParamCount(object cat)
    {
        var pars = Get(cat, "Parameters");
        if (pars == null) return 0;
        int n = 0;
        foreach (var _ in Enumerate(pars)) n++;
        return n;
    }
    public static void CollectCategories(object cat, List<object> outList)
    {
        if (cat == null) return;
        if (ParamCount(cat) > 0 || PresetCount(cat) > 0) outList.Add(cat);
        foreach (var ch in Enumerate(Get(cat, "Children"))) CollectCategories(ch, outList);
        CollectCategories(Get(cat, "ParameterCategoryViewModel"), outList);
        CollectCategories(Get(cat, "PresetCategoryViewModel"), outList);
    }
    /// <summary>Editable tab names of a selector, in tree order (unsupported/hidden tabs omitted).</summary>
    public static List<string> TabNames(object sel)
    {
        var names = new List<string>();
        foreach (var c in Enumerate(Get(Get(sel, "Categories"), "Children")))
        {
            var n = Get(c, "Name") as string;
            if (!string.IsNullOrEmpty(n) && !IsUnsupportedTab(n)) names.Add(n);
        }
        return names;
    }

    public static object FindTab(object sel, string name)
    {
        if (IsUnsupportedTab(name)) return null;
        foreach (var c in Enumerate(Get(Get(sel, "Categories"), "Children")))
            if ((Get(c, "Name") as string) == name) return c;
        return null;
    }

    /// <summary>Find the tab whose engine TransferableGroupType equals <paramref name="type"/>
    /// (e.g. TransferableGroupType.N00.Level1.HairBack). Used to resolve an imported file's own
    /// category instead of trusting the tab the caller happened to be on.</summary>
    public static object FindTabByType(object sel, string type)
    {
        if (sel == null || string.IsNullOrEmpty(type)) return null;
        foreach (var c in Enumerate(Get(Get(sel, "Categories"), "Children")))
            if (string.Equals(Get(c, "TransferableGroupType") as string, type, StringComparison.OrdinalIgnoreCase))
                return c;
        return null;
    }
}
