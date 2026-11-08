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

/// <summary>Native face/body skin image/history operations; no presets or geometry changes.</summary>
public static class SkinTextures
{
    private const string Common = "VRoidCore.Editing.Query.AdvancedEdit.GetCommonQuery";
    private const string Begin = "VRoidCore.Editing.History.Command.AdvancedEdit.BeginAdvancedEditCommand";
    private const string TextureVm = "VRoid.Studio.TextureEditor.ViewModel";

    private sealed class Target
    {
        public object Info, Image;
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
        if (copy.Count > 512) throw new InvalidOperationException("Native skin inventory exceeds its bounded limit.");
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

    private static Target Resolve(object model, string target)
    {
        var groupType = target switch {
            "face" => "TransferableGroupType.N00.Level1.FaceSkin",
            "body" => "TransferableGroupType.N00.Level1.BodySkin",
            _ => throw new ArgumentException("target must be face or body.")
        };
        var vm = FindType(TextureVm) ?? throw new NotSupportedException("Native texture-editor metadata is unavailable.");
        RuntimeHelpers.RunClassConstructor(vm.TypeHandle);
        var map = vm.GetProperty("s_category2TextureInfo", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)
            ?? throw new NotSupportedException("Native texture category metadata is unavailable.");
        if (!NativeStrings(Get(map, "Keys")).Contains(groupType))
            throw new NotSupportedException("The native " + target + " skin texture category is unavailable.");
        var groups = NativeStrings(Call(model, "GetTransferableGroupIdsOfType", groupType));
        if (groups.Count != 1)
            throw new InvalidOperationException($"Expected one native {target} skin group, found {groups.Count}; no changes were made.");
        var category = NativeAs(Call(map, "get_Item", groupType), TextureVm + "+CategoryInfo")
            ?? throw new NotSupportedException("Native skin category info could not be resolved.");
        // Preserve the declared array proxy; RealObj can erase its element type.
        var infoArray = category.GetType().GetProperty("textureInfos")?.GetValue(category)
            ?? throw new NotSupportedException("Native skin texture-info array is unavailable.");
        var infos = IndexedRead(infoArray, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new NotSupportedException("Native skin texture-info array cannot be indexed.");
        if (infos.Count != 1)
            throw new InvalidOperationException($"Expected one native {target} skin texture descriptor, found {infos.Count}; no changes were made.");
        var info = NativeAs(infos[0], TextureVm + "+SkinTextureInfo")
            ?? throw new NotSupportedException("Native category did not contain a skin-specific texture descriptor.");
        var textureKey = Get(info, "TextureParameterKey")
            ?? throw new NotSupportedException("Native skin texture key is unavailable.");
        var input = New(Common + "+Input", groupType, Get(category, "translationKey"),
            NativeList(Common + "+Input+IParameterKey"), NativeList(Common + "+Input+IParameterKey"),
            NativeList(Common + "+Input+TextureKey", textureKey));
        var query = Query("AdvancedEdit.GetCommonQuery", model, NativeList(Common + "+Input", input));
        var images = Sections(query).Select(s => Get(s, "editableImagePath")).Where(p => p != null).ToList();
        if (images.Count != 1)
            throw new InvalidOperationException($"Expected one native {target} skin image, found {images.Count}; no changes were made.");
        var transferableId = Get(images[0], "TransferableId") as string;
        var groupId = groups[0];
        if (string.IsNullOrEmpty(transferableId) || !NativeStrings(Call(model, "GetAllTransferableIdsInTransferableGroup", groupId)).Contains(transferableId))
            throw new InvalidOperationException("The native skin image does not belong to its resolved transferable group.");
        return new Target { Info = info, Image = images[0], GroupType = groupType, GroupId = groupId, TransferableId = transferableId };
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
        if (!Prepared(model, target.Image)) throw new InvalidOperationException("Skin texture is asset-backed; run skin_texture prepare first.");
        var layers = Layers(model, target);
        if (index < 0 || index >= layers.Count) throw new ArgumentException($"layer index {index} is out of range (count {layers.Count}); refresh skin_texture inspect.");
        return RealObj(layers[index]);
    }

    private static object LayerInfo(object model, object layer, int index)
    {
        var p = Query("GetEditableImageLayerTreeParameterSetQuery", model, layer);
        return new Dictionary<string, object> { ["index"] = index, ["path"] = SafeStr(layer), ["name"] = Get(p, "Name"),
            ["visibility"] = Get(p, "Visibility"), ["opacity"] = Get(p, "Opacity"), ["blend"] = SafeStr(Get(p, "CompositionMode")) };
    }

    private static object Inspect(object model, Target target)
    {
        var disabled = Call(model, "GetBoolParameter", target.GroupId, Get(Get(target.Info, "SkinColorDisableKey"), "bodyKey"));
        object ColorInfo(string key) {
            var value = Call(model, "GetColorParameter", target.GroupId, Get(Get(target.Info, key), "bodyKey"));
            return value is Color c ? new[] { c.r, c.g, c.b, c.a } : null;
        }
        return new Dictionary<string, object> {
            ["transferable_group_type"] = target.GroupType, ["transferable_group_id"] = target.GroupId,
            ["transferable_id"] = target.TransferableId, ["image_id"] = Get(target.Image, "ImageId"),
            ["prepared"] = Prepared(model, target.Image),
            ["skin_color_enabled"] = disabled is bool b ? !b : null,
            ["main_color_rgba"] = ColorInfo("MainColorParameterKey"),
            ["shadow_color_rgba"] = ColorInfo("ShadowColorParameterKey"),
            ["highlight_color_rgba"] = ColorInfo("HighlightColorParameterKey"),
            ["layers"] = Layers(model, target).Select((p, i) => LayerInfo(model, p, i)).ToArray(),
            ["changed"] = false, ["geometry_changed"] = false
        };
    }

    public static bool TryExecute(string targetName, string action, int layer, string path, string key, string value, out object result, out string error)
    {
        result = null; error = null;
        try
        {
            var context = Get(VroidState._engineModel, "Context");
            var model = Get(context, "ActiveModel");
            if (!VroidState._inEdit || context == null || model == null) throw new InvalidOperationException("No active native model editing context.");
            var target = Resolve(model, targetName);
            bool changed;
            switch (action)
            {
                case "inspect": result = Inspect(model, target); return true;
                case "prepare":
                    changed = !Prepared(model, target.Image);
                    if (changed)
                    {
                        var editable = Call(target.Info, "CreateMakeImageEditable", false);
                        editable = NativeAs(editable, Begin + "+SkinTextureKey") ?? throw new NotSupportedException("Native skin conversion did not return the skin-specific key.");
                        var input = New(Begin + "+OverlayInput", target.TransferableId, target.GroupId, NativeList(Begin + "+IMakeImageEditable", editable));
                        Execute(context, "AdvancedEdit.BeginAdvancedEditCommand", NativeList(Begin + "+Input", input), "Original skin");
                    }
                    break;
                case "import_texture": Import(context, model, target, layer, path); changed = true; break;
                case "export_texture": result = Export(model, target, layer, path); return true;
                case "set_layer": changed = SetLayer(context, model, target, layer, key, value); break;
                default: throw new ArgumentException("action must be inspect, prepare, import_texture, export_texture or set_layer.");
            }
            result = new Dictionary<string, object> { ["target"] = targetName, ["action"] = action, ["layer"] = layer, ["changed"] = changed,
                ["native_history"] = true, ["geometry_changed"] = false, ["apply_colors"] = false };
            return true;
        }
        catch (Exception e) { while (e is TargetInvocationException t && t.InnerException != null) e = t.InnerException; error = e.Message; return false; }
    }

    private static void Import(object context, object model, Target target, int layer, string path)
    {
        if (!Prepared(model, target.Image)) throw new InvalidOperationException("Skin texture is asset-backed; run skin_texture prepare first.");
        if (layer < -1) throw new ArgumentException("layer must be -1 to add a layer, or an existing non-negative index.");
        var existing = layer >= 0 ? Layer(model, target, layer) : null;
        if (string.IsNullOrEmpty(path) || !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("path must identify an existing PNG file.");
        path = WinePaths.ToHost(path);
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Skin PNG does not exist.", path);
        if (file.Length > 64 * 1024 * 1024) throw new ArgumentException("Skin PNG exceeds the 64 MiB input limit.");
        var bytes = File.ReadAllBytes(path);
        byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (bytes.Length < 33 || !bytes.Take(8).SequenceEqual(signature) || bytes[8] != 0 || bytes[9] != 0 || bytes[10] != 0 || bytes[11] != 13 || bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82)
            throw new ArgumentException("Input must contain a PNG signature and IHDR header.");
        int Big(int i) => (bytes[i] << 24) | (bytes[i + 1] << 16) | (bytes[i + 2] << 8) | bytes[i + 3];
        int w = Big(16), h = Big(20);
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096 || (long)w * h > 16777216) throw new ArgumentException("PNG dimensions must be 1..4096 pixels per side (at most 16 megapixels).");
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!ImageConversion.LoadImage(tex, new Il2CppStructArray<byte>(bytes), false)) throw new ArgumentException("Native PNG decoder rejected this file.");
            if (tex.width != w || tex.height != h) throw new ArgumentException("Decoded PNG dimensions do not match its header.");
            var size = New("VRoidCore.Draw.TiledBitmap.BitmapSize", w, h);
            if (existing == null) Execute(context, "LoadImageAsNewRasterLayerCommand", target.Image, size, tex.GetPixels());
            else Execute(context, "LoadImageToEditableImageRasterLayerCommand", existing, size, tex.GetPixels());
        }
        finally { UnityEngine.Object.Destroy(tex); }
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
            return new Dictionary<string, object> { ["path"] = path, ["width"] = w, ["height"] = h, ["bytes"] = data.Length, ["changed"] = false, ["source"] = "native skin raster layer" };
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
            case "name":
                property = "Name";
                if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
                    throw new ArgumentException("name must contain 1..128 characters without control characters.");
                parsed = value; break;
            case "visibility":
                property = "Visibility";
                if (!ArgParse.Flag(value, "visibility", out var visible, out var boolError, ("true", true), ("false", false))) throw new ArgumentException(boolError);
                parsed = visible; break;
            case "opacity":
                property = "Opacity";
                if (!ArgParse.Float(value, "opacity", out var opacity, out var floatError, 0, 1)) throw new ArgumentException(floatError);
                parsed = opacity; break;
            default: throw new ArgumentException("set_layer key must be name, visibility or opacity.");
        }
        if (Equals(Get(parameters, property), parsed)) return false;
        var type = FindType("VRoidCore.Editing.History.Command.ModifyEditableImageLayerTreeParameterSetCommand");
        var ctor = type?.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 3) ?? throw new NotSupportedException("Native layer command is unavailable.");
        var callback = NativeModifier(ctor.GetParameters()[2].ParameterType, p => {
            if (!Set(p, property, parsed)) throw new InvalidOperationException($"Native layer {property} setter failed.");
        });
        if (Get(context, "IsCommandExecutable") is false) throw new InvalidOperationException("The native model is busy.");
        Call(context, "ExecuteSyncCommand", ctor.Invoke(new[] { "MCP.SkinLayer." + property, selected, callback }));
        return true;
    }
}
