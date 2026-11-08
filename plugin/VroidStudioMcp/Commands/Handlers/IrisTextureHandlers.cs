using System.Collections;
using System.Collections.Generic;

namespace VroidStudioMcp;

public static class IrisTextureHandlers
{
    public static IEnumerator Run(CommandDispatcher.Context ctx)
    {
        var action = CommandUtil.Arg(ctx, 0);
        if (!ArgParse.Choice(action, "action", out _, out var error, "inspect", "prepare", "import_texture", "export_texture", "set_layer"))
        { ctx.Error = error; yield break; }
        action = action.ToLowerInvariant();
        int layer = -1;
        var layerArg = CommandUtil.Arg(ctx, 1);
        if (layerArg != null && !ArgParse.Int(layerArg, "layer", out layer, out error, -1))
        { ctx.Error = error; yield break; }
        var path = CommandUtil.Arg(ctx, 2);
        var key = CommandUtil.Arg(ctx, 3);
        var value = CommandUtil.Arg(ctx, 4);
        if (CommandUtil.Arg(ctx, 5) != null) { ctx.Error = "iris_texture: too many arguments"; yield break; }
        if ((action == "inspect" || action == "prepare") && (layerArg != null || path != null || key != null || value != null))
        { ctx.Error = "iris_texture: inspect/prepare accept no layer, path, key or value"; yield break; }
        if ((action == "import_texture" || action == "export_texture") && (path == null || key != null || value != null))
        { ctx.Error = "iris_texture: texture import/export requires path and accepts no key or value"; yield break; }
        if (action == "export_texture" && layer < 0) { ctx.Error = "iris_texture: export_texture requires an existing layer index"; yield break; }
        if (action == "set_layer" && (layer < 0 || path != null || key == null || value == null))
        { ctx.Error = "iris_texture: set_layer requires layer, key and value, with no path"; yield break; }
        if (!VroidState._modelReady || VroidState._modelUpdating)
        { ctx.Error = "iris_texture: model is not ready; inspect state before editing"; yield break; }
        int version = Signals.ModelRebuildVersion;
        Signals.Arm();
        if (!IrisTextures.TryExecute(action, layer, path, key, value, out var result, out error))
        { ctx.Error = "iris_texture: " + error; yield break; }
        bool mutation = action != "inspect" && action != "export_texture";
        if (mutation)
        {
            bool unchanged = result is Dictionary<string, object> data && data.TryGetValue("changed", out var changed) && changed is false;
            if (!unchanged)
            {
                yield return CommandUtil.WaitUntil(() => Signals.ModelRebuildVersion != version && !VroidState._modelUpdating && VroidState._modelReady, 12f);
                if (Signals.ModelRebuildVersion == version || VroidState._modelUpdating || !VroidState._modelReady)
                { ctx.Error = "iris_texture: native command applied, but readiness was not confirmed within 12 seconds; inspect state and iris layers before retrying"; yield break; }
            }
            ctx.Result = new Dictionary<string, object> { ["applied"] = true, ["ready"] = VroidState._modelReady && !VroidState._modelUpdating,
                ["preview_verified"] = false, ["result"] = result };
        }
        else ctx.Result = result;
    }
}
