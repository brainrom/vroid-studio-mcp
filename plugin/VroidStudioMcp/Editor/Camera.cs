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

public static class CameraApi
{
    internal static readonly string[] _cwPresets = { "body", "face", "head", "upper", "legs", "feet" };

    // Region presets: center = fraction of avatar height from feet (the orbit/rotation center),
    // view = distance as a fraction of the default framing distance (which shows the whole body).
    // Tuned visually.
    private static readonly Dictionary<string, (float center, float view)> _regions =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["body"] = (0.50f, 0.86f),
        ["face"] = (0.91f, 0.24f),
        ["head"] = (0.90f, 0.33f),
        ["upper"] = (0.72f, 0.58f),
        ["legs"] = (0.33f, 0.62f),
        ["feet"] = (0.11f, 0.28f),
    };

    // Angle presets: yaw relative to the model's front direction (0 = front). Combined with a
    // region this gives e.g. (face, profile) or (legs, three-quarter). `*-left`/`*-right` are
    // named from the viewer's viewpoint when looking at the front.
    private static readonly Dictionary<string, float> _angles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["front"] = 0f,
        ["back"] = 180f,
        ["left"] = -90f,
        ["right"] = 90f,
        ["threequarter_left"] = -45f,
        ["threequarter_right"] = 45f,
        ["3q_left"] = -45f,
        ["3q_right"] = 45f,
    };

    private static UnityEngine.Bounds _cachedBounds;
    private static bool _hasCachedBounds;

    // Projection (orthographic/perspective) is toggled via SwitchCameraProjection and is otherwise
    // "sticky" across framing actions. We snapshot the state right after a model becomes editable so
    // `reset` can restore it (ResetPosition/SetCustomView do not touch projection).
    private static bool _initialOrthographic;
    private static bool _hasInitialProjection;

    /// <summary>Record the model's initial projection. Called once per model load (before the first
    /// reset) so `reset` has a target to go back to.</summary>
    public static void CaptureInitialProjection()
    {
        var cam = AvatarCamera();
        if (cam == null) return;
        _initialOrthographic = cam.orthographic;
        _hasInitialProjection = true;
    }

    // SwitchCameraProjection is a toggle (no absolute setter), so restore by toggling when the
    // current mode differs from the captured initial one.
    private static void RestoreInitialProjection()
    {
        if (!_hasInitialProjection) return;
        var cam = AvatarCamera();
        if (cam != null && cam.orthographic != _initialOrthographic && _cameraPosition != null)
            Invoke(_cameraPosition, "SwitchCameraProjection");
    }

    public static bool TryGetAvatarBounds(out UnityEngine.Bounds b)
    {
        b = default;
        try
        {
            float xmin = float.MaxValue, xmax = float.MinValue;
            float ymin = float.MaxValue, ymax = float.MinValue;
            float zmin = float.MaxValue, zmax = float.MinValue;
            bool any = false;
            var arr = UnityEngine.Object.FindObjectsOfType<UnityEngine.SkinnedMeshRenderer>();
            foreach (var o in Enumerate(arr))
            {
                if (o is not UnityEngine.SkinnedMeshRenderer r) continue;
                var bb = r.bounds;
                if (bb.size.y <= 0f) continue;
                any = true;
                xmin = Math.Min(xmin, bb.min.x); xmax = Math.Max(xmax, bb.max.x);
                ymin = Math.Min(ymin, bb.min.y); ymax = Math.Max(ymax, bb.max.y);
                zmin = Math.Min(zmin, bb.min.z); zmax = Math.Max(zmax, bb.max.z);
            }
            if (any)
            {
                b = new UnityEngine.Bounds(
                    new UnityEngine.Vector3((xmin + xmax) / 2f, (ymin + ymax) / 2f, (zmin + zmax) / 2f),
                    new UnityEngine.Vector3(xmax - xmin, ymax - ymin, zmax - zmin));
                _cachedBounds = b;
                _hasCachedBounds = true;
                return true;
            }
            // Mesh renderers are briefly absent while the model is being rebuilt — reuse the
            // last known bounds so framing stays sane instead of collapsing to a fixed height.
            if (_hasCachedBounds) { b = _cachedBounds; return true; }
            return false;
        }
        catch (Exception e) { PluginLog.LogError($"avatar bounds failed: {e.Message}"); return false; }
    }

    /// <summary>Frame the avatar by vertical region. center/view are fractions (see _regions).
    /// yawRelativeDeg rotates the view around Y away from the initial (front) direction, so e.g.
    /// 180 shows the back and 90 a side, without going through ChangeAngles' coroutine.</summary>
    public static void SetFocus(float center, float view, float yawRelativeDeg = 0f)
    {
        if (_cameraPosition == null) { PluginLog.LogError("camera: instance not captured"); return; }
        try
        {
            var c0 = Get(_cameraPosition, "_initialRotationCenter") is UnityEngine.Vector3 a ? a : default;
            var p0 = Get(_cameraPosition, "_initialPosition") is UnityEngine.Vector3 p ? p : default;
            var dir = (p0 - c0).normalized;
            if (dir.sqrMagnitude < 1e-6f) dir = new UnityEngine.Vector3(0f, 0f, -1f);
            if (UnityEngine.Mathf.Abs(yawRelativeDeg) > 0.01f)
            {
                float rad = yawRelativeDeg * UnityEngine.Mathf.Deg2Rad;
                float cs = UnityEngine.Mathf.Cos(rad), sn = UnityEngine.Mathf.Sin(rad);
                dir = new UnityEngine.Vector3(dir.x * cs + dir.z * sn, dir.y, -dir.x * sn + dir.z * cs).normalized;
            }
            float dist0 = UnityEngine.Vector3.Distance(p0, c0);

            float bottom, top;
            if (TryGetAvatarBounds(out var b)) { bottom = b.min.y; top = b.max.y; }
            else { bottom = c0.y - 0.5f; top = c0.y + 0.5f; }
            float h = Math.Max(top - bottom, 0.1f);

            var center3 = new UnityEngine.Vector3(c0.x, bottom + center * h, c0.z);
            var pos = center3 + dir * (dist0 * view);
            Invoke(_cameraPosition, "SetCustomView", pos, center3);
            PluginLog.LogDebug($"camera frame c={center:0.00} v={view:0.00} yaw={yawRelativeDeg:0} h={h:0.000} dist0={dist0:0.000} center3={center3}");
        }
        catch (Exception e) { PluginLog.LogError($"camera frame failed: {e.Message}"); }
    }

    public static bool IsRegion(string name) => name != null && _regions.ContainsKey(name);

    /// <summary>True when the token is a known angle preset (front/back/left/right/threequarter_*).</summary>
    public static bool IsAngle(string name) => name != null && _angles.ContainsKey(name);

    /// <summary>Resolve an angle token to its yaw offset (degrees relative to front).</summary>
    public static bool TryAngle(string token, out float deg)
    {
        deg = 0f;
        return token != null && _angles.TryGetValue(token.Trim(), out deg);
    }

    /// <summary>Parse a view token "<region>" or "<region>:<angle>" (e.g. "face", "face:right").
    /// Region and angle names are case-insensitive.</summary>
    public static bool TryParseView(string view, out string region, out float yaw)
    {
        region = null; yaw = 0f;
        if (string.IsNullOrEmpty(view)) return false;
        var v = view.Trim();
        int colon = v.IndexOf(':');
        if (colon < 0)
        {
            if (!IsRegion(v)) return false;
            region = v; return true;
        }
        var r = v.Substring(0, colon).Trim();
        if (!IsRegion(r) || !TryAngle(v.Substring(colon + 1), out yaw)) return false;
        region = r; return true;
    }

    /// <summary>CameraRotationMode values as an "index=Name, ..." list, for docs/errors. Resolved from
    /// the engine enum at runtime (empty/null when the editor types are not loaded yet).</summary>
    public static string RotationModesText()
    {
        try
        {
            var outer = FindType("VRoid.UI.Component.AvatarCameraPosition");
            var modeType = outer?.GetNestedType("CameraRotationMode", BindingFlags.Public | BindingFlags.NonPublic);
            if (modeType == null) return null;
            return string.Join(", ", Enum.GetNames(modeType)
                .Select(n => $"{Convert.ToInt32(Enum.Parse(modeType, n))}={n}"));
        }
        catch { return null; }
    }

    /// <summary>Apply a named region preset at an optional yaw offset (see SetFocus).</summary>
    public static void ApplyCameraView(string preset, float yawRelativeDeg = 0f)
    {
        if (preset != null && _regions.TryGetValue(preset, out var f)) SetFocus(f.center, f.view, yawRelativeDeg);
        else SetFocus(0.50f, 0.86f, yawRelativeDeg);
    }

    /// <summary>Let a pending camera change take effect before the next read/capture. `ChangeAngles`/
    /// `RotateAxis` run a coroutine that keeps overwriting the transform for a few frames, and even a
    /// direct `SetCustomView` is only visible in the preview RenderTexture after the next render.
    /// Yields that coroutine out, then one extra frame so the texture matches the final transform.</summary>
    public static IEnumerator WaitCameraIdle()
    {
        yield return null;
        float t = 0f;
        while (Get(_cameraPosition, "_isRunningCameraRotateCoroutine") is bool running && running && t < 2f)
        { t += UnityEngine.Time.unscaledDeltaTime; yield return null; }
        yield return null;
    }

    /// <summary>Snapshot of the current camera: angles (yaw/pitch), distance, world position/rotation,
    /// projection and fov. Returned by every `camera` command so a client gets feedback, not just "ok".
    /// Angles/rotation are normalized to (-180, 180] (the engine reports [0,360)), so a client can
    /// nudge from the reported value without crossing the wrap seam.</summary>
    public static Dictionary<string, object> GetState()
    {
        var d = new Dictionary<string, object>();
        try
        {
            if (Invoke(_cameraPosition, "GetAngles") is UnityEngine.Vector2 av)
                d["angles"] = new Dictionary<string, object> { ["yaw"] = NormDeg(av.x), ["pitch"] = NormDeg(av.y) };
            if (Invoke(_cameraPosition, "CalculateDistance") is float df) d["distance"] = Round3(df);
            var cam = AvatarCamera();
            if (cam != null)
            {
                var p = cam.transform.position;
                var r = cam.transform.eulerAngles;
                d["position"] = new Dictionary<string, object> { ["x"] = Round3(p.x), ["y"] = Round3(p.y), ["z"] = Round3(p.z) };
                d["rotation"] = new Dictionary<string, object> { ["x"] = NormDeg(r.x), ["y"] = NormDeg(r.y), ["z"] = NormDeg(r.z) };
                d["projection"] = cam.orthographic ? "orthographic" : "perspective";
                d["fov"] = Round3(cam.fieldOfView);
                d["orthographicSize"] = Round3(cam.orthographicSize);
            }
        }
        catch (Exception e) { PluginLog.LogError($"camera state failed: {e.Message}"); }
        return d;
    }

    private static double Round3(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);

    // Map an engine angle (degrees, reported in [0,360)) to (-180,180], e.g. 354.289 -> -5.711.
    private static double NormDeg(float deg)
    {
        double v = deg % 360.0;
        if (v > 180.0) v -= 360.0;
        else if (v <= -180.0) v += 360.0;
        return Round3(v);
    }

    public static void ProbeCamera()
    {
        try
        {
            var c0 = Get(_cameraPosition, "_initialRotationCenter");
            var p0 = Get(_cameraPosition, "_initialPosition");
            var rc = Get(_cameraPosition, "_rotationCenter");
            var cam = AvatarCamera();
            var cp = cam != null ? cam.transform.position : default(UnityEngine.Vector3);
            bool hasB = TryGetAvatarBounds(out var b);
            PluginLog.LogDebug($"camera probe: initPos={p0} initCenter={c0} rotCenter={rc} camPos={cp} bounds={(hasB ? b.ToString() : "none")} fov={(cam != null ? cam.fieldOfView : 0f)}");
        }
        catch (Exception e) { PluginLog.LogError($"camera probe failed: {e.Message}"); }
    }

    public static UnityEngine.Camera AvatarCamera()
    {
        try
        {
            // Prefer the 3D layer camera by name; other cameras may own tiny RTs (icons/thumbnails).
            UnityEngine.Camera anyTarget = null;
            foreach (var c in AllCameras())
            {
                if (c.targetTexture == null) continue;
                if ((Get(c, "name") as string) == "NormalLayerCamera") return c;
                anyTarget ??= c;
            }
            if (anyTarget != null) return anyTarget;
            if (_cameraController != null)
            {
                var cg = Get(_cameraController, "_instantiatedCamera") ?? Get(_cameraController, "InstantiatedCamera");
                var cam = cg != null ? Get(cg, "PrimaryCamera") as UnityEngine.Camera : null;
                if (cam != null) return cam;
                var cam1 = Get(_cameraController, "sourceUnityCamera") as UnityEngine.Camera;
                if (cam1 != null) return cam1;
            }
            if (_cameraPosition != null)
            {
                var cam2 = Get(_cameraPosition, "_unityCamera") as UnityEngine.Camera;
                if (cam2 != null) return cam2;
            }
        }
        catch { }
        return null;
    }
    public static List<UnityEngine.Camera> AllCameras()
    {
        var res = new List<UnityEngine.Camera>();
        try
        {
            var arr = UnityEngine.Camera.allCameras;
            foreach (var o in Enumerate(arr))
                if (o is UnityEngine.Camera c) res.Add(c);
        }
        catch { }
        return res;
    }
    // Camera framing presets.
    public static void ApplyCameraPreset(string preset, float yawRelativeDeg = 0f)
    {
        if (!_regions.TryGetValue(preset, out var f)) { SetFocus(0.50f, 0.86f, yawRelativeDeg); return; }
        SetFocus(f.center, f.view, yawRelativeDeg);
    }
    internal static string LastError { get; private set; }
    public static bool ApplyCamera(string cmd)
    {
        try
        {
            LastError = null;
            if (_cameraPosition == null) { LastError = "camera: instance not captured yet"; PluginLog.LogError(LastError); return false; }
            var parts = cmd.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return true;
            // Absolute ortho size / fov are diagnostics knobs (deterministic scale for tests and A/B);
            // they are not part of the normal camera API, so keep them behind dev_tools.
            if ((parts[0] == "ortho" || parts[0] == "fov") && !ServerSettings.DevTools)
            { LastError = $"camera {parts[0]} is dev-only (dev_tools=false in servers config)"; PluginLog.LogError(LastError); return false; }
            switch (parts[0])
            {
                case "reset":
                    // ResetPosition restores angles/pan but leaves the zoom baked into the position,
                    // so also restore the exact initial view (position + center) captured at startup.
                    Invoke(_cameraPosition, "ResetPosition");
                    if (Get(_cameraPosition, "_initialPosition") is UnityEngine.Vector3 ip &&
                        Get(_cameraPosition, "_initialRotationCenter") is UnityEngine.Vector3 ic)
                        Invoke(_cameraPosition, "SetCustomView", ip, ic);
                    RestoreInitialProjection();
                    PluginLog.LogDebug("camera reset");
                    break;
                case "angles":
                    if (parts.Length >= 3)
                    {
                        var v = new UnityEngine.Vector2(float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                                                        float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
                        Invoke(_cameraPosition, "ChangeAngles", v);
                        PluginLog.LogDebug($"camera angles {parts[1]},{parts[2]}");
                    }
                    break;
                case "rotate":
                    if (parts.Length >= 2)
                    {
                        // CameraRotationMode is a nested enum; TypeByName does not resolve nested
                        // types (its namespace is empty), so look it up through the outer type.
                        var outer = FindType("VRoid.UI.Component.AvatarCameraPosition");
                        var modeType = outer?.GetNestedType("CameraRotationMode", BindingFlags.Public | BindingFlags.NonPublic);
                        if (modeType == null) { LastError = "camera rotate: CameraRotationMode enum not found"; PluginLog.LogError(LastError); return false; }
                        if (!int.TryParse(parts[1], out var modeIdx) || !Enum.IsDefined(modeType, modeIdx))
                        { LastError = $"camera rotate: invalid mode '{parts[1]}' (valid: {RotationModesText() ?? "?"})"; PluginLog.LogError(LastError); return false; }
                        var mode = Enum.ToObject(modeType, modeIdx);
                        Invoke(_cameraPosition, "RotateAxis", mode);
                        PluginLog.LogDebug($"camera rotate {parts[1]}");
                    }
                    break;
                case "zoom":
                    if (parts.Length >= 2)
                    {
                        Invoke(_cameraPosition, "Zoom", float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                        PluginLog.LogDebug($"camera zoom {parts[1]}");
                    }
                    break;
                case "ortho":
                    // Direct, absolute orthographic size (SetFocus/SetCustomView only move the camera,
                    // they never touch orthographicSize, so `frame`/`view` do not actually zoom an ortho
                    // preview). This is the deterministic way to set the preview scale.
                    if (parts.Length >= 2)
                    {
                        var cam = AvatarCamera();
                        if (cam == null) { LastError = "camera ortho: no camera"; PluginLog.LogError(LastError); return false; }
                        cam.orthographic = true;
                        cam.orthographicSize = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                        PluginLog.LogDebug($"camera ortho {parts[1]}");
                    }
                    break;
                case "fov":
                    if (parts.Length >= 2)
                    {
                        var cam = AvatarCamera();
                        if (cam == null) { LastError = "camera fov: no camera"; PluginLog.LogError(LastError); return false; }
                        cam.fieldOfView = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                        PluginLog.LogDebug($"camera fov {parts[1]}");
                    }
                    break;
                case "pan":
                    if (parts.Length >= 3)
                    {
                        var v = new UnityEngine.Vector2(float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                                                        float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
                        Invoke(_cameraPosition, "Pan", v);
                        PluginLog.LogDebug($"camera pan {parts[1]},{parts[2]}");
                    }
                    break;
                case "projection":
                    Invoke(_cameraPosition, "SwitchCameraProjection");
                    PluginLog.LogDebug("camera projection toggled");
                    break;
                case "frame":
                    {
                        var region = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;
                        if (!IsRegion(region))
                        { LastError = $"camera frame: unknown region '{parts.ElementAtOrDefault(1)}' (body|face|head|upper|legs|feet)"; PluginLog.LogError(LastError); return false; }
                        float yawRel = 0f;
                        if (parts.Length >= 3 && !TryAngle(parts[2], out yawRel))
                        { LastError = $"camera frame: unknown angle '{parts[2]}' (front|back|left|right|threequarter_left|threequarter_right)"; PluginLog.LogError(LastError); return false; }
                        ApplyCameraPreset(region, yawRel);
                        PluginLog.LogDebug($"camera frame {region} yaw={yawRel:0}");
                    }
                    break;
                case "view":
                    if (parts.Length >= 3)
                        SetFocus(float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                                 float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "probe":
                    ProbeCamera();
                    break;
                default:
                    LastError = $"camera: unknown command '{cmd}'";
                    PluginLog.LogError(LastError);
                    return false;
            }
            return true;
        }
        catch (Exception e) { LastError = $"camera failed: {e.Message}"; PluginLog.LogError(LastError); return false; }
    }
}
