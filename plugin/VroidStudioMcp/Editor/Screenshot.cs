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
using static VroidStudioMcp.CameraApi;

namespace VroidStudioMcp;

public static class Screenshot
{
    private const float CropX = 0.153f, CropY = 0.030f, CropW = 0.692f, CropH = 0.940f;

    private static byte[] _lastCanonicalPng;
    private static byte[] _lastUiPng;
    internal static byte[] LastCanonicalPng => _lastCanonicalPng;
    internal static byte[] LastUiPng => _lastUiPng;

    public static string ShotHash()
    {
        var rt = ResolvePreviewRT();
        if (rt == null) return "no-rt";
        // Hash only: no disk artifacts (a before/after hash must not clobber screenshot.png).
        CaptureScreenshot(writeFiles: false);
        if (_lastCanonicalPng == null) return "null";
        uint hsh = 2166136261;
        foreach (var b in _lastCanonicalPng) { hsh ^= b; hsh *= 16777619; }
        return hsh.ToString("x8");
    }
    public static void WritePng(UnityEngine.Texture2D tex, string name)
    {
        try
        {
            var png = UnityEngine.ImageConversion.EncodeToPNG(tex);
            if (png != null)
            {
                File.WriteAllBytes(Path.Combine(BepInEx.Paths.GameRootPath, name), png);
                if (name == "screenshot.png") _lastCanonicalPng = png;
            }
        }
        catch (Exception e) { PluginLog.LogError($"write {name} failed: {e.Message}"); }
    }
    public static void SaveVariants(UnityEngine.Texture2D tex, bool flipCanonical)
    {
        var px = tex.GetPixels32();
        int w = tex.width, h = tex.height;
        WritePng(tex, "screenshot_raw.png");

        var v = new Il2CppStructArray<UnityEngine.Color32>(px.Length);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                v[(h - 1 - y) * w + x] = px[y * w + x];
        tex.SetPixels32(v); tex.Apply(); WritePng(tex, "screenshot_vflip.png");
        if (flipCanonical)
            WritePng(tex, "screenshot.png"); // ScreenCapture path needs vertical flip

        tex.SetPixels32(px); tex.Apply();
        if (!flipCanonical)
            WritePng(tex, "screenshot.png"); // RenderTexture path reads correctly as-is
    }
    /// <summary>Capture the preview crop. writeFiles=false skips disk entirely but still leaves
    /// the PNG in _lastCanonicalPng for inline (base64) consumers.</summary>
    public static void CaptureScreenshot(bool writeFiles = true)
    {
        try
        {
            // the 3D-preview RenderTexture (no UI), rendered by the avatar CameraGroup
            UnityEngine.RenderTexture rt = ResolvePreviewRT();
            bool temporary = false;
            bool cameraRender = false;
            if (rt == null)
            {
                // render the avatar's 3D camera into our own RT (no UI)
                var ucam = AvatarCamera();
                if (ucam != null)
                {
                    int cw = ucam.pixelWidth > 0 ? ucam.pixelWidth : 1080;
                    int ch = ucam.pixelHeight > 0 ? ucam.pixelHeight : 1920;
                    rt = UnityEngine.RenderTexture.GetTemporary(cw, ch, 24);
                    var prevT = ucam.targetTexture;
                    ucam.targetTexture = rt;
                    ucam.Render();
                    ucam.targetTexture = prevT;
                    temporary = true;
                    cameraRender = true;
                }
                else
                {
                    // debugging fallback: engine-internal full-frame capture (includes UI)
                    rt = UnityEngine.RenderTexture.GetTemporary(UnityEngine.Screen.width, UnityEngine.Screen.height, 24);
                    UnityEngine.ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);
                    temporary = true;
                }
            }
            var cr = PreviewCrop(rt.width, rt.height);
            int w = (int)cr.width, h = (int)cr.height;
            // A multi-sampled preview RT (RenderQuality) cannot be read directly; blit resolves it.
            var readRt = ResolveForRead(rt, out var resolveTmp);
            var prevActive = UnityEngine.RenderTexture.active;
            UnityEngine.RenderTexture.active = readRt;
            var tex = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGB24, false);
            tex.ReadPixels(cr, 0, 0);
            tex.Apply();
            if (writeFiles) SaveVariants(tex, temporary);
            else
            {
                var png = UnityEngine.ImageConversion.EncodeToPNG(tex);
                if (png != null) _lastCanonicalPng = png;
            }
            UnityEngine.RenderTexture.active = prevActive;
            if (resolveTmp != null) UnityEngine.RenderTexture.ReleaseTemporary(resolveTmp);
            if (temporary) UnityEngine.RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.Destroy(tex);
            var kind = !temporary ? "preview-rt" : (cameraRender ? "camera-rt" : "temp-rt");
            var exists = writeFiles && File.Exists(Path.Combine(BepInEx.Paths.GameRootPath, "screenshot.png"));
            PluginLog.LogTrace($"screenshot captured ({w}x{h}) [{kind}] write={writeFiles} exists={exists} root={BepInEx.Paths.GameRootPath}");
        }
        catch (Exception e) { PluginLog.LogError($"screenshot failed: {e}"); }
    }
    /// <summary>Capture the whole window (including UI) via the engine. Under Wine/DXVK the
    /// ScreenCapture result is Y-flipped, so it is flipped back here.</summary>
    public static byte[] FullWindowPng(bool writeFile)
    {
        try
        {
            int w = UnityEngine.Screen.width, h = UnityEngine.Screen.height;
            if (w <= 0 || h <= 0) { PluginLog.LogError("full window capture: no screen size"); return null; }
            var rt = UnityEngine.RenderTexture.GetTemporary(w, h, 24);
            UnityEngine.ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);
            var prev = UnityEngine.RenderTexture.active;
            UnityEngine.RenderTexture.active = rt;
            var tex = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGB24, false);
            tex.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            UnityEngine.RenderTexture.active = prev;
            UnityEngine.RenderTexture.ReleaseTemporary(rt);

            var px = tex.GetPixels32();
            var v = new Il2CppStructArray<UnityEngine.Color32>(px.Length);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    v[(h - 1 - y) * w + x] = px[y * w + x];
            tex.SetPixels32(v); tex.Apply();

            var png = UnityEngine.ImageConversion.EncodeToPNG(tex);
            UnityEngine.Object.Destroy(tex);
            if (writeFile && png != null)
                File.WriteAllBytes(Path.Combine(BepInEx.Paths.GameRootPath, "ui_screenshot.png"), png);
            _lastUiPng = png;
            PluginLog.LogTrace($"ui screenshot captured ({w}x{h}) write={writeFile} bytes={(png != null ? png.Length : 0)}");
            return png;
        }
        catch (Exception e) { PluginLog.LogError($"full window capture failed: {e}"); return null; }
    }

    /// <summary>Hash of a fixed fractional region of the window (UI included), e.g. the section
    /// switcher. Coordinates are fractions with a top-left origin (x, y, w, h). Used by tests to assert
    /// a section switch changed the UI without comparing whole frames.</summary>
    public static string WindowRegionHash(float fx, float fy, float fw, float fh)
    {
        try
        {
            int w = UnityEngine.Screen.width, h = UnityEngine.Screen.height;
            if (w <= 0 || h <= 0) return null;
            var rt = UnityEngine.RenderTexture.GetTemporary(w, h, 24);
            UnityEngine.ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);
            var prev = UnityEngine.RenderTexture.active;
            UnityEngine.RenderTexture.active = rt;
            var tex = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGB24, false);
            tex.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            UnityEngine.RenderTexture.active = prev;
            UnityEngine.RenderTexture.ReleaseTemporary(rt);
            var px = tex.GetPixels32();
            UnityEngine.Object.Destroy(tex);

            int x0 = Math.Clamp((int)(fx * w), 0, w - 1);
            int x1 = Math.Clamp((int)((fx + fw) * w), x0 + 1, w);
            int y0 = Math.Clamp((int)(fy * h), 0, h - 1);
            int y1 = Math.Clamp((int)((fy + fh) * h), y0 + 1, h);
            uint hsh = 2166136261;
            // ScreenCapture is Y-flipped, so screen row y is texture row (h-1-y).
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    var c = px[(h - 1 - y) * w + x];
                    hsh = (hsh ^ c.r) * 16777619u;
                    hsh = (hsh ^ c.g) * 16777619u;
                    hsh = (hsh ^ c.b) * 16777619u;
                }
            return hsh.ToString("x8");
        }
        catch (Exception e) { PluginLog.LogError($"region hash failed: {e.Message}"); return null; }
    }

    // The 3D viewport camera ("NormalLayerCamera") renders the avatar into its own RenderTexture,
    // sized to the viewport and free of UI; "MainCamera" is the orthographic UI camera over the whole
    // window. Prefer the former (no crop); fall back to the composited frame with a fixed crop.
    private static bool _previewIsViewport;

    public static UnityEngine.RenderTexture ResolvePreviewRT()
    {
        _previewIsViewport = false;
        try
        {
            foreach (var c in AllCameras())
                if ((Get(c, "name") as string) == "NormalLayerCamera" && c.targetTexture != null)
                { _previewIsViewport = true; return c.targetTexture; }

            // Fallback: the live preview is composited into MainCamera; preview-only = crop of it
            UnityEngine.Camera fallbackCam = null;
            foreach (var c in AllCameras())
            {
                var tt = c.targetTexture;
                if (tt == null) continue;
                var n = Get(c, "name") as string ?? "";
                if (n == "MainCamera") return tt;
                fallbackCam ??= c;
            }
            if (fallbackCam != null) return fallbackCam.targetTexture;

            if (_cameraController != null)
            {
                var rt = Get(_cameraController, "TargetTexture") as UnityEngine.RenderTexture;
                if (rt != null) return rt;
                var cg = Get(_cameraController, "InstantiatedCamera");
                rt = cg != null ? Get(cg, "targetTexture") as UnityEngine.RenderTexture : null;
                if (rt != null) return rt;
                var cam0 = Get(_cameraController, "sourceUnityCamera") as UnityEngine.Camera;
                if (cam0 != null && cam0.targetTexture != null) return cam0.targetTexture;
            }
            if (_cameraPosition != null)
            {
                var ucam = Get(_cameraPosition, "_unityCamera") as UnityEngine.Camera;
                if (ucam != null && ucam.targetTexture != null) return ucam.targetTexture;
            }
        }
        catch { }
        return null;
    }
    // Crop of the composited frame (fractions) excluding side/top/bottom UI. When the resolved RT is
    // already the 3D viewport it needs no crop, so the fixed fractions (tuned for one window size)
    // never clip the model or leak UI when the window is resized.
    public static UnityEngine.Rect PreviewCrop(int w, int h)
    {
        if (_previewIsViewport) return new UnityEngine.Rect(0, 0, w, h);
        int x = (int)(w * CropX);
        int cw = (int)(w * CropW);
        int ch = (int)(h * CropH);
        // RT origin is bottom-left; crop is measured from top
        int y = (int)(h * (1f - CropY - CropH));
        return new UnityEngine.Rect(x, y, cw, ch);
    }
    /// <summary>Return an RT that can be read with ReadPixels: a multi-sampled RT is blitted into a
    /// temporary single-sample RT first (sampling resolves MSAA). `tmp` must be released by the caller.</summary>
    private static UnityEngine.RenderTexture ResolveForRead(UnityEngine.RenderTexture rt, out UnityEngine.RenderTexture tmp)
    {
        tmp = null;
        try
        {
            if (rt != null && rt.antiAliasing > 1)
            {
                tmp = UnityEngine.RenderTexture.GetTemporary(rt.width, rt.height, 0, rt.format, UnityEngine.RenderTextureReadWrite.Default);
                UnityEngine.Graphics.Blit(rt, tmp);
                return tmp;
            }
        }
        catch (Exception e) { PluginLog.LogWarning($"msaa resolve failed: {e.Message}"); }
        return rt;
    }
    public static bool PreviewHasContent(UnityEngine.RenderTexture rt)
    {
        if (rt == null) return false;
        UnityEngine.RenderTexture resolveTmp = null;
        try
        {
            int s = 48;
            var cr = PreviewCrop(rt.width, rt.height);
            int x = Math.Max(0, (int)(cr.x + cr.width / 2 - s / 2));
            int y = Math.Max(0, (int)(cr.y + cr.height / 2 - s / 2));
            var readRt = ResolveForRead(rt, out resolveTmp);
            var prev = UnityEngine.RenderTexture.active;
            UnityEngine.RenderTexture.active = readRt;
            var t = new UnityEngine.Texture2D(s, s, UnityEngine.TextureFormat.RGB24, false);
            t.ReadPixels(new UnityEngine.Rect(x, y, s, s), 0, 0);
            t.Apply();
            var px = t.GetPixels32();
            UnityEngine.RenderTexture.active = prev;
            if (resolveTmp != null) UnityEngine.RenderTexture.ReleaseTemporary(resolveTmp);
            UnityEngine.Object.Destroy(t);
            var first = px[0];
            for (int i = 1; i < px.Length; i++)
                if (px[i].r != first.r || px[i].g != first.g || px[i].b != first.b) return true;
            return false;
        }
        catch { return false; }
    }
}
