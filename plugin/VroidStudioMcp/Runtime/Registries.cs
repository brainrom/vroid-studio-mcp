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

public static class Registries
{
    internal static readonly UTF8Encoding Utf8 = new(false);
}

/// <summary>
/// Captured presets-selector view models, keyed by type name. Hooks track them as the editor creates
/// them (only while the edit root is alive); consumers look one up by the short "*.PresetsSelectorViewModel"
/// suffix or enumerate the current session's set.
/// </summary>
internal static class Selectors
{
    private static readonly ConcurrentDictionary<string, object> Map = new();

    internal static int Count => Map.Count;
    internal static IEnumerable<KeyValuePair<string, object>> All => Map;

    internal static void Track(object selector, string fallbackName)
        => Map[TypeName(selector) ?? fallbackName] = selector;

    internal static object BySuffix(string shortName)
    {
        foreach (var kv in Map) if (kv.Key.EndsWith(shortName)) return kv.Value;
        return null;
    }

    internal static void Clear() => Map.Clear();
}

/// <summary>
/// Colour-selection view models captured as the editor builds/uses them, plus a capture order. A model
/// reload leaves several generations in the registry, so consumers must prefer the newest instance
/// (otherwise a colour write goes to a stale VM that no longer drives the visible model).
/// </summary>
internal static class ColorRegistry
{
    private static readonly ConcurrentDictionary<IntPtr, object> ByPtr = new();
    private static readonly ConcurrentDictionary<IntPtr, long> Seq = new();
    private static long _counter;

    internal static IEnumerable<object> Vms => ByPtr.Values;

    /// <summary>Number of captured colour VMs (diagnostics: it grows with every colour edit and is only
    /// cleared on demand — see the memory-leak note in docs/hair-export.md).</summary>
    internal static int Count => ByPtr.Count;

    /// <summary>Drop every captured reference. Safe for consumers (they already prefer the newest
    /// instance); used by the <c>memclear</c> dev command to test whether the registry is a leak root.</summary>
    internal static void Clear() { ByPtr.Clear(); Seq.Clear(); }

    internal static void Register(object vm)
    {
        if (vm is not Il2CppObjectBase b) return;
        var p = b.Pointer;
        if (p == IntPtr.Zero) return;
        // Keep only what Colors.RefreshRegistry actually reads (SubColorSelection leaves and the
        // HairEditor colour VM). Registering every colour-setter instance (material colour nodes,
        // pickers, …) rooted large per-session object graphs and leaked ~90 MB per design-session
        // entry — see the "Known leak" note in docs/hair-export.md.
        var t = TypeName(vm) ?? "";
        if (!t.Contains("SubColorSelection") && !t.EndsWith("HairEditor.ViewModel")) return;
        ByPtr[p] = vm;
        Seq[p] = Interlocked.Increment(ref _counter);
    }

    internal static long SeqOf(object vm)
        => vm is Il2CppObjectBase b && Seq.TryGetValue(b.Pointer, out var q) ? q : 0L;
}
