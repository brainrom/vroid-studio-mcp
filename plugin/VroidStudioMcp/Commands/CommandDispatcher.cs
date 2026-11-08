using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VroidStudioMcp;

public sealed class CommandResult { public bool Ok; public object Result; public string Error; }

/// <summary>Transport-agnostic command core: named method + positional args → result (main thread).
/// Thin scheduler/registry; the actual work lives in per-domain <c>Commands/Handlers</c> classes.</summary>
public static class CommandDispatcher
{
    public sealed class Context { public string Method; public string[] Args; public object Result; public string Error; }
    private sealed class Pending { public Context Ctx; public IEnumerator Routine; public TaskCompletionSource<CommandResult> Tcs; }

    private static readonly ConcurrentQueue<Pending> Queue = new();
    // Number of commands waiting behind the in-flight one (read by MCP `status`, out of queue).
    public static int QueueDepth => Queue.Count;
    private static readonly Dictionary<string, Func<Context, IEnumerator>> Handlers = BuildHandlers();
    private static Pending _current;
    private static CoroutineDriver _driver;
    // In-flight command, for a background watchdog and for MCP `ping` (which is answered without the
    // main-thread queue, so it can still report why the editor is unresponsive).
    private static volatile string _currentMethod;
    private static DateTime _currentStarted;

    public static bool TryGetInFlight(out string method, out double seconds)
    {
        method = _currentMethod;
        seconds = method == null ? 0 : (DateTime.UtcNow - _currentStarted).TotalSeconds;
        return method != null;
    }

    /// <summary>Background thread: logs when a command has been in flight too long, so a wedged
    /// main thread is attributable to a specific command in LogOutput.log.</summary>
    public static void WatchdogLoop()
    {
        while (true)
        {
            System.Threading.Thread.Sleep(5000);
            try
            {
                if (TryGetInFlight(out var m, out var s) && s >= 15)
                    PluginLog.LogError($"[watchdog] command '{m}' in flight for {s:F0}s (main thread may be stuck)");
            }
            catch { }
        }
    }

    // Commands that operate on the currently edited model. Rejected up front when no model is open
    // (`_inEdit` false) so a stale edit-root/VM from a previous session is never touched - e.g. a
    // pathless `save` at the model select used to invoke the engine's Save on a stale VM.
    private static readonly HashSet<string> EditRequired = new()
    {
        "save", "export_avatar", "categories", "enter", "presets", "presets_full", "select_preset",
        "set_param", "label_set", "preset_preview_list", "preset_preview_range",
        "camera", "screenshot",
        "haircustom", "create_custom_preset",
        "import_custom_preset", "export_custom_preset",
    };

    public static Task<CommandResult> Submit(string method, string[] args)
    {
        var tcs = new TaskCompletionSource<CommandResult>();
        Queue.Enqueue(new Pending { Ctx = new Context { Method = (method ?? "").ToLowerInvariant(), Args = args ?? Array.Empty<string>() }, Tcs = tcs });
        return tcs.Task;
    }

    public static void Tick()
    {
        if (_current == null)
        {
            if (!Queue.TryDequeue(out _current)) return;
            _currentMethod = _current.Ctx.Method;
            _currentStarted = DateTime.UtcNow;
            if (_current.Ctx.Method.Length == 0) _current.Ctx.Error = "empty method";
            else if (NativeAvatarExport.Busy && _current.Ctx.Method != "state" && _current.Ctx.Method != "ping")
                _current.Ctx.Error = "a native avatar export is still running; wait for its native callback before submitting commands";
            else if (EditRequired.Contains(_current.Ctx.Method) && !VroidState._inEdit)
                _current.Ctx.Error = $"{_current.Ctx.Method}: no model open; call open or create first";
            else if (Handlers.TryGetValue(_current.Ctx.Method, out var handler))
            {
                try { _current.Routine = handler(_current.Ctx); } catch (Exception e) { _current.Ctx.Error = e.Message; }
            }
            else _current.Ctx.Error = $"unknown method '{_current.Ctx.Method}'";
            _driver = new CoroutineDriver();
            _driver.Start(Wrap(_current));
        }
        _driver.Tick();
        if (!_driver.Running)
        {
            if (_current.Ctx.Error == null && _driver.LastError != null) _current.Ctx.Error = _driver.LastError.Message;
            _current.Tcs.SetResult(new CommandResult { Ok = _current.Ctx.Error == null, Result = _current.Ctx.Result, Error = _current.Ctx.Error });
            _current = null;
            _currentMethod = null;
        }
    }

    private static IEnumerator Wrap(Pending pending) => pending.Routine ?? CommandUtil.Done();

    // Coroutine primitives re-exposed for the scheduler's own tests (see TimeScaleFuzzTest).
    internal static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds) => CommandUtil.WaitUntil(condition, timeoutSeconds);
    internal static IEnumerator Pause(float seconds) => CommandUtil.Pause(seconds);

    // ---------------- handler registry ----------------
    private static Dictionary<string, Func<Context, IEnumerator>> BuildHandlers() => new()
    {
        ["ping"] = ctx => { ctx.Result = "pong"; return CommandUtil.Done(); },
        ["state"] = SessionHandlers.State,
        ["sections"] = ctx => { ctx.Result = Sections.SectionList(); return CommandUtil.Done(); },
        ["open"] = SessionHandlers.Open,
        ["create"] = SessionHandlers.Create,
        ["test"] = SessionHandlers.Test,
        ["camera"] = CameraHandlers.Camera,
        ["screenshot"] = ScreenshotHandlers.Screenshot,
        ["save"] = SessionHandlers.Save,
        ["export_avatar"] = AvatarExportHandlers.Export,
        ["exit"] = SessionHandlers.Exit,
        ["main_menu"] = SessionHandlers.MainMenu,
        ["categories"] = CategoryHandlers.List,
        ["enter"] = CategoryHandlers.Enter,
        ["presets"] = PresetHandlers.List,
        ["presets_full"] = PresetHandlers.ListFull,
        ["preset_preview_list"] = PresetHandlers.PreviewList,
        ["preset_preview_range"] = PresetHandlers.PreviewRange,
        ["select_preset"] = PresetHandlers.Select,
        ["set_param"] = ParamHandlers.Set,
        ["labels"] = LabelHandlers.Labels,
        ["label_set"] = LabelHandlers.LabelSet,
        ["settingsprobe"] = SettingsHandlers.Probe,
        ["settingsdump"] = SettingsHandlers.Dump,
        ["settingsstate"] = SettingsHandlers.State,
        ["settingstoggle"] = SettingsHandlers.Toggle,
        ["settingsedit"] = SettingsHandlers.Edit,
        ["uiscreenshot"] = ScreenshotHandlers.UiScreenshot,
        ["i18ndump"] = I18nHandlers.Dump,
        ["i18nreload"] = I18nHandlers.Reload,
        ["i18nlang"] = I18nHandlers.Lang,
        ["renderprobe"] = RenderHandlers.Probe,
        ["rendermaasa"] = RenderHandlers.Msaa,
        ["renderscale"] = RenderHandlers.Scale,
        ["haircustom"] = PresetHandlers.Custom,
        ["create_custom_preset"] = PresetHandlers.CreateCustom,
        ["import_custom_preset"] = CustomItemHandlers.Import,
        ["export_custom_preset"] = CustomItemHandlers.Export,
        ["hook"] = DebugHandlers.Hook,
        ["unhook"] = DebugHandlers.Unhook,
        ["hooks"] = DebugHandlers.HooksList,
        ["memstat"] = DebugHandlers.MemStat,
        ["memclear"] = DebugHandlers.MemClear,
        ["memgc"] = DebugHandlers.MemGc,
    };

    // Test hooks: run a core handler directly on the calling coroutine (no dispatcher queue).
    public static IEnumerator TestCreate(Context ctx) => SessionHandlers.Create(ctx);
    public static IEnumerator TestPreviewList(Context ctx) => PresetHandlers.PreviewList(ctx);
    public static IEnumerator TestPreviewRange(Context ctx) => PresetHandlers.PreviewRange(ctx);
    public static IEnumerator TestSetParam(Context ctx) => ParamHandlers.Set(ctx);
    public static IEnumerator TestEnter(Context ctx) => CategoryHandlers.Enter(ctx);
    public static IEnumerator TestSelectPreset(Context ctx) => PresetHandlers.Select(ctx);

    /// <summary>Test context for <see cref="TestRun"/>.</summary>
    public static Context TestContext(string method, params string[] args)
        => new() { Method = method, Args = args ?? Array.Empty<string>() };

    /// <summary>Test hook: run ANY registered handler directly on the calling coroutine (no dispatcher
    /// queue, no EditRequired guard), so an in-plugin test can drive the public command surface.</summary>
    public static IEnumerator TestRun(Context ctx)
    {
        ctx.Method = (ctx.Method ?? "").ToLowerInvariant();
        return Handlers.TryGetValue(ctx.Method, out var handler) ? handler(ctx) : CommandUtil.Done();
    }
}
