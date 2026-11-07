using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

/// <summary>
/// Reusable custom items (<c>*.vroidcustomitem</c>, the editor's Custom sub-tab) for Hairstyle and
/// Outfit. Both operations are engine methods with no dialog:
/// <list type="bullet">
/// <item><c>CustomItemRegistry.ImportCustomItemByTransferableType(globalBus, path, context, onError)</c>
/// reads a file, writes a fresh registry file and notifies the Custom tab.</item>
/// <item><c>CustomItemRegistry.ExportCustomItem(model, globalBus, path, context, onError)</c> re-writes
/// the item's stored contents to an explicit path.</item>
/// </list>
/// The stock UI wraps them in an open/save file dialog, and "Save and export" first saves the worn
/// item's current design (<c>CustomItemOptionViewModel.SaveCustomItem(tryOverwrite: true)</c>). That
/// save step is why the menu is normally offered on the worn item only; <see cref="ExportCustomItem"/>
/// itself takes any <c>FileCustomItemModel</c>, so a stored, unworn item can be exported as-is.
/// </summary>
public static class CustomItems
{
    public const string Extension = ".vroidcustomitem";

    // The engine follows a successful import with a modal ImportCustomItemDialogViewModel
    // ("Imported successfully" / Wear now / Wear later). It is not awaited, but it blocks the
    // editor until a human clicks a button. The import command arms these flags; the Show prefix
    // answers the dialog before it is displayed (Wear later by default, Wear now when requested).
    public static bool AnswerImportDialog;
    public static bool AnswerImportWear;
    private static float _dialogDeadline;

    /// <summary>Arm the one-shot answer for the import-success dialog. The deadline is a safety net:
    /// if the command aborts before the engine shows the dialog, the arm expires instead of
    /// swallowing a later human-driven import dialog.</summary>
    public static void ArmImportDialog(bool wear)
    {
        AnswerImportDialog = true;
        AnswerImportWear = wear;
        _dialogDeadline = UnityEngine.Time.unscaledTime + 30f;
    }

    public static void ClearImportDialog()
    {
        AnswerImportDialog = false;
        AnswerImportWear = false;
    }

    /// <summary>Custom items exist for the Hairstyle and Outfit sections only.</summary>
    public static bool SectionSupported(string section)
        => string.Equals(section, "HairEditor", StringComparison.Ordinal)
        || string.Equals(section, "ClothingEditor", StringComparison.Ordinal);

    // ------------------------------------------------------------------ file / metadata helpers

    public static bool LooksLikeCustomItem(string path)
        => !string.IsNullOrEmpty(path)
        && string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Append the extension when the caller passed a bare path; keep an explicit one.</summary>
    public static string NormalizeExportPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return Path.HasExtension(path) ? path : path + Extension;
    }

    /// <summary>Read a custom item file through the engine's own loader
    /// (<c>CustomItemRegistry.LoadCustomItemContentFromPath</c>); this is the same reader the import
    /// itself uses, so the plugin never parses the zip format itself.</summary>
    public static bool TryBeginLoad(object registry, string hostPath, out object task, out string error)
    {
        task = null;
        error = null;
        if (registry == null) { error = "no custom item registry"; return false; }
        if (!TryInvoke(registry, "LoadCustomItemContentFromPath", out task, out error, WinePaths.ToDos(hostPath)))
        { error = $"load failed: {error}"; return false; }
        if (task == null) { error = "load: the engine returned no task"; return false; }
        return true;
    }

    /// <summary>Extract the category type and display name from the engine's
    /// <c>V1CustomItemContents&lt;VRoidCustomItemFileMetaData&gt;</c> returned by the loader.</summary>
    public static bool TryReadContents(object contents, out string typeId, out string displayName, out string error)
    {
        typeId = null;
        displayName = null;
        error = null;
        var meta = Get(contents, "customItemMetaData");
        if (meta == null) { error = "the engine returned no customItemMetaData"; return false; }
        typeId = Get(meta, "rootTransferableGroupTypeId") as string;
        displayName = Get(meta, "displayName") as string;
        if (string.IsNullOrEmpty(typeId))
        {
            error = "the file has no rootTransferableGroupTypeId";
            return false;
        }
        return true;
    }

    /// <summary>The category's TransferableGroupType (e.g. TransferableGroupType.N00.Level1.HairBack).</summary>
    public static string CategoryType(object category)
        => Get(category, "TransferableGroupType") as string;

    public static string DisplayName(object item)
    {
        var n = Presets.CustomItemName(item);
        return string.IsNullOrWhiteSpace(n) ? null : n.Trim();
    }

    // ------------------------------------------------------------------ dialog answer

    /// <summary>Harmony prefix on <c>DialogProviderViewModel.Show</c>. Answers the import-success
    /// dialog for a programmatic import without ever displaying it; every other dialog falls through.
    /// "Wear later" (default) leaves the model untouched and matches <c>applied:false</c>; "Wear now"
    /// invokes the dialog's primary action callback so the imported item is applied.</summary>
    public static bool OnDialogShow(object __0)
    {
        if (!AnswerImportDialog) return true;
        if (UnityEngine.Time.unscaledTime > _dialogDeadline) { ClearImportDialog(); return true; }
        var type = TypeName(__0) ?? "";
        if (!type.EndsWith("ImportCustomItemDialogViewModel", StringComparison.Ordinal)) return true;

        bool wear = AnswerImportWear;
        ClearImportDialog();
        try
        {
            if (wear)
            {
                var actions = Enumerate(Get(__0, "DialogActions")).ToList();
                var primary = actions.FirstOrDefault(a => SafeInt(Get(a, "ButtonType")) == 0)
                           ?? actions.FirstOrDefault();
                if (primary != null) Invoke(primary, "InvokeCallback");
            }
        }
        catch (Exception e) { PluginLog.LogError($"import dialog: {e.Message}"); }
        PluginLog.LogDebug($"import: answered the 'Imported successfully' dialog immediately " +
                             $"({(wear ? "Wear now" : "Wear later")})");
        return false;
    }

    private static int SafeInt(object o)
    {
        try { return Convert.ToInt32(o); } catch { return -1; }
    }

    // ------------------------------------------------------------------ engine calls

    /// <summary>Start the engine import. Returns the engine task; <paramref name="engineErrors"/> is
    /// filled by the native error callback (a boxed <c>CustomItemErrorType</c> value per failure).</summary>
    public static bool TryBeginImport(object presetNode, string hostPath, out object task,
                                      out List<int> engineErrors, out string error)
    {
        task = null;
        engineErrors = new List<int>();
        error = null;

        var slot = Presets.CustomItemOfKind(presetNode, "import");
        if (slot == null) { error = "this category has no Custom Import slot"; return false; }
        var registry = Get(slot, "_customItemRegistry");
        var globalBus = Get(slot, "_globalBus");
        var context = Get(slot, "_context");
        if (registry == null || globalBus == null || context == null)
        { error = "the Custom Import slot is missing its registry/bus/context"; return false; }

        var method = FindMethod(registry, "ImportCustomItemByTransferableType");
        if (method == null) { error = "engine method ImportCustomItemByTransferableType not found"; return false; }
        var callback = MakeErrorCallback(method.GetParameters().LastOrDefault()?.ParameterType, engineErrors);
        if (!TryInvoke(registry, "ImportCustomItemByTransferableType", out task, out error,
                       globalBus, WinePaths.ToDos(hostPath), context, callback))
        { error = $"import failed: {error}"; return false; }
        if (task == null) { error = "import: the engine returned no task"; return false; }
        return true;
    }

    /// <summary>Save the worn item's current design into its reusable file (the "Save" half of the
    /// UI's "Save and export"). A non-worn item has no design in the model to save, so this is a
    /// no-op for it (the stored file is what gets exported).</summary>
    public static bool TrySaveWorn(object item, out bool saved, out string error)
    {
        saved = false;
        error = null;
        if (!(Get(item, "IsSelected") is bool worn) || !worn) return true;
        if (!TryInvoke(item, "OverwriteCustomItem", out _, out error))
        { error = $"save before export failed: {error}"; return false; }
        saved = true;
        return true;
    }

    /// <summary>Start the engine export of one custom item to an explicit path.</summary>
    public static bool TryBeginExport(object item, string hostPath, out object task,
                                      out List<int> engineErrors, out string error)
    {
        task = null;
        engineErrors = new List<int>();
        error = null;

        var model = Get(item, "_model");
        var registry = Get(item, "_customItemRegistry");
        var globalBus = Get(item, "_globalBus");
        var context = Get(item, "_context");
        if (model == null || registry == null || globalBus == null || context == null)
        { error = "the custom item is missing its model/registry/bus/context"; return false; }

        var method = FindMethod(registry, "ExportCustomItem");
        if (method == null) { error = "engine method ExportCustomItem not found"; return false; }
        var callback = MakeErrorCallback(method.GetParameters().LastOrDefault()?.ParameterType, engineErrors);
        if (!TryInvoke(registry, "ExportCustomItem", out task, out error,
                       model, globalBus, WinePaths.ToDos(hostPath), context, callback))
        { error = $"export failed: {error}"; return false; }
        if (task == null) { error = "export: the engine returned no task"; return false; }
        return true;
    }

    /// <summary>Delete a saved custom item through the registry (the dialog-free half of the UI's
    /// Delete action). Used by the round-trip test to leave the user's library untouched.</summary>
    public static bool TryDelete(object item, out string error)
    {
        error = null;
        var model = Get(item, "_model");
        var registry = Get(item, "_customItemRegistry");
        var globalBus = Get(item, "_globalBus");
        if (model == null || registry == null || globalBus == null)
        { error = "the custom item is missing its model/registry/bus"; return false; }
        var path = Get(model, "FilePath") as string;
        if (string.IsNullOrEmpty(path)) { error = "the custom item has no file path"; return false; }

        var method = FindMethod(registry, "DeleteCustomItem");
        var callback = MakeErrorCallback(method?.GetParameters().LastOrDefault()?.ParameterType, new List<int>());
        if (!TryInvoke(registry, "DeleteCustomItem", out _, out error, path, globalBus, callback))
        { error = $"delete failed: {error}"; return false; }
        return true;
    }

    // ------------------------------------------------------------------ custom-list bookkeeping

    private static string NormalizedFile(string path)
        => string.IsNullOrEmpty(path) ? null : path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    /// <summary>Snapshot of the category's saved custom-item files (the add/import tiles excluded).</summary>
    public static HashSet<string> Files(string selectorShort, string tab)
        => new(Presets.Entries(selectorShort, tab, "custom")
                   .Select(e => NormalizedFile(e.File))
                   .Where(f => f != null));

    /// <summary>The entry that appeared after an import (matched by its registry file, not by object
    /// identity: the engine rebuilds the whole Custom list on every registry notification).</summary>
    public static Presets.Entry NewEntry(string selectorShort, string tab, HashSet<string> before)
        => Presets.Entries(selectorShort, tab, "custom")
                  .FirstOrDefault(e => !string.IsNullOrEmpty(e.File) && !before.Contains(NormalizedFile(e.File)));

    /// <summary>Re-resolve an entry after a save/rename rebuilt the Custom list.</summary>
    public static Presets.Entry FindByFile(string selectorShort, string tab, string file)
    {
        var wanted = NormalizedFile(file);
        if (wanted == null) return null;
        return Presets.Entries(selectorShort, tab, "custom")
                      .FirstOrDefault(e => NormalizedFile(e.File) == wanted);
    }

    public static int IdOf(string selectorShort, string tab, Presets.Entry entry)
    {
        if (entry == null) return -1;
        var wanted = NormalizedFile(entry.File);
        var list = Presets.Entries(selectorShort, tab, "all");
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i].Option, entry.Option)) return i;
            if (wanted != null && NormalizedFile(list[i].File) == wanted) return i;
        }
        return -1;
    }

    // ------------------------------------------------------------------ native error callback

    private static System.Reflection.MethodInfo FindMethod(object instance, string name)
    {
        var obj = RealObj(instance);
        return obj?.GetType().GetMethods(System.Reflection.BindingFlags.Public
                                         | System.Reflection.BindingFlags.NonPublic
                                         | System.Reflection.BindingFlags.Instance)
                  .FirstOrDefault(m => m.Name == name);
    }

    /// <summary>Build the engine's native <c>Action&lt;GlobalBus, CustomItemErrorType&gt;</c> from a
    /// managed delegate through Il2CppInterop; errors are collected as the enum's numeric value. The
    /// delegate type is taken from the engine method itself, so nothing is hard-coded to this build.</summary>
    private static object MakeErrorCallback(Type nativeDelegateType, List<int> errors)
    {
        if (nativeDelegateType == null || !typeof(Delegate).IsAssignableFrom(nativeDelegateType)) return null;
        try
        {
            var args = nativeDelegateType.GetGenericArguments();
            if (args.Length != 2) return null;
            var makeManaged = typeof(CustomItems)
                .GetMethod(nameof(MakeManagedCallback), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.MakeGenericMethod(args);
            var managed = makeManaged?.Invoke(null, new object[] { errors }) as Delegate;
            if (managed == null) return null;

            RuntimeHelpers.RunClassConstructor(nativeDelegateType.TypeHandle);
            var convert = typeof(DelegateSupport).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "ConvertDelegate" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            if (convert == null) return null;
            return convert.MakeGenericMethod(nativeDelegateType).Invoke(null, new object[] { managed });
        }
        catch (Exception e)
        {
            PluginLog.LogError($"custom item error callback: {e.Message}");
            return null;
        }
    }

    private static Delegate MakeManagedCallback<TBus, TError>(List<int> errors)
    {
        Action<TBus, TError> callback = (_, error) =>
        {
            try { errors.Add(Convert.ToInt32(error)); }
            catch { errors.Add(-1); }
        };
        return callback;
    }
}
