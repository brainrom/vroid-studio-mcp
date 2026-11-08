using System;

namespace VroidStudioMcp;

/// <summary>
/// Main-thread engine-change signal. A change is "settled" once the engine fires
/// <c>UpdateSelectedPresets</c> or a model rebuild completes (<c>IsUpdatingModel</c> false). Hooks
/// call <see cref="PresetsUpdated"/>/<see cref="ModelRebuilt"/>; a command or test calls
/// <see cref="Arm"/> and then polls <see cref="Ready"/> from its coroutine (so it yields frames
/// while waiting) until the version moves or the timeout elapses.
/// </summary>
public static class Signals
{
    private static int _presetsUpdated;
    private static int _modelRebuilt;

    private static bool _seen;
    private static bool _armed;
    private static DateTime _t0;
    private static int _baseline;

    public static int Version => _presetsUpdated + _modelRebuilt;
    public static int ModelRebuildVersion => _modelRebuilt;

    public static void PresetsUpdated() => _presetsUpdated++;
    public static void ModelRebuilt() => _modelRebuilt++;

    public static void Arm() { _baseline = Version; _t0 = DateTime.UtcNow; _seen = false; _armed = true; }

    /// <summary>True once the version moved since <see cref="Arm"/>, or after the timeout elapses.</summary>
    public static bool Ready(double timeoutSeconds)
    {
        // Defensive: without a prior Arm the elapsed-time check would use a stale _t0 and return true
        // immediately. Lazy-arm here so an unarmed wait degrades to "wait for the next change up to
        // the timeout" instead of silently not waiting.
        if (!_armed) { _baseline = Version; _t0 = DateTime.UtcNow; _seen = false; _armed = true; }
        if (!_seen && Version != _baseline) _seen = true;
        if (_seen) return true;
        return (DateTime.UtcNow - _t0).TotalSeconds >= timeoutSeconds;
    }
}
