using System;
using System.Collections;
using System.Collections.Generic;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

/// <summary>Render diagnostics (dev, TCP only): what the preview actually renders into, and a runtime
/// MSAA switch for A/B comparisons.</summary>
public static class RenderHandlers
{
    public static IEnumerator Probe(CommandDispatcher.Context ctx)
    {
        var d = new Dictionary<string, object>();
        try
        {
            d["quality"] = new Dictionary<string, object>
            {
                ["aa"] = UnityEngine.QualitySettings.antiAliasing,
                ["aniso"] = UnityEngine.QualitySettings.anisotropicFiltering.ToString(),
                ["masterTextureLimit"] = UnityEngine.QualitySettings.masterTextureLimit,
                ["vSync"] = UnityEngine.QualitySettings.vSyncCount,
                ["level"] = UnityEngine.QualitySettings.GetQualityLevel(),
                ["targetFrameRate"] = UnityEngine.Application.targetFrameRate,
                ["screen"] = $"{UnityEngine.Screen.width}x{UnityEngine.Screen.height}",
                ["msaa_samples"] = ServerSettings.MsaaSamples,
                ["aniso_filtering"] = ServerSettings.AnisoFiltering,
                ["render_scale"] = ServerSettings.RenderScale,
            };
            d["preview"] = RtInfo(Screenshot.ResolvePreviewRT());
            var cams = new List<object>();
            foreach (var c in CameraApi.AllCameras())
            {
                if (c == null) continue;
                var tt = c.targetTexture;
                cams.Add(new Dictionary<string, object>
                {
                    ["name"] = Get(c, "name") as string,
                    ["orthographic"] = c.orthographic,
                    ["orthoSize"] = Math.Round(c.orthographicSize, 3),
                    ["fov"] = Math.Round(c.fieldOfView, 3),
                    ["allowHDR"] = c.allowHDR,
                    ["depth"] = c.depth,
                    ["target"] = RtInfo(tt),
                });
            }
            d["cameras"] = cams;
        }
        catch (Exception e) { ctx.Error = e.Message; return CommandUtil.Done(); }
        ctx.Result = d;
        return CommandUtil.Done();
    }

    public static IEnumerator Msaa(CommandDispatcher.Context ctx)
    {
        var a = CommandUtil.Arg(ctx, 0);
        if (!ArgParse.Int(a, "msaa_samples", out var n, out var error, 0, 8))
        { ctx.Error = $"rendermaasa: {error}"; return CommandUtil.Done(); }
        ServerSettings.SetMsaaSamples(n);
        ctx.Result = new Dictionary<string, object>
        {
            ["msaa_samples"] = ServerSettings.MsaaSamples,
            ["note"] = "saved; restart the app to apply (RenderTexture samples cannot change on a live texture)",
        };
        return CommandUtil.Done();
    }

    public static IEnumerator Scale(CommandDispatcher.Context ctx)
    {
        var a = CommandUtil.Arg(ctx, 0);
        if (!ArgParse.Float(a, "render_scale", out var f, out var error, 1f, 4f))
        { ctx.Error = $"renderscale: {error}"; return CommandUtil.Done(); }
        ServerSettings.SetRenderScale(f);
        ctx.Result = new Dictionary<string, object>
        {
            ["render_scale"] = ServerSettings.RenderScale,
            ["note"] = "saved; restart the app to apply (RenderTexture size cannot change on a live texture)",
        };
        return CommandUtil.Done();
    }

    private static object RtInfo(UnityEngine.RenderTexture rt)
    {
        if (rt == null) return null;
        return new Dictionary<string, object>
        {
            ["width"] = rt.width,
            ["height"] = rt.height,
            ["format"] = rt.format.ToString(),
            ["depth"] = rt.depth,
            ["aa"] = rt.antiAliasing,
            ["filterMode"] = rt.filterMode.ToString(),
            ["useMipMap"] = rt.useMipMap,
        };
    }
}
