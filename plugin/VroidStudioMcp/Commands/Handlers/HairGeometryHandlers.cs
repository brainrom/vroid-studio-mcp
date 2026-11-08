using System.Collections;
using System.Collections.Generic;

namespace VroidStudioMcp;

public static class HairGeometryHandlers
{
    public static IEnumerator Run(CommandDispatcher.Context ctx)
    {
        if (!ArgParse.Choice(CommandUtil.Arg(ctx, 0), "action", out var choice, out var error, "inspect", "add", "set"))
        { ctx.Error = error; yield break; }
        var action = new[] { "inspect", "add", "set" }[choice];
        int group = -1, material = -1;
        var groupArg = CommandUtil.Arg(ctx, 1);
        var materialArg = CommandUtil.Arg(ctx, 2);
        if (groupArg != null && !ArgParse.Int(groupArg, "group", out group, out error, 0))
        { ctx.Error = error; yield break; }
        if (materialArg != null && !ArgParse.Int(materialArg, "material", out material, out error, 0))
        { ctx.Error = error; yield break; }
        var name = CommandUtil.Arg(ctx, 3);
        var key = CommandUtil.Arg(ctx, 4);
        var value = CommandUtil.Arg(ctx, 5);
        if (ctx.Args.Length > 6) { ctx.Error = "hair_geometry: unexpected extra arguments"; yield break; }
        if (action != "inspect" && group < 0) { ctx.Error = "hair_geometry: group is required; use inspect"; yield break; }
        if (action == "add" && (material < 0 || string.IsNullOrWhiteSpace(name)))
        { ctx.Error = "hair_geometry: add requires an existing material index and a unique name"; yield break; }
        if (action == "set" && (string.IsNullOrEmpty(key) || value == null))
        { ctx.Error = "hair_geometry: set requires key and value"; yield break; }
        if (action == "inspect" && (materialArg != null || name != null || key != null || value != null)
            || action == "add" && (key != null || value != null)
            || action == "set" && (materialArg != null || name != null))
        { ctx.Error = "hair_geometry: unsupported arguments for this action; inspect accepts group, add accepts group/material/name, set accepts group/key/value"; yield break; }
        if (!VroidState._modelReady || VroidState._modelUpdating)
        { ctx.Error = "hair_geometry: model is not ready; inspect state before editing"; yield break; }
        int version = Signals.ModelRebuildVersion;
        Signals.Arm();
        if (!HairGeometry.TryExecute(action, group, material, name, key, value, out var result, out error))
        { ctx.Result = result; ctx.Error = "hair_geometry: " + error; yield break; }
        if (action == "inspect") { ctx.Result = result; yield break; }
        bool unchanged = result is Dictionary<string, object> data && data.TryGetValue("changed", out var changed) && changed is false;
        if (!unchanged)
        {
            yield return CommandUtil.WaitUntil(() => Signals.ModelRebuildVersion != version && !VroidState._modelUpdating && VroidState._modelReady, 12f);
            if (Signals.ModelRebuildVersion == version || VroidState._modelUpdating || !VroidState._modelReady)
            {
                ctx.Result = new Dictionary<string, object> { ["applied"] = true, ["ready"] = false, ["preview_verified"] = false, ["result"] = result };
                ctx.Error = "hair_geometry: native edit applied, but readiness was not confirmed within 12 seconds; inspect state and geometry before retrying";
                yield break;
            }
        }
        ctx.Result = new Dictionary<string, object> { ["applied"] = true, ["ready"] = VroidState._modelReady && !VroidState._modelUpdating,
            ["preview_verified"] = false, ["result"] = result };
    }
}
