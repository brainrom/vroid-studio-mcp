using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using static VroidStudioMcp.Interop;
using static VroidStudioMcp.HairMaterials;

namespace VroidStudioMcp;

/// <summary>Native eyelid raster inspection, export and layer-history controls only.</summary>
public static class EyelidTextures
{
    private const string Common = "VRoidCore.Editing.Query.AdvancedEdit.GetCommonQuery";
    private const string GroupType = "TransferableGroupType.N00.Level2.Eyelid";
    private const string TextureVm = "VRoid.Studio.TextureEditor.ViewModel";

    private sealed class Target
    {
        public object Image;
        public string GroupType, GroupId, TransferableId;
    }

    private static object NativeList(string elementType, params object[] items)
    {
        var element = FindType(elementType) ?? throw new NotSupportedException($"Native type {elementType} is unavailable.");
        var type = typeof(Il2CppSystem.Collections.Generic.List<>).MakeGenericType(element);
        RuntimeHelpers.RunClassConstructor(type.TypeHandle);
        var list = Activator.CreateInstance(type);
        foreach (var item in items) Call(list, "Add", item);
        return list;
    }

    private static IEnumerable<object> Sections(object parent)
    {
        foreach (var child in EnumerateList(Get(parent, "sections") ?? Get(parent, "childSections")))
        {
            var real = RealObj(child);
            yield return real;
            foreach (var descendant in Sections(real)) yield return descendant;
        }
    }

    private static List<string> NativeStrings(object source)
    {
        var enumerable = RewrapTo(source, typeof(Il2CppSystem.Collections.Generic.IEnumerable<string>))
            as Il2CppSystem.Collections.Generic.IEnumerable<string>
            ?? throw new NotSupportedException("Native string collection is unavailable.");
        var copy = new Il2CppSystem.Collections.Generic.List<string>(enumerable);
        if (copy.Count > 512) throw new InvalidOperationException("Native eyelid inventory exceeds its bounded limit.");
        var result = new List<string>();
        for (int i = 0; i < copy.Count; i++)
        {
            var key = copy[i];
            if (string.IsNullOrEmpty(key) || result.Contains(key)) throw new InvalidOperationException("Native texture category metadata contains missing or duplicate keys.");
            result.Add(key);
        }
        return result;
    }

    private static object NativeAs(object source, string typeName)
    {
        if (source is not Il2CppObjectBase) return null;
        var type = FindType(typeName) ?? throw new NotSupportedException($"Native type {typeName} is unavailable.");
        var cast = typeof(Il2CppObjectBase).GetMethods().Single(m => m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        return cast.MakeGenericMethod(type).Invoke(source, null);
    }

    private static Target Resolve(object model)
    {
        var vm = FindType(TextureVm) ?? throw new NotSupportedException("Native texture-editor metadata is unavailable.");
        RuntimeHelpers.RunClassConstructor(vm.TypeHandle);
        var map = vm.GetProperty("s_category2TextureInfo", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)
            ?? throw new NotSupportedException("Native texture category metadata is unavailable.");
        if (!NativeStrings(Get(map, "Keys")).Contains(GroupType))
            throw new NotSupportedException("The native eyelid texture category is unavailable.");
        var groups = NativeStrings(Call(model, "GetTransferableGroupIdsOfType", GroupType));
        if (groups.Count == 0)
            throw new InvalidOperationException("No eyelid item is selected. Select or wear an eyelid item before inspecting its texture.");
        if (groups.Count != 1)
            throw new InvalidOperationException($"Expected one native eyelid group, found {groups.Count}; no changes were made.");
        var category = NativeAs(Call(map, "get_Item", GroupType), TextureVm + "+CategoryInfo")
            ?? throw new NotSupportedException("Native eyelid texture category info could not be resolved.");
        // Preserve the declared nested-element array proxy; RealObj can erase its element type.
        var infoArray = category.GetType().GetProperty("textureInfos")?.GetValue(category)
            ?? throw new NotSupportedException("Native eyelid texture-info array is unavailable.");
        var infos = IndexedRead(infoArray, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new NotSupportedException("Native eyelid texture-info array cannot be indexed.");
        if (infos.Count != 1)
            throw new InvalidOperationException($"Expected one native eyelid texture descriptor, found {infos.Count}; no changes were made.");
        var info = NativeAs(infos[0], TextureVm + "+TextureInfoBase")
            ?? throw new NotSupportedException("Native eyelid texture descriptor could not be resolved.");
        var textureKey = Get(info, "TextureParameterKey")
            ?? throw new NotSupportedException("Native eyelid texture key is unavailable.");
        var input = New(Common + "+Input", GroupType, Get(category, "translationKey"),
            NativeList(Common + "+Input+IParameterKey"), NativeList(Common + "+Input+IParameterKey"),
            NativeList(Common + "+Input+TextureKey", textureKey));
        var query = Query("AdvancedEdit.GetCommonQuery", model, NativeList(Common + "+Input", input));
        var images = Sections(query).Select(s => Get(s, "editableImagePath")).Where(p => p != null).ToList();
        if (images.Count != 1)
            throw new InvalidOperationException($"Expected one native eyelid image, found {images.Count}; no changes were made.");
        var transferableId = Get(images[0], "TransferableId") as string;
        var groupId = groups[0];
        if (string.IsNullOrEmpty(transferableId) || !NativeStrings(Call(model, "GetAllTransferableIdsInTransferableGroup", groupId)).Contains(transferableId))
            throw new InvalidOperationException("The native eyelid image does not belong to its resolved transferable group.");
        return new Target { Image = images[0], GroupType = GroupType, GroupId = groupId, TransferableId = transferableId };
    }

    private static bool Prepared(object model, object image)
    {
        model = RealObj(model);
        var method = model.GetType().GetMethods().FirstOrDefault(m => m.Name == "TryGetEditableImage" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType.IsByRef)
            ?? throw new NotSupportedException("Native editable-image readiness query is unavailable.");
        var args = new object[] { image, null };
        return method.Invoke(model, args) is true && args[1] != null;
    }

    private static List<object> Layers(object model, Target target)
        => Prepared(model, target.Image) ? EnumerateList(Query("GetEditableImageRasterLayersQuery", model, target.Image)) : new List<object>();

    private static object Layer(object model, Target target, int index)
    {
        if (!Prepared(model, target.Image)) throw new InvalidOperationException("Eyelid texture is not prepared for layer operations; wear an editable native eyelid custom item first.");
        var layers = Layers(model, target);
        if (index < 0 || index >= layers.Count) throw new ArgumentException($"layer index {index} is out of range (count {layers.Count}); refresh eyelid_texture inspect.");
        return RealObj(layers[index]);
    }

    private static object LayerInfo(object model, object layer, int index)
    {
        var p = Query("GetEditableImageLayerTreeParameterSetQuery", model, layer);
        return new Dictionary<string, object> { ["index"] = index, ["path"] = SafeStr(layer), ["name"] = Get(p, "Name"),
            ["visibility"] = Get(p, "Visibility"), ["opacity"] = Get(p, "Opacity"), ["blend"] = SafeStr(Get(p, "CompositionMode")) };
    }

    private static object Inspect(object model, Target target)
        => new Dictionary<string, object> {
            ["transferable_group_type"] = target.GroupType, ["transferable_group_id"] = target.GroupId,
            ["transferable_id"] = target.TransferableId, ["image_id"] = Get(target.Image, "ImageId"),
            ["prepared"] = Prepared(model, target.Image),
            ["layers"] = Layers(model, target).Select((p, i) => LayerInfo(model, p, i)).ToArray(),
            ["changed"] = false, ["geometry_changed"] = false
        };

    public static bool TryExecute(string action, int layer, string path, string key, string value, out object result, out string error)
    {
        result = null; error = null;
        try
        {
            if (action != "inspect" && action != "export_texture" && action != "set_layer")
                throw new ArgumentException("action must be inspect, export_texture or set_layer.");
            var context = Get(VroidState._engineModel, "Context");
            var model = Get(context, "ActiveModel");
            if (!VroidState._inEdit || context == null || model == null) throw new InvalidOperationException("No active native model editing context.");
            var target = Resolve(model);
            switch (action)
            {
                case "inspect": result = Inspect(model, target); return true;
                case "export_texture": result = Export(model, target, layer, path); return true;
                case "set_layer":
                    var changed = SetLayer(context, model, target, layer, key, value);
                    result = new Dictionary<string, object> { ["action"] = action, ["layer"] = layer, ["changed"] = changed,
                        ["native_history"] = true, ["geometry_changed"] = false };
                    return true;
            }
            return false;
        }
        catch (Exception e) { while (e is TargetInvocationException t && t.InnerException != null) e = t.InnerException; error = e.Message; return false; }
    }

    private static object Export(object model, Target target, int layer, string path)
    {
        var selected = Layer(model, target, layer);
        if (string.IsNullOrEmpty(path) || !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("path must end in .png.");
        path = WinePaths.ToHost(path);
        if (File.Exists(path)) throw new ArgumentException("Export destination already exists; choose a new path.");
        var content = Query("GetRasterLayerContentQuery", model, selected);
        var size = Get(content, "size");
        int w = Convert.ToInt32(Get(size, "x")), h = Convert.ToInt32(Get(size, "y"));
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) throw new InvalidOperationException("Native layer exceeds the 8192-pixel export limit.");
        var pixels = Get(content, "rgbaBytes") as Il2CppStructArray<byte> ?? throw new InvalidOperationException("Native layer returned no RGBA pixels.");
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        try
        {
            tex.LoadRawTextureData(pixels); tex.Apply();
            var png = ImageConversion.EncodeToPNG(tex);
            var data = new byte[png.Length]; for (int i = 0; i < data.Length; i++) data[i] = png[i];
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(data);
            return new Dictionary<string, object> { ["path"] = path, ["width"] = w, ["height"] = h, ["bytes"] = data.Length, ["changed"] = false, ["source"] = "native eyelid raster layer" };
        }
        finally { UnityEngine.Object.Destroy(tex); }
    }

    private static bool SetLayer(object context, object model, Target target, int layer, string key, string value)
    {
        var selected = Layer(model, target, layer);
        var parameters = Query("GetEditableImageLayerTreeParameterSetQuery", model, selected);
        string property; object parsed;
        switch (key)
        {
            case "visibility":
                property = "Visibility";
                if (!ArgParse.Flag(value, "visibility", out var visible, out var boolError, ("true", true), ("false", false))) throw new ArgumentException(boolError);
                parsed = visible; break;
            case "opacity":
                property = "Opacity";
                if (!ArgParse.Float(value, "opacity", out var opacity, out var floatError, 0, 1)) throw new ArgumentException(floatError);
                parsed = opacity; break;
            default: throw new ArgumentException("set_layer key must be visibility or opacity.");
        }
        if (Equals(Get(parameters, property), parsed)) return false;
        var type = FindType("VRoidCore.Editing.History.Command.ModifyEditableImageLayerTreeParameterSetCommand");
        var ctor = type?.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 3) ?? throw new NotSupportedException("Native layer command is unavailable.");
        var callback = NativeModifier(ctor.GetParameters()[2].ParameterType, p => {
            if (!Set(p, property, parsed)) throw new InvalidOperationException($"Native layer {property} setter failed.");
        });
        if (Get(context, "IsCommandExecutable") is false) throw new InvalidOperationException("The native model is busy.");
        Call(context, "ExecuteSyncCommand", ctor.Invoke(new[] { "MCP.EyelidLayer." + property, selected, callback }));
        return true;
    }
}
