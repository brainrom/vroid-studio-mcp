using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

/// <summary>Reads custom-item palettes without installing or wearing their hair geometry.</summary>
public static class HairMaterialTransfer
{
    private const string Specific = "VRoidCore.Common.SpecificTypes.";
    private const string DuplicateCommand = "VRoidCore.Editing.History.Command.DuplicateMaterialCommand";
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Dictionary<IntPtr, Transfer> Transfers = new();
    private static MethodInfo _modify;

    private sealed class Transfer
    {
        // Native history owns this command across undo/redo. Keep its source and managed wrapper
        // rooted until the command leaves both history stacks, preventing native pointer reuse.
        public object Command;
        public object Context;
        public object Source;
        public string SourceId;
        public string ImportedId;
        public string Error;
        public bool Observed;
    }

    public static bool TryBeginLoad(string path, out object task, out string error)
    {
        task = null;
        error = null;
        if (!CustomItems.LooksLikeCustomItem(path))
        { error = "The source must be a .vroidcustomitem file."; return false; }
        if (!FileOps.IsRegularFile(path) && !FileOps.IsRegularFile(WinePaths.ToDos(path)))
        { error = $"Source file not found or not a regular file: {path}"; return false; }
        var selector = Categories.FindSelectorByShort("HairEditor.PresetsSelectorViewModel");
        foreach (var category in Enumerate(Get(Get(selector, "Categories"), "Children")))
        {
            var node = Get(category, "PresetCategoryViewModel") ?? category;
            var registry = Get(node, "_customItemRegistry")
                ?? Get(Presets.CustomItemOfKind(node, "import"), "_customItemRegistry");
            if (registry != null) return CustomItems.TryBeginLoad(registry, path, out task, out error);
        }
        error = "No active native custom-item registry; enter Hairstyle first.";
        return false;
    }

    public static bool TryRead(object loadedContents, out object sourceCollection, out object result, out string error)
    {
        sourceCollection = null;
        result = null;
        error = null;
        try
        {
            if (!CustomItems.TryReadContents(loadedContents, out var type, out var name, out error)) return false;
            // The payload carries native binary encoding/schema metadata; its base bytes are
            // not a bare SpecificCollection protobuf. Use the same reader as the stock Custom tab.
            var utility = RequiredType("VRoid.Studio.ModelPresetsSelector.CustomItemSerializationUtil");
            var args = new[] { loadedContents };
            var decode = utility.GetMethods(BindingFlags.Public | BindingFlags.Static).SingleOrDefault(m =>
                m.Name == "DeserializeCustomItem" && Matches(m.GetParameters(), args))
                ?? throw new NotSupportedException("The native custom-item deserializer is unavailable in this app build.");
            var item = decode.Invoke(null, Coerce(decode.GetParameters(), args));
            var source = Get(item, "baseCollection")
                ?? throw new InvalidOperationException("The native custom-item reader returned no base collection.");
            var catalog = MaterialEntries(source).Select(e => new Dictionary<string, object>
            {
                ["material_id"] = e.Id,
                ["name"] = Get(e.Material, "Name"),
                ["template_id"] = Get(Get(e.Material, "Template"), "TemplateId"),
                ["main_color"] = Color(source, Get(e.Material, "OverlayColorId") as string),
                ["highlight_color"] = Color(source, Get(e.Material, "EmissionColorId") as string),
            }).ToList();
            if (catalog.Count == 0) throw new InvalidOperationException("The custom item contains no reusable shared hair materials.");
            sourceCollection = source;
            result = new Dictionary<string, object>
            {
                ["name"] = name,
                ["type"] = type,
                ["materials"] = catalog,
                ["count"] = catalog.Count,
                ["geometry_imported"] = false,
                ["library_modified"] = false,
            };
            return true;
        }
        catch (Exception ex) { error = Error(ex); return false; }
    }

    public static bool TryImport(object sourceCollection, string sourceMaterialId, string name,
                                 out object result, out string error)
    {
        result = null;
        error = null;
        try
        {
            if (!HairMaterials.TryContext(out var context, out var model, out error)) return false;
            if (Get(context, "IsCommandExecutable") is bool ready && !ready)
                throw new InvalidOperationException("The native model is busy; wait for its update-complete signal.");
            if (string.IsNullOrEmpty(sourceMaterialId)) throw new ArgumentException("A source material_id from hair_material_source list is required.");
            if (name != null && (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)))
                throw new ArgumentException("name must contain 1..128 characters without control characters.");
            var sourceMaterial = MaterialEntries(sourceCollection).SingleOrDefault(e => e.Id == sourceMaterialId).Material;
            if (sourceMaterial == null) throw new ArgumentException($"Source material_id '{sourceMaterialId}' does not exist; refresh the source list.");

            // Work only on a detached native collection. Source transferable/group data is
            // never installed or passed to a hairstyle replacement API.
            var source = Call(sourceCollection, "CloneSpecificDeep");
            var restoration = Get(context, "RestorationInfo") ?? Get(Get(context, "historyManagerContext"), "RestorationInfo");
            if (restoration == null) throw new NotSupportedException("The native restoration context is unavailable.");
            var updateInfo = Get(context, "UpdateInfo") ?? Get(Get(context, "historyManagerContext"), "UpdateInfo");
            if (updateInfo == null) throw new NotSupportedException("The native asset-update context is unavailable.");
            if (name != null)
                Call(MaterialEntries(source).Single(e => e.Id == sourceMaterialId).Material, "set_Name", name.Trim());
            var current = Get(model, "BaseCollection") ?? throw new InvalidOperationException("The active model has no base collection.");
            var existingId = MaterialEntries(current).FirstOrDefault().Id
                ?? throw new InvalidOperationException("The active model has no shared hair material for a native history command.");
            var path = New(Specific + "SharedHairMaterialPath", existingId);
            var command = New(DuplicateCommand, path);
            EnsureHook(command.GetType());
            Prune(context);
            if (Transfers.Count >= 256) throw new InvalidOperationException("The material-transfer history retention limit was reached; no import was performed.");
            var record = new Transfer { Command = command, Context = context, Source = source, SourceId = sourceMaterialId };
            Transfers.Add(Pointer(command), record);

            // Verify native prefix dispatch against a detached clone before handing a command to
            // active history. A failed hook can only duplicate a material on this disposable clone.
            try
            {
                var shadow = Call(current, "CloneSpecificDeep");
                Call(command, "ModifyBaseCollection", restoration, updateInfo, model, shadow);
                if (!record.Observed || record.Error != null || string.IsNullOrEmpty(record.ImportedId))
                    throw new InvalidOperationException(record.Error ?? "The native material-transfer hook did not run; active model unchanged.");
                // Restore only the copied dependency graph in the detached destination.
                Call(shadow, "RestoreAssets", restoration);
            }
            catch
            {
                Transfers.Remove(Pointer(command));
                throw;
            }
            record.Observed = false;
            record.ImportedId = null;
            record.Error = null;
            // Keep the source rooted if an exception occurs after native history accepted the
            // command. A later prune removes only entries proven absent from both stacks.
            Call(context, "ExecuteSyncCommand", command);
            if (record.Error != null) throw new InvalidOperationException(record.Error);
            if (!record.Observed) throw new InvalidOperationException("The native material-transfer callback did not execute; import is unverified.");
            var committed = Get(Get(context, "ActiveModel"), "BaseCollection");
            var imported = MaterialEntries(committed).FirstOrDefault(e => e.Id == record.ImportedId).Material;
            if (string.IsNullOrEmpty(record.ImportedId) || imported == null)
                throw new InvalidOperationException("The native history command did not expose the copied material; import is unverified.");
            result = new Dictionary<string, object>
            {
                ["imported"] = true,
                ["material_id"] = record.ImportedId,
                ["source_material_id"] = sourceMaterialId,
                ["name"] = Get(imported, "Name"),
                ["geometry_imported"] = false,
                ["assigned"] = false,
                ["saved"] = false,
            };
            return true;
        }
        catch (Exception ex) { error = Error(ex); return false; }
    }

    private static void EnsureHook(Type commandType)
    {
        if (_modify != null) return;
        var target = commandType.GetMethods(Instance).Single(m => m.Name == "ModifyBaseCollection" && m.DeclaringType == commandType);
        var prefix = typeof(HairMaterialTransfer).GetMethod(nameof(ModifyPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (Plugin.Harmony == null) throw new NotSupportedException("The native Harmony bridge is not initialized.");
        Plugin.Harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        _modify = target;
    }

    private static bool ModifyPrefix(object __instance, object __1, object __3, ref bool __result)
    {
        if (!Transfers.TryGetValue(Pointer(__instance), out var transfer)) return true;
        transfer.Observed = true;
        __result = false;
        try
        {
            var utility = RequiredType(Specific + "SharedMaterial.SharedMaterialDuplicateUtil");
            var duplicate = utility.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == "DuplicateSharedHairMaterial" && m.GetParameters().Length == 3);
            var args = new[] { transfer.Source, __3, transfer.SourceId };
            transfer.ImportedId = duplicate.Invoke(null, Coerce(duplicate.GetParameters(), args)) as string;
            if (string.IsNullOrEmpty(transfer.ImportedId)
                || !MaterialEntries(__3).Any(e => e.Id == transfer.ImportedId))
                throw new InvalidOperationException("Native material duplication returned no copied material.");
            // Native asset updates resolve each stored stable resource name to the current
            // catalog ID, including custom items authored against an older app asset catalog.
            if (__1 == null) throw new NotSupportedException("The native history command has no asset-update context.");
            Call(__3, "UpdateAssets", __1);
            __result = true;
        }
        catch (Exception ex) { transfer.Error = Error(ex); }
        // A targeted failure must never fall through to normal duplicate behavior.
        return false;
    }

    private static void Prune(object context)
    {
        var manager = Get(context, "historyManager");
        var undo = Get(manager, "undoStack");
        var redo = Get(manager, "redoStack");
        if (manager == null || undo == null || redo == null) return;
        // Enumeration failures must retain source records: losing one while a native redo command
        // is alive would turn that command back into an ordinary current-model duplication.
        var undoEntries = Enumerate(undo).ToList();
        var redoEntries = Enumerate(redo).ToList();
        if (Get(undo, "Count") is not int undoCount || undoEntries.Count != undoCount
            || Get(redo, "Count") is not int redoCount || redoEntries.Count != redoCount) return;
        var live = new HashSet<IntPtr>();
        foreach (var wrapper in undoEntries.Concat(redoEntries).Concat(new[] { Get(manager, "pendingCommand") }))
        {
            if (wrapper == null) continue;
            object command;
            try { command = Call(wrapper, "get_SyncCommand"); }
            catch { return; }
            if (command != null) live.Add(Pointer(command));
        }
        foreach (var key in Transfers.Where(e => Pointer(e.Value.Context) == Pointer(context) && !live.Contains(e.Key)).Select(e => e.Key).ToArray())
            Transfers.Remove(key);
    }

    private static List<(string Id, object Material)> MaterialEntries(object collection)
    {
        var dictionary = Get(Get(collection, "Global"), "HairMaterials")
            ?? throw new InvalidOperationException("The native collection has no shared hair-material dictionary.");
        if (Call(dictionary, "get_Count") is not int count || count < 0)
            throw new NotSupportedException("The native hair-material dictionary returned no valid Count.");
        var keys = Call(dictionary, "get_Keys")
            ?? throw new NotSupportedException("The native hair-material dictionary exposes no key collection.");
        if (Call(keys, "get_Count") is not int keyCount || keyCount != count)
            throw new InvalidOperationException("The native hair-material dictionary and key counts do not match.");
        var ids = new Il2CppStringArray(count);
        Call(keys, "CopyTo", ids, 0);
        if (Call(dictionary, "get_Count") is not int afterCount || afterCount != count)
            throw new InvalidOperationException("The native hair-material dictionary changed while reading its keys.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string Id, object Material)>(count);
        for (var i = 0; i < count; i++)
        {
            var id = ids[i];
            if (string.IsNullOrEmpty(id) || !seen.Add(id))
                throw new InvalidOperationException("The native hair-material key copy returned missing or duplicate IDs.");
            var material = Call(dictionary, "get_Item", id)
                ?? throw new InvalidOperationException($"The native hair-material dictionary returned no material for '{id}'.");
            result.Add((id, material));
        }
        return result.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    private static object Color(object source, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var dictionary = Get(Get(source, "Common"), "Colors")
            ?? throw new InvalidOperationException("The native collection has no color dictionary.");
        if (Call(dictionary, "ContainsKey", id) is not bool present)
            throw new NotSupportedException("The native color dictionary returned no ContainsKey result.");
        if (!present) return null;
        var value = Call(dictionary, "get_Item", id)
            ?? throw new InvalidOperationException($"The native color dictionary returned no value for '{id}'.");
        return ParamCodec.Hex(Get(value, "Value"));
    }

    private static IntPtr Pointer(object value) => (value as Il2CppObjectBase)?.Pointer ?? IntPtr.Zero;
    private static Type RequiredType(string name) => FindType(name) ?? throw new NotSupportedException($"Native type {name} is unavailable in this app build.");
    private static object New(string name, params object[] args)
    {
        var ctor = RequiredType(name).GetConstructors().SingleOrDefault(c => Matches(c.GetParameters(), args))
            ?? throw new NotSupportedException($"Native constructor {name} is unavailable for these arguments.");
        return ctor.Invoke(Coerce(ctor.GetParameters(), args));
    }
    private static object Call(object target, string method, params object[] args)
    {
        target = RealObj(target) ?? throw new InvalidOperationException($"Native target for {method} is absent.");
        var member = target.GetType().GetMethods(Instance).SingleOrDefault(m => m.Name == method && !m.IsGenericMethodDefinition && Matches(m.GetParameters(), args))
            ?? throw new NotSupportedException($"Native method {target.GetType().FullName}.{method} is unavailable for these arguments.");
        return member.Invoke(target, Coerce(member.GetParameters(), args));
    }
    private static bool Matches(ParameterInfo[] parameters, object[] args)
        => parameters.Length == args.Length && parameters.Select((p, i) => args[i] == null
            ? !p.ParameterType.IsValueType : p.ParameterType.IsInstanceOfType(args[i])
                || args[i] is Il2CppObjectBase && typeof(Il2CppObjectBase).IsAssignableFrom(p.ParameterType)
                    && RewrapTo(args[i], p.ParameterType) != null).All(v => v);
    private static object[] Coerce(ParameterInfo[] parameters, object[] args)
        => parameters.Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])
            ? args[i] : RewrapTo(args[i], p.ParameterType)
                ?? throw new InvalidOperationException($"Native argument {i} cannot be converted to {p.ParameterType.Name}.")).ToArray();
    private static string Error(Exception ex)
    {
        while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
        return ex.Message;
    }
}
