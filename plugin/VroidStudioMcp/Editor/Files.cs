using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using static VroidStudioMcp.Interop;
using static VroidStudioMcp.VroidState;

namespace VroidStudioMcp;

public static class FileOps
{
    // Quit without the "Unsaved Edits" dialog (clear the dirty flag first).
    public static void QuitClean()
    {
        var mah = MainActionHandler();
        if (mah != null) { try { Set(mah, "IsUnSaved", false); } catch { } }
        UnityEngine.Application.Quit();
    }
    // Create a fresh base model (the Male/Female template dialog ultimately calls
    // MainViewModel.InitializeCurrentFileAndMoveToEditRoot(presetPattern, modelVariantType)).
    public static void CreateCharacter(string presetPattern, string modelVariantType)
    {
        try
        {
            if (_mainViewModel == null) { PluginLog.LogError("create: MainViewModel not captured yet"); return; }
            Invoke(_mainViewModel, "InitializeCurrentFileAndMoveToEditRoot", presetPattern, modelVariantType);
            PluginLog.LogDebug($"create: {presetPattern} / {modelVariantType}");
        }
        catch (Exception e) { PluginLog.LogError($"create failed: {e.Message}"); }
    }
    public static void OpenCharacter(string path)
    {
        try
        {
            if (_mainViewModel == null) { PluginLog.LogError("open: MainViewModel not captured yet"); return; }
            var p = WinePaths.ToDos(path);
            var obj = RealObj(_mainViewModel);
            var m = obj.GetType().GetMethod("ImportFileAndMoveToEditRoot",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) { PluginLog.LogError("open: ImportFileAndMoveToEditRoot not found"); return; }
            m.Invoke(obj, new object[] { p, false, false });
            PluginLog.LogDebug($"open: {p}");
        }
        catch (Exception e) { PluginLog.LogError($"open failed: {e.Message}"); }
    }

    // The engine's dirty flag on MainActionHandler (`IsUnSaved`); a missing handler counts as clean.
    public static bool IsUnsaved()
    {
        var mah = MainActionHandler();
        return mah != null && Get(mah, "IsUnSaved") is bool b && b;
    }

    // Current on-disk path of the edited model (empty for a model created from the template and not
    // saved yet). Read via EditRoot.ViewModel -> MainViewModel -> MainModel.currentFile.path, with a
    // fallback to the last path the plugin wrote (the engine sometimes keeps it stale after save-as).
    public static string CurrentModelPath()
    {
        try
        {
            var cf = Get(Get(Get(_editRootVm, "_mainViewModel"), "_mainModel"), "currentFile");
            var p = Get(cf, "path") as string;
            if (!string.IsNullOrEmpty(p)) return p;
        }
        catch { }
        return _lastSavePath;
    }

    // After a successful save-as, point the engine's currentFile at the new path (so its own dirty /
    // save-in-place logic agrees) and remember it as the fallback.
    public static void RememberSavedPath(string winPath)
    {
        _lastSavePath = winPath;
        try
        {
            var cf = Get(Get(Get(_editRootVm, "_mainViewModel"), "_mainModel"), "currentFile");
            if (cf != null) Set(cf, "path", winPath);
        }
        catch { }
    }

    // Best-effort regular-file check: reject directories and device files. On Unix .NET flags device
    // files as FileAttributes.Device; a FIFO may still read Normal, but Wine already reports most
    // special files as nonexistent, so this is belt-and-suspenders against a wedged import.
    public static bool IsRegularFile(string p)
    {
        try
        {
            var fi = new FileInfo(p);
            if (!fi.Exists) return false;
            var a = fi.Attributes;
            return (a & FileAttributes.Directory) == 0 && (a & FileAttributes.Device) == 0;
        }
        catch { return false; }
    }
}
