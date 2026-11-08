using System;
using System.Threading;

namespace VroidStudioMcp;

using static VroidStudioMcp.Interop;

/// <summary>Captured engine objects, readiness and change-synchronisation signals.</summary>
public static class VroidState
{
    internal static object _cameraController;
    internal static object _cameraPosition;
    internal static object _mainViewModel;
    internal static object _editRootVm;
    internal static object _engineModel;
    private static object _mainActionHandler;
    internal static object _loadErrorDialog;
    internal static object _loadErrorProvider;
    // True only while open/create waits for the model: dialog capture is gated on it so unrelated
    // dialogs (tutorial, settings, ...) are never mistaken for a load error.
    internal static volatile bool _awaitingLoadError;
    // Last path the plugin wrote the model to (open / save-as). Fallback for "save in place" when
    // the engine's currentFile.path has not been refreshed (e.g. right after a plugin save-as).
    internal static string _lastSavePath;

    internal static volatile bool _modelReady;
    internal static volatile bool _modelUpdating;
    // "a model is being edited" — driven by EditRootScreen hooks (true on activate/open, false on
    // BackToModelSelect), because IsEditModelActive stays true after returning to the model select.
    internal static volatile bool _inEdit;

    [ThreadStatic] internal static int _refreshDepth;

    public static object MainActionHandler()
    {
        if (_mainActionHandler == null && _editRootVm != null)
            _mainActionHandler = Get(_editRootVm, "_mainActionHandler");
        return _mainActionHandler;
    }

    public static void OnMainActionHandler(object __instance) { if (__instance != null) _mainActionHandler = __instance; }
    public static void OnLoadErrorDialog(object __instance)
    {
        try
        {
            if (__instance == null || !_awaitingLoadError) return;
            _loadErrorDialog = __instance;
        }
        catch { }
    }

    // Funnel for every dialog the engine shows (DialogProviderViewModel.Show). The failed-load
    // dialog is not the generic ErrorDialogViewModel, so capture whatever the provider is handed and
    // remember the provider so it can be dismissed. Only while a load is in flight.
    public static void OnDialogShown(object __instance, object[] __args)
    {
        // Trace EVERY modal dialog (dialog_trace devtool) - a dialog shown during an in-flight command
        // is one the plugin did not intercept and will block until a human answers.
        var dialog = First(__args);
        DialogTrace.OnShow(dialog);
        NativeAvatarExport.OnDialogProvider(__instance);
        try
        {
            if (__instance == null || !_awaitingLoadError) return;
            _loadErrorProvider = __instance;
            _loadErrorDialog = dialog;
        }
        catch { }
    }
    public static void DismissLoadErrorDialog()
    {
        if (_loadErrorDialog == null && _loadErrorProvider == null) return;
        try { if (_loadErrorDialog != null) Interop.Invoke(_loadErrorDialog, "Dismiss"); } catch { }
        // The provider owns the active dialog; dismiss through it too in case the dialog VM has no
        // Dismiss (the failed-load dialog is not necessarily an ErrorDialogViewModel).
        try { if (_loadErrorProvider != null) Interop.Invoke(_loadErrorProvider, "Dismiss"); } catch { }
        _loadErrorDialog = null;
        _loadErrorProvider = null;
    }
    public static void OnModelUpdate(object __instance) { _modelReady = true; }

    // Postfix on VRoidCoreModelUpdater.set_IsUpdatingModel(bool). Harmony binds by name,
    // so the parameter must be named after the original (`value`), not `updating`.
    public static void OnIsUpdating(bool value)
    {
        if (value) { _modelUpdating = true; _modelReady = false; }
        else if (_modelUpdating) { _modelUpdating = false; _modelReady = true; Signals.ModelRebuilt(); }
    }

    // Session transitions live here so the readiness flags are not written from other layers.
    internal static void EnterEdit() => _inEdit = true;
    // Drop the objects that belong to the edit session: a stale edit-root VM / last save path must
    // never be reused after returning to the model select. _mainViewModel and the camera objects are
    // captured once and persist across screens (camera Start/OnEnable do not re-fire), so they stay.
    internal static void LeaveEdit()
    {
        _inEdit = false; _modelReady = false; _modelUpdating = false;
        _editRootVm = null;
        _lastSavePath = null;
        _loadErrorDialog = null;
        _loadErrorProvider = null;
        _awaitingLoadError = false;
    }
    internal static void MarkModelLoading() { _modelReady = false; _modelUpdating = false; }
}
