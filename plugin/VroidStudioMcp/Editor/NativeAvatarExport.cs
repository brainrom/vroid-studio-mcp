using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

/// <summary>Experimental, dialog-free use of the existing native export-screen coroutine.</summary>
public static class NativeAvatarExport
{
    private static object _screen;
    private static object _dialogProvider;
    private static volatile Job _job;
    public static bool Busy => _job != null && !_job.Done;

    public sealed class Job
    {
        public volatile bool Done;
        public bool Success, Abandoned;
        public string StagePath;
        // Keep reverse P/Invoke delegates alive until the native coroutine finishes.
        internal Il2CppSystem.Action OnSuccess, OnFailure;
        internal Il2CppSystem.Action<double> OnProgress;
        internal object Coroutine;
    }

    public static void OnScreenActivate(object __instance) => _screen = __instance;
    public static void OnScreenDeactivate(object __instance)
    {
        if (_screen is Il2CppObjectBase active && __instance is Il2CppObjectBase leaving && active.Pointer == leaving.Pointer)
            _screen = null;
    }
    public static void OnDialogProvider(object provider) => _dialogProvider = provider;

    public static bool TryPrepare(string hostPath, string version, string metadataJson, bool overwrite,
                                  out string target, out object vm, out object metadata, out string error)
    {
        target = null; vm = null; metadata = null; error = null;
        try
        {
            if (!OperatingSystem.IsWindows() || WinePaths.IsWine)
                throw new InvalidOperationException("experimental native avatar export currently supports Windows only");
            if (UnityEngine.Application.version != "2.16.0")
                throw new InvalidOperationException("native avatar export signatures have only been inspected for app 2.16.0");
            if (Busy) throw new InvalidOperationException("a native export is still running; do not retry it");
            if (_screen == null)
                throw new InvalidOperationException("open the app's native VRM Export screen first; this tool does not navigate or save the source model");
            if (!VroidState._modelReady || VroidState._modelUpdating)
                throw new InvalidOperationException("model is not ready or is updating");
            if (Get(_dialogProvider, "Dialog") != null || HasNativeModal())
                throw new InvalidOperationException("a native dialog is open; finish or cancel it before exporting");
            vm = RealObj(Get(_screen, "ObsoleteLegacyPhotoBoothViewModel"));
            if (Get(vm, "V1PhotoBooth") == null || Get(vm, "PhotoBoothCoroutineExecutor") == null)
                throw new InvalidOperationException("the native export screen has no initialized photo-booth context/coroutine executor");
            if (Get(vm, "_isBlockingUpdateExportPreview") is not bool updating || updating)
                throw new InvalidOperationException("native export preview is updating or its readiness is unknown");
            if (!Path.IsPathFullyQualified(hostPath) || hostPath.StartsWith("\\\\") || hostPath.IndexOf(':', 2) >= 0)
                throw new ArgumentException("path must be an absolute local file path (no UNC, device paths or alternate streams)");
            target = Path.GetFullPath(hostPath);
            if (!string.Equals(Path.GetExtension(target), ".vrm", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("path must end with .vrm");
            if (!Directory.Exists(Path.GetDirectoryName(target)) || Directory.Exists(target))
                throw new ArgumentException("target directory must exist and target must be a file path");
            if (File.Exists(target) && !overwrite) throw new IOException("file exists; pass overwrite=true explicitly to replace it");
            if (File.Exists(target) && (File.GetAttributes(target) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new IOException("target is not a regular file");
            metadata = BuildMetadata(version, metadataJson);
            return true;
        }
        catch (Exception e) { error = e.GetBaseException().Message; return false; }
    }

    public static bool TryBegin(object vm, object metadata, string version, string target, out Job job, out string error)
    {
        job = null; error = null;
        try
        {
            var current = new Job { StagePath = Path.Combine(Path.GetDirectoryName(target), ".vroid-mcp-" + Guid.NewGuid().ToString("N") + ".vrm") };
            current.OnSuccess = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)(() => Finish(current, true)));
            current.OnFailure = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)(() => Finish(current, false)));
            current.OnProgress = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<double>>((Action<double>)(_ => { }));
            var reductionType = FindType("VRoidStudio.PhotoBooth.MaterialReductionMode") ?? throw new MissingMemberException("MaterialReductionMode");
            if (!TryInvoke(vm, version == "0" ? "ExportVRM0" : "ExportVRM1", out var routine, out error,
                current.StagePath, metadata, true, false, Enum.Parse(reductionType, "None"), false,
                current.OnSuccess, current.OnFailure, current.OnProgress)) return false;
            if (routine is not Il2CppSystem.Collections.IEnumerator enumerator)
                throw new InvalidOperationException("native exporter returned no coroutine");
            var executor = RewrapTo(Get(vm, "PhotoBoothCoroutineExecutor"), typeof(UnityEngine.MonoBehaviour)) as UnityEngine.MonoBehaviour;
            if (executor == null || !executor.isActiveAndEnabled) throw new InvalidOperationException("native coroutine executor is inactive");
            current.Coroutine = executor.StartCoroutine(enumerator);
            if (current.Coroutine == null) throw new InvalidOperationException("native coroutine failed to start");
            _job = current;
            job = current;
            return true;
        }
        catch (Exception e) { error = e.GetBaseException().Message; return false; }
    }

    private static void Finish(Job job, bool success)
    {
        job.Success = success;
        job.Done = true;
        if (job.Abandoned) RemoveStage(job);
    }

    public static void RemoveStage(Job job)
    {
        try { if (File.Exists(job.StagePath)) File.Delete(job.StagePath); }
        catch (Exception e) { PluginLog.LogWarning("avatar export temporary file cleanup failed: " + e.Message); }
    }

    private static object BuildMetadata(string version, string json)
    {
        string[] required = version == "0"
            ? new[] { "title", "version", "author", "contactInformation", "reference", "allowedUser", "violentUssage", "sexualUssage", "commercialUssage", "otherPermissionUrl", "licenseType", "otherLicenseUrl" }
            : new[] { "Name", "Version", "Authors", "CopyrightInformation", "ContactInformation", "References", "ThirdPartyLicenses", "AvatarPermission", "ViolentUsage", "SexualUsage", "CommercialUsage", "PoliticalOrReligiousUsage", "AntisocialOrHateUsage", "CreditNotation", "Redistribution", "Modification", "OtherLicenseUrl" };
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("metadata_json must contain an object");
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!required.Contains(field.Name) || !values.TryAdd(field.Name, field.Value))
                throw new ArgumentException("unknown or duplicate metadata field: " + field.Name);
        foreach (var key in required)
            if (!values.ContainsKey(key)) throw new ArgumentException("explicit metadata field required: " + key);
        var type = FindType(version == "0" ? "VRM.glTF_VRM_Meta" : "UniVRM10.VRM10ObjectMeta")
                   ?? throw new MissingMemberException("native metadata type not found");
        var meta = Activator.CreateInstance(type);
        foreach (var (key, value) in values)
        {
            var property = type.GetProperty(key) ?? throw new MissingMemberException("native metadata property: " + key);
            object parsed;
            if (property.PropertyType == typeof(string) && value.ValueKind == JsonValueKind.String) parsed = value.GetString();
            else if (property.PropertyType == typeof(bool) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)) parsed = value.GetBoolean();
            else if (property.PropertyType.IsEnum && value.ValueKind == JsonValueKind.String
                     && Enum.GetNames(property.PropertyType).Contains(value.GetString())) parsed = Enum.Parse(property.PropertyType, value.GetString());
            else if ((key == "Authors" || key == "References") && value.ValueKind == JsonValueKind.Array)
            {
                var list = new Il2CppSystem.Collections.Generic.List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                        throw new ArgumentException(key + " must contain non-empty strings");
                    list.Add(item.GetString());
                }
                if (key == "Authors" && list.Count == 0) throw new ArgumentException("Authors must not be empty");
                parsed = list;
            }
            else throw new ArgumentException("invalid metadata value for " + key + (property.PropertyType.IsEnum ? "; use " + string.Join("|", Enum.GetNames(property.PropertyType)) : ""));
            if ((key == "title" || key == "Name" || key == "author" || key == "version" || key == "Version")
                && string.IsNullOrWhiteSpace(parsed as string)) throw new ArgumentException(key + " must not be empty");
            property.SetValue(meta, parsed);
        }
        return meta;
    }

    private delegate bool EnumWindow(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    private static bool HasNativeModal()
    {
        bool found = false;
        uint process = (uint)Environment.ProcessId;
        if (!EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint id);
            if (id != process || !IsWindowVisible(hwnd)) return true;
            var name = new StringBuilder(128);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() == "#32770") found = true;
            return true;
        }, IntPtr.Zero)) throw new InvalidOperationException("could not inspect native dialogs");
        return found;
    }
}
