using System;
using System.Globalization;
using System.Linq;

namespace VroidStudioMcp;

/// <summary>
/// Strict, shared argument parsing for the command handlers. The contract is the same everywhere:
/// a missing/empty optional argument is handled by the caller (it does not call the parser), every
/// non-empty value must match exactly, and a failure returns a ready-to-prefix actionable message.
/// This replaces the per-handler <c>TryParse</c> blocks and the silent <c>x == "true"</c> fallbacks.
/// </summary>
public static class ArgParse
{
    public static bool Int(string text, string name, out int value, out string error,
                           int min = int.MinValue, int max = int.MaxValue)
    {
        error = null;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value >= min && value <= max)
            return true;
        var range = min == int.MinValue && max == int.MaxValue ? ""
                  : min == int.MinValue ? $" <= {max}"
                  : max == int.MaxValue ? $" >= {min}"
                  : $" in [{min},{max}]";
        error = $"'{name}' must be an integer{range} (got '{text}')";
        return false;
    }

    public static bool Float(string text, string name, out float value, out string error,
                             float min = float.MinValue, float max = float.MaxValue)
    {
        error = null;
        if (ParamCodec.TryParseFinite(text, out value) && value >= min && value <= max)
            return true;
        var range = min == float.MinValue && max == float.MaxValue ? ""
                  : min == float.MinValue ? $" <= {max}"
                  : max == float.MaxValue ? $" >= {min}"
                  : $" in [{min},{max}]";
        error = $"'{name}' must be a finite number{range} (got '{text}')";
        return false;
    }

    /// <summary>One of a finite set of spellings mapped to a boolean, e.g. true|false or on|off.
    /// The accepted list is exactly the map; anything else is an error, never a silent false.</summary>
    public static bool Flag(string text, string name, out bool value, out string error,
                            params (string word, bool value)[] map)
    {
        error = null;
        if (!string.IsNullOrEmpty(text))
        {
            var t = text.Trim();
            foreach (var (word, v) in map)
                if (string.Equals(t, word, StringComparison.OrdinalIgnoreCase))
                {
                    value = v;
                    return true;
                }
        }
        value = false;
        error = $"'{name}' must be one of {string.Join("|", map.Select(m => m.word))} (got '{text}')";
        return false;
    }

    /// <summary>Case-insensitive choice returning the matched index.</summary>
    public static bool Choice(string text, string name, out int index, out string error, params string[] choices)
    {
        error = null;
        index = -1;
        if (!string.IsNullOrEmpty(text))
        {
            var t = text.Trim();
            for (int i = 0; i < choices.Length; i++)
                if (string.Equals(t, choices[i], StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    return true;
                }
        }
        error = $"'{name}' must be one of {string.Join("|", choices)} (got '{text}')";
        return false;
    }
}
