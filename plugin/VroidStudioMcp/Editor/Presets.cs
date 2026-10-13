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

    public static object CustomItemAt(object presetCategoryNode, int index)
    {
        var items = Get(presetCategoryNode, "_customItems") ?? Get(presetCategoryNode, "CustomItemOptions");
        return Enumerate(items).ElementAtOrDefault(index);
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
