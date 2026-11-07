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

namespace VroidStudioMcp;

public static class Logging
{
    private static MethodInfo _displayNameMethod;
    private static bool _displayNameMethodSearched;

    public static string TransferableGroupDisplayName(string resourceName)
    {
        try
        {
            if (!_displayNameMethodSearched)
            {
                _displayNameMethodSearched = true;
                var t = FindType("VRoid.Studio.ClothingEditor.Util.ResourceNameUtil");
                _displayNameMethod = t?.GetMethod("TransferableGroupResourceNameToDisplayName",
                    BindingFlags.Public | BindingFlags.Static);
            }
            return _displayNameMethod?.Invoke(null, new object[] { resourceName }) as string;
        }
        catch { return null; }
    }
    public static void WriteJson(string name, object payload)
    {
        try
        {
            var path = Path.Combine(BepInEx.Paths.GameRootPath, name);
            File.WriteAllText(path, MiniJson.Serialize(payload, true), Utf8);
            PluginLog.LogDebug($"written {path}");
        }
        catch (Exception e) { PluginLog.LogError($"write {name} failed: {e.Message}"); }
    }
}
