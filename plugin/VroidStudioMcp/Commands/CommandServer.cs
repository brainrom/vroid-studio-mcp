using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace VroidStudioMcp;

/// <summary>
/// TCP front for CommandDispatcher on 127.0.0.1:17890. A line starting with '{' is treated as
/// JSON-RPC 2.0 (params = array); any other line is a plain text command ("enter Face "Irises"").
/// </summary>
public static class CommandServer
{
    public const int Port = 17890;

    private static TcpListener _listener;
    private static volatile bool _running;

    public static void Start()
    {
        if (!ServerSettings.Enabled) { PluginLog.LogInfo("command server: disabled"); return; }
        if (!ServerSettings.TryParse(ServerSettings.TcpAddress, out var host, out var port))
        { PluginLog.LogError($"command server: bad address '{ServerSettings.TcpAddress}'"); return; }
        try
        {
            var ip = ResolveHost(host);
            _listener = new TcpListener(ip, port);
            _listener.Start();
            _running = true;
            new Thread(() => AcceptLoop(_listener)) { IsBackground = true }.Start();
            PluginLog.LogInfo($"command server: {host}:{port} (text + json-rpc)");
        }
        catch (Exception e) { PluginLog.LogError($"command server failed: {e.Message}"); }
    }

    public static void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public static void Restart() { Stop(); Start(); }

    private static IPAddress ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var addrs = Dns.GetHostAddresses(host);
        return addrs.Length > 0 ? addrs[0] : IPAddress.Loopback;
    }

    public static void Tick() => CommandDispatcher.Tick();

    private static void AcceptLoop(TcpListener listener)
    {
        while (_running)
        {
            try
            {
                var client = listener.AcceptTcpClient();
                new Thread(() => ClientLoop(client)) { IsBackground = true }.Start();
            }
            catch (Exception e) { if (_running) PluginLog.LogError($"accept: {e.Message}"); else break; }
        }
    }

    private static void ClientLoop(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var clean = Clean(line);
                if (clean.Length == 0) continue;
                writer.WriteLine(clean[0] == '{' ? JsonRpc(clean)
                                 : clean[0] == '[' ? Batch(clean)
                                 : Text(clean));
            }
        }
        catch (Exception e) { PluginLog.LogError($"client: {e.Message}"); }
        finally { try { client.Close(); } catch { } }
    }

    private static string Clean(string line)
    {
        var sb = new StringBuilder();
        // Drop control characters only; keep the full Unicode range so non-ASCII paths and labels
        // survive the line protocol.
        foreach (var c in line) if (c != '\t' && !char.IsControl(c)) sb.Append(c);
        return sb.ToString().Trim();
    }

    // ---------------- text ----------------
    private static string Text(string line)
    {
        var tokens = Tokenize(line);
        var result = CommandDispatcher.Submit(tokens.Count > 0 ? tokens[0] : "", tokens.Skip(1).ToArray()).GetAwaiter().GetResult();
        var payload = result.Ok
            ? new Dictionary<string, object> { ["ok"] = true, ["result"] = result.Result }
            : new Dictionary<string, object> { ["ok"] = false, ["error"] = result.Error };
        return MiniJson.Serialize(payload, false);
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && char.IsWhiteSpace(c)) { if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    // ---------------- batch ----------------
    // A JSON array of items executed sequentially on the main thread, one response per item.
    // Item may be a plain command string ("enter Face Irises") or {method,params:[...]}.
    // If items look like JSON-RPC requests (have "jsonrpc"/"method"), answers are JSON-RPC.
    private static string Batch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.EnumerateArray().ToList();
            bool rpc = items.Any(it => it.ValueKind == JsonValueKind.Object
                                       && (it.TryGetProperty("jsonrpc", out _) || it.TryGetProperty("id", out _)));
            var results = new List<object>();
            foreach (var item in items)
            {
                object id = null;
                string method; string[] args;
                if (item.ValueKind == JsonValueKind.String)
                {
                    var toks = Tokenize(item.GetString());
                    method = toks.Count > 0 ? toks[0] : ""; args = toks.Skip(1).ToArray();
                }
                else
                {
                    if (item.TryGetProperty("id", out var idEl)) id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : idEl.GetString();
                    method = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    args = ReadParams(item);
                }
                // Tolerate an argv-style list that repeats the method name as the first element
                // (e.g. {"method":"camera","args":["camera","angles",...]}).
                if (args.Length > 0 && string.Equals(args[0], method, StringComparison.OrdinalIgnoreCase))
                    args = args.Skip(1).ToArray();
                var r = CommandDispatcher.Submit(method, args).GetAwaiter().GetResult();
                results.Add(rpc
                    ? (r.Ok ? ResponseObj(id, r.Result) : (object)ErrorObj(id, -32000, r.Error))
                    : (object)(r.Ok
                        ? new Dictionary<string, object> { ["ok"] = true, ["result"] = r.Result }
                        : new Dictionary<string, object> { ["ok"] = false, ["error"] = r.Error }));
            }
            return MiniJson.Serialize(results, false);
        }
        catch (Exception e) { return MiniJson.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = $"batch: {e.Message}" }, false); }
    }

    private static string[] ReadParams(JsonElement obj)
    {
        var args = new List<string>();
        JsonElement p;
        // Accept both keys: TCP historically used "params"; the MCP batch uses "args". Be liberal so
        // the same {method, args} object works on either channel.
        if (!obj.TryGetProperty("params", out p) && !obj.TryGetProperty("args", out p)) return args.ToArray();
        if (p.ValueKind == JsonValueKind.Array)
            foreach (var item in p.EnumerateArray()) args.Add(ArgValue(item));
        else if (p.ValueKind == JsonValueKind.Object)
            foreach (var prop in p.EnumerateObject()) args.Add(ArgValue(prop.Value));
        return args.ToArray();
    }

    private static string ArgValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => null,
        _ => v.GetRawText(),
    };

    // ---------------- json-rpc ----------------
    private static string JsonRpc(string line)
    {
        object id = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl)) id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : idEl.GetString();
            var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
            var args = new List<string>();
            if (root.TryGetProperty("params", out var p))
            {
                if (p.ValueKind == JsonValueKind.Array)
                    foreach (var item in p.EnumerateArray()) args.Add(item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText());
                else if (p.ValueKind == JsonValueKind.Object)
                    foreach (var prop in p.EnumerateObject()) args.Add(prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText());
            }
            var result = CommandDispatcher.Submit(method, args.ToArray()).GetAwaiter().GetResult();
            return result.Ok ? Response(id, result.Result) : ErrorResponse(id, -32000, result.Error);
        }
        catch (Exception e) { return ErrorResponse(id, -32700, e.Message); }
    }

    private static Dictionary<string, object> ResponseObj(object id, object result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static Dictionary<string, object> ErrorObj(object id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = message } };

    private static string Response(object id, object result) => MiniJson.Serialize(ResponseObj(id, result), false);

    private static string ErrorResponse(object id, int code, string message) => MiniJson.Serialize(ErrorObj(id, code, message), false);
}
