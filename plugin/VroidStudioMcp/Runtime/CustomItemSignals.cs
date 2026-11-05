namespace VroidStudioMcp;

/// <summary>
/// Version counters for the custom-item registry's asynchronous work. They follow the same
/// arm-then-wait pattern as <see cref="Signals"/> for model rebuilds: capture the counter before a
/// mutation and wait for it to advance instead of sleeping for a fixed time.
/// <list type="bullet">
/// <item><see cref="SaveVersion"/> - <c>CustomItemRegistry.Save</c> finished writing a
/// <c>*.vroidcustomitem</c> (import, overwrite, rename or export).</item>
/// <item><see cref="NotifyVersion"/> - <c>CustomItemRegistry.NotifyUpdateFiles</c> finished, i.e. the
/// Custom tab listeners have rebuilt their item lists (also fires after the file-system observer sees
/// an import/delete).</item>
/// </list>
/// All hooks run on the editor main thread together with the commands, so plain increments are enough.
/// </summary>
public static class CustomItemSignals
{
    public static int SaveVersion;
    public static int NotifyVersion;
    /// <summary>Path argument of the most recent <c>Save</c> (the item file, or the export target).</summary>
    public static string LastSavePath;

    // Harmony passes the original arguments as __args; argument 1 of CustomItemRegistry.Save is the
    // target path. Keeping it lets a caller wait for *its* file instead of any registry write.
    public static void OnSave(object[] __args)
    {
        SaveVersion++;
        LastSavePath = __args != null && __args.Length > 1 ? __args[1] as string : null;
    }

    public static void OnNotify() => NotifyVersion++;

    public static bool SamePath(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), System.StringComparison.Ordinal);

    private static string Normalize(string p)
        => string.IsNullOrEmpty(p) ? null : p.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}
