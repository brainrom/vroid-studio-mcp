using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace VroidStudioMcp;

/// <summary>
/// Persisted configuration for the two control services (raw TCP command socket + MCP HTTP
/// endpoint). Stored as JSON in the BepInEx config folder. Changes are applied live by
/// restarting the affected listener.
/// </summary>
public static class ServerSettings
{
    public const string DefaultTcp = "127.0.0.1:17890";
    public const string DefaultMcp = "127.0.0.1:17891";

    internal static bool Enabled { get; private set; } = true;
    internal static string TcpAddress { get; private set; } = DefaultTcp;
    internal static string McpAddress { get; private set; } = DefaultMcp;
    // Exposes developer-only MCP tools (currently ui_screenshot: full window incl. native UI).
    internal static bool DevTools { get; private set; } = false;
    // Verbose runtime diagnostics in the log (e.g. PARAM[EDIT/REFRESH]); no files written.
    internal static bool DebugArtifacts { get; private set; } = false;
    // Writes diagnostic artifacts to the app root (catalog_*.json, category_catalog.json,
    // catalog_params.json, ui_events.jsonl, param_trace.log). Only needed when re-verifying a new
    // editor build; off by default so the working folder stays clean.
    internal static bool DumpArtifacts { get; private set; } = false;
    // Devtool for dialog tracking: traces every engine dialog the plugin sees (DialogProviderViewModel
    // Show/ShowNotification/Dismiss) and every custom-item save prompt that was NOT intercepted, to
    // <app root>/dialog_trace.log. Independent of the two artifact flags (it is neither a catalog dump
    // nor verbose param logging); off by default.
    internal static bool DialogTrace { get; private set; } = false;
    // Hard upper bound on one `batch` call's operation count (0 = unlimited). A guard against absurd
    // requests; the tight bound for the MCP client's request timeout is chunking + status, not this.
    internal static int MaxBatchOps { get; private set; } = 200;
    // Anti-aliasing for the 3D preview: MSAA samples on the preview RenderTexture (0/1 = off,
    // 2/4/8 = MSAA). The engine builds these RTs single-sampled, so the plugin rebuilds them.
    internal static int MsaaSamples { get; private set; } = 4;
    // Force anisotropic filtering on all textures (fixes the shimmering on the collar/zipper/etc.).
    internal static bool AnisoFiltering { get; private set; } = true;
    // Supersampling factor for the preview RenderTexture (1 = off, 2 = 2x2). The compositor stretches
    // the RT onto the UI panel, so rendering larger and letting it downscale antialiases thin features
    // (the MToon outline) that MSAA alone leaves shimmering.
    internal static float RenderScale { get; private set; } = 2f;

    // Live updates from the settings UI (validated; apply/restart the affected listener on change).
    internal static void SetEnabled(bool value) { Enabled = value; Apply(); }
    // Runtime MSAA switch (dev command). New RenderTextures use the new sample count; the engine
    // recreates the preview RT on retarget/resize, so the change lands without a restart.
    internal static void SetMsaaSamples(int value) { MsaaSamples = Math.Clamp(value, 0, 8); Save(); }
    internal static void SetRenderScale(float value) { RenderScale = Math.Clamp(value, 1f, 4f); Save(); }
    internal static bool SetTcpAddress(string value)
    {
        if (!TryParse(value, out _, out _)) return false;
        TcpAddress = value.Trim(); Apply(); return true;
    }
    internal static bool SetMcpAddress(string value)
    {
        if (!TryParse(value, out _, out _)) return false;
        McpAddress = value.Trim(); Apply(); return true;
    }

    public static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "com.kokokoshka.vroidstudiomcp.servers.json");

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) { Save(); return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;
            if (root.TryGetProperty("enabled", out var en) && (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False))
                Enabled = en.GetBoolean();
            if (root.TryGetProperty("tcp", out var tcp) && tcp.ValueKind == JsonValueKind.String)
                TcpAddress = tcp.GetString();
            if (root.TryGetProperty("mcp", out var mcp) && mcp.ValueKind == JsonValueKind.String)
                McpAddress = mcp.GetString();
            if (root.TryGetProperty("dev_tools", out var dt) && (dt.ValueKind == JsonValueKind.True || dt.ValueKind == JsonValueKind.False))
                DevTools = dt.GetBoolean();
            if (root.TryGetProperty("debug_artifacts", out var da) && (da.ValueKind == JsonValueKind.True || da.ValueKind == JsonValueKind.False))
                DebugArtifacts = da.GetBoolean();
            if (root.TryGetProperty("dump_artifacts", out var dmp) && (dmp.ValueKind == JsonValueKind.True || dmp.ValueKind == JsonValueKind.False))
                DumpArtifacts = dmp.GetBoolean();
            if (root.TryGetProperty("dialog_trace", out var dtr) && (dtr.ValueKind == JsonValueKind.True || dtr.ValueKind == JsonValueKind.False))
                DialogTrace = dtr.GetBoolean();
            if (root.TryGetProperty("max_batch_ops", out var mb) && mb.ValueKind == JsonValueKind.Number && mb.TryGetInt32(out var mbi) && mbi >= 0)
                MaxBatchOps = mbi;
            if (root.TryGetProperty("msaa_samples", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.TryGetInt32(out var msi) && msi >= 0 && msi <= 8)
                MsaaSamples = msi;
            if (root.TryGetProperty("aniso_filtering", out var an) && (an.ValueKind == JsonValueKind.True || an.ValueKind == JsonValueKind.False))
                AnisoFiltering = an.GetBoolean();
            if (root.TryGetProperty("render_scale", out var rs) && rs.ValueKind == JsonValueKind.Number && rs.TryGetSingle(out var rsf) && rsf >= 1f && rsf <= 4f)
                RenderScale = rsf;
            if (!TryParse(TcpAddress, out _, out _)) TcpAddress = DefaultTcp;
            if (!TryParse(McpAddress, out _, out _)) McpAddress = DefaultMcp;
            PluginLog.LogInfo($"servers config: enabled={Enabled} tcp={TcpAddress} mcp={McpAddress} dev_tools={DevTools} debug_artifacts={DebugArtifacts} dump_artifacts={DumpArtifacts} dialog_trace={DialogTrace} max_batch_ops={MaxBatchOps} msaa={MsaaSamples} aniso={AnisoFiltering} render_scale={RenderScale}");
        }
        catch (Exception e) { PluginLog.LogError($"servers config load failed: {e.Message}"); }
    }

    public static void Save()
    {
        try
        {
            var json = "{\n" +
                       $"  \"enabled\": {(Enabled ? "true" : "false")},\n" +
                       $"  \"tcp\": {JsonSerializer.Serialize(TcpAddress)},\n" +
                       $"  \"mcp\": {JsonSerializer.Serialize(McpAddress)},\n" +
                       $"  \"dev_tools\": {(DevTools ? "true" : "false")},\n" +
                       $"  \"debug_artifacts\": {(DebugArtifacts ? "true" : "false")},\n" +
                       $"  \"dump_artifacts\": {(DumpArtifacts ? "true" : "false")},\n" +
                       $"  \"dialog_trace\": {(DialogTrace ? "true" : "false")},\n" +
                       $"  \"max_batch_ops\": {MaxBatchOps},\n" +
                       $"  \"msaa_samples\": {MsaaSamples},\n" +
                       $"  \"aniso_filtering\": {(AnisoFiltering ? "true" : "false")},\n" +
                       $"  \"render_scale\": {RenderScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n" +
                       "}\n";
            File.WriteAllText(FilePath, json, new UTF8Encoding(false));
        }
        catch (Exception e) { PluginLog.LogError($"servers config save failed: {e.Message}"); }
    }

    /// <summary>Validate "host:port". Host may be an IPv4 address or a DNS name.</summary>
    public static bool TryParse(string value, out string host, out int port)
    {
        host = null; port = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        int colon = s.LastIndexOf(':');
        if (colon <= 0 || colon == s.Length - 1) return false;
        host = s.Substring(0, colon).Trim();
        var portStr = s.Substring(colon + 1).Trim();
        if (host.Length == 0) return false;
        bool validHost = IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) != UriHostNameType.Unknown;
        if (!validHost) return false;
        if (!int.TryParse(portStr, out port) || port < 1 || port > 65535) return false;
        return true;
    }

    public static void Apply()
    {
        Save();
        bool tcpChanged = Enabled != _appliedEnabled || TcpAddress != _appliedTcp;
        bool mcpChanged = Enabled != _appliedEnabled || McpAddress != _appliedMcp;
        if (tcpChanged) { try { CommandServer.Restart(); } catch (Exception e) { PluginLog.LogError($"tcp restart: {e.Message}"); } }
        if (mcpChanged) { try { McpHost.Restart(); } catch (Exception e) { PluginLog.LogError($"mcp restart: {e.Message}"); } }
        MarkApplied();
    }

    private static bool _appliedEnabled = true;
    private static string _appliedTcp = DefaultTcp;
    private static string _appliedMcp = DefaultMcp;

    public static void MarkApplied()
    {
        _appliedEnabled = Enabled;
        _appliedTcp = TcpAddress;
        _appliedMcp = McpAddress;
    }
}
