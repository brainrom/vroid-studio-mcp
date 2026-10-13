using System.Collections;
using System.Collections.Generic;

namespace VroidStudioMcp;

/// <summary>
/// General developer/debug commands (TCP only, not part of the public API): attach the plugin's
/// method logger to arbitrary engine methods at runtime, so unknown call paths can be explored
/// without rebuilding. Not tied to any domain.
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
}
