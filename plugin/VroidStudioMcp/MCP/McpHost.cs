using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace VroidStudioMcp;

/// <summary>
/// Embedded MCP server (Streamable HTTP, JSON-RPC 2.0) on 127.0.0.1 via HttpListener.
/// Tools translate named arguments into CommandDispatcher calls (same core as telnet).
/// Self-contained: no external dependencies beyond the .NET 6 BCL (System.Text.Json, HttpListener).
/// </summary>
public static class McpHost
{
    public const int Port = 17891;
    private const string ProtocolVersion = "2025-06-18";
    private static readonly HashSet<string> KnownProtocols = new(StringComparer.Ordinal)
        { "2024-11-05", "2025-03-26", "2025-06-18" };

    private static HttpListener _listener;
    private static volatile bool _running;
    private static bool _loopbackOnly = true;

    // Progress of the MCP `batch` currently executing, for the out-of-queue `status` tool. volatile:
    // written on the batch's handler thread, read on the status handler thread.
    private static volatile int _batchIndex;
    private static volatile int _batchTotal;
    private static volatile string _batchMethod;

    private const int MaxBatchImages = 12;
    private const long MaxBatchImageBytes = 8L * 1024 * 1024;

    // Host OS as seen by the MCP client (which runs on the same machine): under Wine the editor
    // runs on a Unix host, otherwise on Windows. Told to the agent so it uses the right path style.
    private static readonly bool HostIsUnix = WinePaths.IsWine;
    private static readonly string HostPathExample = HostIsUnix ? "/home/user/model.vroid" : "C:\\Users\\user\\model.vroid";
    private static readonly string PathHint = HostIsUnix
        ? "PATHS: this editor runs on a Unix host - use POSIX paths (e.g. /home/user/model.vroid). open/save return POSIX paths."
        : "PATHS: this editor runs on Windows - use Windows paths (e.g. C:\\Users\\user\\model.vroid). open/save return Windows paths.";

    // Global session contract only. Feature specifics live in the owning tool's description and in
    // help(topic); this keeps the always-loaded instructions short and avoids duplication/drift.
    private const string Instructions =
        "vroid-studio-mcp drives a running VRoid Studio 2.14 editor through its own internal API. " +
        "The editor, plugin and this endpoint are already running; you do NOT launch anything.\n\n" +
        "WORKFLOW: open(path) or create(gender) -> sections() -> categories(section) -> enter(section, category) -> set_param/select_preset.\n" +
        "Sections are stable UI labels: Face, Hairstyle, Body, Outfit. A category is a tab inside a section. " +
        "Section and category names are CASE-SENSITIVE: use them exactly as returned by sections()/categories(). " +
        "enter returns its parameters as one list (numeric and colors), each with a stable global 'path' (Section/Category/Name, " +
        "or Section/Name for global hair colours) " +
        "and a short 'index' valid only for the category you just entered (re-enter to refresh). Shared section colours are " +
        "returned separately under 'globals'. Prefer the path; " +
        "set_param also accepts the short name or the index. By default enter also frames the camera on the category's " +
        "subject (view='keep' to leave it; the applied view is reported as 'view').\n" +
        "Numeric/combined values are floats passed as strings (clamped to the parameter's [min,max]; the result carries " +
        "clamped:true when adjusted); colors take '#RRGGBB' or 'r,g,b' (0..1). Presets are addressed by index from presets(); " +
        "the clear entry is named '(clear)'.\n\n" +
        "SEMANTICS: every mutating call is BLOCKING for the client and runs on the editor's main thread; it resolves by waiting " +
        "for the engine rebuild signal, so it may take a few seconds. Do not poll screenshots while a change is applied (that starves " +
        "the rebuild); take one after it settles. Prefer chunking large batches instead of launching one huge batch (large " +
        "batches take long and may not return). If a batch call times out, call status to see whether it is still running " +
        "rather than blindly retrying. Errors come back as isError=true with a human-readable message " +
        "(on the TCP channel: {ok:false,error}).\n\n" +
        "HELP: the tool descriptions are intentionally concise. If ANY semantics are unclear - how a category's parameters behave, " +
        "item-specific Outfit sliders, clear presets, colours, camera, values, sessions - call help(topic) before guessing; " +
        "it is the detailed, authoritative reference. help() with no topic lists the topics.\n\n" +
        "TOOLS: state, status, sections, open, create, categories, enter, presets, preset_preview_list, preset_preview_range, select_preset, " +
        "create_custom_preset, set_param, camera, screenshot, save, main_menu, exit, batch, help. " +
        "Use batch for several ordered operations + captures in one call (it returns captured frames inline and accepts any " +
        "tool name, including help; each op takes named `arguments`, the same keys as tools/call). status is answered without " +
        "the main-thread queue, so it works while a command or batch runs. " +
        "(ui_screenshot is a dev-only tool, disabled when dev_tools=false in the servers config.)\n";

    // Deep reference for help(topic). Kept out of the always-loaded instructions and tool blurbs.
    private static readonly Dictionary<string, string> HelpTopics = BuildHelp();

    private static Dictionary<string, string> BuildHelp()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["outfit"] =
                "OUTFIT\n" +
                "- A clothing category's parameters belong to the currently applied item, not to the tab. Every item exposes its " +
                "own slider set; sizes vary widely (a few to well over a hundred) and some items also carry body/bodysuit fit sliders, " +
                "so the list can change completely when you switch items. There is no stable per-tab schema: re-read enter after " +
                "changing the item and do not cache the parameter list across items.\n" +
                "- enter returns 'item' (the applied item's display name) so you know whose sliders these are. With no item applied, " +
                "parameters is empty and needs_preset=true. Tip: select_preset(..., params=true) returns the fresh parameter list " +
                "in the same call, so switching an item does not need a separate enter.\n" +
                "- Whole Sets is preset-only and has no sliders of its own: choosing one applies a complete outfit across the " +
                "other slots (Tops/Bottoms/Dress/...). To fit it, select the item in its slot tab and tune its sliders there.\n" +
                "- Duplicate slider labels within one item come from different mesh parts and are disambiguated with a '#N' suffix on " +
                "the path (e.g. 'Volume #1'). Address them by full path or by index.\n" +
                "- Slots are independent: Tops and Bottoms coexist; Dresses (Onepiece) is mutually exclusive with them (selecting a " +
                "dress clears top/bottom and vice versa). Each slot keeps its own item, so enter Tops after selecting Bottoms still " +
                "describes the top.",
            ["presets"] =
                "PRESETS\n" +
                "- presets(section, category, [source]) -> [{id,index,name,kind}] in display order, one continuous list. " +
                "id == index is the value for select_preset/preset_preview_*; kind is 'custom' | 'preset' | 'clear'. Custom " +
                "presets (the user's saved items) come first, then the stock presets; a category may have neither. source " +
                "(default 'all', stateless) filters: 'presets' (stock only) or 'custom' (custom only). name is a human label " +
                "when known, otherwise the internal id. The clear entry has kind 'clear'/name '(clear)'; its id is NOT fixed.\n" +
                "- select_preset(section, category, id) blocks until applied and returns the applied preset's name or 'cleared'. Use the " +
                "kind 'clear' entry to clear; id 0 may be a real preset or a custom item. With params=true it instead returns an object " +
                "{name, category, kind, has_clear_preset, parameters, globals?, item?} - the same payload as enter - so Outfit can " +
                "fetch the new item's sliders without a second call.\n" +
                "- Custom presets are the user's own saved items (Custom sub-tab), applied exactly like a stock preset. They are " +
                "global (saved on disk) and show up in every model. Create one, named, with " +
                "create_custom_preset(section, category, name) -> {id,name}; it is an empty template until it is\n" +
                "designed in the hair design editor.\n" +
                "- has_clear_preset (in categories/enter): true means the tab has a Clear slot - selecting it " +
                "removes the element entirely. When such a tab is currently empty, enter returns no parameters and " +
                "needs_preset=true; after choosing any real preset the parameter set is identical for every preset. " +
                "The flag does not imply needs_preset (a tab can have a Clear slot and still be populated). false means " +
                "there is no Clear slot, so a preset is always applied and replaces all parameter values.\n" +
                "- Previews: preset_preview_list / preset_preview_range render thumbnails by id (custom items included); pick " +
                "candidates by name first. Fidelity varies: outfit/hair/eye previews reflect the actual preset, but some face " +
                "overlays (lips/lashes/cheeks) render on a generic template head, and every preview carries the editor's fixed " +
                "lighting/self-shadow (e.g. a pink neck shading) that is NOT a parameter and cannot be removed.",
            ["hair"] =
                "HAIR\n" +
                "- Overall hair colour is two global dials (Main / Highlight) shared by every Hairstyle tab except Base Hair. " +
                "They use the stable section-level paths 'Hairstyle/Main' and 'Hairstyle/Emission' (no tab in the path) and are " +
                "returned under enter's separate 'globals' field (not in 'parameters'). Address them from any non-Base hair tab, e.g. " +
                "set_param Hairstyle <tab> \"Hairstyle/Main\". " +
                "The colour panel is enabled by the Level-0 'Hairstyle Sets' preset (group presets alone do not re-enable it); once " +
                "active, the colours work on any non-Base tab, even one without its own preset. Only a fully empty Hairstyle section " +
                "gives a 'needs a preset' error.\n" +
                "- There are no numeric hair parameters in this API. Hairstyle tabs expose only those colours; the hair-shape " +
                "sliders live in the editor's separate Base Hair design panel and are not surfaced.\n" +
                "- Base Hair is special: it has its own single colour (group 'Main Color', path 'Hairstyle/Base Hair/Main Color') " +
                "and does not expose the global Main/Highlight dials.",
            ["colors"] =
                "COLOURS\n" +
                "- Format: '#RRGGBB' or 'r,g,b' in 0..1. A colour parameter exposes 'name' and 'group' in addition to path/label.\n" +
                "- A symmetric colour whose two sides differ (e.g. Iris 'Both Eyes') is reported with value=null, because a single " +
                "readout would lie (the aggregate mirrors one side). You can still set it by name/label; it applies to both sides.\n" +
                "- Colours flagged 'global':true are one shared value addressed from several tabs; enter lists them under the " +
                "separate 'globals' field (not in 'parameters'). Hair: " +
                "'Hairstyle/Main' and 'Hairstyle/Emission' (any non-Base Hairstyle tab). Skin base: one accessor writes " +
                "FaceSkin/BodySkin/NoseLighting/Eyelid, so it is exposed once as 'Skin/Color' (plus 'Skin/Dark Color' / " +
                "'Skin/Light Color') on any Face/Body tab that shows it - setting it there sets all of them. 'global' means " +
                "shared across those tabs, not across every section.",
            ["values"] =
                "VALUES\n" +
                "- Parameter 'kind' is 'float' | 'combined' | 'color'. Numeric values are floats passed as strings; both kinds " +
                "read back rounded to 3 decimals (matching the UI).\n" +
                "- min/max are the UI slider range, not the model's valid domain (preset defaults can sit outside it). set_param " +
                "clamps to the union of [min,max] and the current value - so it never moves an existing out-of-range default, " +
                "while still rejecting clearly bogus input - and adds clamped:true when it adjusted the value.",
            ["tabs"] =
                "TABS\n" +
                "- 'Face Sets' is an aggregate tab: it exposes the same underlying parameters as the individual Face tabs. " +
                "enter() reports each numeric parameter's stable 'key' (the engine's transferable key); the SAME key seen in " +
                "different tabs is the SAME parameter, e.g. 'Face/Face Sets/Eye Size X' and 'Face/Eyeliner/Eye Size X' are one " +
                "value - setting one is visible in the other. The same applies to Face Sets vs Eyeliner/Eyebrows/Eyelids/Skin " +
                "(eye shape, contour, ears).\n" +
                "- Selecting a preset in a Face/Body tab REPLACES the values of every key that preset owns, including keys also " +
                "shown in other tabs. There is no per-tab isolation (the engine stores one key); use the individual tab when you " +
                "mean a specific element, and expect a preset to overwrite the affected keys.\n" +
                "- 'Expression Editor' exists in the editor tree but is not editable through this API: it is not listed by " +
                "categories() and enter() rejects it as an unknown tab ('no tab').",
            ["camera"] =
                "CAMERA\n" +
                "- Actions: reset | frame | angles | zoom | pan | rotate | projection | view | probe.\n" +
                "- frame <region> [angle]: the primary inspection tool. region (the orbit/rotation center AND framing) is " +
                "body|face|head|upper|legs|feet; angle (optional, default front) is front|back|left|right|" +
                "threequarter_left|threequarter_right (left/right are from the viewer's side when facing the model; " +
                "threequarter = 45 degrees). E.g. frame face right = face close-up in profile; frame body front = " +
                "full-body front. It re-aims from the model's front reference and sets the distance to frame the region.\n" +
                "- angles: x=yaw (180=front), y=pitch (0=level); it ORBITS the current center and KEEPS the current " +
                "distance/pan (it does not re-frame), so use frame when you want a new region. view: x=centre fraction " +
                "of height (0=feet,1=head), y=distance fraction (manual calibration). zoom: positive zooms in. rotate: " +
                "CameraRotationMode enum index. angles/zoom/pan are auxiliary fine-tuning after a frame.\n" +
                "- reset restores the initial framing (angles, pan AND zoom). Framing uses avatar bounds, so frame regions work for any model.\n" +
                "- Every action blocks until the change is visible and returns the resulting state " +
                "{angles:{yaw,pitch}, distance, position, rotation, projection, fov}. Missing/invalid arguments are errors.\n" +
                "- enter applies a category-appropriate view by default (Face -> face, back hair -> head from behind, Shoes -> feet, " +
                "Tops -> upper, Body/Bottoms/Dresses -> body) and reports it as 'view' ('keep' when the camera was left alone); " +
                "pass view='keep' to keep your own framing, view='reset' for the initial view, or an explicit <region>[:<angle>].\n" +
                "- Projection is 'sticky': only the 'projection' action (a toggle) changes it; 'reset' restores the projection " +
                "captured when the model was opened; frame/angles/zoom/pan/view leave it alone.\n" +
                "- frame and the explicit enter view re-aim from the FRONT reference, so they also bring yaw back to front; " +
                "call camera angles afterwards (or pass an angle) if you need another viewpoint.",
            ["sessions"] =
                "SESSIONS\n" +
                "- open(path) and create(gender) are only valid from the model select and BLOCK until the model is ready. If a model is " +
                "open, first save then main_menu (keep), or main_menu (discard). Unsaved edits are never discarded implicitly.\n" +
                "- Model-dependent tools (categories, enter, presets, select_preset, set_param, save, camera, screenshot, " +
                "label_set, preset_preview_*) require an open model; at the model select they return an error ('no model open') " +
                "instead of touching stale session state. ui_screenshot is the exception: it works without a model.\n" +
                "- Section and category names are CASE-SENSITIVE: pass them exactly as listed by sections()/categories().\n" +
                "- save: with a path it is save-as; without a path it saves in place only if the model already has a file (a freshly " +
                "created model has none, so pass a path).\n" +
                "- main_menu returns to the model select without quitting. exit quits the application (mode save|discard); " +
                "for save it waits until the in-place save finishes (dirty flag cleared) before quitting.",
            ["paths"] = "PATHS\n- " + PathHint,
        };
    }



    private sealed class Tool
    {
        public string Name;
        public string Description;
        public string[] Order;                    // positional args for CLI-style translation
        public string[] Required;
        public Dictionary<string, object> Props;  // JSON-Schema per property
    }

    private static readonly Dictionary<string, Tool> Tools = BuildTools();

    public static void Start()
    {
        if (!ServerSettings.Enabled) { PluginLog.LogWarning("mcp: disabled"); return; }
        if (!ServerSettings.TryParse(ServerSettings.McpAddress, out var host, out var port))
        { PluginLog.LogError($"mcp: bad address '{ServerSettings.McpAddress}'"); return; }
        try
        {
            var prefixHost = (host == "0.0.0.0" || host == "*" || host == "+") ? "+" : host;
            _loopbackOnly = IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
            var prefix = $"http://{prefixHost}:{port}/mcp/";
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _listener.IgnoreWriteExceptions = true;
            _listener.Start();
            _running = true;
            new Thread(() => AcceptLoop(_listener)) { IsBackground = true }.Start();
            PluginLog.LogWarning($"mcp: {prefix}");
        }
        catch (Exception e) { PluginLog.LogError($"mcp start failed: {e.Message}"); }
    }

    public static void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public static void Restart() { Stop(); Start(); }

    private static void AcceptLoop(HttpListener listener)
    {
        while (_running)
        {
            HttpListenerContext context;
            try { context = listener.GetContext(); }
            catch (Exception e) { if (_running) PluginLog.LogError($"mcp accept: {e.Message}"); else break; continue; }
            new Thread(() => Handle(context)) { IsBackground = true }.Start();
        }
    }

    private static void Handle(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            response.Headers["Access-Control-Allow-Origin"] = "*";
            // Force connection close: keep-alive under Wine's HttpListener leaks the accepted
            // connection, and after ~140 sequential requests the listener stops accepting entirely.
            response.KeepAlive = false;
            var request = context.Request;

            // Wine's http.sys binds the socket on 0.0.0.0; enforce loopback at the request level
            // only when the configured endpoint is itself loopback.
            var remote = request.RemoteEndPoint?.Address;
            if (_loopbackOnly && remote != null && !System.Net.IPAddress.IsLoopback(remote))
            {
                response.StatusCode = 403;
                response.ContentLength64 = 0;
                response.Close();
                return;
            }

            if (request.HttpMethod == "OPTIONS")
            {
                response.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
                response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Mcp-Session-Id, MCP-Protocol-Version";
                response.StatusCode = 204;
                response.ContentLength64 = 0;
                response.Close();
                return;
            }

            if (request.HttpMethod == "DELETE") { response.StatusCode = 200; response.ContentLength64 = 0; response.Close(); return; }

            if (request.HttpMethod != "POST")
            {
                // We don't offer server-initiated SSE; per spec that's allowed (405).
                response.StatusCode = 405;
                response.ContentLength64 = 0;
                response.Close();
                return;
            }

            string body;
            using (var reader = new System.IO.StreamReader(request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
            string reply; string sessionId;
            try { reply = Process(body, out sessionId); }
            catch (JsonException e) { sessionId = null; reply = Error(null, -32700, "Parse error: " + e.Message); }
            response.ContentType = "application/json";
            if (sessionId != null) response.Headers["Mcp-Session-Id"] = sessionId;
            if (reply == null) { response.StatusCode = 202; response.ContentLength64 = 0; response.Close(); return; }
            // Explicit Content-Length: without it the runtime uses chunked encoding, and under Wine
            // the terminating chunk is not flushed on a non-keep-alive connection (clients hang) and
            // connections leak until the listener stops accepting (~140 sequential requests).
            var bytes = Encoding.UTF8.GetBytes(reply);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }
        catch (Exception e)
        {
            PluginLog.LogError($"mcp handler: {e.Message}");
            try { response.StatusCode = 500; response.ContentLength64 = 0; response.Close(); } catch { }
        }
    }

    // Returns null for notifications (no response body).
    private static string Process(string body, out string sessionId)
    {
        sessionId = null;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        // Streamable HTTP dropped JSON-RPC batching; a top-level array (or any non-object) is a
        // caller error, not a server fault. Refuse gracefully with -32600 instead of throwing (which
        // used to surface as an empty HTTP 500). The MCP `batch` tool is the supported alternative.
        if (root.ValueKind != JsonValueKind.Object)
            return Error(null, -32600, root.ValueKind == JsonValueKind.Array
                ? "JSON-RPC batch requests are not supported; use the MCP 'batch' tool"
                : "invalid request: expected a JSON-RPC object");
        // A wrong-typed `id`/`method` is an invalid request, not a server fault: validate kinds before
        // touching them (JsonElement.GetString()/TryGetProperty throw on non-string/non-object).
        bool hasId = root.TryGetProperty("id", out var idEl);
        // JSON-RPC notifications (no id) must never receive a body, even if otherwise malformed.
        if (!hasId) return null;
        if (idEl.ValueKind != JsonValueKind.String && idEl.ValueKind != JsonValueKind.Number && idEl.ValueKind != JsonValueKind.Null)
            return Error(null, -32600, "invalid request: 'id' must be a string, number or null");
        object id = IdValue(idEl);
        if (root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind != JsonValueKind.String)
            return Error(id, -32600, "invalid request: 'method' must be a string");
        var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
        var parameters = root.TryGetProperty("params", out var p) ? p : default;

        if (method == "initialize")
        {
            sessionId = Guid.NewGuid().ToString("N");
            var requested = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null;
            return Result(id, new Dictionary<string, object>
            {
                ["protocolVersion"] = requested != null && KnownProtocols.Contains(requested) ? requested : ProtocolVersion,
                ["capabilities"] = new Dictionary<string, object> { ["tools"] = new Dictionary<string, object>() },
                ["serverInfo"] = new Dictionary<string, object> { ["name"] = "vroid-studio-mcp", ["version"] = "1.0.0" },
                ["instructions"] = Instructions + "\n" + PathHint + "\n",
            });
        }

        if (method == "notifications/initialized" || method == "notifications/cancelled") return null;
        if (method == "ping")
        {
            // Answered without the main-thread queue, so it can report why the editor is unresponsive.
            var info = new Dictionary<string, object>();
            if (CommandDispatcher.TryGetInFlight(out var inflight, out var secs) && secs > 3)
            {
                info["inflight"] = inflight;
                info["inflight_seconds"] = Math.Round(secs, 1);
            }
            return Result(id, info);
        }
        if (method == "tools/list") return Result(id, new Dictionary<string, object> { ["tools"] = Tools.Values.Where(t => ServerSettings.DevTools || t.Name != "ui_screenshot").Select(ToolSchema).ToList() });
        if (method == "tools/call") return Result(id, CallTool(parameters));

        return Error(id, -32601, $"method not found: {method}");
    }

    private static object CallTool(JsonElement parameters)
    {
        // params/name/arguments may be wrong-typed; validate kinds so a bad request is a clean error,
        // never an exception (which would surface as HTTP 500).
        bool isObj = parameters.ValueKind == JsonValueKind.Object;
        if (!isObj && parameters.ValueKind != JsonValueKind.Undefined && parameters.ValueKind != JsonValueKind.Null)
            return Content("'params' must be an object", true);
        string name = null;
        if (isObj && parameters.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
        JsonElement a = default;
        if (isObj && parameters.TryGetProperty("arguments", out var rawArgs))
        {
            if (rawArgs.ValueKind != JsonValueKind.Object && rawArgs.ValueKind != JsonValueKind.Null)
                return Content("'arguments' must be an object", true);
            if (rawArgs.ValueKind == JsonValueKind.Object) a = rawArgs;
        }
        var args = a;
        if (name == null || !Tools.ContainsKey(name))
            return Content($"unknown tool '{name}'", true);

        // MCP-only tools have no TCP core counterpart and are answered here.
        if (name == "batch") return CallBatch(args);
        if (name == "help") return CallHelp(args);
        if (name == "status") return CallStatus();

        if (!TryBuildCall(name, args, false, out var method, out var list, out var error))
            return Content(error, true);
        var result = CommandDispatcher.Submit(method, list.ToArray()).GetAwaiter().GetResult();
        if (!result.Ok) return Content(result.Error, true);

        // Image-returning tools rebuild their MCP content block after the engine call.
        if (name == "screenshot") return ScreenshotContent(ArgAt(list, 0) ?? "inline");
        if (name == "ui_screenshot") return UiScreenshotContent(ArgAt(list, 0) ?? "inline");
        if (name == "preset_preview_list" || name == "preset_preview_range")
            return PreviewContent(result, ArgAt(list, name == "preset_preview_list" ? 3 : 4) ?? "inline");
        return Content(result.Result == null ? "ok" : MiniJson.Serialize(result.Result, false), false);
    }

    private static string ArgAt(List<string> list, int i) =>
        list != null && i >= 0 && i < list.Count && !string.IsNullOrEmpty(list[i]) ? list[i] : null;

    // Named arguments (object) -> the positional form of the command core. Missing required arguments
    // are always an error; unknown keys are an error only in the strict (batch) mode - a direct
    // tools/call keeps tolerating extra/odd arguments (the long-standing read-only contract).
    private static List<string> PositionalArgs(Tool tool, JsonElement args, bool strict, out string error)
    {
        error = null;
        if (args.ValueKind == JsonValueKind.Undefined) args = default;
        if (args.ValueKind != JsonValueKind.Object)
        { error = $"{tool.Name}: arguments must be an object of named arguments"; return null; }
        if (strict)
            foreach (var prop in args.EnumerateObject())
                if (!tool.Order.Contains(prop.Name))
                { error = $"{tool.Name}: unknown argument '{prop.Name}' (expected: {string.Join(", ", tool.Order)})"; return null; }
        foreach (var req in tool.Required)
            if (!args.TryGetProperty(req, out var rv) || rv.ValueKind == JsonValueKind.Null)
            { error = $"{tool.Name}: missing required argument '{req}' (expected: {string.Join(", ", tool.Order)})"; return null; }
        var list = new List<string>();
        foreach (var key in tool.Order)
        {
            if (args.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null) list.Add(Scalar(v));
            else list.Add(null);
        }
        return list;
    }

    // Resolve a tool call to (method, positional args) - shared by tools/call and batch, so both accept
    // the same named arguments and validate them identically. A few tools have a positional shape that
    // is not their schema (camera, screenshot, previews) and are built here.
    private static bool TryBuildCall(string name, JsonElement args, bool strict, out string method, out List<string> list, out string error)
    {
        method = null; list = null; error = null;
        if (!Tools.TryGetValue(name, out var tool)) { error = $"unknown tool '{name}'"; return false; }
        if (name == "ui_screenshot" && !ServerSettings.DevTools)
        { error = "ui_screenshot is disabled (dev_tools=false in the servers config)"; return false; }
        switch (name)
        {
            case "camera": return BuildCamera(args, out method, out list, out error);
            case "screenshot": return BuildScreenshot(args, out method, out list, out error);
            case "ui_screenshot": return BuildUiScreenshot(args, out method, out list, out error);
            case "preset_preview_list": return BuildPreviewList(args, out method, out list, out error);
            case "preset_preview_range": return BuildPreviewRange(args, out method, out list, out error);
            default:
                list = PositionalArgs(tool, args, strict, out error);
                if (list == null) return false;
                method = name;
                return true;
        }
    }

    private static string Val(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? Scalar(v) : null;

    private static bool BuildCamera(JsonElement args, out string method, out List<string> list, out string error)
    {
        method = "camera"; list = null; error = null;
        var action = Val(args, "action");
        if (string.IsNullOrEmpty(action))
        { error = "camera: 'action' is required (reset|frame|angles|zoom|pan|rotate|projection|view|probe)"; return false; }
        list = new List<string> { action };
        switch (action)
        {
            // frame takes a region and an optional angle; `region` is the canonical key, `preset` is
            // accepted as a legacy alias.
            case "frame":
                list.Add(Val(args, "region") ?? Val(args, "preset") ?? "body");
                var angle = Val(args, "angle");
                if (!string.IsNullOrEmpty(angle)) list.Add(angle);
                break;
            case "angles":
            case "pan":
            case "view": list.Add(Val(args, "x")); list.Add(Val(args, "y")); break;
            case "zoom":
            case "rotate": list.Add(Val(args, "value")); break;
        }
        return true;
    }

    private static bool BuildScreenshot(JsonElement args, out string method, out List<string> list, out string error)
    {
        method = "screenshot"; error = null;
        list = new List<string> { Val(args, "mode") ?? "inline", Val(args, "view") };
        return true;
    }

    private static bool BuildUiScreenshot(JsonElement args, out string method, out List<string> list, out string error)
    {
        method = "uiscreenshot"; error = null;
        list = new List<string> { Val(args, "mode") ?? "inline" };
        return true;
    }

    // Preset thumbnails as inline images (presets have no human-readable names). `indexes` is a
    // curated list chosen from presets(); the range form renders a consecutive run.
    private static bool BuildPreviewList(JsonElement args, out string method, out List<string> list, out string error)
    {
        method = "preset_preview_list"; list = null; error = null;
        var section = Val(args, "section");
        var category = Val(args, "category");
        if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(category))
        { error = "preset_preview_list: 'section' and 'category' are required"; return false; }
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("indexes", out var idx) || idx.ValueKind == JsonValueKind.Null)
        { error = "preset_preview_list: 'indexes' (array of preset indexes) is required"; return false; }

        var parts = new List<string>();
        if (idx.ValueKind == JsonValueKind.Array)
            foreach (var e in idx.EnumerateArray()) parts.Add(Scalar(e));
        else if (idx.ValueKind == JsonValueKind.String)
            parts.Add(idx.GetString());
        else { error = "preset_preview_list: 'indexes' must be an array of integers"; return false; }
        if (parts.Count == 0) { error = "preset_preview_list: 'indexes' is empty"; return false; }

        var mode = Val(args, "mode") ?? "inline";
        list = new List<string> { section, category, string.Join(",", parts), mode };
        return true;
    }

    private static bool BuildPreviewRange(JsonElement args, out string method, out List<string> list, out string error)
    {
        method = "preset_preview_range"; list = null; error = null;
        var section = Val(args, "section");
        var category = Val(args, "category");
        if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(category))
        { error = "preset_preview_range: 'section' and 'category' are required"; return false; }
        list = new List<string> { section, category, Val(args, "start") ?? "0", Val(args, "count") ?? "1", Val(args, "mode") ?? "inline" };
        return true;
    }

    // help(topic) -> deep reference. No topic (or an unknown one) returns the topic index, without
    // erroring, so a caller can recover and pick a topic.
    private static object CallHelp(JsonElement args) => Content(HelpText(Val(args, "topic")), false);

    // status -> liveness/progress WITHOUT the main-thread queue (safe to call while a mutation is
    // running, unlike state/commands). Lets a client tell whether a timed-out batch is still working.
    private static object CallStatus() => Content(MiniJson.Serialize(StatusDictionary(), false), false);

    private static Dictionary<string, object> StatusDictionary()
    {
        var d = new Dictionary<string, object>();
        if (CommandDispatcher.TryGetInFlight(out var method, out var secs) && secs > 3)
        {
            d["inflight"] = method;
            d["inflight_seconds"] = Math.Round(secs, 1);
        }
        d["queued"] = CommandDispatcher.QueueDepth;
        var total = _batchTotal;
        if (total > 0)
            d["batch"] = new Dictionary<string, object> { ["index"] = _batchIndex, ["total"] = total, ["method"] = _batchMethod };
        return d;
    }

    // Shared by the help tool and by batch (help has no TCP command).
    private static string HelpText(string topic)
    {
        if (string.IsNullOrEmpty(topic))
            return "help topics: " + string.Join(", ", HelpTopics.Keys) + ". Call help('<topic>') for the detailed reference.";
        if (HelpTopics.TryGetValue(topic.Trim(), out var text))
        {
            // The rotate enum values are resolved from the engine at runtime, so append them here
            // rather than baking them into the static help text.
            if (string.Equals(topic.Trim(), "camera", StringComparison.OrdinalIgnoreCase))
            {
                var modes = CameraApi.RotationModesText();
                if (!string.IsNullOrEmpty(modes)) text += "\n- rotate mode indices: " + modes + ".";
            }
            return text;
        }
        return $"unknown help topic '{topic}'. topics: " + string.Join(", ", HelpTopics.Keys);
    }

    // mode=inline returns the preview as an MCP image block (no file); file writes screenshot.png
    // only; both does both. Default is inline, since a visual check rarely needs a file.
    private static object ScreenshotContent(string mode)
    {
        bool wantImage = mode == "inline" || mode == "both";
        var png = wantImage ? Screenshot.LastCanonicalPng : null;
        var content = new List<object>();
        if (png != null && png.Length > 0)
        {
            content.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = $"{PngWidth(png)}x{PngHeight(png)}, {png.Length} bytes" + (mode == "both" ? " (screenshot.png)" : "") });
            content.Add(new Dictionary<string, object> { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" });
        }
        else content.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = mode == "file" ? "screenshot.png written" : "screenshot captured but no image data" });
        return new Dictionary<string, object> { ["content"] = content, ["isError"] = false };
    }

    private static int PngWidth(byte[] b) => b.Length > 24 ? (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19] : 0;
    private static int PngHeight(byte[] b) => b.Length > 24 ? (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23] : 0;

    // Full window including UI (via ScreenCapture); runs on the main thread through the dispatcher.
    private static object UiScreenshotContent(string mode)
    {
        var png = Screenshot.LastUiPng;
        if (png == null || png.Length == 0) return Content("ui_screenshot captured but no image data", true);
        var content = new List<object>();
        content.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = $"{PngWidth(png)}x{PngHeight(png)}, {png.Length} bytes" + (mode == "file" || mode == "both" ? " (ui_screenshot.png)" : "") });
        if (mode != "file")
            content.Add(new Dictionary<string, object> { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" });
        return new Dictionary<string, object> { ["content"] = content, ["isError"] = false };
    }

    private static object PreviewContent(CommandResult result, string mode)
    {
        if (!result.Ok) return Content(result.Error, true);
        var content = new List<object>();
        content.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = MiniJson.Serialize(result.Result, false) });
        if (mode == "inline" || mode == "both")
        {
            foreach (var p in Icons.LastPreviews)
            {
                content.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = $"[{p.Index}] {p.Name}" });
                content.Add(new Dictionary<string, object> { ["type"] = "image", ["data"] = Convert.ToBase64String(p.Png), ["mimeType"] = "image/png" });
            }
        }
        return new Dictionary<string, object> { ["content"] = content, ["isError"] = false };
    }


    // Batch operations take NAMED arguments only (`arguments` object, or an `args` object alias); a JSON
    // array is rejected with a clear error, so the old positional guessing cannot silently mis-apply.
    private static object CallBatch(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("operations", out var ops) || ops.ValueKind != JsonValueKind.Array)
            return Content("batch: 'operations' must be an array of {method, arguments}", true);

        int total = ops.GetArrayLength();
        if (ServerSettings.MaxBatchOps > 0 && total > ServerSettings.MaxBatchOps)
            return Content($"batch: {total} operations exceeds the configured limit of {ServerSettings.MaxBatchOps} " +
                           "(raise max_batch_ops in the servers config, or split into smaller batches)", true);

        var results = new List<object>();
        // Inline images captured from image-returning ops (screenshot/ui_screenshot/previews); capped
        // so one batch cannot return a huge payload.
        var images = new List<(int op, string label, byte[] png)>();
        long imageBytes = 0;
        int index = -1;
        _batchTotal = total;
        try
        {
            foreach (var op in ops.EnumerateArray())
            {
                index++;
                string method = op.ValueKind == JsonValueKind.Object && op.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                _batchIndex = index + 1;
                _batchMethod = method;

                if (method == null)
                { results.Add(OpError("batch: each operation needs a string 'method'")); continue; }

                // `arguments` (preferred, MCP idiom) or an `args` object alias; positional arrays are refused.
                JsonElement a = default;
                bool hasArgs = false;
                if (op.TryGetProperty("arguments", out var ag) && ag.ValueKind != JsonValueKind.Null) { a = ag; hasArgs = true; }
                else if (op.TryGetProperty("args", out var ag2) && ag2.ValueKind != JsonValueKind.Null) { a = ag2; hasArgs = true; }
                if (hasArgs && a.ValueKind != JsonValueKind.Object)
                { results.Add(OpError($"batch: '{method}' arguments must be an object of named arguments; positional args are not supported")); continue; }

                if (method == "batch") { results.Add(OpError("batch: cannot nest batch")); continue; }
                if (method == "ping") { results.Add(OkResult("pong")); continue; }
                if (method == "help")
                { results.Add(OkResult(HelpText(Val(a, "topic")))); continue; }
                if (method == "status")
                { results.Add(OkResult(StatusDictionary())); continue; }
                if (!Tools.ContainsKey(method))
                { results.Add(OpError($"unknown method '{method}'")); continue; }

                if (!TryBuildCall(method, a, true, out var buildMethod, out var list, out var berr))
                { results.Add(OpError(berr)); continue; }
                var result = CommandDispatcher.Submit(buildMethod, list.ToArray()).GetAwaiter().GetResult();
                results.Add(result.Ok ? OkResult(result.Result) : OpError(result.Error));
                if (result.Ok) CollectBatchImages(method, list, index, images, ref imageBytes);
            }
        }
        finally { _batchTotal = 0; _batchIndex = 0; _batchMethod = null; }

        // Keep the per-op JSON array as the first text block (the long-standing contract), then append
        // the captured images with an [op N] marker so a client can associate each frame.
        var content = new List<object>
        {
            new Dictionary<string, object> { ["type"] = "text", ["text"] = MiniJson.Serialize(results, false) },
        };
        foreach (var im in images)
        {
            content.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = $"[op {im.op}] {im.label}" });
            content.Add(new Dictionary<string, object> { ["type"] = "image", ["data"] = Convert.ToBase64String(im.png), ["mimeType"] = "image/png" });
        }
        return new Dictionary<string, object> { ["content"] = content, ["isError"] = false };
    }

    private static Dictionary<string, object> OkResult(object result) =>
        new() { ["ok"] = true, ["result"] = result };

    private static Dictionary<string, object> OpError(string message) =>
        new() { ["ok"] = false, ["error"] = message };

    // Capture inline images produced by image-returning ops in a batch, honouring an explicit mode.
    private static void CollectBatchImages(string method, List<string> args, int opIndex, List<(int op, string label, byte[] png)> images, ref long bytes)
    {
        switch (method)
        {
            case "screenshot":
            {
                var mode = args.Count > 0 && !string.IsNullOrEmpty(args[0]) ? args[0] : "inline";
                if (mode == "file") return;
                AddBatchImage(images, ref bytes, opIndex, "screenshot", Screenshot.LastCanonicalPng);
                break;
            }
            case "ui_screenshot":
            {
                var mode = args.Count > 0 && !string.IsNullOrEmpty(args[0]) ? args[0] : "inline";
                if (mode == "file") return;
                AddBatchImage(images, ref bytes, opIndex, "ui_screenshot", Screenshot.LastUiPng);
                break;
            }
            case "preset_preview_list":
            case "preset_preview_range":
            {
                int mi = method == "preset_preview_list" ? 3 : 4;
                var mode = args.Count > mi && !string.IsNullOrEmpty(args[mi]) ? args[mi] : "inline";
                if (mode != "inline" && mode != "both") return;
                foreach (var p in Icons.LastPreviews)
                    AddBatchImage(images, ref bytes, opIndex, $"preview[{p.Index}] {p.Name}", p.Png);
                break;
            }
        }
    }

    private static void AddBatchImage(List<(int op, string label, byte[] png)> images, ref long bytes, int op, string label, byte[] png)
    {
        if (png == null || png.Length == 0) return;
        if (images.Count >= MaxBatchImages || bytes + png.Length > MaxBatchImageBytes) return;
        images.Add((op, label, png));
        bytes += png.Length;
    }
    private static object Content(string text, bool isError) => new Dictionary<string, object>
    {
        ["content"] = new List<object> { new Dictionary<string, object> { ["type"] = "text", ["text"] = text } },
        ["isError"] = isError,
    };

    private static object ToolSchema(Tool tool)
    {
        var props = new Dictionary<string, object>();
        foreach (var kv in tool.Props)
        {
            var schema = kv.Value as Dictionary<string, object> ?? new Dictionary<string, object>();
            if (!schema.ContainsKey("type")) schema["type"] = "string";
            props[kv.Key] = schema;
        }
        // The rotate mode mapping is a runtime enum; surface it in the schema so a client knows the
        // valid `value`s without guessing.
        if (tool.Name == "camera" && props.TryGetValue("value", out var v) && v is Dictionary<string, object> vd)
        {
            var modes = CameraApi.RotationModesText();
            if (!string.IsNullOrEmpty(modes))
                vd["description"] = "Value for zoom, or the CameraRotationMode index for rotate (" + modes + ").";
        }
        return new Dictionary<string, object>
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["inputSchema"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = tool.Required,
            },
        };
    }

    private static object IdValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.String => el.GetString(),
        _ => null,
    };

    private static string Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => null,
        _ => v.GetRawText(),
    };

    private static string Result(object id, object result) =>
        MiniJson.Serialize(new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }, false);

    private static string Error(object id, int code, string message) =>
        MiniJson.Serialize(new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = message } }, false);

    private static Dictionary<string, Tool> BuildTools()
    {
        // property schema helpers
        static Dictionary<string, object> P(string type, string desc) =>
            new() { ["type"] = type, ["description"] = desc };
        static Dictionary<string, object> EnumSchema(string desc, params string[] vals) =>
            new() { ["type"] = "string", ["description"] = desc, ["enum"] = vals.ToList() };

        var sections = new[] { "Face", "Hairstyle", "Body", "Outfit" };
        var camActions = new[] { "reset", "frame", "angles", "zoom", "pan", "rotate", "projection", "view", "probe" };
        var camRegions = new[] { "body", "face", "head", "upper", "legs", "feet" };
        var camAngles = new[] { "front", "back", "left", "right", "threequarter_left", "threequarter_right" };

        Tool T(string name, string desc, string[] order, string[] required, Dictionary<string, object> props) =>
            new() { Name = name, Description = desc, Order = order, Required = required, Props = props };

        var list = new List<Tool>
        {
            T("state",
              "Report editor readiness. Returns {ready, updating, selectors, in_edit, section}. " +
              "in_edit=true while a model is being edited (false at the model select / before any model); " +
              "ready=true once the model has finished building; updating=true while a rebuild is in flight; " +
              "selectors is the number of loaded category selectors (0 outside edit; >=4 once an editable model is open); " +
              "section is the current UI section label or null. Read-only, no side effects.",
              Array.Empty<string>(), Array.Empty<string>(), new()),

            T("sections",
              "List editable sections. Returns [{id,title}] where id is the stable label to pass to " +
              "categories/enter: Face, Hairstyle, Body, Outfit. Accessories/Look are out of scope. Read-only.",
              Array.Empty<string>(), Array.Empty<string>(), new()),

            T("open",
              "Open a .vroid model file. BLOCKS until the model is fully built and ready (can take several seconds): " +
              "when it returns, state() is already meaningful and the editor can be queried right away - do NOT poll " +
              "state() after open. path: a host path (e.g. " + HostPathExample + "). Returns the resolved " +
              "host path. " +
              "Only valid from the model select (like create): if a model is open, first call save then " +
              "main_menu (keep), or main_menu (discard), then open. Unsaved edits are never discarded implicitly.",
              new[] { "path" }, new[] { "path" },
              new() { ["path"] = P("string", "Path to a .vroid file (e.g. " + HostPathExample + ").") }),

            T("create",
              "Create a fresh model from the built-in Male/Female template (the choice shown by the editor's " +
              "'Create New Model' dialog). gender is required: 'female' or 'male'. BLOCKS until the model is fully " +
              "built and ready, exactly like open (do NOT poll state() after it). Only valid from the model select: " +
              "if a model is open, first call save then main_menu (keep), or main_menu (discard), then create. " +
              "Returns {gender, variant}.",
              new[] { "gender" }, new[] { "gender" },
              new() { ["gender"] = EnumSchema("Template to create.", "female", "male") }),

            T("categories",
              "List the category tabs of a section. section: one of Face, Hairstyle, Body, Outfit. " +
              "Returns {section, categories:[{name,kind,params,colors,presets,has_clear_preset}]}: `section` is the section the " +
              "editor is now on (this call switches to it). kind is 'parametric' when the tab exposes " +
              "any adjustable parameter (numeric or color), else 'preset-only'. params/colors/presets are counts, correct " +
              "before the tab is entered (no warm-up needed). Outfit is the exception: clothing sliders belong to the " +
              "currently selected item (the property panel), not the tab, so Outfit tabs read 'preset-only' here while " +
              "enter() returns those sliders once an item is applied. " +
              "has_clear_preset=true means the tab has a Clear slot that removes the element entirely; it is not " +
              "the same as needs_preset (see enter). " +
              "Side effect: switches the editor to that section (reported back as `section`). Section names are case-sensitive; " +
              "tab names are listed exactly as returned.",
              new[] { "section" }, new[] { "section" },
              new() { ["section"] = EnumSchema("Section label.", sections) }),

            T("enter",
              "Open a category and return its parameters. Returns {category, kind, parameters:[{index,path,label,kind,value,min,max}], " +
              "globals?}. " +
              "parameter kind is 'float' | 'combined' | 'color'. Colors additionally expose {name,group}; a symmetric " +
              "color whose two sides differ (e.g. Iris 'Both Eyes') reports value=null (it can still be set). " +
              "'globals' lists the section's shared colours (hair Main/Emission, the shared skin base) separately from the " +
              "category's own parameters; they are addressed by their portable Section-level path with set_param from any tab. " +
              "If the category is currently empty (has_clear_preset slot active) and no real preset is chosen yet, " +
              "parameters is empty and needs_preset=true is returned - select a preset first; the parameter set is then " +
              "identical for every preset. " +
              "Use the stable global 'path' (Section/Category/Name; global hair colours use Section/Name) with set_param; " +
              "it survives re-entry, while the " +
              "short 'index' is only valid until you re-enter the category or the model changes. Names/labels are also accepted. " +
              "For Outfit the parameters belong to the currently applied clothing item, not to the tab: every item " +
              "exposes its own set (sizes vary widely, some include body/bodysuit fit sliders), and the result carries " +
              "'item' with the applied item's name. Switch the item with select_preset; with no item applied " +
              "the list is empty and needs_preset=true. " +
              "view (default 'auto') also frames the camera on the category's subject and reports it as 'view' " +
              "(e.g. face -> close-up, back hair -> rear view); pass 'keep' to leave the camera untouched, 'reset' " +
              "for the initial view, or an explicit <region>[:<angle>] (region body|face|head|upper|legs|feet, optional " +
              "angle front|back|left|right|threequarter_left|threequarter_right). " +
              "Side effect: selects the category tab and (blocking) waits for the preview to settle. " +
              "Section/category names are case-sensitive.",
              new[] { "section", "category", "view" }, new[] { "section", "category" },
              new()
              {
                  ["section"] = EnumSchema("Section label.", sections),
                  ["category"] = P("string", "Category/tab name from categories()."),
                  ["view"] = P("string", "Camera framing: auto (default), keep, reset, or <region>[:<angle>] (body|face|head|upper|legs|feet, optional front|back|left|right|threequarter_left|threequarter_right)."),
              }),

            T("presets",
              "List the presets of a category as one continuous list. Returns [{id,index,name,kind}] " +
              "in display order, where id == index is the value to pass to select_preset / " +
              "preset_preview_*; kind is 'custom' | 'preset' | 'clear'. Custom presets (the user's saved " +
              "items) come first, followed by the stock presets; a category may have neither. source " +
              "(optional, default 'all') filters the result without changing any state: 'all' (custom + " +
              "stock), 'presets' (stock only), 'custom' (custom only). 'name' is a human-readable label " +
              "when known, otherwise the internal id; the empty/Clear slot is named '(clear)'. Read-only.",
              new[] { "section", "category", "source" }, new[] { "section", "category" },
              new()
              {
                  ["section"] = EnumSchema("Section label.", sections),
                  ["category"] = P("string", "Category/tab name from categories()."),
                  ["source"] = EnumSchema("Filter (stateless): all (default) | presets | custom.", "all", "presets", "custom"),
              }),

            T("preset_preview_list",
              "Return preview thumbnails for a curated list of presets as inline images. Read presets() first " +
              "(it gives each entry an index and a human label when known), pick the most promising ones, then pass " +
              "their indexes here. 'indexes' is required: an array of preset indexes from presets() (numbers only, " +
              "max 12 per call, duplicates are collapsed; an out-of-range index fails the whole call). " +
              "Each returned item carries {index,name} (the same name as in presets), so no separate join is needed. " +
              "mode='inline' (default) returns image blocks with no files; 'file' writes preset_<i>.png; 'both' does both. " +
              "Blocking: each preview is generated, so responses take a few seconds per preset. " +
              "Fidelity varies: outfit/hair/eye previews reflect the actual preset, but some Face overlays " +
              "(lips/lashes/cheeks) render on a generic template head, not the current model.",
              new[] { "section", "category", "indexes", "mode" }, new[] { "section", "category", "indexes" },
              new()
              {
                  ["section"] = EnumSchema("Section label.", sections),
                  ["category"] = P("string", "Category/tab name from categories()."),
                  ["indexes"] = new Dictionary<string, object>
                  {
                      ["type"] = "array",
                      ["description"] = "Preset indexes from presets() to render (max 12; clear index has no preview).",
                      ["items"] = new Dictionary<string, object> { ["type"] = "integer" },
                  },
                  ["mode"] = new Dictionary<string, object>
                  {
                      ["type"] = "string",
                      ["description"] = "inline (default, no file) | file | both.",
                      ["enum"] = new List<object> { "inline", "file", "both" },
                      ["default"] = "inline",
                  },
              }),

            T("preset_preview_range",
              "Return preview thumbnails of a consecutive run of a category's presets as inline images. " +
              "start = preset index from presets(); count = how many consecutive presets to return " +
              "(1..12, default 1; any index past the category's presets, or count > 12, is an error - nothing is trimmed). " +
              "Each returned item carries {index,name} (the same name as in presets), so no separate join is needed. " +
              "mode='inline' (default) returns image blocks with no files; 'file' writes preset_<i>.png; 'both' does both. " +
              "Prefer preset_preview_list when you already know which presets you want. Blocking. " +
              "Fidelity varies: outfit/hair/eye previews reflect the actual preset, but some Face overlays " +
              "(lips/lashes/cheeks) render on a generic template head, not the current model.",
              new[] { "section", "category", "start", "count", "mode" }, new[] { "section", "category" },
              new()
              {
                  ["section"] = EnumSchema("Section label.", sections),
                  ["category"] = P("string", "Category/tab name from categories()."),
                  ["start"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "First preset index from presets() (default 0).", ["minimum"] = 0, ["default"] = 0 },
                  ["count"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "How many consecutive previews (1..12, default 1).", ["minimum"] = 1, ["maximum"] = 12, ["default"] = 1 },
                  ["mode"] = new Dictionary<string, object>
                  {
                      ["type"] = "string",
                      ["description"] = "inline (default, no file) | file | both.",
                      ["enum"] = new List<object> { "inline", "file", "both" },
                      ["default"] = "inline",
                  },
              }),

            T("select_preset",
              "Select a preset by the id from presets() (custom and stock share one id space). The clear/reset " +
              "entry is the one whose kind is 'clear'/name '(clear)' (its id is not fixed). Blocks until the " +
              "change is applied. Returns the applied preset's name (the same 'name' as in presets; or 'cleared'). " +
              "Set params=true to instead return an object {name, category, kind, has_clear_preset, parameters, item?} " +
              "- the same payload as enter() - so the new parameter set is available immediately (useful for Outfit, where the " +
              "applied item's sliders change on every selection and would otherwise need a re-enter). " +
              "Prefer this over manual group manipulation.",
              new[] { "section", "category", "index", "params" }, new[] { "section", "category", "index" },
              new()
              {
                  ["section"] = EnumSchema("Section label.", sections),
                  ["category"] = P("string", "Category/tab name from categories()."),
                  ["index"] = P("integer", "Preset id from presets() (custom and stock share one list); the clear entry has kind 'clear'."),
                  ["params"] = P("boolean", "When true, also return the refreshed parameter list (like enter()). Default false returns just the preset's name."),
              }),

            T("create_custom_preset",
              "Create a new custom preset (a Custom sub-tab item, the user's own reusable preset) with the given " +
              "name, in one call. Custom presets are global (saved on disk) and appear in every model; they list, " +
              "preview and apply exactly like a stock preset (see presets/select_preset). A freshly created item is " +
              "an empty template (it has no strand geometry yet) until it is sculpted. Returns " +
              "{section,category,id,name,kind:'custom',renamed}: id is the preset's id in presets() (custom first). " +
              "Creating does not apply the preset to the model; select it with select_preset when you want to use it. Blocking.",
              new[] { "section", "category", "name" }, new[] { "section", "category", "name" },
              new()
              {
                  ["section"] = EnumSchema("Section label.", sections),
                  ["category"] = P("string", "Category/tab name from categories()."),
                  ["name"] = P("string", "Meaningful identifier for the new custom preset (shown by presets())."),
              }),

            T("set_param",
              "Set one parameter of a category. Address it by its stable global 'path' from enter() " +
              "(e.g. 'Face/Irises/L Eye'), or by its short name/label (numeric: 'Iris Size (X)'; colors: 'L Eye', 'Main Color'), " +
              "or by its numeric 'index' (valid only for the category you just entered). " +
              "value: a float as a string for 'float'/'combined', or a color as '#RRGGBB' (or 'r,g,b' in 0..1) for 'color'. " +
              "Blocks until applied. Returns {index,label,value}.",
              new[] { "section", "category", "index", "value" }, new[] { "section", "category", "index", "value" },
              new()
              {
                  ["section"] = EnumSchema("Section label.", sections),
                  ["category"] = P("string", "Category/tab name from categories()."),
                  ["index"] = P("string", "Global path from enter() (e.g. \"Face/Irises/L Eye\"), or a short name/label (e.g. \"L Eye\", \"Iris Size (X)\"), or a numeric index as a string."),
                  ["value"] = new Dictionary<string, object>
                  {
                      ["description"] = "Float for numeric parameters, or '#RRGGBB' / 'r,g,b' (0..1) for color.",
                      ["oneOf"] = new List<object> { new Dictionary<string, object> { ["type"] = "number" }, new Dictionary<string, object> { ["type"] = "string" } },
                  },
              }),

            T("camera",
              "Control the editor camera. action is required:\n" +
              "  reset | frame | angles | zoom | pan | rotate | projection | view | probe\n" +
              "  frame   -> region: " + string.Join("|", camRegions) + "; optional angle: " + string.Join("|", camAngles) +
              " (left/right = viewer's side when facing the model). Primary inspection tool: frames the region (its orbit " +
              "center and distance) at the angle.\n" +
              "  angles  -> x=yaw, y=pitch (degrees; yaw 180 = front, pitch 0 = level); orbits the current center and KEEPS " +
              "the current distance/pan (does not re-frame)\n" +
              "  pan     -> x,y\n" +
              "  zoom    -> value (positive zooms in)\n" +
              "  rotate  -> value (CameraRotationMode enum index)\n" +
              "  view    -> x=center fraction of height (0=feet,1=head), y=distance fraction (manual calibration)\n" +
              "  reset/projection/probe -> no extra args; reset restores the initial framing (angles, pan and zoom)\n" +
              "Framing uses avatar bounds, so frame regions work for any model. Every action blocks until the change " +
              "is applied and returns the resulting state {angles:{yaw,pitch}, distance, position, rotation, projection, fov}. " +
              "Requires an open model. Missing/invalid arguments are errors.",
              Array.Empty<string>(), new[] { "action" },
              new()
              {
                  ["action"] = EnumSchema("Camera action.", camActions),
                  ["region"] = EnumSchema("Region for action=frame (the orbit center and framing).", camRegions),
                  ["angle"] = EnumSchema("Optional angle for action=frame (default front).", camAngles),
                  ["x"] = P("number", "First value: yaw for angles (180=front), X for pan/view."),
                  ["y"] = P("number", "Second value: pitch for angles (0=level), Y for pan/view."),
                  ["value"] = P("number", "Value for zoom/rotate."),
              }),

            T("screenshot",
              "Capture the 3D preview (no UI). mode='inline' (default) returns it as an MCP image content " +
              "block (base64 PNG) with no file written; mode='file' writes screenshot.png in the app root " +
              "and returns only text; mode='both' does both. Blocking; call only after a change has settled, " +
              "never in a polling loop (it starves the rebuild). Requires an open model.",
              new[] { "mode" }, Array.Empty<string>(),
              new()
              {
                  ["mode"] = new Dictionary<string, object>
                  {
                      ["type"] = "string",
                      ["description"] = "inline (default, no file) | file | both.",
                      ["enum"] = new List<object> { "inline", "file", "both" },
                      ["default"] = "inline",
                  },
              }),

            T("ui_screenshot",
              "Capture the whole window INCLUDING UI (dialogs/panels), not just the 3D preview. " +
              "Returns it inline as an MCP image content block (default) with no file written; " +
              "mode='file' writes ui_screenshot.png only; 'both' does both. Useful to inspect the " +
              "settings dialog and other native UI. Blocking. Available without an open model.",
              new[] { "mode" }, Array.Empty<string>(),
              new()
              {
                  ["mode"] = new Dictionary<string, object>
                  {
                      ["type"] = "string",
                      ["description"] = "inline (default, no file) | file | both.",
                      ["enum"] = new List<object> { "inline", "file", "both" },
                      ["default"] = "inline",
                  },
              }),

            T("save",
              "Save the model (blocking, ~few seconds). With 'path' it does a save-as to that file (no dialog) and " +
              "returns the resolved host path. Without 'path' it saves in place and returns 'saved' - but only if " +
              "the model already has a file; for a freshly created (unsaved) model it fails with an error instead of " +
              "opening the editor's native save dialog, so pass a path then. path is a host path (e.g. " + HostPathExample + ").",
              new[] { "path" }, Array.Empty<string>(),
              new() { ["path"] = P("string", "Optional target .vroid path for save-as (e.g. " + HostPathExample + ").") }),

            T("main_menu",
              "Return to the main menu without quitting; clears the unsaved flag so no dialog is shown.",
              Array.Empty<string>(), Array.Empty<string>(), new()),

            T("help",
              "Detailed reference for a topic, when the concise tool descriptions are not enough. Call it whenever the semantics " +
              "are unclear (Outfit item sliders, clear presets, hair colours, colours/values, camera, sessions, paths). " +
              "help() with no topic lists the available topics. It is the authoritative reference.",
              new[] { "topic" }, Array.Empty<string>(),
              new() { ["topic"] = P("string", "Topic (optional): outfit, presets, hair, colors, values, camera, sessions, paths.") }),

            T("status",
              "Liveness/queue probe answered WITHOUT the main-thread queue, so it works while a mutation or batch is " +
              "still running (unlike state/commands). Returns {inflight?, inflight_seconds?, queued, batch?}: the " +
              "command currently executing (when it has run >3s), how many commands are queued behind it, and the " +
              "position of an in-progress batch ({index,total,method}). Use it after a batch call times out to tell " +
              "whether it is still applying, instead of retrying. Read-only, no side effects, no model required.",
              Array.Empty<string>(), Array.Empty<string>(), new()),

            T("exit",
              "Quit the application. mode: 'save' or 'discard' (default 'discard'). " +
              "Ends the session and stops the MCP/TCP servers; do not expect further calls to succeed. " +
              "Unsaved-changes dialog is bypassed.",
              new[] { "mode" }, Array.Empty<string>(),
              new() { ["mode"] = EnumSchema("Whether to save before quitting.", "save", "discard") }),

            T("batch",
              "Run several operations sequentially in one call. operations is an array of {method, arguments}: method is " +
              "any tool name (including help), and arguments is an OBJECT of named arguments using exactly the same keys as " +
              "that tool's inputSchema (e.g. {\"method\":\"enter\",\"arguments\":{\"section\":\"Face\",\"category\":\"Irises\"}}, " +
              "{\"method\":\"hair_group_add\",\"arguments\":{\"category\":\"Front\",\"from\":\"look/fringe\",\"name\":\"Bangs\"}}). " +
              "Positional arrays are NOT supported: an array in `arguments` is rejected, an unknown key or a missing required " +
              "argument is an error, so a call can never silently land on the wrong parameter. Array-valued arguments (e.g. " +
              "preset_preview_list indexes) are passed as JSON arrays inside `arguments`. " +
              "Returns one {ok,result|error} per operation, in order, followed by the frames captured by image-returning " +
              "ops (screenshot/ui_screenshot/previews) as inline images with an [op N] marker. Use this for the visual " +
              "loop: mutate + move the camera + screenshot in one call. Large batches of mutating ops can take a long " +
              "time; prefer chunking them, and if a batch call times out, call status to see whether it is still running " +
              "instead of blindly retrying. Blocks until all finish.",
              Array.Empty<string>(), new[] { "operations" },
              new()
              {
                  ["operations"] = new Dictionary<string, object>
                  {
                      ["type"] = "array",
                      ["description"] = "Operations to run in order.",
                      ["items"] = new Dictionary<string, object>
                      {
                          ["type"] = "object",
                          ["properties"] = new Dictionary<string, object>
                          {
                              ["method"] = P("string", "Tool/command name."),
                              ["arguments"] = new Dictionary<string, object>
                              {
                                  ["type"] = "object",
                                  // Named arguments, same keys as the tool's inputSchema (array-valued
                                  // arguments such as preset_preview_list `indexes` stay JSON arrays).
                                  ["additionalProperties"] = true,
                                  ["description"] = "Named arguments, the same keys as the corresponding tool's inputSchema.",
                              },
                          },
                          ["required"] = new List<object> { "method" },
                      },
                  },
              }),
        };
        return list.ToDictionary(t => t.Name, t => t);
    }
}
