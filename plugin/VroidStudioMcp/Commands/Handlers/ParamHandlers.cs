using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

/// <summary>set_param: address one parameter of a category by index/path/name and apply it.</summary>
public static class ParamHandlers
{
    public const string HairGlobalWarning = "Global hair Main/Emission overrides the corresponding colors across non-Base hair, including custom per-material colors. Use per-material editing to preserve distinct hair colors.";
    // Address a parameter by index or name. section/category are REQUIRED on purpose: a shorter form
    // that reuses the "last entered category" was deliberately removed - hidden state makes correctness
    // depend on call history and can silently write to the wrong category after a rebuild/re-enter.
    public static IEnumerator Set(CommandDispatcher.Context ctx)
    {
        var section = CommandUtil.ResolveSectionArg(ctx, 0, "usage: set_param <section> <category> <index|name> <value>"); var tab = CommandUtil.Arg(ctx, 1); var sel = CommandUtil.Arg(ctx, 2); var value = CommandUtil.Arg(ctx, 3);
        if (section == null) yield break;
        if (tab == null || sel == null || value == null)
        { ctx.Error = "usage: set_param <section> <category> <index|name> <value>"; yield break; }
        yield return CommandUtil.EnterSection(section);
        var category = CommandUtil.EnterCategory(section, tab);
        if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
        yield return CommandUtil.Pause(0.6f);
        var list = Parameters.Build(section, tab, category);

        Parameters.ParamRef param;
        if (int.TryParse(sel, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i))
        {
            if (i < 0 || i >= list.Count) { ctx.Error = "index out of range"; yield break; }
            param = list[i];
        }
        else
        {
            var matches = list.Where(p =>
                string.Equals(p.Path, sel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Label, sel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, sel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Id, sel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Key, sel, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                // A near-miss on the global hair colours gets a targeted hint: either the tab is empty
                // (no preset) or the caller used a tab-prefixed path instead of the stable one.
                if (section == "HairEditor" && tab != "Base Hair" && IsHairGlobalSelector(sel))
                    ctx.Error = list.Count == 0
                        ? $"no parameter '{sel}' in '{tab}': select a preset first; global hair colours are shared across non-Base tabs"
                        : $"no parameter '{sel}' in '{tab}'; global hair colours are addressed as 'Hairstyle/Main' / 'Hairstyle/Emission'";
                else ctx.Error = $"no parameter '{sel}' in '{tab}'";
                yield break;
            }
            if (matches.Count > 1) { ctx.Error = $"ambiguous parameter '{sel}' ({matches.Count} matches)"; yield break; }
            param = matches[0]; i = param.Index;
        }

        bool clamped = false;
        if (param.Kind == "color")
        {
            if (!ParamCodec.TryParseColor(value, out var color)) { ctx.Error = "color must be #RRGGBB or r,g,b (0-1)"; yield break; }
            Signals.Arm();
            if (!TryInvoke(param.Target, param.Setter ?? "set_Color", out _, out var error, color))
            { ctx.Error = $"setter failed; the change may have applied: {error}"; yield break; }
            // Normalise the echo so the client sees the canonical form it can feed back (e.g. "0,1,0"
            // -> "#00FF00"), matching what enter reports.
            value = ParamCodec.Hex(color);
        }
        else
        {
            if (!ParamCodec.TryParseFinite(value, out var f)) { ctx.Error = "value must be a finite float"; yield break; }
            // min/max mirror the UI slider range. The engine itself does not clamp (preset defaults
            // can even sit outside the range). Clamp to the union of the slider range and the current
            // value, so read-modify-write never moves an existing out-of-range default, while clearly
            // bogus input is still rejected and reported via clamped:true.
            if (ParamCodec.TryDbl(param.Min, out var min) && ParamCodec.TryDbl(param.Max, out var max) && max > min)
            {
                double lo = min, hi = max;
                if (ParamCodec.TryDbl(param.Value, out var cur)) { lo = Math.Min(lo, cur); hi = Math.Max(hi, cur); }
                if (f < lo) { f = (float)lo; clamped = true; }
                else if (f > hi) { f = (float)hi; clamped = true; }
            }
            Signals.Arm();
            if (!TryInvoke(param.Target, param.Kind == "combined" ? "set_CombinedValue" : "set_Value", out _, out var error, f))
            { ctx.Error = $"setter failed; the change may have applied: {error}"; yield break; }
            value = Math.Round((double)f, 3, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        yield return CommandUtil.Settle(1.5f);
        var result = new Dictionary<string, object> { ["index"] = i, ["label"] = param.Label, ["value"] = value };
        if (clamped) result["clamped"] = true;
        if (param.ColorId == "Hair|Main" || param.ColorId == "Hair|Emission") result["warning"] = HairGlobalWarning;
        ctx.Result = result;
    }

    private sealed class PreparedChange
    {
        public Parameters.ParamRef Param;
        public string Setter;
        public object Value;
        public Dictionary<string, object> Echo;
    }

    // A bounded same-category batch avoids repeated navigation and plugin settle waits. Each setter
    // still follows the native history/rebuild path; this does not disable or merge native rebuilds.
    public static IEnumerator SetMany(CommandDispatcher.Context ctx)
    {
        const string usage = "usage: set_params <section> <category> <changes_json>";
        var section = CommandUtil.ResolveSectionArg(ctx, 0, usage);
        var tab = CommandUtil.Arg(ctx, 1);
        var json = CommandUtil.Arg(ctx, 2);
        if (section == null) yield break;
        if (tab == null || json == null || ctx.Args.Length != 3) { ctx.Error = usage; yield break; }
        if (!ParseChanges(json, out var changes, out var parseError)) { ctx.Error = parseError; yield break; }
        yield return CommandUtil.EnterSection(section);
        var category = CommandUtil.EnterCategory(section, tab);
        if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
        yield return CommandUtil.Pause(0.6f);
        var list = Parameters.Build(section, tab, category);
        var prepared = new List<PreparedChange>();
        var seen = new HashSet<int>();
        for (int n = 0; n < changes.Count; n++)
        {
            if (!PrepareChange(list, changes[n].selector, changes[n].value, out var change, out var error))
            { ctx.Error = $"change {n}: {error}; no parameter setters called"; yield break; }
            if (!seen.Add(change.Param.Index))
            { ctx.Error = $"change {n}: duplicate parameter '{change.Param.Path}'; no parameter setters called"; yield break; }
            prepared.Add(change);
        }
        yield return CommandUtil.WaitUntil(() => !VroidState._modelUpdating, 8f);
        if (VroidState._modelUpdating || !VroidState._modelReady)
        { ctx.Error = "model is not ready; no parameter setters called"; yield break; }
        var applied = new List<object>();
        int rebuildBaseline = Signals.RebuildVersion;
        Signals.Arm();
        for (int n = 0; n < prepared.Count; n++)
        {
            var change = prepared[n];
            if (!TryInvoke(change.Param.Target, change.Setter, out _, out var error, change.Value))
            {
                ctx.Result = new Dictionary<string, object> { ["applied"] = applied, ["failed_index"] = n,
                    ["failed_change_may_have_applied"] = true, ["native_rebuilds_suppressed"] = false };
                ctx.Error = $"change {n} setter failed after {applied.Count} successful setter calls; failed change may have applied: {error}; successful prefix: {JsonSerializer.Serialize(applied)}";
                yield return WaitForBatch(rebuildBaseline);
                yield break;
            }
            applied.Add(change.Echo);
        }
        yield return WaitForBatch(rebuildBaseline);
        if (Signals.RebuildVersion == rebuildBaseline || VroidState._modelUpdating)
        {
            ctx.Error = $"readiness deadline reached after {applied.Count} successful setter calls; changes may be applied or still updating; inspect state before retrying. Applied: {JsonSerializer.Serialize(applied)}";
            yield break;
        }
        ctx.Result = new Dictionary<string, object> { ["applied"] = applied, ["count"] = applied.Count,
            ["native_rebuilds_suppressed"] = false,
            ["model_rebuild_observed"] = Signals.RebuildVersion != rebuildBaseline,
            ["model_updating"] = VroidState._modelUpdating };
    }

    private static IEnumerator WaitForBatch(int rebuildBaseline)
    {
        // A preset-selection signal from category entry cannot prove the parameter rebuild finished.
        yield return CommandUtil.WaitUntil(() => Signals.RebuildVersion != rebuildBaseline && !VroidState._modelUpdating, 8f);
        yield return CommandUtil.Pause(1.5f);
        yield return CommandUtil.WaitUntil(() => !VroidState._modelUpdating, 8f);
    }

    private static bool ParseChanges(string json, out List<(string selector, string value)> changes, out string error)
    {
        changes = new(); error = null;
        if (json.Length > 65536) { error = "changes_json exceeds 65536 characters"; return false; }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 1 || root.GetArrayLength() > 64)
            { error = "changes_json must be an array of 1 to 64 {parameter,value} objects"; return false; }
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { error = "each change must be a {parameter,value} object"; return false; }
                var keys = item.EnumerateObject().Select(p => p.Name).ToList();
                if (keys.Count != 2 || keys.Distinct().Count() != 2 || !keys.Contains("parameter") || !keys.Contains("value"))
                { error = "each change must contain exactly parameter and value"; return false; }
                var parameter = item.GetProperty("parameter"); var value = item.GetProperty("value");
                if ((parameter.ValueKind != JsonValueKind.String && parameter.ValueKind != JsonValueKind.Number) ||
                    (value.ValueKind != JsonValueKind.String && value.ValueKind != JsonValueKind.Number))
                { error = "parameter and value must be strings or numbers"; return false; }
                var selector = parameter.ValueKind == JsonValueKind.String ? parameter.GetString() : parameter.GetRawText();
                var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                if (string.IsNullOrEmpty(selector) || string.IsNullOrEmpty(text)) { error = "parameter and value cannot be empty"; return false; }
                changes.Add((selector, text));
            }
            return true;
        }
        catch (JsonException e) { error = $"invalid changes_json: {e.Message}"; return false; }
    }

    private static bool PrepareChange(List<Parameters.ParamRef> list, string selector, string text, out PreparedChange change, out string error)
    {
        change = null; error = null;
        Parameters.ParamRef param;
        if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            if (index < 0 || index >= list.Count) { error = "index out of range"; return false; }
            param = list[index];
        }
        else
        {
            var matches = list.Where(p => new[] { p.Path, p.Label, p.Name, p.Id, p.Key }
                .Any(s => string.Equals(s, selector, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count != 1) { error = matches.Count == 0 ? $"no parameter '{selector}'" : $"ambiguous parameter '{selector}'"; return false; }
            param = matches[0];
        }
        object value; string setter; string echo; bool clamped = false;
        if (param.Kind == "color")
        {
            if (!ParamCodec.TryParseColor(text, out var color)) { error = "color must be #RRGGBB or r,g,b (0-1)"; return false; }
            value = color; setter = param.Setter ?? "set_Color"; echo = ParamCodec.Hex(color);
        }
        else
        {
            if (!ParamCodec.TryParseFinite(text, out var number)) { error = "value must be a finite float"; return false; }
            if (ParamCodec.TryDbl(param.Min, out var min) && ParamCodec.TryDbl(param.Max, out var max) && max > min)
            {
                if (ParamCodec.TryDbl(param.Value, out var current)) { min = Math.Min(min, current); max = Math.Max(max, current); }
                if (number < min) { number = (float)min; clamped = true; }
                else if (number > max) { number = (float)max; clamped = true; }
            }
            value = number; setter = param.Kind == "combined" ? "set_CombinedValue" : "set_Value";
            echo = Math.Round((double)number, 3, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        }
        var target = RealObj(param.Target);
        if (target == null || !target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(m => m.Name == setter && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(value)))
        { error = $"native setter '{setter}' is unavailable"; return false; }
        var result = new Dictionary<string, object> { ["index"] = param.Index, ["path"] = param.Path, ["label"] = param.Label, ["value"] = echo };
        if (clamped) result["clamped"] = true;
        if (param.ColorId == "Hair|Main" || param.ColorId == "Hair|Emission") result["warning"] = HairGlobalWarning;
        change = new PreparedChange { Param = param, Setter = setter, Value = value, Echo = result };
        return true;
    }

    // True when a selector clearly refers to one of the two global hair colours (stable path, label
    // or id). Used to produce a targeted hint instead of a bare "no parameter".
    private static bool IsHairGlobalSelector(string sel)
    {
        if (string.IsNullOrEmpty(sel)) return false;
        return sel.Equals("Main", StringComparison.OrdinalIgnoreCase)
            || sel.Equals("Emission", StringComparison.OrdinalIgnoreCase)
            || sel.Equals("Hair|Main", StringComparison.OrdinalIgnoreCase)
            || sel.Equals("Hair|Emission", StringComparison.OrdinalIgnoreCase)
            || sel.EndsWith("/Main", StringComparison.OrdinalIgnoreCase)
            || sel.EndsWith("/Emission", StringComparison.OrdinalIgnoreCase);
    }
}
