using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

public static class RecentModelHandlers
{
    public static IEnumerator Run(CommandDispatcher.Context ctx)
    {
        var action = CommandUtil.Arg(ctx, 0) ?? "list";
        if (!ArgParse.Choice(action, "action", out var choice, out var error, "list", "remove", "restore"))
        { ctx.Error = error; yield break; }
        var path = CommandUtil.Arg(ctx, 1);
        if (choice == 0 && !string.IsNullOrEmpty(path))
        { ctx.Error = "recent_models: path is only valid with remove or restore"; yield break; }
        var type = FindType("VRoid.Studio.StartScreen.ModelFileAccessHistory");
        var read = type?.GetMethod("GetPaths", Type.EmptyTypes);
        if (read == null) { ctx.Error = "recent_models: native history API unavailable"; yield break; }
        List<string> Paths()
        {
            var raw = read.Invoke(null, null);
            var enumerable = RewrapTo(raw, typeof(Il2CppSystem.Collections.Generic.IEnumerable<string>))
                as Il2CppSystem.Collections.Generic.IEnumerable<string>;
            if (enumerable == null) throw new NotSupportedException("recent_models: native string history collection unavailable");
            var copy = new Il2CppSystem.Collections.Generic.List<string>(enumerable);
            var values = new List<string>(copy.Count);
            for (int i = 0; i < copy.Count; i++) values.Add(copy[i]);
            return values;
        }
        var paths = Paths();
        if (choice == 1)
        {
            if (string.IsNullOrWhiteSpace(path)) { ctx.Error = "recent_models: remove requires an exact path from list"; yield break; }
            var nativePath = WinePaths.ToDos(path);
            // Remove only one exact native entry, never a model file or wildcard match.
            var match = paths.FirstOrDefault(p => string.Equals(p, nativePath, StringComparison.Ordinal));
            if (match == null) { ctx.Error = "recent_models: exact path is not in history; list again"; yield break; }
            if (VroidState._inEdit && string.Equals(Path.GetFullPath(match), Path.GetFullPath(FileOps.CurrentModelPath() ?? "."), StringComparison.OrdinalIgnoreCase))
            { ctx.Error = "recent_models: cannot remove the currently open model; return to main_menu first"; yield break; }
            var remove = type.GetMethod("Remove", new[] { typeof(string) });
            if (remove == null) { ctx.Error = "recent_models: native remove API unavailable"; yield break; }
            remove.Invoke(null, new object[] { match });
            paths = Paths();
            if (paths.Contains(match)) { ctx.Error = "recent_models: native history still contains the requested entry"; yield break; }
        }
        if (choice == 2)
        {
            var hostPath = string.IsNullOrWhiteSpace(path) ? null : WinePaths.ToHost(path);
            if (hostPath == null || !Path.IsPathFullyQualified(hostPath)
                || !string.Equals(Path.GetExtension(hostPath), ".vroid", StringComparison.OrdinalIgnoreCase) || !File.Exists(hostPath))
            { ctx.Error = "recent_models: restore requires an absolute path to an existing .vroid file"; yield break; }
            var nativePath = WinePaths.ToDos(path);
            if (!paths.Contains(nativePath))
            {
                var capacity = type.GetProperty("MaxHistoryCount")?.GetValue(null);
                var insert = type.GetMethod("Insert", new[] { typeof(int), typeof(string) });
                if (capacity is not int limit || limit <= 0 || insert == null)
                { ctx.Error = "recent_models: safe native history restoration is unavailable"; yield break; }
                if (paths.Count >= limit)
                { ctx.Error = "recent_models: history is full; remove known temporary entries before restoring, so owner entries are not evicted"; yield break; }
                insert.Invoke(null, new object[] { paths.Count, nativePath });
                paths = Paths();
                if (!paths.Contains(nativePath))
                { ctx.Error = "recent_models: native history did not retain the restored path"; yield break; }
            }
        }
        ctx.Result = new Dictionary<string, object> { ["paths"] = paths.Select(WinePaths.ToHost).ToList(), ["removed"] = choice == 1 ? path : null,
            ["restored"] = choice == 2 ? path : null, ["model_files_deleted"] = false };
    }
}
