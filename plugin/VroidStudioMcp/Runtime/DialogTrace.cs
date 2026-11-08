using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BepInEx;
using static VroidStudioMcp.Interop;
using static VroidStudioMcp.VroidState;

namespace VroidStudioMcp;

/// <summary>
/// Devtool that traces every engine dialog the plugin sees, so a window that was NOT intercepted is
/// visible together with the command that reached it. It hooks the single dialog funnel
/// (<c>DialogProviderViewModel.Show</c> / <c>ShowNotification</c> / <c>Dismiss</c>) and is also called
/// from the custom-item save prefixes when they fall through to the native dialog.
/// Enabled by <c>dialog_trace</c> in servers.json (off by default); writes
/// <c>&lt;app root&gt;/dialog_trace.log</c> and mirrors each line to the BepInEx log.
/// </summary>
public static class DialogTrace
{
    private static readonly string _path = Path.Combine(BepInEx.Paths.GameRootPath, "dialog_trace.log");
    private static readonly object _lock = new();
    private static int _seq;

    private static void Write(string kind, object dialog, string extra)
    {
        if (!ServerSettings.DialogTrace) return;
        try
        {
            var n = Interlocked.Increment(ref _seq);
            var type = dialog == null ? "-" : TypeName(dialog) ?? dialog.GetType().FullName;
            var title = dialog == null ? null : Get(dialog, "Title") as string;
            var info = dialog == null ? null : Get(dialog, "InfoText") as string;
            CommandDispatcher.TryGetInFlight(out var cmd, out var secs);
            var line =
                $"{DateTime.UtcNow:O}\t#{n}\t{kind}\t{type}\t" +
                $"title='{title}'\tinfo='{info}'\tin_flight='{cmd}'({secs:F1}s)\t" +
                $"awaitingLoad={_awaitingLoadError} inEdit={_inEdit}]{extra}";
            lock (_lock) File.AppendAllText(_path, line + "\n");
            PluginLog.LogDebug(">>> DIALOG " + line);
        }
        catch { }
    }

    // DialogProviderViewModel.Show(dialog) / ShowNotification(dialogViewModel) / Dismiss() callbacks.
    public static void OnShow(object dialog) => Write("SHOW", dialog, "");
    public static void OnNotify(object dialogViewModel) => Write("NOTIFY", dialogViewModel, "");
    public static void OnDismiss(object __instance) => Write("DISMISS", Get(__instance, "Dialog"), "");

    // A custom-item save hook that had to fall through to the native dialog (no pre-armed answer).
    // Carries the call stack so the engine entry point that raised it is identifiable.
    public static void Unarmed(string tag) =>
        Write("UNARMED", null, $"\ttag='{tag}'\tstack={StackTop()}");

    // Naming probe for the "Saving temporary items" prompt: which temporary custom item it is about and
    // by which predicate. The reached overload's 3rd argument is the category's TransferableGroupType
    // (worn-temporary variant); the other overload asks about stashed-but-not-worn items instead. This
    // is the concrete "what does this window refer to".
    public static void Stash(object[] args)
    {
        if (!ServerSettings.DialogTrace || args == null || args.Length < 2 || args[1] == null) return;
        try
        {
            var multi = args[1];
            var wornTg = args.Length > 2 ? args[2] as string : null;
            bool stashedNotWorn = Invoke(multi, "CheckStashedButNotWornExists") is bool b1 && b1;
            bool wornTemp = wornTg != null &&
                            Invoke(multi, "CheckStashedOrSpecifiedWornTemporaryExists", wornTg) is bool b2 && b2;
            var cats = new List<string>();
            foreach (var c in Enumerate(Invoke(multi, "IterateStashedButNotWorn")))
            {
                var tg = Get(c, "TransferableGroupType") as string;
                var nm = Get(c, "Name") as string ?? Get(c, "TranslatedDisplayName") as string;
                cats.Add($"{nm ?? "?"}({tg})");
            }
            Write("STASH", null,
                  $"\twornTemporaryTg={wornTg ?? "-"}\twornTemporaryExists={wornTemp}" +
                  $"\tstashedButNotWornExists={stashedNotWorn}" +
                  $"\tstashedButNotWorn=[{string.Join(", ", cats)}]");
        }
        catch { }
    }
}
