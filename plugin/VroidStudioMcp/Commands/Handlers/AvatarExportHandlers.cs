using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace VroidStudioMcp;

public static class AvatarExportHandlers
{
    public static IEnumerator Export(CommandDispatcher.Context ctx)
    {
        var path = CommandUtil.Arg(ctx, 0);
        var version = CommandUtil.Arg(ctx, 1);
        var metadata = CommandUtil.Arg(ctx, 2);
        if (ctx.Args.Length < 3 || ctx.Args.Length > 5 || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(metadata))
        { ctx.Error = "usage: export_avatar <absolute.vrm> <0|1> <metadata_json> [overwrite] [timeout_seconds]"; yield break; }
        if (!ArgParse.Choice(version, "version", out var selected, out var error, "0", "1"))
        { ctx.Error = error; yield break; }
        version = selected.ToString();
        bool overwrite = false;
        if (CommandUtil.Arg(ctx, 3) is string replace && replace.Length > 0
            && !ArgParse.Flag(replace, "overwrite", out overwrite, out error, ("true", true), ("false", false)))
        { ctx.Error = error; yield break; }
        int timeout = 120;
        if (CommandUtil.Arg(ctx, 4) is string seconds && seconds.Length > 0
            && !ArgParse.Int(seconds, "timeout_seconds", out timeout, out error, 10, 300))
        { ctx.Error = error; yield break; }
        if (!NativeAvatarExport.TryPrepare(path, version, metadata, overwrite, out var target, out var vm, out var meta, out error)
            || !NativeAvatarExport.TryBegin(vm, meta, version, target, out var job, out error))
        { ctx.Error = "export_avatar: " + error; yield break; }
        var clock = Stopwatch.StartNew();
        while (!job.Done && clock.Elapsed.TotalSeconds < timeout) yield return null;
        if (!job.Done)
        {
            job.Abandoned = true;
            ctx.Error = "export_avatar: native callback timed out; no final file published. Native work may still be running and MCP mutations remain blocked until its callback. Do not blindly retry. Temporary path: " + job.StagePath;
            yield break;
        }
        if (!job.Success)
        {
            NativeAvatarExport.RemoveStage(job);
            ctx.Error = "export_avatar: native exporter reported failure; final target was not changed; inspect app logs";
            yield break;
        }
        try
        {
            if (!FileOps.IsRegularFile(job.StagePath) || new FileInfo(job.StagePath).Length == 0)
                throw new IOException("native success callback produced no non-empty regular file");
            // Same-directory staging keeps the source and final target untouched on native failure.
            File.Move(job.StagePath, target, overwrite);
            ctx.Result = new Dictionary<string, object>
            {
                ["exported"] = true, ["path"] = target, ["version"] = version,
                ["native_success"] = true, ["bytes"] = new FileInfo(target).Length,
                ["source_saved"] = false, ["experimental"] = true,
                ["optimization"] = "none; combine hair; no texture shrinking or mesh optimization",
            };
        }
        catch (Exception e)
        {
            ctx.Error = "export_avatar: " + e.Message + "; native output retained at " + job.StagePath;
        }
    }
}
