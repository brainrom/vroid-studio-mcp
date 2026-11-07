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
using static VroidStudioMcp.VroidState;

namespace VroidStudioMcp;

public static class Sections
{
    internal sealed class Def { public string Id; public string Title; public string Enum; }

    // External section names are UI labels ("Face"/"Hairstyle"/...); the internal "*Editor" enum
    // names never leave the plugin and are not accepted as input.
    private static readonly Def[] Defs =
    {
        new Def { Id = "Face", Title = "Face", Enum = "FaceEditor" },
        new Def { Id = "Hairstyle", Title = "Hairstyle", Enum = "HairEditor" },
        new Def { Id = "Body", Title = "Body", Enum = "BodyEditor" },
        new Def { Id = "Outfit", Title = "Outfit", Enum = "ClothingEditor" },
    };

    public static List<object> SectionList() =>
        Defs.Select(d => (object)new Dictionary<string, object> { ["id"] = d.Id, ["title"] = d.Title }).ToList();

    /// <summary>External UI label → internal EditMode enum name. Unknown/empty input returns null so
    /// callers can reject it instead of silently passing it to the editor (which would never load a
    /// selector). Internal "*Editor" enum names are deliberately not accepted here.
    /// Case-SENSITIVE (Ordinal): the caller must use the exact labels listed by `sections`, matching
    /// how category/tab names are resolved (see Categories.FindTab).</summary>
    public static string ResolveSection(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        foreach (var d in Defs)
            if (string.Equals(s, d.Id, StringComparison.Ordinal))
                return d.Enum;
        return null;
    }

    /// <summary>Accepted external labels, comma-separated, for actionable "unknown section" errors.</summary>
    public static string LabelsText() => string.Join(", ", Defs.Select(d => d.Id));

    public static string SectionLabel(string enumName)
    {
        foreach (var d in Defs)
            if (string.Equals(enumName, d.Enum, StringComparison.OrdinalIgnoreCase)) return d.Id;
        return enumName;
    }

    /// <summary>Internal helper: switch to a section by its EditMode enum name (or UI label).</summary>
    public static void SwitchSectionForCat(string mode)
    {
        try
        {
            var resolved = Defs.FirstOrDefault(d => string.Equals(mode, d.Enum, StringComparison.OrdinalIgnoreCase))?.Enum
                        ?? Defs.FirstOrDefault(d => string.Equals(mode, d.Id, StringComparison.OrdinalIgnoreCase))?.Enum;
            if (resolved == null) { PluginLog.LogError($"switch: unknown section '{mode}'"); return; }
            var modeType = FindType("VRoid.Studio.EditRootScreen.EditMode");
            var m = Enum.Parse(modeType, resolved);
            Invoke(_editRootVm, "set_EditMode", m);
            PluginLog.LogDebug($"switch->{resolved}");
        }
        catch (Exception e) { PluginLog.LogError($"switch {mode} failed: {e.Message}"); }
    }
}
