using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using static VroidStudioMcp.Interop;
using static VroidStudioMcp.VroidState;

namespace VroidStudioMcp;

/// <summary>Model/session lifecycle commands: state, open, create, save, exit, main_menu, test.</summary>
public static class SessionHandlers
{
    public static IEnumerator State(CommandDispatcher.Context ctx)
    {
        // _inEdit is hook-driven (edit root activate/open vs BackToModelSelect); IsEditModelActive
        // stays true after returning to the model select, so it cannot be used directly here.
        bool inEdit = _inEdit;
        ctx.Result = new Dictionary<string, object>
        {
            ["ready"] = _modelReady && inEdit,
            ["updating"] = _modelUpdating,
            ["selectors"] = inEdit ? Selectors.Count : 0,
            ["in_edit"] = inEdit,
            ["process_id"] = Environment.ProcessId,
            ["app_version"] = UnityEngine.Application.version,
            ["plugin_build"] = typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString(),
            ["model_path"] = inEdit ? WinePaths.ToHost(FileOps.CurrentModelPath()) : null,
            ["unsaved"] = inEdit && MainActionHandler() != null
                ? Get(MainActionHandler(), "IsUnSaved") : null,
            ["section"] = inEdit && _editRootVm != null ? Sections.SectionLabel(Get(_editRootVm, "EditMode")?.ToString()) : null,
        };
        yield break;
    }

    // After the selectors exist: wait for the edit-root VM (so `section` is valid), the engine
    // rebuild signal (IsUpdatingModel true->false, so the model is fully built), and reset the
    // camera so the first screenshot is not a blank frame.
    private static IEnumerator WaitModelBuilt()
    {
        yield return CommandUtil.WaitUntil(() => _editRootVm != null, 5f);
        yield return CommandUtil.WaitUntil(() => !_modelUpdating && _modelReady, 25f);
        yield return CommandUtil.WaitUntil(() => _cameraPosition != null, 5f);
        // Snapshot the fresh model's projection before the first reset, so `reset` can restore it.
        CameraApi.CaptureInitialProjection();
        CameraApi.ApplyCamera("reset");
        yield return CommandUtil.Pause(0.3f);
    }

    // A failed open/create can leave the session half-entered (the edit root activated, but no model
    // loaded) with the error dialog still up. Reset to the model select so open/create/main_menu keep
    // working instead of the session being permanently stuck (`_inEdit` true, no selectors).
    public static void ResetSession()
    {
        _awaitingLoadError = false;
        DismissLoadErrorDialog();
        Selectors.Clear();
        Catalog.ResetDumps();
        LeaveEdit();
    }

    public static IEnumerator Open(CommandDispatcher.Context ctx)
    {
        var path = CommandUtil.Arg(ctx, 0);
        if (path == null) { ctx.Error = "usage: open <path>"; yield break; }
        // Opening is a separate editor session, exactly like create: require the model select so the
        // caller always makes an explicit decision about unsaved edits (which open would discard).
        if (_inEdit)
        { ctx.Error = "a model is open; call save then main_menu (keep), or main_menu (discard), then open"; yield break; }
        var win = WinePaths.ToDos(path);
        // Reject directories before touching the engine: File.Exists can report true for a directory
        // under Wine, and ImportFileAndMoveToEditRoot on a directory blocks the main thread forever.
        if (Directory.Exists(win) || Directory.Exists(path)) { ctx.Error = $"not a file: {path}"; yield break; }
        var existing = File.Exists(win) ? win : (File.Exists(path) ? path : null);
        if (existing == null) { ctx.Error = $"file not found: {path}"; yield break; }
        // Also reject non-regular files (FIFO/socket/device): importing one can block the loader
        // forever. Symlinks are fine (attributes resolve to the target, not the link).
        if (!FileOps.IsRegularFile(existing)) { ctx.Error = $"not a regular file: {path}"; yield break; }

        _loadErrorDialog = null;
        _loadErrorProvider = null;
        _awaitingLoadError = true;
        MarkModelLoading();
        var sw = Stopwatch.StartNew();
        FileOps.OpenCharacter(win);
        yield return CommandUtil.WaitUntil(() => Selectors.Count >= 4 || _loadErrorDialog != null, 40f);
        _awaitingLoadError = false;
        Perf.Since("open: selectors", sw);
        if (_loadErrorDialog != null) { ResetSession(); ctx.Error = $"failed to load: {path}"; yield break; }
        if (Selectors.Count < 4) { ResetSession(); ctx.Error = $"failed to load: {path}"; yield break; }
        yield return WaitModelBuilt();
        Perf.Since("open: model ready", sw);
        _lastSavePath = win;
        ctx.Result = WinePaths.ToHost(win);
    }

    // Create a fresh base model from the Male/Female template dialog choice. Only valid from the
    // model-select screen: if a model is open the caller must first save (or discard) and go to the
    // main menu, so a decision about unsaved edits is always made explicitly.
    public static IEnumerator Create(CommandDispatcher.Context ctx)
    {
        var gender = (CommandUtil.Arg(ctx, 0) ?? "").Trim().ToLowerInvariant();
        string preset, variant;
        switch (gender)
        {
            case "female": preset = "Preset_F"; variant = "F00"; break;
            case "male": preset = "Preset_M"; variant = "M00"; break;
            default: ctx.Error = "usage: create <female|male>"; yield break;
        }
        if (_inEdit)
        {
            ctx.Error = "a model is open; call save then main_menu (keep), or main_menu (discard), then create";
            yield break;
        }
        _loadErrorDialog = null;
        _loadErrorProvider = null;
        _awaitingLoadError = true;
        MarkModelLoading();
        _lastSavePath = null;
        var sw = Stopwatch.StartNew();
        FileOps.CreateCharacter(preset, variant);
        yield return CommandUtil.WaitUntil(() => Selectors.Count >= 4 || _loadErrorDialog != null, 40f);
        _awaitingLoadError = false;
        Perf.Since("create: selectors", sw);
        if (_loadErrorDialog != null || Selectors.Count < 4) { ResetSession(); ctx.Error = $"failed to create {gender} model"; yield break; }
        yield return WaitModelBuilt();
        Perf.Since("create: model ready", sw);
        ctx.Result = new Dictionary<string, object> { ["gender"] = gender, ["variant"] = variant };
    }

    public static IEnumerator Test(CommandDispatcher.Context ctx)
    {
        var name = CommandUtil.Arg(ctx, 0); var path = CommandUtil.Arg(ctx, 1);
        var test = TestRunner.Create(name, path);
        if (test == null) { ctx.Error = $"unknown test '{name}'"; yield break; }
        yield return test.Execute();
        ctx.Result = name;
    }

    public static IEnumerator Save(CommandDispatcher.Context ctx)
    {
        var mah = MainActionHandler();
        var path = CommandUtil.Arg(ctx, 0);
        if (!string.IsNullOrEmpty(path))
        {
            // save-as: write the current model to an explicit path (no file dialog)
            var win = WinePaths.ToDos(path);
            if (mah == null) { ctx.Error = "save: action handler not captured"; yield break; }
            bool ok = false;
            try { ok = Invoke(mah, "SaveSync", win, true) is bool b && b; }
            catch (Exception e) { ctx.Error = $"save failed: {WinePaths.ToHost(e.Message)}"; yield break; }
            // Fail fast when the engine refused; otherwise wait (bounded) for the file to appear on
            // disk instead of a fixed sleep, so a slow/flaky filesystem is handled without padding.
            if (!ok) { ctx.Error = $"save failed: {WinePaths.ToHost(win)}"; yield break; }
            yield return CommandUtil.WaitUntil(() => File.Exists(win), 15f);
            if (!File.Exists(win)) { ctx.Error = $"save failed: {WinePaths.ToHost(win)}"; yield break; }
            // The engine does not always refresh currentFile.path after a plugin save-as, so record
            // it ourselves; without this a following pathless save() still claims "no file".
            FileOps.RememberSavedPath(win);
            ctx.Result = WinePaths.ToHost(win);
            yield break;
        }
        // No path: only "save in place" if the model already has a file. A freshly created model has
        // none, and calling Save then pops the editor's native Save-As dialog (and blocks nothing,
        // so it looks successful) -> refuse instead.
        if (string.IsNullOrEmpty(FileOps.CurrentModelPath()))
        { ctx.Error = "save: model has no file yet; pass a path (save-as)"; yield break; }
        // In-place Save is an engine command: resolve by the dirty flag instead of a fixed sleep
        // (an already-clean model clears immediately; a dirty one waits for the actual save).
        if (_editRootVm != null) Invoke(_editRootVm, "Save");
        yield return CommandUtil.WaitUntil(() => !FileOps.IsUnsaved(), 20f);
        if (FileOps.IsUnsaved()) { ctx.Error = "save failed: model still marked unsaved"; yield break; }
        ctx.Result = "saved";
    }

    public static IEnumerator Exit(CommandDispatcher.Context ctx)
    {
        var mode = (CommandUtil.Arg(ctx, 0) ?? "discard").ToLowerInvariant();
        if (mode == "save")
        {
            // Wait for the dirty flag to clear where a real in-place save can happen, instead of a
            // fixed sleep; a model with no file would pop a native Save-As dialog, so don't wait on it.
            var savePath = FileOps.CurrentModelPath();
            bool wasDirty = FileOps.IsUnsaved();
            if (_editRootVm != null) Invoke(_editRootVm, "Save");
            if (!string.IsNullOrEmpty(savePath)) yield return CommandUtil.WaitUntil(() => !FileOps.IsUnsaved(), 20f);
            else yield return CommandUtil.Pause(0.5f);
            PluginLog.LogDebug($"exit save: path='{savePath}' wasDirty={wasDirty} dirtyAfter={FileOps.IsUnsaved()}");
        }
        ctx.Result = mode;
        FileOps.QuitClean();
    }

    public static IEnumerator MainMenu(CommandDispatcher.Context ctx)
    {
        var handler = MainActionHandler();
        if (handler != null) Set(handler, "IsUnSaved", false);
        if (_editRootVm != null) Invoke(_editRootVm, "BackToModelSelect");
        // BackToModelSelect (if it fired) resets the session via its hook; if the edit-root VM was
        // never captured (e.g. after a failed open) reset here so main_menu always recovers.
        if (_inEdit) ResetSession();
        ctx.Result = "main menu";
        yield break;
    }
}
