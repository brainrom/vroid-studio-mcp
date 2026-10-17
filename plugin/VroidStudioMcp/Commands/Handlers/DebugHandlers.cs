using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace VroidStudioMcp;

/// <summary>
/// General developer/debug commands (TCP only, not part of the public API): attach the plugin's
/// method logger to arbitrary engine methods at runtime, so unknown call paths can be explored
/// without rebuilding, and the <c>memstat</c>/<c>memclear</c> probes used to localise the
/// design-editor memory leak (see docs/hair-export.md).
/// </summary>
public static class DebugHandlers
{
    // Attach the OnHook postfix to every overload of type.method. The log line is the same
    // `>>> HOOK Type.Method(args)` the statically hooked methods emit. Avoid hot getters (see
    // docs/quirks.md): a getter that runs every frame floods the log or stalls loading.
    public static IEnumerator Hook(CommandDispatcher.Context ctx)
    {
        var type = CommandUtil.Arg(ctx, 0);
        var method = CommandUtil.Arg(ctx, 1);
        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(method))
        { ctx.Error = "usage: hook <Full.Type.Name> <MethodName>"; yield break; }
        Hooks.AddDynamicHook(type, method);
        ctx.Result = new Dictionary<string, object> { ["hooked"] = $"{type}.{method}", ["count"] = Hooks.DynamicHooks().Count };
        yield break;
    }

    public static IEnumerator Unhook(CommandDispatcher.Context ctx)
    {
        var type = CommandUtil.Arg(ctx, 0);
        var method = CommandUtil.Arg(ctx, 1);
        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(method))
        { ctx.Error = "usage: unhook <Full.Type.Name> <MethodName>"; yield break; }
        var n = Hooks.RemoveDynamicHook(type, method);
        ctx.Result = new Dictionary<string, object> { ["unhooked"] = $"{type}.{method}", ["methods"] = n };
        yield break;
    }

    public static IEnumerator HooksList(CommandDispatcher.Context ctx)
    {
        ctx.Result = Hooks.DynamicHooks();
        yield break;
    }

    // memstat — process/registry memory snapshot (no model required).
    public static IEnumerator MemStat(CommandDispatcher.Context ctx)
    {
        ctx.Result = Memory();
        yield break;
    }

    // memclear — drop the colour-VM registry and force a managed + asset GC, then report the delta.
    // Used to tell whether the plugin's static roots (vs the editor itself) retain memory.
    public static IEnumerator MemClear(CommandDispatcher.Context ctx)
    {
        var before = Memory();
        ColorRegistry.Clear();
        yield return Collect();
        ctx.Result = new Dictionary<string, object> { ["before"] = before, ["after"] = Memory() };
    }

    // memgc — managed + asset GC only, WITHOUT clearing the plugin registries. The control for
    // memclear: if the growth drops here too, the leak is "nothing ever collects" rather than the
    // registry pinning objects.
    public static IEnumerator MemGc(CommandDispatcher.Context ctx)
    {
        var before = Memory();
        yield return Collect();
        ctx.Result = new Dictionary<string, object> { ["before"] = before, ["after"] = Memory() };
    }

    private static IEnumerator Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        UnityEngine.AsyncOperation op = null;
        try { op = UnityEngine.Resources.UnloadUnusedAssets(); } catch { }
        float t = 0f;
        while (op != null && !op.isDone && t < 20f) { t += UnityEngine.Time.unscaledDeltaTime; yield return null; }
    }

    private static Dictionary<string, object> Memory()
    {
        var d = new Dictionary<string, object>
        {
            ["color_vms"] = ColorRegistry.Count,
            ["selectors"] = Selectors.Count,
            ["managed_mb"] = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
        };
        try
        {
            foreach (var line in File.ReadAllLines("/proc/self/status"))
            {
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    d["rss_kb"] = int.Parse(line.Substring(6).Trim().Split(' ')[0]);
                else if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
                    d["rss_peak_kb"] = int.Parse(line.Substring(6).Trim().Split(' ')[0]);
            }
        }
        catch { }
        return d;
    }
}
