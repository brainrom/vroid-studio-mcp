using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace VroidStudioMcp;

public static class HairMaterialHandlers
{
    public static IEnumerator Run(CommandDispatcher.Context ctx)
    {
        var action = CommandUtil.Arg(ctx, 0);
        if (!ArgParse.Choice(action, "action", out _, out var error,
                "list", "inspect", "prepare", "create", "duplicate", "rename", "assign",
                "import_texture", "export_texture", "preview_texture", "set_color", "set_color_enabled", "set_float", "set_layer"))
        { ctx.Error = error; yield break; }
        action = action.ToLowerInvariant();
        var indices = new int[4];
        var names = new[] { "material", "slot", "layer", "group" };
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = -1;
            var arg = CommandUtil.Arg(ctx, i + 1);
            if (arg != null && !ArgParse.Int(arg, names[i], out indices[i], out error, 0))
            { ctx.Error = error; yield break; }
        }
        var path = CommandUtil.Arg(ctx, 5);
        var name = CommandUtil.Arg(ctx, 6);
        var key = CommandUtil.Arg(ctx, 7);
        var value = CommandUtil.Arg(ctx, 8);
        bool preview = action == "preview_texture";
        if (preview && path != null) { ctx.Error = "preview_texture does not accept a path"; yield break; }
        if (!VroidState._modelReady || VroidState._modelUpdating)
        { ctx.Error = "hair_material: model is not ready; inspect state before editing"; yield break; }
        string temporary = preview ? Path.Combine(Path.GetTempPath(), "vroid-material-" + Guid.NewGuid().ToString("N") + ".png") : null;
        object result;
        int version = Signals.ModelRebuildVersion;
        Signals.Arm();
        try
        {
            if (!HairMaterials.TryExecute(preview ? "export_texture" : action,
                    indices[0], indices[1], indices[2], indices[3], temporary ?? path, name, key, value, out result, out error))
            { ctx.Error = "hair_material: " + error; yield break; }
            if (preview)
                result = new Dictionary<string, object> { ["material"] = indices[0], ["slot"] = indices[1], ["layer"] = indices[2], ["preview_png"] = File.ReadAllBytes(temporary) };
        }
        finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }

        bool mutation = action != "list" && action != "inspect" && action != "export_texture" && !preview;
        if (mutation)
        {
            bool unchanged = result is Dictionary<string, object> data && data.TryGetValue("changed", out var changed) && changed is false;
            // Even material creation can rebuild asynchronously. Wait for native completion
            // instead of allowing the next queued command to race that rebuild.
            if (!unchanged)
            {
                yield return CommandUtil.WaitUntil(() => Signals.ModelRebuildVersion != version && !VroidState._modelUpdating && VroidState._modelReady, 12f);
                if (Signals.ModelRebuildVersion == version || VroidState._modelUpdating || !VroidState._modelReady)
                { ctx.Error = "hair_material: native command applied, but model readiness was not confirmed within 12 seconds; inspect state and material before retrying"; yield break; }
            }
            ctx.Result = new Dictionary<string, object> { ["applied"] = true, ["ready"] = VroidState._modelReady && !VroidState._modelUpdating,
                ["preview_verified"] = false, ["preview_note"] = "Native model update completed. VRoid 2.16 may retain the old rendered assignment until an explicit save/reopen; inspect a screenshot before accepting appearance.", ["result"] = result };
        }
        else ctx.Result = result;
    }
}
