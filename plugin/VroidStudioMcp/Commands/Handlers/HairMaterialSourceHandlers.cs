using System;
using System.Collections;
using Il2CppInterop.Runtime.InteropTypes;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

public static class HairMaterialSourceHandlers
{
    public static IEnumerator Run(CommandDispatcher.Context ctx)
    {
        if (!ArgParse.Choice(CommandUtil.Arg(ctx, 0), "action", out int action, out var error, "list", "import"))
        { ctx.Error = error; yield break; }
        string path = CommandUtil.Arg(ctx, 1), id = CommandUtil.Arg(ctx, 2), name = CommandUtil.Arg(ctx, 3);
        if (action == 1 && string.IsNullOrWhiteSpace(id))
        { ctx.Error = "hair_material_source: import requires material_id from list"; yield break; }
        if (action == 0 && (id != null || name != null))
        { ctx.Error = "hair_material_source: material_id/name apply only to import"; yield break; }
        if (!HairMaterials.TryContext(out var originalContext, out var originalModel, out error))
        { ctx.Error = error; yield break; }
        IntPtr Pointer(object obj) => (obj as Il2CppObjectBase)?.Pointer ?? IntPtr.Zero;
        var originalBase = Get(originalModel, "BaseCollection");
        var originalPath = FileOps.CurrentModelPath();
        if (!HairMaterialTransfer.TryBeginLoad(path, out var task, out error))
        { ctx.Error = "hair_material_source: " + error; yield break; }
        yield return CommandUtil.WaitUntil(() => Get(task, "IsCompleted") is true, 30f);
        if (Get(task, "IsCompleted") is not true)
        { ctx.Error = "hair_material_source: native file load timed out; no material imported"; yield break; }
        if (Get(task, "IsCanceled") is true || Get(task, "IsFaulted") is true)
        { ctx.Error = "hair_material_source: native file load failed: " + (Get(task, "Exception")?.ToString() ?? "canceled"); yield break; }
        if (!HairMaterialTransfer.TryRead(Get(task, "Result"), out var source, out var result, out error))
        { ctx.Error = "hair_material_source: " + error; yield break; }
        if (action == 1)
        {
            if (!VroidState._inEdit || !VroidState._modelReady || VroidState._modelUpdating
                || !HairMaterials.TryContext(out var currentContext, out var currentModel, out error)
                || Pointer(currentContext) != Pointer(originalContext) || Pointer(currentModel) != Pointer(originalModel)
                || Pointer(Get(currentModel, "BaseCollection")) != Pointer(originalBase)
                || !string.Equals(FileOps.CurrentModelPath(), originalPath, StringComparison.Ordinal))
            { ctx.Error = "hair_material_source: model changed while loading the source; no material imported; refresh and retry"; yield break; }
            int version = Signals.ModelRebuildVersion;
            Signals.Arm();
            if (!HairMaterialTransfer.TryImport(source, id, name, out result, out error))
            { ctx.Error = "hair_material_source: " + error; yield break; }
            yield return CommandUtil.WaitUntil(() => Signals.ModelRebuildVersion != version && !VroidState._modelUpdating && VroidState._modelReady, 12f);
            if (Signals.ModelRebuildVersion == version || VroidState._modelUpdating || !VroidState._modelReady)
            { ctx.Error = "hair_material_source: material copied, but model readiness was not confirmed within 12 seconds; inspect before retrying"; yield break; }
        }
        ctx.Result = result;
    }
}
