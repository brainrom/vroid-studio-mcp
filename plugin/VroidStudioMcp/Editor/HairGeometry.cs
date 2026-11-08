using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

/// <summary>Restricted native hair geometry operations; no design-editor or library writes.</summary>
public static class HairGeometry
{
    private const string Specific = "VRoidCore.Common.SpecificTypes.";
    private const string ModifyCommand = "VRoidCore.Editing.History.Command.ModifyProceduralHairGroupParameterSetCommand";
    private static readonly (string Key, string Set, string Property, float Min, float Max, bool Integer)[] Writable = {
        ("count", "ProceduralFormParameterSet", "HairCount", 1, 4, true),
        ("length", "ProceduralFormParameterSet", "Length", 0.05f, 1f, false),
        ("width", "HairParameterSet", "WidthScale", 0.001f, 1f, false),
        ("position", "ProceduralFormParameterSet", "AngleFrom", 0f, 360f, false),
        ("interval", "ProceduralFormParameterSet", "AngleWidth", 1f, 90f, false)
    };

    private static List<string> Transferables()
    {
        var ids = EnumerateList(HairMaterials.Call(Colors._colorHairVm, "GetTransferableIds"))
            .Select(SafeStr).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        var selected = Get(Get(Colors._colorHairVm, "Model"), "TransferableId") as string;
        if (!string.IsNullOrEmpty(selected) && !ids.Contains(selected)) ids.Add(selected);
        if (ids.Count == 0) throw new InvalidOperationException("The current Hairstyle category has no editable hair transferable.");
        return ids;
    }

    private static List<object> Groups(object model) => Transferables()
        .SelectMany(id => EnumerateList(HairMaterials.Query("GetHairGroupListQuery", model, id))).Select(RealObj).ToList();
    private static List<object> Materials(object model) => Transferables()
        .SelectMany(id => HairMaterials.Materials(model, id))
        .GroupBy(m => Get(m, "SharedHairMaterialId") as string).Select(g => RealObj(g.First())).ToList();
    private static object At(List<object> values, int index, string kind)
        => index >= 0 && index < values.Count ? RealObj(values[index])
            : throw new ArgumentException($"{kind} index {index} is out of range (count {values.Count}); refresh inspect.");
    private static bool Procedural(object path) => TypeName(path) == Specific + "ProceduralHairGroupPath";
    private static void CheckPath(object path)
    {
        if (!Procedural(path) && TypeName(path) != Specific + "BrushHairGroupPath")
            throw new NotSupportedException("Only native procedural and brush hair groups are supported.");
    }
    private static string Path(object path) => (Get(path, "TransferableId") as string) + "/" + (Get(path, "HairGroupId") as string);
    private static object Parameters(object model, object path)
        => HairMaterials.Query(Procedural(path) ? "GetProceduralHairGroupParameterSetQuery" : "GetBrushHairGroupParameterSetQuery", model, path);
    private static object NativeGroup(object model, object path)
    {
        CheckPath(path);
        return RealObj(HairMaterials.Call(model, "GetHairGroup", path));
    }
    private static string Name(object model, object path)
    {
        var localized = HairMaterials.Query("GetHairGroupNameQuery", model, path);
        foreach (var field in new[] { "UserDefinedString", "FallbackString", "LocalizationKey" })
            if (Get(localized, field) is string value && !string.IsNullOrEmpty(value)) return value;
        return null;
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
    private static bool Finite(Quaternion q) => Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w);

    private static void ValidateGuide(object model, object path, object guide)
    {
        if (TypeName(guide) != Specific + "Guide") throw new NotSupportedException("The source has no native Guide.");
        var raw = EnumerateList(Get(guide, "RawCylinderPoints"));
        var offset = EnumerateList(Get(guide, "OffsetCylinderPoints"));
        if (raw.Count < 2 || raw.Count > 256 || raw.Count != offset.Count)
            throw new InvalidOperationException("The source guide must have 2..256 paired raw/offset cylinder points.");
        foreach (var entry in raw.Select(p => (Point: p, Offset: false)).Concat(offset.Select(p => (Point: p, Offset: true))))
        {
            var point = entry.Point;
            if (Get(point, "position") is not Vector3 position || !Finite(position)
                || Get(point, "quaternion") is not Quaternion rotation || !Finite(rotation))
                throw new InvalidOperationException("The source guide contains missing or non-finite control-point data.");
            // hull is a native value-type list: inspect it through its indexer, never IEnumerator.
            var hull = Get(point, "hull");
            var count = Get(hull, "Count");
            if (count is not int n || n < 3 || n > 256)
                throw new InvalidOperationException("The source guide has an invalid cylinder hull.");
            for (int i = 0; i < n; i++)
            {
                var ar = HairMaterials.Call(hull, "get_Item", i);
                if (Get(ar, "angle") is not float angle || !Finite(angle)
                    || Get(ar, "radius") is not float radius || !Finite(radius) || (!entry.Offset && radius < 0))
                    throw new InvalidOperationException("The source guide has non-finite hull data or a negative raw radius; signed offsets are allowed.");
            }
        }
        if (HairMaterials.Call(model, "GetHairGuideOrigin", path) is not Matrix4x4 origin)
            throw new InvalidOperationException("The source guide origin could not be read.");
        for (int i = 0; i < 16; i++) if (!Finite(origin[i]))
            throw new InvalidOperationException("The source guide origin is non-finite.");
    }

    private static Dictionary<string, object> ScalarParameters(object parameters)
    {
        var result = new Dictionary<string, object>();
        if (parameters == null) return result;
        foreach (var property in RealObj(parameters).GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead || property.Name.Contains("BackingField")) continue;
            if (property.PropertyType != typeof(float) && property.PropertyType != typeof(int) && property.PropertyType != typeof(bool)) continue;
            var value = property.GetValue(RealObj(parameters));
            result[property.Name] = value is float f && !Finite(f) ? null : value;
        }
        return result;
    }

    private static object Inspect(object model, object path, int index)
    {
        CheckPath(path);
        var native = NativeGroup(model, path);
        var guide = Get(native, "Guide");
        string guideError = null;
        try { ValidateGuide(model, path, guide); } catch (Exception e) { guideError = Unwrap(e).Message; }
        var parameters = Parameters(model, path);
        return new Dictionary<string, object> {
            ["index"] = index, ["path"] = Path(path), ["name"] = Name(model, path),
            ["kind"] = Procedural(path) ? "procedural" : "brush", ["transferable_id"] = Get(path, "TransferableId"),
            ["transform_id"] = Get(Get(native, "TransformId"), "Value"), ["visible"] = Get(native, "IsVisible"),
            ["strand_count"] = EnumerateList(HairMaterials.Query("GetHairGroupChildrenQuery", model, path)).Count,
            ["material_id"] = Get(Get(parameters, "HairMaterialReference"), "SharedHairMaterialId"),
            ["guide"] = new Dictionary<string, object> { ["valid_for_copy"] = guideError == null, ["error"] = guideError,
                ["raw_points"] = EnumerateList(Get(guide, "RawCylinderPoints")).Count, ["offset_points"] = EnumerateList(Get(guide, "OffsetCylinderPoints")).Count },
            ["hair_parameters"] = ScalarParameters(Get(parameters, "HairParameterSet")),
            ["guide_parameters"] = ScalarParameters(Get(parameters, "GuideParameterSet")),
            ["procedural_parameters"] = ScalarParameters(Get(parameters, "ProceduralFormParameterSet")),
            ["settable"] = Procedural(path) ? Writable.Select(w => new Dictionary<string, object> {
                ["key"] = w.Key, ["native_property"] = w.Set + "." + w.Property,
                ["value"] = Get(Get(parameters, w.Set), w.Property), ["type"] = w.Integer ? "integer" : "number",
                ["min"] = w.Min, ["max"] = w.Max, ["range_kind"] = "tool_safety_bounds"
            }).ToArray() : Array.Empty<object>(),
            ["note"] = "Position and interval use degrees around the guide: 0 front, 90 model left. Confirm placement on the actual guide. Bounds are conservative tool limits, not the full editor ranges. Other raw native values are read-only; Arch of Hair Length is never writable."
        };
    }

    public static bool TryExecute(string action, int group, int material, string name, string key, string value,
        out object result, out string error)
    {
        result = null; error = null;
        bool mutationStarted = false;
        var progress = new Dictionary<string, object> { ["action"] = action, ["group"] = group,
            ["requested_name"] = name, ["applied"] = null, ["ready"] = false,
            ["retry_safe"] = false, ["preview_verified"] = false };
        try
        {
            if (!HairMaterials.TryContext(out var context, out var model, out error)) return false;
            var groups = Groups(model);
            if (action == "inspect")
            {
                result = group >= 0 ? Inspect(model, At(groups, group, "group"), group)
                    : new Dictionary<string, object> { ["groups"] = groups.Select((p, i) => Inspect(model, p, i)).ToArray(),
                        ["materials"] = Materials(model).Select((p, i) => new Dictionary<string, object> {
                            ["index"] = i, ["id"] = Get(p, "SharedHairMaterialId"), ["name"] = HairMaterials.Query("GetMaterialNameQuery", model, p)
                        }).ToArray() };
                return true;
            }
            var selected = At(groups, group, "group");
            CheckPath(selected);
            progress["target_path"] = Path(selected);
            if (action == "add")
            {
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
                    throw new ArgumentException("name must contain 1..128 characters without control characters.");
                name = name.Trim();
                if (groups.Any(p => string.Equals(Name(model, p), name, StringComparison.Ordinal)))
                    throw new ArgumentException("name must be unique in the current Hairstyle category; inspect before adding.");
                var native = NativeGroup(model, selected);
                var guide = Get(native, "Guide");
                ValidateGuide(model, selected, guide);
                var transform = Get(Get(native, "TransformId"), "Value") as string;
                var transferable = Get(selected, "TransferableId") as string;
                if (string.IsNullOrEmpty(transform) || string.IsNullOrEmpty(transferable))
                    throw new InvalidOperationException("The source group has no verified transform/transferable ID.");
                var copiedGuide = HairMaterials.Call(guide, "CloneSpecificDeep");
                if (copiedGuide == null || guide is not Il2CppObjectBase source || copiedGuide is not Il2CppObjectBase copy || source.Pointer == copy.Pointer)
                    throw new InvalidOperationException("The native Guide deep clone did not produce an independent object.");
                ValidateGuide(model, selected, copiedGuide);
                var sharedMaterial = At(Materials(model), material, "material");
                var localized = HairMaterials.New("VRoidCore.Protobuf.LocalizedString");
                if (!Set(localized, "UserDefinedString", name)) throw new InvalidOperationException("Native name initialization failed.");
                var before = new HashSet<string>(groups.Select(Path), StringComparer.Ordinal);
                progress["before_group_paths"] = before.ToArray();
                mutationStarted = true;
                HairMaterials.Execute(context, "AddProceduralHairGroupCommand", transferable, transform, localized, sharedMaterial, copiedGuide);
                var after = Groups(model);
                progress["after_group_paths"] = after.Select(Path).ToArray();
                var added = after.Select((p, i) => (Path: p, Index: i)).Where(p => !before.Contains(Path(p.Path))).ToList();
                if (added.Count != 1 || !Procedural(added[0].Path))
                    throw new InvalidOperationException("Native add returned without exactly one identifiable new procedural group.");
                var addedPath = added[0].Path;
                progress["added_path"] = Path(addedPath);
                var addedNative = NativeGroup(model, addedPath);
                var addedMaterial = Get(Get(Parameters(model, addedPath), "HairMaterialReference"), "SharedHairMaterialId") as string;
                if (after.Count != groups.Count + 1 || !before.IsSubsetOf(after.Select(Path))
                    || Get(addedPath, "TransferableId") as string != transferable
                    || Get(Get(addedNative, "TransformId"), "Value") as string != transform
                    || addedMaterial != Get(sharedMaterial, "SharedHairMaterialId") as string
                    || Name(model, addedPath) != name)
                    throw new InvalidOperationException("Added group identity, scope or material readback did not match the request.");
                result = new Dictionary<string, object> { ["changed"] = true, ["group"] = added[0].Index, ["path"] = Path(added[0].Path),
                    ["name"] = name, ["source_group"] = group, ["source_path"] = Path(selected), ["guide_deep_copied"] = true,
                    ["note"] = "Added with native default procedural parameters. After readiness, inspect and explicitly set count/length/width before visual acceptance. No library item was saved." };
                return true;
            }
            if (action != "set") throw new ArgumentException("action must be inspect, add or set.");
            if (!Procedural(selected)) throw new ArgumentException("set supports procedural groups only; existing brush groups are read-only.");
            var field = Writable.FirstOrDefault(w => w.Key == key);
            if (field.Key == null) throw new ArgumentException("key must be count, length, width, position or interval. Inspect settable for the current safety bounds.");
            object parsed;
            if (field.Integer)
            {
                if (!ArgParse.Int(value, "value", out var integer, out var parseError, (int)field.Min, (int)field.Max)) throw new ArgumentException(parseError);
                parsed = integer;
            }
            else
            {
                if (!ArgParse.Float(value, "value", out var number, out var parseError, field.Min, field.Max)) throw new ArgumentException(parseError);
                parsed = number;
            }
            var current = Parameters(model, selected);
            var set = Get(current, field.Set) ?? throw new NotSupportedException("The native parameter set is unavailable.");
            if (Equals(Get(set, field.Property), parsed))
            { result = new Dictionary<string, object> { ["changed"] = false, ["group"] = group, ["key"] = key, ["value"] = parsed }; return true; }
            var commandType = FindType(ModifyCommand) ?? throw new NotSupportedException("Native procedural modification is unavailable.");
            var ctor = commandType.GetConstructors().Single(c => c.GetParameters().Length == 4);
            var callback = HairMaterials.NativeModifier(ctor.GetParameters()[2].ParameterType, target => {
                if (!Set(Get(target, field.Set), field.Property, parsed)) throw new InvalidOperationException("Native procedural parameter setter failed.");
            });
            mutationStarted = true;
            // Geometry changes regenerate strands; reuseStrands=false is essential for count changes.
            HairMaterials.Execute(context, "ModifyProceduralHairGroupParameterSetCommand", "MCP.HairGeometry." + key, selected, callback, false);
            var readback = Get(Get(Parameters(model, selected), field.Set), field.Property);
            if (!Equals(readback, parsed)) throw new InvalidOperationException("Native procedural parameter readback did not match the request.");
            result = new Dictionary<string, object> { ["changed"] = true, ["group"] = group, ["path"] = Path(selected), ["key"] = key, ["value"] = readback };
            return true;
        }
        catch (Exception e)
        {
            error = Unwrap(e).Message + (mutationStarted ? " The native edit may have applied; inspect state and hair_geometry before retrying." : "");
            if (mutationStarted) result = progress;
            return false;
        }
    }

    private static Exception Unwrap(Exception e) => e is TargetInvocationException t && t.InnerException != null ? Unwrap(t.InnerException) : e;
}
