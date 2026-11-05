using System;
using System.Collections;

namespace VroidStudioMcp;

/// <summary>Injects/reads the "Programmatic Control (MCP)" section of the editor's settings dialog
/// (dev/TCP only).</summary>
public static class SettingsHandlers
{
    public static IEnumerator Probe(CommandDispatcher.Context ctx) { ctx.Result = SettingsUi.Open(); return CommandUtil.Done(); }
    public static IEnumerator Dump(CommandDispatcher.Context ctx) { SettingsUi.RequestDump(); ctx.Result = "dumped"; return CommandUtil.Done(); }
    public static IEnumerator State(CommandDispatcher.Context ctx) { ctx.Result = SettingsUi.WidgetState(); return CommandUtil.Done(); }
    public static IEnumerator Toggle(CommandDispatcher.Context ctx)
    {
        if (!ArgParse.Flag(CommandUtil.Arg(ctx, 0), "enabled", out var enabled, out var error,
                           ("true", true), ("false", false)))
        { ctx.Error = $"settingstoggle: {error}"; return CommandUtil.Done(); }
        ctx.Result = SettingsUi.SetEnabledFromCommand(enabled);
        return CommandUtil.Done();
    }
    public static IEnumerator Edit(CommandDispatcher.Context ctx)
    {
        ctx.Result = SettingsUi.EditFromCommand(CommandUtil.Arg(ctx, 0), CommandUtil.Arg(ctx, 1));
        return CommandUtil.Done();
    }
}
