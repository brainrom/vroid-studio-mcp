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

public static class Presets
{
    // Single definition of a preset's public display name, shared by `presets`, the preview commands
    // and `select_preset`: the overlay label when known, otherwise the internal id (resourceName,
    // then itemId). The raw ids stay on the dev/TCP surface (`presets_full`).
    public static string DisplayName(object option)
    {
        if ((TypeName(option) ?? "").Contains("ClearOption")) return "(clear)";
        var model = Get(option, "Model");
        var resourceName = Get(model, "resourceName") as string;
        var itemId = Get(model, "itemId") as string;
        return PresetLabels.Get(resourceName, itemId) ?? resourceName ?? itemId;
    }

    /// <summary>Custom items of a category ("Custom" sub-tab): the saved user-created presets. They
    /// live on <c>PresetCategoryViewModel.CustomItemOptions</c> (backing field <c>_customItems</c>),
    /// not in the stock <c>PresetOptions</c>; the registry stores them as *.vroidcustomitem files.</summary>
    public static List<object> CustomItems(object presetCategoryNode)
    {
        var list = new List<object>();
        if (presetCategoryNode == null) return list;
        var items = Get(presetCategoryNode, "_customItems") ?? Get(presetCategoryNode, "CustomItemOptions");
        int i = 0;
        foreach (var o in Enumerate(items))
        {
            var type = TypeName(o) ?? "";
            var kind = type.EndsWith("AddCustomItemOptionViewModel") ? "add"
                     : type.EndsWith("ImportCustomItemOptionViewModel") ? "import" : "custom";
            var name = kind == "custom" ? (Get(o, "DisplayName") as string ?? TryGetLabel(o) ?? "(custom)")
                     : $"<{kind}>";
            list.Add(new Dictionary<string, object>
            {
                ["index"] = i++,
                ["name"] = name,
                ["kind"] = kind,
                ["selected"] = kind == "custom" ? Get(o, "IsSelected") : null,
                ["tgtype"] = Get(o, "TransferableGroupTypeId") as string,
                ["type"] = type,
            });
        }
        return list;
    }

    /// <summary>One entry of the unified preset list: custom items first, then the stock presets
    /// (Clear included). <c>Id</c> is the continuous 0-based position and is the value used by
    /// select_preset / preset_preview_*.</summary>
    public sealed class Entry
    {
        public int Id;
        public string Kind;   // clear | preset | custom
        public string Name;
        public object Option;
        public string ResourceName;
        public string ItemId;
        public string File;
        public string CustomId;
        public bool IsCustom => Kind == "custom";
    }

    /// <summary>Unified preset list of a tab. <paramref name="source"/>: all (default) | presets | custom.
    /// Custom items come first (so clients notice the user's own presets), then the stock PresetOptions
    /// (Clear at its usual position).</summary>
    public static List<Entry> Entries(string selectorShort, string tab, string source)
    {
        var list = new List<Entry>();
        var selector = Categories.FindSelectorByShort(selectorShort);
        var category = Categories.FindTab(selector, tab);
        if (category == null) return list;
        var presetNode = Get(category, "PresetCategoryViewModel") ?? category;

        source = string.IsNullOrEmpty(source) ? "all" : source.ToLowerInvariant();
        bool wantCustom = source == "all" || source == "custom";
        bool wantPreset = source == "all" || source == "presets";

        if (wantCustom)
        {
            foreach (var o in Enumerate(Get(presetNode, "_customItems") ?? Get(presetNode, "CustomItemOptions")))
            {
                var t = TypeName(o) ?? "";
                if (t.EndsWith("AddCustomItemOptionViewModel") || t.EndsWith("ImportCustomItemOptionViewModel")) continue;
                var model = Get(o, "_model");
                list.Add(new Entry
                {
                    Kind = "custom",
                    Name = Get(o, "DisplayName") as string ?? TryGetLabel(o) ?? "(custom)",
                    Option = o,
                    File = Get(model, "FilePath") as string,
                    CustomId = SafeStr(Get(model, "CustomItemId")),
                });
            }
        }
        if (wantPreset)
        {
            foreach (var o in Enumerate(Get(presetNode, "PresetOptions")))
            {
                var model = Get(o, "Model");
                bool clear = (TypeName(o) ?? "").Contains("ClearOption");
                list.Add(new Entry
                {
                    Kind = clear ? "clear" : "preset",
                    Name = DisplayName(o),
                    Option = o,
                    ResourceName = Get(model, "resourceName") as string,
                    ItemId = Get(model, "itemId") as string,
                });
            }
        }
        for (int i = 0; i < list.Count; i++) list[i].Id = i;
        return list;
    }

    /// <summary>Find an entry by its continuous id (the value select_preset accepts).</summary>
    public static Entry EntryAt(string selectorShort, string tab, int id)
    {
        var entries = Entries(selectorShort, tab, "all");
        return id >= 0 && id < entries.Count ? entries[id] : null;
    }

    public static object CustomItemAt(object presetCategoryNode, int index)
    {
        var items = Get(presetCategoryNode, "_customItems") ?? Get(presetCategoryNode, "CustomItemOptions");
        return Enumerate(items).ElementAtOrDefault(index);
    }

    /// <summary>The saved custom items (excludes the Create New / Import tiles), in display order.</summary>
    public static List<object> CustomItemRefs(object presetCategoryNode)
    {
        var list = new List<object>();
        if (presetCategoryNode == null) return list;
        foreach (var o in Enumerate(Get(presetCategoryNode, "_customItems") ?? Get(presetCategoryNode, "CustomItemOptions")))
        {
            var t = TypeName(o) ?? "";
            if (t.EndsWith("AddCustomItemOptionViewModel") || t.EndsWith("ImportCustomItemOptionViewModel")) continue;
            list.Add(o);
        }
        return list;
    }

    /// <summary>The item that appeared after a create: the first one not in <paramref name="before"/>.</summary>
    public static object NewCustomItem(object presetCategoryNode, List<object> before)
    {
        foreach (var o in CustomItemRefs(presetCategoryNode))
            if (before == null || !before.Any(b => ReferenceEquals(b, o))) return o;
        return null;
    }

    /// <summary>The active (applied) preset of a category when it is a custom item, else null. This is
    /// the guard for editing: only custom items can be procedurally sculpted, stock presets are
    /// freehand and cannot.</summary>
    public static Entry SelectedCustomEntry(string selectorShort, string tab)
    {
        foreach (var e in Entries(selectorShort, tab, "custom"))
            if (Get(e.Option, "IsSelected") is bool b && b) return e;
        return null;
    }

    /// <summary>Number of real saved custom items (excludes the add/import tiles).</summary>
    public static int CustomItemCount(object presetCategoryNode)
    {
        int n = 0;
        foreach (var o in Enumerate(Get(presetCategoryNode, "_customItems") ?? Get(presetCategoryNode, "CustomItemOptions")))
        {
            var t = TypeName(o) ?? "";
            if (!t.EndsWith("AddCustomItemOptionViewModel") && !t.EndsWith("ImportCustomItemOptionViewModel")) n++;
        }
        return n;
    }

    /// <summary>First custom-item tile of a kind ("add"/"import"/"custom").</summary>
    public static object CustomItemOfKind(object presetCategoryNode, string kind)
    {
        foreach (var o in Enumerate(Get(presetCategoryNode, "_customItems") ?? Get(presetCategoryNode, "CustomItemOptions")))
        {
            var t = TypeName(o) ?? "";
            var k = t.EndsWith("AddCustomItemOptionViewModel") ? "add"
                  : t.EndsWith("ImportCustomItemOptionViewModel") ? "import" : "custom";
            if (k == kind) return o;
        }
        return null;
    }

    /// <summary>Rename a saved custom item (the Custom-tab preset) through the registry — no dialog.
    /// "Selected" here is the currently worn item; for others the registry offers a non-selected path.</summary>
    public static bool RenameCustomItem(object item, string newName, out string error)
    {
        error = null;
        var model = Get(item, "_model");
        var registry = Get(item, "_customItemRegistry");
        var globalBus = Get(item, "_globalBus");
        var context = Get(item, "_context");
        if (model == null || registry == null || globalBus == null || context == null)
        { error = "rename: custom item is missing registry/model/context"; return false; }
        try
        {
            // RenameNotSelectedCustomItem rewrites the item's file (DisplayName persists). The
            // "selected" path only renames the worn transferable in the model and does not update the
            // file, so always use the not-selected registry path.
            Invoke(registry, "RenameNotSelectedCustomItem", model, globalBus, newName, context, null);
        }
        catch (Exception e) { error = $"rename failed: {e.Message}"; return false; }
        return true;
    }
}
