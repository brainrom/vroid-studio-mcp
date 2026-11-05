using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

/// <summary>
/// Public import/export of reusable custom items (*.vroidcustomitem) for Hairstyle and Outfit.
/// The engine entry points live in <see cref="CustomItems"/>; this layer parses the arguments,
/// enters the category and drives the async engine work on the main thread. Completion is resolved by
/// engine signals (<see cref="CustomItemSignals"/>) and task state, never by fixed pauses.
/// </summary>
public static class CustomItemHandlers
{
    private sealed class MetaBox
    {
        public string Name;
        public string Type;
        public string Error;
    }

    public static IEnumerator Import(CommandDispatcher.Context ctx)
    {
        const string usage = "usage: import_custom_preset <section> <category> <path> [name] [wear=now|later]";
        var section = CommandUtil.ResolveSectionArg(ctx, 0, usage);
        var requestedTab = CommandUtil.Arg(ctx, 1);
        var path = CommandUtil.Arg(ctx, 2);
        var wantName = CommandUtil.Arg(ctx, 3);
        if (section == null) yield break;
        if (requestedTab == null || path == null) { ctx.Error = usage; yield break; }
        bool wear = false;
        var wearArg = CommandUtil.Arg(ctx, 4);
        if (wearArg != null
            && !ArgParse.Flag(wearArg, "wear", out wear, out var wearError, ("now", true), ("later", false)))
        { ctx.Error = $"import_custom_preset: {wearError}"; yield break; }
        if (!CustomItems.SectionSupported(section))
        { ctx.Error = "import_custom_preset: custom items exist for Hairstyle and Outfit only"; yield break; }
        if (!CustomItems.LooksLikeCustomItem(path))
        { ctx.Error = $"import_custom_preset: '{path}' must be a {CustomItems.Extension} file"; yield break; }

        var win = WinePaths.ToDos(path);
        if (!FileOps.IsRegularFile(win) && !FileOps.IsRegularFile(path))
        { ctx.Error = $"import_custom_preset: file not found or not a regular file: {path}"; yield break; }

        yield return CommandUtil.EnterSection(section);
        var selectorShort = section + ".PresetsSelectorViewModel";
        var selector = Categories.FindSelectorByShort(selectorShort);
        var tab = requestedTab;
        var category = CommandUtil.EnterCategory(section, tab);
        if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
        yield return WaitCategorySelected(selectorShort, tab);

        var presetNode = Get(category, "PresetCategoryViewModel") ?? category;
        var importSlot = Presets.CustomItemOfKind(presetNode, "import");
        if (importSlot == null) { ctx.Error = $"import_custom_preset: '{tab}' has no Custom Import slot"; yield break; }
        var registry = Get(importSlot, "_customItemRegistry");
        if (registry == null) { ctx.Error = "import_custom_preset: the Custom Import slot has no registry"; yield break; }

        // The engine is the single source of truth for the file format and its category. Its own
        // loader reads the *.vroidcustomitem and exposes the CustomItemMetaData (no zip parsing here).
        if (!CustomItems.TryBeginLoad(registry, path, out var loadTask, out var loadError))
        { ctx.Error = $"import_custom_preset: {loadError}"; yield break; }
        yield return WaitTask(loadTask, 30f);
        var loadTaskError = TaskError(loadTask);
        if (loadTaskError != null)
        { ctx.Error = $"import_custom_preset: {loadTaskError}"; yield break; }
        if (!CustomItems.TryReadContents(Get(loadTask, "Result"), out var typeId, out var engineName, out var metaError))
        { ctx.Error = $"import_custom_preset: {metaError}"; yield break; }

        // The import target comes from the file, not from the caller's current tab: switch to the
        // category the engine reports (when the section has it).
        var target = Categories.FindTabByType(selector, typeId);
        if (target == null)
        {
            ctx.Error = $"import_custom_preset: the file belongs to {typeId}, which is not a category in " +
                        $"{Sections.SectionLabel(section)}; open the matching section and import it there";
            yield break;
        }
        var targetTab = Get(target, "Name") as string;
        if (!string.IsNullOrEmpty(targetTab) && !string.Equals(targetTab, tab, StringComparison.Ordinal))
        {
            tab = targetTab;
            category = CommandUtil.EnterCategory(section, tab);
            if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
            yield return WaitCategorySelected(selectorShort, tab);
            presetNode = Get(category, "PresetCategoryViewModel") ?? category;
            importSlot = Presets.CustomItemOfKind(presetNode, "import");
            if (importSlot == null) { ctx.Error = $"import_custom_preset: '{tab}' has no Custom Import slot"; yield break; }
        }

        var before = CustomItems.Files(selectorShort, tab);
        int saveBefore = CustomItemSignals.SaveVersion;
        int notifyBefore = CustomItemSignals.NotifyVersion;

        // The engine follows a successful import with the "Imported successfully" modal. Answer it
        // immediately: the Show prefix consumes these flags and never lets the window appear.
        CustomItems.ArmImportDialog(wear);
        if (!CustomItems.TryBeginImport(presetNode, path, out var task, out var engineErrors, out var beginError))
        {
            CustomItems.ClearImportDialog();
            ctx.Error = $"import_custom_preset: {beginError}";
            yield break;
        }

        yield return WaitTask(task, 30f);
        var taskError = TaskError(task);
        if (engineErrors.Count > 0 || taskError != null)
        {
            CustomItems.ClearImportDialog();
            ctx.Error = $"import_custom_preset: {(taskError ?? EngineErrors(engineErrors))}";
            yield break;
        }

        // ImportCustomItem calls CustomItemRegistry.Save (file written) and that Save notifies the
        // Custom tab listeners; both signals advance before the import task completes.
        yield return CommandUtil.WaitUntil(
            () => CustomItemSignals.SaveVersion > saveBefore && CustomItemSignals.NotifyVersion > notifyBefore, 20f);

        Presets.Entry added = null;
        yield return CommandUtil.WaitUntil(() =>
        {
            added = CustomItems.NewEntry(selectorShort, tab, before);
            return added != null;
        }, 5f);
        if (added == null)
        {
            CustomItems.ClearImportDialog();
            ctx.Error = "import_custom_preset: the item was imported but no new Custom-tab entry appeared";
            yield break;
        }
        CustomItems.ClearImportDialog();

        int rawIndex = -1;
        string oldFile = added.File;
        bool renameFailed = false;
        var wantedName = string.IsNullOrWhiteSpace(wantName) ? null : wantName.Trim();
        var box = new MetaBox();
        if (wantedName != null)
        {
            rawIndex = Presets.CustomItemRawIndex(presetNode, added.Option);
            if (!Presets.RenameCustomItem(added.Option, wantedName, out _)) renameFailed = true;
            else yield return WaitFileMetaName(registry, oldFile, wantedName, box, 10f);
        }
        else yield return ReadFileMeta(registry, oldFile, box);
        if (box.Name == null && engineName != null) box.Name = engineName;

        // Rename/import rebuilds the Custom list, so resolve the final instance by its file (or by
        // the raw position) instead of keeping the captured ViewModel.
        var node2 = Categories.FindTab(Categories.FindSelectorByShort(selectorShort), tab) ?? category;
        var pn2 = Get(node2, "PresetCategoryViewModel") ?? node2 ?? presetNode;
        var fresh = CustomItems.FindByFile(selectorShort, tab, oldFile);
        if (fresh == null && rawIndex >= 0)
        {
            var raw = Presets.CustomItemAt(pn2, rawIndex);
            var rawFile = Get(Get(raw, "_model"), "FilePath") as string;
            fresh = CustomItems.FindByFile(selectorShort, tab, rawFile);
            if (fresh == null && raw != null)
                fresh = new Presets.Entry
                {
                    Kind = "custom",
                    Option = raw,
                    File = rawFile,
                    Name = box.Name ?? CustomItems.DisplayName(raw),
                };
        }
        fresh ??= added;
        var finalFile = fresh.File ?? oldFile;
        var sourceName = Path.GetFileNameWithoutExtension(path);
        var finalName = box.Name ?? CustomItems.DisplayName(fresh) ?? sourceName;
        bool renamed = wantedName != null
                       && string.Equals(box.Name ?? CustomItems.DisplayName(fresh), wantedName, StringComparison.Ordinal);
        if (wantedName != null && !renamed) renameFailed = true;
        int id = CustomItems.IdOf(selectorShort, tab, fresh);

        var result = new Dictionary<string, object>
        {
            ["imported"] = true,
            ["section"] = Sections.SectionLabel(section),
            ["category"] = tab,
            ["id"] = id,
            ["name"] = finalName,
            ["file"] = WinePaths.ToHost(finalFile),
            ["applied"] = Get(fresh.Option, "IsSelected") is bool sel && sel,
            ["wear_requested"] = wear,
            ["count"] = Presets.CustomItemCount(pn2),
            ["renamed"] = renamed,
            ["type"] = typeId,
        };
        if (!string.Equals(tab, requestedTab, StringComparison.Ordinal)) result["requested_category"] = requestedTab;
        if (renameFailed) result["rename_error"] = "the item was imported but renaming failed";
        ctx.Result = result;
    }

    public static IEnumerator Export(CommandDispatcher.Context ctx)
    {
        const string usage = "usage: export_custom_preset <section> <category> <index> <path> [save=true|false] [overwrite=true|false]";
        var section = CommandUtil.ResolveSectionArg(ctx, 0, usage);
        var tab = CommandUtil.Arg(ctx, 1);
        var indexArg = CommandUtil.Arg(ctx, 2);
        var rawPath = CommandUtil.Arg(ctx, 3);
        if (section == null) yield break;
        if (tab == null || indexArg == null || rawPath == null) { ctx.Error = usage; yield break; }
        if (!ArgParse.Int(indexArg, "index", out var id, out var idError, 0))
        { ctx.Error = $"export_custom_preset: {idError}; use the id from presets()"; yield break; }
        if (!CustomItems.SectionSupported(section))
        { ctx.Error = "export_custom_preset: custom items exist for Hairstyle and Outfit only"; yield break; }

        bool save = true;
        var saveArg = CommandUtil.Arg(ctx, 4);
        if (saveArg != null
            && !ArgParse.Flag(saveArg, "save", out save, out var saveArgError, ("true", true), ("false", false)))
        { ctx.Error = $"export_custom_preset: {saveArgError}"; yield break; }
        bool overwrite = true;
        var overwriteArg = CommandUtil.Arg(ctx, 5);
        if (overwriteArg != null
            && !ArgParse.Flag(overwriteArg, "overwrite", out overwrite, out var overwriteError, ("true", true), ("false", false)))
        { ctx.Error = $"export_custom_preset: {overwriteError}"; yield break; }
        var path = CustomItems.NormalizeExportPath(rawPath.Trim());
        if (!string.Equals(Path.GetExtension(path), CustomItems.Extension, StringComparison.OrdinalIgnoreCase))
        { ctx.Error = $"export_custom_preset: path must end with {CustomItems.Extension}"; yield break; }

        var win = WinePaths.ToDos(path);
        var dir = Path.GetDirectoryName(win);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        { ctx.Error = $"export_custom_preset: directory does not exist: {WinePaths.ToHost(dir)}"; yield break; }
        if (Directory.Exists(win) || Directory.Exists(path))
        { ctx.Error = $"export_custom_preset: not a file path: {path}"; yield break; }
        if (!overwrite && (File.Exists(win) || File.Exists(path)))
        { ctx.Error = $"export_custom_preset: file exists (pass overwrite=true to replace it): {path}"; yield break; }

        yield return CommandUtil.EnterSection(section);
        var selectorShort = section + ".PresetsSelectorViewModel";
        var category = CommandUtil.EnterCategory(section, tab);
        if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
        yield return WaitCategorySelected(selectorShort, tab);

        var entry = Presets.EntryAt(selectorShort, tab, id);
        if (entry == null) { ctx.Error = $"export_custom_preset: id {id} out of range (from presets())"; yield break; }
        if (!entry.IsCustom)
        { ctx.Error = $"export_custom_preset: id {id} is a {entry.Kind} preset, not a custom item"; yield break; }

        var item = entry.Option;
        var registry = Get(item, "_customItemRegistry");
        bool worn = Get(item, "IsSelected") is bool sel && sel;
        bool saved = false;
        var box = new MetaBox();
        if (save && worn)
        {
            var beforeName = CustomItems.DisplayName(item);
            int saveBefore = CustomItemSignals.SaveVersion;
            if (!CustomItems.TrySaveWorn(item, out saved, out var saveError))
            { ctx.Error = $"export_custom_preset: {saveError}"; yield break; }
            if (saved)
            {
                yield return WaitSave(entry.File, saveBefore, 20f);
                yield return ReadFileMeta(registry, entry.File, box);
                // The engine rewrites the reusable file and renames the in-place item to the category
                // name; put the preset's own name back so export does not rename the user's item.
                if (!string.IsNullOrWhiteSpace(beforeName)
                    && !string.Equals(box.Name, beforeName, StringComparison.Ordinal))
                {
                    Presets.RenameCustomItem(item, beforeName, out _);
                    yield return WaitFileMetaName(registry, entry.File, beforeName, box, 10f);
                }
            }
        }

        var name = box.Name ?? CustomItems.DisplayName(item) ?? entry.Name;
        int exportSave = CustomItemSignals.SaveVersion;
        if (!CustomItems.TryBeginExport(item, path, out var task, out var engineErrors, out var beginError))
        { ctx.Error = $"export_custom_preset: {beginError}"; yield break; }

        yield return WaitTask(task, 30f);
        var taskError = TaskError(task);
        if (engineErrors.Count > 0 || taskError != null)
        {
            ctx.Error = $"export_custom_preset: {(taskError ?? EngineErrors(engineErrors))}";
            yield break;
        }

        // ExportCustomItem's Save writes the requested file; wait for that exact path, not a timer.
        yield return WaitSave(win, exportSave, 20f);
        yield return CommandUtil.WaitUntil(() => File.Exists(win) || File.Exists(path), 2f);
        var existing = File.Exists(win) ? win : (File.Exists(path) ? path : null);
        if (existing == null)
        { ctx.Error = $"export_custom_preset: the engine reported success but wrote no file: {path}"; yield break; }

        ctx.Result = new Dictionary<string, object>
        {
            ["exported"] = true,
            ["section"] = Sections.SectionLabel(section),
            ["category"] = tab,
            ["id"] = id,
            ["name"] = name,
            ["path"] = WinePaths.ToHost(existing),
            ["bytes"] = new FileInfo(existing).Length,
            ["worn"] = worn,
            ["saved"] = saved,
            ["save_requested"] = save,
        };
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerator WaitCategorySelected(string selectorShort, string tab)
    {
        yield return CommandUtil.WaitUntil(() =>
        {
            var sel = Categories.FindSelectorByShort(selectorShort);
            return string.Equals(Get(Get(sel, "SelectedCategory"), "Name") as string, tab, StringComparison.Ordinal);
        }, 5f);
    }

    private static IEnumerator WaitSave(string expectedPath, int saveBefore, float timeout)
    {
        yield return CommandUtil.WaitUntil(
            () => CustomItemSignals.SaveVersion > saveBefore
                  && CustomItemSignals.SamePath(CustomItemSignals.LastSavePath, expectedPath), timeout);
    }

    // Read a *.vroidcustomitem through the engine's own loader (the same reader the import uses).
    private static IEnumerator ReadFileMeta(object registry, string file, MetaBox box)
    {
        box.Name = null;
        box.Type = null;
        box.Error = null;
        if (!CustomItems.TryBeginLoad(registry, file, out var task, out var error))
        { box.Error = error; yield break; }
        yield return WaitTask(task, 20f);
        var taskError = TaskError(task);
        if (taskError != null) { box.Error = taskError; yield break; }
        if (!CustomItems.TryReadContents(Get(task, "Result"), out var typeId, out var name, out var metaError))
        { box.Error = metaError; yield break; }
        box.Type = typeId;
        box.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    // Rename writes the file asynchronously; re-read the file until its engine metadata carries the
    // expected name. Each iteration is a fresh engine read (not a fixed sleep).
    private static IEnumerator WaitFileMetaName(object registry, string file, string expected, MetaBox box, float timeout)
    {
        float deadline = UnityEngine.Time.unscaledTime + timeout;
        do
        {
            yield return ReadFileMeta(registry, file, box);
            if (string.Equals(box.Name, expected, StringComparison.Ordinal)) yield break;
            yield return null;
        } while (UnityEngine.Time.unscaledTime < deadline);
    }

    private static IEnumerator WaitTask(object task, float timeout)
    {
        if (task == null) yield break;
        yield return CommandUtil.WaitUntil(() => Get(task, "IsCompleted") is bool b && b, timeout);
    }

    private static string TaskError(object task)
    {
        if (task == null) return null;
        if (Get(task, "IsCanceled") is bool canceled && canceled) return "engine task canceled";
        if (!(Get(task, "IsCompleted") is bool done) || !done) return "engine task timed out";
        if (Get(task, "IsFaulted") is bool faulted && faulted)
        {
            var ex = Get(task, "Exception");
            return Get(ex, "Message") as string ?? SafeStr(ex) ?? "engine task faulted";
        }
        return null;
    }

    private static string EngineErrors(List<int> errors)
        => "engine rejected the item (error type " + string.Join(", ", errors) + ")";

}
