using BepInEx.Logging;

namespace VroidStudioMcp;

public static class PluginLog
{
    internal static ManualLogSource Source;
    // Whether any BepInEx listener accepts Debug. Cached at load: log levels only change on restart.
    internal static bool DebugEnabled { get; set; }

    public static void LogInfo(string message) => Source?.LogInfo(message);
    public static void LogWarning(string message) => Source?.LogWarning(message);
    public static void LogError(string message) => Source?.LogError(message);
    public static void LogDebug(string message) => Source?.LogDebug(message);
}
