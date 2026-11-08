using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

/// <summary>Allowlisted native hair material/history operations. Call only on the app thread;
/// callers must arm and await the normal model-update signal for mutations.</summary>
public static class HairMaterials
{
    private const string QueryPrefix = "VRoidCore.Editing.Query.";
    private const string CommandPrefix = "VRoidCore.Editing.History.Command.";
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static bool TryContext(out object context, out object model, out string error)
    {
        context = Get(Get(Colors._colorHairVm, "_engine"), "Context");
        model = Get(context, "ActiveModel");
        error = context == null || model == null ? "No active native hair editor/model; enter Hairstyle first." : null;
        return error == null;
    }

    internal static object New(string type, params object[] args)
    {
        var t = FindType(type) ?? throw new NotSupportedException($"Native type {type} is unavailable in this app build.");
        var ctor = t.GetConstructors().FirstOrDefault(c => Matches(c.GetParameters(), args));
        if (ctor == null) throw new NotSupportedException($"Native constructor {type} with {args.Length} arguments is unavailable.");
        return ctor.Invoke(Coerce(ctor.GetParameters(), args));
    }

    private static bool Matches(ParameterInfo[] ps, object[] args)
        => ps.Length == args.Length && ps.Select((p, i) => args[i] == null
            ? !p.ParameterType.IsValueType : p.ParameterType.IsInstanceOfType(args[i])
              || args[i] is Il2CppObjectBase && typeof(Il2CppObjectBase).IsAssignableFrom(p.ParameterType) && RewrapTo(args[i], p.ParameterType) != null).All(v => v);

    private static object[] Coerce(ParameterInfo[] parameters, object[] args)
        => parameters.Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])
            ? args[i] : RewrapTo(args[i], p.ParameterType)).ToArray();

    internal static object Call(object target, string method, params object[] args)
    {
        target = RealObj(target) ?? throw new InvalidOperationException($"Native target for {method} is absent.");
        var m = target.GetType().GetMethods(Instance).FirstOrDefault(m => m.Name == method && !m.IsGenericMethodDefinition && Matches(m.GetParameters(), args));
        if (m == null) throw new NotSupportedException($"Native method {target.GetType().FullName}.{method} is unavailable for these arguments.");
        return m.Invoke(target, Coerce(m.GetParameters(), args));
    }

    internal static object Query(string name, object model, params object[] args)
        => Call(New(QueryPrefix + name, args), "Execute", model);

    internal static void Execute(object context, string name, params object[] args)
    {
        if (Get(context, "IsCommandExecutable") is bool ready && !ready)
            throw new InvalidOperationException("The native model is busy; wait for its update-complete signal.");
        Call(context, "ExecuteSyncCommand", New(CommandPrefix + name, args));
    }

    internal static List<object> Materials(object model, string transferableId)
        => EnumerateList(Query("GetAllMaterialsQuery", model, transferableId));

    private static List<string> TransferableIds()
    {
        var vm = Colors._colorHairVm;
        var ids = EnumerateList(Call(vm, "GetTransferableIds")).Select(SafeStr).Where(s => !string.IsNullOrEmpty(s)).ToList();
        var selected = Get(Get(vm, "Model"), "TransferableId") as string;
        if (!string.IsNullOrEmpty(selected) && !ids.Contains(selected)) ids.Add(selected);
        if (ids.Count == 0) throw new InvalidOperationException("The current Hairstyle category contains no editable hair transferable.");
        return ids;
    }

    private static List<object> CurrentMaterials(object model)
        => TransferableIds().SelectMany(id => Materials(model, id)).GroupBy(MaterialId).Select(g => g.First()).ToList();

    private static string MaterialId(object path) => Get(path, "SharedHairMaterialId") as string;
    private static object At(List<object> list, int id, string kind)
    {
        if (id < 0 || id >= list.Count) throw new ArgumentException($"{kind} index {id} is out of range (count {list.Count}). Refresh hair_material list/inspect.");
        return RealObj(list[id]);
    }

    private static object Design => Get(Colors._colorHairVm, "DesignEditor");
    private static List<object> Groups(object model)
        => TransferableIds().SelectMany(id => EnumerateList(Query("GetHairGroupListQuery", model, id))).Select(RealObj).ToList();

    private static bool IsProcedural(object groupPath) => (TypeName(groupPath) ?? "").EndsWith(".ProceduralHairGroupPath", StringComparison.Ordinal);
    private static object GroupParameters(object model, object groupPath)
        => Query(IsProcedural(groupPath) ? "GetProceduralHairGroupParameterSetQuery" : "GetBrushHairGroupParameterSetQuery", model, groupPath);

    private static bool Assign(object context, object model, object material, int group)
    {
        var groupPath = group >= 0 ? At(Groups(model), group, "group") : RealObj(Get(Get(Design, "SelectedItem"), "HairGroupPath"));
        if (groupPath == null) throw new ArgumentException("Supply the group index returned by list, or select a native hair group.");
        var parameters = GroupParameters(model, groupPath);
        var reference = Get(parameters, "HairMaterialReference");
        var newReference = Call(material, "GetHairMaterialReference");
        if (Get(reference, "SharedHairMaterialId") as string == MaterialId(material)) return false;
        var name = IsProcedural(groupPath) ? "ModifyProceduralHairGroupParameterSetCommand" : "ModifyBrushHairGroupParameterSetCommand";
        var commandType = FindType(CommandPrefix + name) ?? throw new NotSupportedException("Native group material command is unavailable.");
        var ctor = commandType.GetConstructors().First(c => c.GetParameters().Length == (IsProcedural(groupPath) ? 4 : 3));
        var callback = NativeModifier(ctor.GetParameters()[2].ParameterType, target => {
            if (!Set(target, "HairMaterialReference", newReference)) throw new InvalidOperationException("Native group material reference setter failed.");
        });
        object[] args = IsProcedural(groupPath)
            ? new object[] { "MCP.HairMaterial", groupPath, callback, true }
            : new object[] { "MCP.HairMaterial", groupPath, callback };
        Call(context, "ExecuteSyncCommand", ctor.Invoke(Coerce(ctor.GetParameters(), args)));
        return true;
    }

    private static List<object> Sections(object model, object material)
    {
        var result = Query("AdvancedEdit.GetHairMaterialQuery", model, material);
        return Flatten(EnumerateList(Get(result, "sections"))).ToList();
    }

    private static IEnumerable<object> Flatten(IEnumerable<object> sections)
    {
        foreach (var item in sections)
        {
            var real = RealObj(item);
            yield return real;
            foreach (var child in Flatten(EnumerateList(Get(real, "childSections")))) yield return child;
            // ColorGroup holds IColor leaves; each is itself a native supported parameter.
            foreach (var child in Flatten(EnumerateList(Get(real, "colors")))) yield return child;
        }
    }

    private static List<object> Slots(object model, object material)
        => Sections(model, material).Where(s => Get(s, "editableImagePath") != null).ToList();

    private static bool SlotPrepared(object model, object slot)
    {
        var path = Get(slot, "editableImagePath");
        if (path == null) return false;
        model = RealObj(model);
        var method = model.GetType().GetMethods(Instance).FirstOrDefault(m => m.Name == "TryGetEditableImage"
            && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType.IsByRef)
            ?? throw new NotSupportedException("Native editable-image readiness query is unavailable.");
        var args = new object[] { path, null };
        return method.Invoke(model, args) is true && args[1] != null;
    }

    private static List<object> Layers(object model, object slot)
        => SlotPrepared(model, slot)
            ? EnumerateList(Query("GetEditableImageRasterLayersQuery", model, Get(slot, "editableImagePath")))
            : new List<object>();

    private static Dictionary<string, object> LayerInfo(object model, object path, int index)
    {
        var p = Query("GetEditableImageLayerTreeParameterSetQuery", model, path);
        var blend = Get(p, "CompositionMode");
        return new Dictionary<string, object> {
            ["index"] = index, ["path"] = SafeStr(path), ["name"] = Get(p, "Name"),
            ["visibility"] = Get(p, "Visibility"), ["opacity"] = Get(p, "Opacity"),
            ["blend"] = SafeStr(blend), ["supported_blends"] = blend == null ? Array.Empty<string>() : Enum.GetNames(blend.GetType()),
            ["opacity_min"] = 0, ["opacity_max"] = 1
        };
    }

    private sealed class ColorTarget
    {
        public string Key;
        public string Label;
        public string ColorId;
        public string DisableId;
        public bool IsBase;
    }

    private static List<ColorTarget> ColorTargets(object model, object material, List<object> sections)
    {
        var targets = new List<ColorTarget>();
        object hairMaterial = null;
        foreach (var section in sections)
        {
            var type = TypeName(section) ?? "";
            if (type.EndsWith(".MaterialColor", StringComparison.Ordinal))
            {
                var parameter = Get(section, "parameterKey") as string;
                if (!string.IsNullOrEmpty(parameter)) targets.Add(new ColorTarget {
                    Key = parameter, Label = Get(section, "translationKey") as string,
                    DisableId = Get(section, "disableValueId") as string
                });
            }
            else if (type.EndsWith(".BaseColor", StringComparison.Ordinal))
            {
                // A BaseColor is accepted only when its native IDs belong to this shared material.
                // Other global colors must not become writable through a material-scoped command.
                var id = Get(section, "colorId") as string;
                var disable = Get(section, "disableFlagBoolId") as string;
                if (string.IsNullOrEmpty(id)) continue;
                hairMaterial ??= Call(Get(Get(Get(model, "BaseCollection"), "Global"), "HairMaterials"), "get_Item", MaterialId(material));
                var channel = id == Get(hairMaterial, "OverlayColorId") as string ? "overlay"
                    : id == Get(hairMaterial, "EmissionColorId") as string ? "emission" : null;
                if (channel == null) continue;
                var ownDisable = Get(hairMaterial, channel == "overlay" ? "OverlayColorDisableFlagId" : "EmissionColorDisableFlagId") as string;
                if (!string.Equals(disable, ownDisable, StringComparison.Ordinal)) continue;
                targets.Add(new ColorTarget {
                    Key = "base:" + channel, Label = Get(section, "translationKey") as string,
                    ColorId = id, DisableId = disable, IsBase = true
                });
            }
        }
        return targets.GroupBy(t => t.Key, StringComparer.Ordinal).Select(g => g.First()).ToList();
    }

    private static ColorTarget SupportedColor(object model, object material, string key)
        => ColorTargets(model, material, Sections(model, material)).FirstOrDefault(t => t.Key == key)
           ?? throw new ArgumentException($"Unsupported material color '{key}'. Use a key returned by inspect.colors.");

    private static object ColorValue(object model, object material, ColorTarget target)
        => target.IsBase ? Query("GetBaseColorQuery", model, target.ColorId)
            : Query("GetMaterialColorQuery", model, material, target.Key);

    private static bool ColorDisabled(object model, object material, ColorTarget target)
    {
        if (string.IsNullOrEmpty(target.DisableId)) return false;
        return target.IsBase ? (bool)Query("GetBaseBoolQuery", model, target.DisableId)
            : (bool)Query("GetMaterialColorDisabledQuery", model, material, target.Key);
    }

    private static object Inspect(object model, object material, int index)
    {
        var sections = Sections(model, material);
        var slots = sections.Where(s => Get(s, "editableImagePath") != null).ToList();
        var colors = ColorTargets(model, material, sections).Select(t => {
            var disabled = ColorDisabled(model, material, t);
            return (object)new Dictionary<string, object> {
                ["key"] = t.Key, ["label"] = t.Label, ["kind"] = t.IsBase ? "base_color" : "material_color",
                ["value"] = ParamCodec.Hex(ColorValue(model, material, t)), ["disable_flag"] = t.DisableId,
                ["disabled"] = disabled, ["enabled"] = !disabled, ["can_set_enabled"] = !string.IsNullOrEmpty(t.DisableId)
            };
        }).ToList();
        var floats = new List<object>();
        foreach (var s in sections)
        {
            var key = Get(s, "parameterKey") as string;
            if (key == null) continue;
            var type = TypeName(s) ?? "";
            if (type.EndsWith(".MaterialFloat", StringComparison.Ordinal))
                floats.Add(new Dictionary<string, object> { ["key"] = key, ["label"] = Get(s, "translationKey"), ["value"] = Query("GetMaterialFloatQuery", model, material, key), ["min"] = Get(s, "minValue"), ["max"] = Get(s, "maxValue") });
        }
        return new Dictionary<string, object> {
            ["index"] = index, ["id"] = MaterialId(material), ["name"] = Query("GetMaterialNameQuery", model, material),
            ["prepared"] = slots.Count > 0 && slots.All(slot => SlotPrepared(model, slot)), ["colors"] = colors, ["floats"] = floats,
            ["slots"] = slots.Select((slot, i) => new Dictionary<string, object> {
                ["index"] = i, ["label"] = Get(slot, "translationKey"), ["path"] = SafeStr(Get(slot, "editableImagePath")),
                ["prepared"] = SlotPrepared(model, slot),
                ["texture_paths"] = EnumerateList(Get(slot, "texturePaths")).Select(SafeStr).ToArray(),
                ["layers"] = Layers(model, slot).Select((layer, n) => LayerInfo(model, layer, n)).ToArray()
            }).ToArray(),
            ["note"] = slots.Count == 0 || slots.Any(slot => !SlotPrepared(model, slot))
                ? "Run prepare to convert native asset-backed images into editable layers before importing textures."
                : "Texture indexes and layer indexes refer to this current material revision."
        };
    }

    private static object GroupInfo(object model, object group, int index)
    {
        var localized = Query("GetHairGroupNameQuery", model, group);
        var groupName = Get(localized, "UserDefinedString") as string;
        if (string.IsNullOrEmpty(groupName)) groupName = Get(localized, "FallbackString") as string;
        if (string.IsNullOrEmpty(groupName)) groupName = Get(localized, "LocalizationKey") as string;
        var info = new Dictionary<string, object> {
            ["index"] = index, ["name"] = groupName,
            ["path"] = (Get(group, "TransferableId") as string) + "/" + (Get(group, "HairGroupId") as string), ["strand_count"] = EnumerateList(Query("GetHairGroupChildrenQuery", model, group)).Count,
            ["material_id"] = Get(Get(GroupParameters(model, group), "HairMaterialReference"), "SharedHairMaterialId"),
            ["material_reference_type"] = SafeStr(Get(Get(GroupParameters(model, group), "HairMaterialReference"), "Type"))
        };
        return info;
    }

    public static bool TryExecute(string action, int material, int slot, int layer, int group,
        string path, string name, string key, string value, out object result, out string error)
    {
        result = null; error = null;
        try
        {
            if (!TryContext(out var context, out var model, out error)) return false;
            var materials = CurrentMaterials(model);
            if (action == "list")
            {
                result = new Dictionary<string, object> {
                    ["materials"] = materials.Select((m, i) => new Dictionary<string, object> { ["index"] = i, ["id"] = MaterialId(m), ["name"] = Query("GetMaterialNameQuery", model, m) }).ToArray(),
                    ["groups"] = Groups(model).Select((g, i) => GroupInfo(model, g, i)).ToArray(),
                    ["scope"] = "Current native Hairstyle category; group assignment changes material reference only."
                };
                return true;
            }
            if (action == "create")
            {
                var defaults = FindType("VRoid.Studio.Engine.Model+DefaultHairMaterialParameters") ?? throw new NotSupportedException("Native default hair material parameters are unavailable.");
                object Static(string n) => defaults.GetProperty(n, BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                var displayName = string.IsNullOrWhiteSpace(name) ? "MCP Hair Material" : CheckedName(name);
                Execute(context, "CreateMaterialCommand", Static("HairMaterialTemplateBody"), Static("OverlayColor"), Static("EmissionColor"), displayName);
                result = new Dictionary<string, object> { ["changed"] = true, ["name"] = displayName, ["note"] = "Created native shared material. Refresh list; assign explicitly to a group." };
                return true;
            }
            var selected = At(materials, material, "material");
            bool changed = true;
            switch (action)
            {
                case "inspect": result = Inspect(model, selected, material); return true;
                case "prepare":
                    var prepareSlots = Slots(model, selected);
                    if (prepareSlots.Count > 0 && prepareSlots.All(s => SlotPrepared(model, s))) changed = false;
                    else Execute(context, "AdvancedEdit.BeginHairMaterialAdvancedEditCommand", selected, "Layer", false);
                    break;
                case "duplicate": Execute(context, "DuplicateMaterialCommand", selected); break;
                case "rename":
                    name = CheckedName(name);
                    if (Equals(Query("GetMaterialNameQuery", model, selected), name)) changed = false;
                    else Execute(context, "SetHairMaterialNameCommand", selected, name);
                    break;
                case "assign":
                    changed = Assign(context, model, selected, group);
                    break;
                case "import_texture":
                    Import(context, model, selected, slot, layer, path);
                    break;
                case "export_texture":
                    result = Export(model, selected, slot, layer, path); return true;
                case "set_color":
                    var colorTarget = SupportedColor(model, selected, key);
                    if (!ParamCodec.TryParseColor(value, out var color)) throw new ArgumentException("value must be #RRGGBB or three finite RGB components in 0..1.");
                    if (ColorValue(model, selected, colorTarget) is Color oldColor && oldColor.Equals(color)) changed = false;
                    else if (colorTarget.IsBase) Execute(context, "SetBaseColorCommand", colorTarget.ColorId, color);
                    else Execute(context, "SetMaterialColorCommand", selected, colorTarget.Key, color);
                    break;
                case "set_color_enabled":
                    var enabledTarget = SupportedColor(model, selected, key);
                    if (!ArgParse.Flag(value, "enabled", out var enabled, out var enabledError, ("true", true), ("false", false))) throw new ArgumentException(enabledError);
                    if (string.IsNullOrEmpty(enabledTarget.DisableId)) throw new NotSupportedException($"Color '{key}' has no native enable/disable flag.");
                    if (ColorDisabled(model, selected, enabledTarget) == !enabled) changed = false;
                    else if (enabledTarget.IsBase) Execute(context, "SetBaseBoolCommand", enabledTarget.DisableId, !enabled);
                    else Execute(context, "SetMaterialColorDisabledCommand", selected, enabledTarget.Key, !enabled);
                    break;
                case "set_float":
                    var f = SupportedParameter(model, selected, key, ".MaterialFloat");
                    var min = Convert.ToSingle(Get(f, "minValue"), CultureInfo.InvariantCulture);
                    var max = Convert.ToSingle(Get(f, "maxValue"), CultureInfo.InvariantCulture);
                    if (!ArgParse.Float(value, key, out var number, out var floatError, min, max)) throw new ArgumentException(floatError);
                    if (Equals(Query("GetMaterialFloatQuery", model, selected, key), number)) changed = false;
                    else Execute(context, "SetMaterialFloatCommand", selected, key, number);
                    break;
                case "set_layer": changed = SetLayer(context, model, selected, slot, layer, key, value); break;
                default: throw new ArgumentException("Unsupported hair material action. Use list, inspect, prepare, create, duplicate, rename, assign, import_texture, export_texture, set_color, set_color_enabled, set_float or set_layer.");
            }
            result = new Dictionary<string, object> { ["action"] = action, ["material"] = material, ["material_id"] = MaterialId(selected), ["changed"] = changed,
                ["native_history"] = true, ["readiness"] = changed ? "Caller must await native update signal where applicable." : "unchanged" };
            return true;
        }
        catch (Exception e) { error = Unwrap(e).Message; return false; }
    }

    private static object SupportedParameter(object model, object material, string key, string suffix)
        => Sections(model, material).FirstOrDefault(s => (TypeName(s) ?? "").EndsWith(suffix, StringComparison.Ordinal) && Get(s, "parameterKey") as string == key)
           ?? throw new ArgumentException($"Unsupported material parameter '{key}'. Use a key returned by inspect.");

    private static string CheckedName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
            throw new ArgumentException("name must contain 1..128 characters without control characters.");
        return name.Trim();
    }

    private static void Import(object context, object model, object material, int slot, int layer, string path)
    {
        var s = At(Slots(model, material), slot, "editable texture slot (run prepare first)");
        if (!SlotPrepared(model, s)) throw new InvalidOperationException("This native texture slot is asset-backed; run prepare before importing a texture layer.");
        if (string.IsNullOrEmpty(path) || !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("path must identify an existing PNG file.");
        path = WinePaths.ToHost(path);
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Texture PNG does not exist.", path);
        if (file.Length > 64 * 1024 * 1024) throw new ArgumentException("Texture PNG exceeds the 64 MiB input limit.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 24 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71)
            throw new ArgumentException("The input does not have a PNG signature.");
        int Big(int i) => (bytes[i] << 24) | (bytes[i + 1] << 16) | (bytes[i + 2] << 8) | bytes[i + 3];
        var w = Big(16); var h = Big(20);
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096 || (long)w * h > 16777216)
            throw new ArgumentException("Texture dimensions must be 1..4096 pixels per side (at most 16 megapixels).");
        var layerPath = layer >= 0 ? At(Layers(model, s), layer, "layer") : null;
        if (layer < -1) throw new ArgumentException("layer must be -1 to add a new layer, or an existing non-negative layer index.");
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!ImageConversion.LoadImage(tex, new Il2CppStructArray<byte>(bytes), false)) throw new ArgumentException("The native PNG decoder rejected this file.");
            if (tex.width != w || tex.height != h) throw new ArgumentException("Decoded PNG dimensions do not match its header.");
            var size = New("VRoidCore.Draw.TiledBitmap.BitmapSize", w, h);
            if (layerPath == null) Execute(context, "LoadImageAsNewRasterLayerCommand", Get(s, "editableImagePath"), size, tex.GetPixels());
            else Execute(context, "LoadImageToEditableImageRasterLayerCommand", layerPath, size, tex.GetPixels());
        }
        finally { UnityEngine.Object.Destroy(tex); }
    }

    private static object Export(object model, object material, int slot, int layer, string path)
    {
        var s = At(Slots(model, material), slot, "editable texture slot (run prepare first)");
        var l = At(Layers(model, s), layer, "layer");
        if (string.IsNullOrEmpty(path) || !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("path must end in .png.");
        path = WinePaths.ToHost(path);
        if (File.Exists(path)) throw new ArgumentException("Export destination exists; choose a new path to preserve the existing file.");
        var content = Query("GetRasterLayerContentQuery", model, l);
        var size = Get(content, "size");
        int w = Convert.ToInt32(Get(size, "x")), h = Convert.ToInt32(Get(size, "y"));
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) throw new InvalidOperationException("Native layer dimensions are invalid or exceed the 8192 export limit.");
        var pixels = Get(content, "rgbaBytes") as Il2CppStructArray<byte> ?? throw new InvalidOperationException("The native layer returned no RGBA pixels.");
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        try
        {
            tex.LoadRawTextureData(pixels); tex.Apply();
            var png = ImageConversion.EncodeToPNG(tex);
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(dir);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var data = new byte[png.Length]; for (var i = 0; i < data.Length; i++) data[i] = png[i];
            stream.Write(data);
            return new Dictionary<string, object> { ["path"] = path, ["width"] = w, ["height"] = h, ["bytes"] = data.Length, ["changed"] = false, ["source"] = "native editable texture layer" };
        }
        finally { UnityEngine.Object.Destroy(tex); }
    }

    private static bool SetLayer(object context, object model, object material, int slot, int layer, string key, string value)
    {
        var s = At(Slots(model, material), slot, "editable texture slot (run prepare first)");
        var l = At(Layers(model, s), layer, "layer");
        var parameters = Query("GetEditableImageLayerTreeParameterSetQuery", model, l);
        string property; object parsed;
        switch (key)
        {
            case "visibility":
                property = "Visibility";
                if (!ArgParse.Flag(value, "visibility", out var visible, out var boolError, ("true", true), ("false", false))) throw new ArgumentException(boolError);
                parsed = visible; break;
            case "opacity":
                property = "Opacity";
                if (!ArgParse.Float(value, "opacity", out var opacity, out var opacityError, 0, 1)) throw new ArgumentException(opacityError);
                parsed = opacity; break;
            case "blend":
                property = "CompositionMode";
                var nativeType = Get(parameters, property)?.GetType() ?? throw new NotSupportedException("Native layer blend mode is unavailable.");
                if (!Enum.GetNames(nativeType).Contains(value)) throw new ArgumentException("blend must be an exact native name returned by inspect.supported_blends.");
                parsed = Enum.Parse(nativeType, value); break;
            default: throw new ArgumentException("set_layer key must be visibility, opacity or blend.");
        }
        if (Equals(Get(parameters, property), parsed)) return false;
        var commandType = FindType(CommandPrefix + "ModifyEditableImageLayerTreeParameterSetCommand");
        var ctor = commandType?.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 3) ?? throw new NotSupportedException("Native layer parameter command is unavailable.");
        var callback = NativeModifier(ctor.GetParameters()[2].ParameterType, target => {
            if (!Set(target, property, parsed)) throw new InvalidOperationException($"Native layer {property} setter failed.");
        });
        var command = ctor.Invoke(new[] { "MCP.Layer." + property, l, callback });
        Call(context, "ExecuteSyncCommand", command);
        return true;
    }

    internal static object NativeModifier(Type nativeType, Action<object> modify)
    {
        var argument = nativeType.GetGenericArguments().Single();
        var factory = typeof(HairMaterials).GetMethod(nameof(ManagedModifier), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(argument);
        var managed = (Delegate)factory.Invoke(null, new object[] { modify });
        RuntimeHelpers.RunClassConstructor(nativeType.TypeHandle);
        var converter = typeof(DelegateSupport).GetMethods(BindingFlags.Public | BindingFlags.Static).First(m => m.Name == "ConvertDelegate" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
        return converter.MakeGenericMethod(nativeType).Invoke(null, new object[] { managed });
    }

    private static Delegate ManagedModifier<T>(Action<object> modify) => new Action<T>(target => modify(target));
    private static Exception Unwrap(Exception e) => e is TargetInvocationException t && t.InnerException != null ? Unwrap(t.InnerException) : e;
}
