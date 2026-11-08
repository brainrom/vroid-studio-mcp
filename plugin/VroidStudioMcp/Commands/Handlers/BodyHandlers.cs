using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

public static class BodyHandlers
{
    public static IEnumerator Height(CommandDispatcher.Context ctx)
    {
        if (ctx.Args.Length != 0) { ctx.Error = "usage: body_height"; yield break; }
        if (!VroidState._modelReady || VroidState._modelUpdating)
        { ctx.Error = "model is not ready or is updating; wait before reading height"; yield break; }
        var height = Get(Get(VroidState._editRootVm, "BodyEditor"), "BodyHeight");
        if (height == null) { ctx.Error = "native body-height view model is unavailable"; yield break; }
        var value = Get(height, "BodyHeightValue");
        var label = Get(height, "BodyHeightLabel") as string;
        if (!ParamCodec.TryDbl(value, out var number) || !double.IsFinite(number) || number <= 0 || string.IsNullOrEmpty(label))
        { ctx.Error = "native body-height readout is not ready"; yield break; }
        var type = FindType("VRoid.Studio.BodyEditor.BodyHeightViewModel");
        var unit = type?.GetProperty("UnitLabel", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as string;
        ctx.Result = new Dictionary<string, object>
        {
            ["value"] = number,
            ["label"] = label,
            ["unit_label"] = unit,
            ["property_label"] = Get(height, "BodyHeightPropertyLabel"),
            ["source"] = "VRoid.Studio.BodyEditor.BodyHeightViewModel",
        };
    }
}
