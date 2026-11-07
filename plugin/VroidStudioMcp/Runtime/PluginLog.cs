using BepInEx.Logging;

namespace VroidStudioMcp;

public static class PluginLog
{
    internal static ManualLogSource Source;

    public static void LogInfo(string message) => Source?.LogInfo(message);
    public static void LogWarning(string message) => Source?.LogWarning(message);
    public static void LogError(string message) => Source?.LogError(message);
    public static void LogDebug(string message) => Source?.LogDebug(message);
    public static void LogTrace(string message)
    {
        if (ServerSettings.DebugArtifacts) Source?.LogInfo(message);
    }
}
