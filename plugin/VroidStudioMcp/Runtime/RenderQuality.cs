using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VroidStudioMcp;

/// <summary>
/// Anti-aliasing for the 3D preview.
///
/// The editor builds every preview RenderTexture through
/// VRoid.UI.CameraTargetTextureProvider.CreateRenderTexture, which creates a single-sample RT
/// (antiAliasing == 1). RenderTexture.antiAliasing is get-only in this Unity build, so MSAA cannot be
/// switched on after the fact: the RT has to be rebuilt from its own descriptor with msaaSamples > 1.
/// This postfix does exactly that and hands the MSAA RT back, so the engine's normal assignment path
/// (CameraViewController -> RenderTextureManager -> CompositionRenderer) picks it up untouched.
/// </summary>
public static class RenderQuality
{
    private static int _frames;
    private static string _lastLog;
    // Upper bound on the (supersampled) preview RT area, so a 4K panel with render_scale=2 does not
    // blow up memory. The engine applies its own MaxNumPixels cap before we scale, so we clamp again.
    private const long MaxPreviewPixels = 8_000_000;

    public static void Install()
    {
        var post = new HarmonyMethod(typeof(RenderQuality).GetMethod(nameof(OnCreateRenderTexture), BindingFlags.Public | BindingFlags.Static));
        Hooks.PatchByTypeName("VRoid.UI.CameraTargetTextureProvider", "CreateRenderTexture", post);
        Hooks.PatchByTypeName("VRoidStudio.Application.CameraTargetTextureProvider", "CreateRenderTexture", post);
        ApplyAniso();
        PluginLog.LogInfo($"render quality: msaa={ServerSettings.MsaaSamples} aniso={ServerSettings.AnisoFiltering}");
    }

    private static void ApplyAniso()
    {
        if (!ServerSettings.AnisoFiltering) return;
        try
        {
            UnityEngine.QualitySettings.anisotropicFiltering = UnityEngine.AnisotropicFiltering.ForceEnable;
            UnityEngine.QualitySettings.masterTextureLimit = 0;
        }
        catch (Exception e) { PluginLog.LogWarning($"aniso set failed: {e.Message}"); }
    }

    public static void OnCreateRenderTexture(ref UnityEngine.RenderTexture __result)
    {
        int samples = ServerSettings.MsaaSamples;
        float scale = ServerSettings.RenderScale;
        if (__result == null) return;
        if (samples <= 1 && scale <= 1f) return;
        try
        {
            if (scale <= 1f && __result.antiAliasing >= samples) return;
            var desc = __result.descriptor;
            if (scale > 1f)
            {
                int w = Mathf.Max(1, (int)(desc.width * scale));
                int h = Mathf.Max(1, (int)(desc.height * scale));
                long area = (long)w * h;
                if (area > MaxPreviewPixels)
                {
                    float f = (float)Math.Sqrt((double)MaxPreviewPixels / area);
                    w = Mathf.Max(1, (int)(w * f));
                    h = Mathf.Max(1, (int)(h * f));
                }
                desc.width = w;
                desc.height = h;
            }
            desc.msaaSamples = samples > 1 ? samples : 1;
            var rt = new UnityEngine.RenderTexture(desc);
            rt.Create();
            var old = __result;
            __result = rt;
            try { old.Release(); UnityEngine.Object.Destroy(old); } catch { }
            var key = $"{rt.width}x{rt.height}x{rt.antiAliasing}x{scale}";
            if (key != _lastLog)
            {
                _lastLog = key;
                PluginLog.LogDebug($"quality preview RT {rt.width}x{rt.height} fmt={rt.format} depth={rt.depth} aa={rt.antiAliasing} scale={scale}");
            }
        }
        catch (Exception e) { PluginLog.LogError($"quality RT failed: {e.Message}"); }
    }

    /// <summary>MSAA also needs camera.allowMSAA; its setter has no getter, so re-assert it periodically
    /// for every camera that renders into a target texture (the preview layer cameras).</summary>
    public static void Tick()
    {
        if (ServerSettings.MsaaSamples <= 1) return;
        if ((++_frames & 31) != 0) return;
        try
        {
            foreach (var c in CameraApi.AllCameras())
                if (c != null && c.targetTexture != null) c.allowMSAA = true;
        }
        catch { }
    }
}
