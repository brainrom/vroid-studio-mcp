using System;
using System.Collections;
using System.Linq;

namespace VroidStudioMcp;

/// <summary>Camera control: reset/frame/angles/zoom/pan/rotate/projection/view/probe.</summary>
public static class CameraHandlers
{
    public static IEnumerator Camera(CommandDispatcher.Context ctx)
    {
        if (ctx.Args.Length == 0) { ctx.Error = "usage: camera <reset|angles|zoom|pan|rotate|projection|frame|view|probe> ..."; yield break; }
        var sub = ctx.Args[0].ToLowerInvariant();
        var known = new[] { "reset", "angles", "zoom", "pan", "rotate", "projection", "frame", "view", "probe", "ortho", "fov" };
        if (!known.Contains(sub)) { ctx.Error = $"unknown camera command '{sub}'"; yield break; }
        // Validate arguments up front so a missing/typo value is an error instead of a silent no-op.
        // Cap the magnitude: a huge finite value (e.g. 1e30, a typo) is otherwise accepted and pushed
        // into the engine transform.
        const float MaxMagnitude = 1e6f;
        bool HasFloat(int i) => i < ctx.Args.Length && ParamCodec.TryParseFinite(ctx.Args[i], out var v) && Math.Abs(v) <= MaxMagnitude;
        switch (sub)
        {
            case "angles":
            case "pan":
            case "view":
                if (!HasFloat(1) || !HasFloat(2)) { ctx.Error = $"camera {sub} needs two numbers in [-1e6,1e6]: x y"; yield break; }
                break;
            case "zoom":
            case "ortho":
            case "fov":
                if (!HasFloat(1)) { ctx.Error = $"camera {sub} needs a number in [-1e6,1e6]"; yield break; }
                break;
            case "rotate":
                if (!ArgParse.Int(CommandUtil.Arg(ctx, 1), "mode", out _, out var modeError, 0))
                { ctx.Error = $"camera rotate: {modeError}"; yield break; }
                break;
            case "frame":
                if (CommandUtil.Arg(ctx, 1) == null) { ctx.Error = "camera frame needs a region (body|face|head|upper|legs|feet) [angle]"; yield break; }
                if (!CameraApi.IsRegion(ctx.Args[1].ToLowerInvariant())) { ctx.Error = $"camera frame: unknown region '{ctx.Args[1]}'"; yield break; }
                if (ctx.Args.Length >= 3 && !CameraApi.IsAngle(ctx.Args[2]))
                { ctx.Error = $"camera frame: unknown angle '{ctx.Args[2]}' (front|back|left|right|threequarter_left|threequarter_right)"; yield break; }
                break;
        }
        // ApplyCamera validates/returns false on a bad (e.g. missing enum) request; surface it as an
        // error instead of silently reporting ok with stale state.
        if (!CameraApi.ApplyCamera(string.Join(" ", ctx.Args)))
        { ctx.Error = CameraApi.LastError ?? "camera failed"; yield break; }
        // Mutations (angles/rotate are coroutines, focus/view are direct) only reach the preview
        // RenderTexture after a frame; let it settle so a following screenshot in the same batch is
        // fresh, and so the returned state is the applied one.
        yield return CameraApi.WaitCameraIdle();
        ctx.Result = CameraApi.GetState();
    }
}
