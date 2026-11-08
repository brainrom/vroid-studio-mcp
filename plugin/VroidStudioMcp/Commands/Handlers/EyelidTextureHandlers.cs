using System.Collections;
using System.Collections.Generic;

namespace VroidStudioMcp;

public static class EyelidTextureHandlers
{
    public static IEnumerator Run(CommandDispatcher.Context ctx)
    {
        var actions = new[] { "inspect", "export_texture", "set_layer" };
        if (!ArgParse.Choice(CommandUtil.Arg(ctx, 0), "action", out var actionIndex, out var error, actions))
        { ctx.Error = error; yield break; }
        var action = actions[actionIndex];
        int layer = -1;
        var layerArg = CommandUtil.Arg(ctx, 1);
        if (layerArg != null && !ArgParse.Int(layerArg, "layer", out layer, out error, 0))
        { ctx.Error = error; yield break; }
        var path = CommandUtil.Arg(ctx, 2);
        var key = CommandUtil.Arg(ctx, 3);
        var value = CommandUtil.Arg(ctx, 4);
        if (CommandUtil.Arg(ctx, 5) != null) { ctx.Error = "eyelid_texture: too many arguments"; yield break; }
        if (action == "inspect" && (layerArg != null || path != null || key != null || value != null))
        { ctx.Error = "eyelid_texture: inspect accepts no layer, path, key or value"; yield break; }
        if (action == "export_texture" && (layer < 0 || path == null || key != null || value != null))
        { ctx.Error = "eyelid_texture: export_texture requires an existing layer index and PNG path, with no key or value"; yield break; }
        if (action == "set_layer")
        {
            if (layer < 0 || path != null || key == null || value == null)
            { ctx.Error = "eyelid_texture: set_layer requires layer, key and value, with no path"; yield break; }
            if (!ArgParse.Choice(key, "key", out var keyIndex, out error, "visibility", "opacity"))
            { ctx.Error = error; yield break; }
            key = keyIndex == 0 ? "visibility" : "opacity";
            if (key == "visibility" && !ArgParse.Flag(value, "visibility", out _, out error, ("true", true), ("false", false)))
            { ctx.Error = error; yield break; }
            if (key == "opacity" && !ArgParse.Float(value, "opacity", out _, out error, 0, 1))
            { ctx.Error = error; yield break; }
        }
        if (!VroidState._modelReady || VroidState._modelUpdating)
        { ctx.Error = "eyelid_texture: model is not ready; inspect state before editing"; yield break; }
        int version = Signals.ModelRebuildVersion;
        Signals.Arm();
        if (!EyelidTextures.TryExecute(action, layer, path, key, value, out var result, out error))
        { ctx.Error = "eyelid_texture: " + error; yield break; }
        if (action == "set_layer")
        {
            bool unchanged = result is Dictionary<string, object> data && data.TryGetValue("changed", out var changed) && changed is false;
            if (!unchanged)
            {
                yield return CommandUtil.WaitUntil(() => Signals.ModelRebuildVersion != version && !VroidState._modelUpdating && VroidState._modelReady, 12f);
                if (Signals.ModelRebuildVersion == version || VroidState._modelUpdating || !VroidState._modelReady)
                { ctx.Error = "eyelid_texture: native command applied, but readiness was not confirmed within 12 seconds; inspect state and eyelid layers before retrying"; yield break; }
            }
            ctx.Result = new Dictionary<string, object> { ["applied"] = true, ["ready"] = VroidState._modelReady && !VroidState._modelUpdating,
                ["preview_verified"] = false, ["result"] = result };
        }
        else ctx.Result = result;
    }
}
