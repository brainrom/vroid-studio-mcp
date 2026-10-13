using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

/// <summary>Preset listing, selection and thumbnail previews.</summary>
public static class PresetHandlers
{
    // Dev command: custom items (saved user presets) of a category — the "Custom" sub-tab. They are
    // not in the stock PresetOptions list, so they need their own list/select.
    public static IEnumerator Custom(CommandDispatcher.Context ctx)
    {
        var action = (CommandUtil.Arg(ctx, 0) ?? "list").ToLowerInvariant();
        if (action == "list")
        {
            var section = CommandUtil.ResolveSectionArg(ctx, 1, "usage: haircustom list <section> <tab>");
            var tab = CommandUtil.Arg(ctx, 2);
            if (section == null) yield break;
            if (tab == null) { ctx.Error = "usage: haircustom list <section> <tab>"; yield break; }
            yield return CommandUtil.EnterSection(section);
            var category = Categories.FindTab(Categories.FindSelectorByShort(section + ".PresetsSelectorViewModel"), tab);
            if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
            var presetNode = Get(category, "PresetCategoryViewModel") ?? category;
            ctx.Result = new Dictionary<string, object>
            {
                ["section"] = section,
                ["category"] = tab,
                ["custom_items"] = Presets.CustomItems(presetNode),
            };
            yield break;
        }
        if (action == "select")
        {
            var section = CommandUtil.ResolveSectionArg(ctx, 1, "usage: haircustom select <section> <tab> <index>");
            var tab = CommandUtil.Arg(ctx, 2); var index = CommandUtil.Arg(ctx, 3);
            if (section == null) yield break;
            if (tab == null || index == null) { ctx.Error = "usage: haircustom select <section> <tab> <index>"; yield break; }
            if (!int.TryParse(index, out var i) || i < 0) { ctx.Error = "haircustom select: index must be >= 0"; yield break; }
            yield return CommandUtil.EnterSection(section);
            var category = CommandUtil.EnterCategory(section, tab);
            if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
            yield return CommandUtil.Pause(0.6f);
            var presetNode = Get(category, "PresetCategoryViewModel") ?? category;
            var item = Presets.CustomItemAt(presetNode, i);
            if (item == null) { ctx.Error = $"haircustom select: no custom item at index {i} (run `haircustom list`)"; yield break; }
            var name = Get(item, "DisplayName") as string ?? TryGetLabel(item);
            Signals.Arm();
            Invoke(item, "set_IsSelected", true);
            yield return CommandUtil.Settle(1.5f);
            ctx.Result = new Dictionary<string, object> { ["index"] = i, ["name"] = name };
            yield break;
        }
        if (action == "create")
        {
            var section = CommandUtil.ResolveSectionArg(ctx, 1, "usage: haircustom create <section> <tab>");
            var tab = CommandUtil.Arg(ctx, 2);
            if (section == null) yield break;
            if (tab == null) { ctx.Error = "usage: haircustom create <section> <tab>"; yield break; }
            yield return CommandUtil.EnterSection(section);
            var category = Categories.FindTab(Categories.FindSelectorByShort(section + ".PresetsSelectorViewModel"), tab);
            if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
            var presetNode = Get(category, "PresetCategoryViewModel") ?? category;
            var add = Presets.CustomItemOfKind(presetNode, "add");
            if (add == null) { ctx.Error = "haircustom create: no 'Create New' tile in this category"; yield break; }
            int before = Presets.CustomItemCount(presetNode);
            Invoke(add, "AddItem");
            yield return CommandUtil.WaitUntil(() =>
            {
                var node = Categories.FindTab(Categories.FindSelectorByShort(section + ".PresetsSelectorViewModel"), tab);
                var pn = Get(node, "PresetCategoryViewModel") ?? node ?? presetNode;
                return Presets.CustomItemCount(pn) > before;
            }, 20f);
            yield return CommandUtil.Pause(0.5f);
            ctx.Result = new Dictionary<string, object>
            {
                ["created"] = Presets.CustomItemCount(presetNode) > before,
                ["count"] = Presets.CustomItemCount(presetNode),
                ["custom_items"] = Presets.CustomItems(presetNode),
            };
            yield break;
        }

        if (action == "rename")
        {
            var section = CommandUtil.ResolveSectionArg(ctx, 1, "usage: haircustom rename <section> <tab> <index> <name>");
            var tab = CommandUtil.Arg(ctx, 2); var index = CommandUtil.Arg(ctx, 3); var name = CommandUtil.Arg(ctx, 4);
            if (section == null) yield break;
            if (tab == null || index == null || name == null) { ctx.Error = "usage: haircustom rename <section> <tab> <index> <name>"; yield break; }
            if (!int.TryParse(index, out var i) || i < 0) { ctx.Error = "haircustom rename: index must be >= 0"; yield break; }
            yield return CommandUtil.EnterSection(section);
            var category = Categories.FindTab(Categories.FindSelectorByShort(section + ".PresetsSelectorViewModel"), tab);
            if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
            var presetNode = Get(category, "PresetCategoryViewModel") ?? category;
            var item = Presets.CustomItemAt(presetNode, i);
            if (item == null) { ctx.Error = $"haircustom rename: no item at index {i}"; yield break; }
            if (!(TypeName(item) ?? "").EndsWith("CustomItemOptionViewModel")) { ctx.Error = "haircustom rename: index is not a custom item"; yield break; }
            if (!Presets.RenameCustomItem(item, name, out var rerr)) { ctx.Error = rerr; yield break; }
            yield return CommandUtil.Pause(0.5f);
            ctx.Result = new Dictionary<string, object> { ["renamed"] = true, ["name"] = name, ["custom_items"] = Presets.CustomItems(presetNode) };
            yield break;
        }

        if (action == "save" || action == "saveasnew")
        {
            var section = CommandUtil.ResolveSectionArg(ctx, 1, "usage: haircustom save <section> <tab> <index>");
            var tab = CommandUtil.Arg(ctx, 2); var index = CommandUtil.Arg(ctx, 3);
            if (section == null) yield break;
            if (tab == null || index == null) { ctx.Error = "usage: haircustom save <section> <tab> <index>"; yield break; }
            if (!int.TryParse(index, out var i) || i < 0) { ctx.Error = "haircustom save: index must be >= 0"; yield break; }
            yield return CommandUtil.EnterSection(section);
            var category = Categories.FindTab(Categories.FindSelectorByShort(section + ".PresetsSelectorViewModel"), tab);
            if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
            var presetNode = Get(category, "PresetCategoryViewModel") ?? category;
            var item = Presets.CustomItemAt(presetNode, i);
            if (item == null || !(TypeName(item) ?? "").EndsWith("CustomItemOptionViewModel"))
            { ctx.Error = $"haircustom save: no custom item at index {i}"; yield break; }
            // The design-editor close saves the item on the model "in place"; writing/refreshing the
            // reusable *.vroidcustomitem preset is this separate button.
            Invoke(item, action == "saveasnew" ? "SaveAsNewCustomItem" : "OverwriteCustomItem");
            yield return CommandUtil.Pause(1.0f);
            ctx.Result = new Dictionary<string, object> { ["saved"] = true, ["custom_items"] = Presets.CustomItems(presetNode) };
            yield break;
        }

        if (action == "tab")
        {
            var section = CommandUtil.ResolveSectionArg(ctx, 1, "usage: haircustom tab <section> <tab> <presets|custom>");
            var tab = CommandUtil.Arg(ctx, 2); var which = (CommandUtil.Arg(ctx, 3) ?? "custom").ToLowerInvariant();
            if (section == null) yield break;
            if (tab == null || (which != "custom" && which != "presets")) { ctx.Error = "usage: haircustom tab <section> <tab> <presets|custom>"; yield break; }
            yield return CommandUtil.EnterSection(section);
            var category = Categories.FindTab(Categories.FindSelectorByShort(section + ".PresetsSelectorViewModel"), tab);
            if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
            var presetNode = Get(category, "PresetCategoryViewModel") ?? category;
            bool custom = which == "custom";
            Invoke(presetNode, "set_IsShowCustomItem", custom);
            Invoke(presetNode, "set_IsShowPreset", !custom);
            yield return CommandUtil.Pause(0.3f);
            ctx.Result = new Dictionary<string, object> { ["category"] = tab, ["tab"] = which };
            yield break;
        }
        ctx.Error = "usage: haircustom [list <section> <tab> | select <section> <tab> <index> | create <section> <tab> | save|saveasnew <section> <tab> <index> | rename <section> <tab> <index> <name> | tab <section> <tab> <presets|custom>]";
    }

    public static IEnumerator List(CommandDispatcher.Context ctx) => ListPresets(ctx, false);

    // Dev command (TCP only, not exposed over MCP): like `presets` but also returns the raw
    // resourceName/itemId, which the labeler harness needs to key labels and detect gaps.
    public static IEnumerator ListFull(CommandDispatcher.Context ctx) => ListPresets(ctx, true);

    private static IEnumerator ListPresets(CommandDispatcher.Context ctx, bool full)
    {
        var section = CommandUtil.ResolveSectionArg(ctx, 0, full ? "usage: presets_full <section> <tab>" : "usage: presets <section> <tab>"); var tab = CommandUtil.Arg(ctx, 1);
        if (section == null) yield break;
        if (tab == null) { ctx.Error = full ? "usage: presets_full <section> <tab>" : "usage: presets <section> <tab>"; yield break; }
        yield return CommandUtil.EnterSection(section);
        var selector = Categories.FindSelectorByShort(section + ".PresetsSelectorViewModel");
        var category = Categories.FindTab(selector, tab);
        if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
        var presetNode = Get(category, "PresetCategoryViewModel");
        var src = (object)presetNode ?? category;
        var list = new List<object>();
        var options = Enumerate(Get(src, "PresetOptions")).ToList();
        for (int i = 0; i < options.Count; i++)
        {
            // One name per entry: the human label when known, otherwise the internal id, so the
            // client never has to parse two shapes. Clear is spelled out (it is not a preset).
            var entry = new Dictionary<string, object> { ["index"] = i, ["name"] = Presets.DisplayName(options[i]) };
            if (full)
            {
                var model = Get(options[i], "Model");
                entry["resourceName"] = Get(model, "resourceName") as string;
                entry["itemId"] = Get(model, "itemId") as string;
            }
            list.Add(entry);
        }
        ctx.Result = list;
    }

    public static IEnumerator Select(CommandDispatcher.Context ctx)
    {
        var section = CommandUtil.ResolveSectionArg(ctx, 0, "usage: select_preset <section> <tab> <index> [params]"); var tab = CommandUtil.Arg(ctx, 1); var index = CommandUtil.Arg(ctx, 2);
        if (section == null) yield break;
        if (tab == null || index == null) { ctx.Error = "usage: select_preset <section> <tab> <index> [params]"; yield break; }
        // Optional 4th arg `true`: return the parameter list alongside the resourceName. Mainly for
        // Outfit, where the applied item's sliders only exist after selection (saves a re-`enter`).
        bool withParams = string.Equals(CommandUtil.Arg(ctx, 3), "true", StringComparison.OrdinalIgnoreCase);
        yield return CommandUtil.EnterSection(section);
        var category = CommandUtil.EnterCategory(section, tab);
        if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
        yield return CommandUtil.Pause(0.6f);
        var presetNode = Get(category, "PresetCategoryViewModel");
        var src = (object)presetNode ?? category;
        var options = Enumerate(Get(src, "PresetOptions")).ToList();
        if (!int.TryParse(index, out var i) || i < 0 || i >= options.Count) { ctx.Error = "index out of range"; yield break; }
        // Public result is the applied preset's display name, never the raw resourceName.
        bool clear = (TypeName(options[i]) ?? "").Contains("ClearOption");
        var applied = clear ? "cleared" : Presets.DisplayName(options[i]);
        Signals.Arm();
        if (clear) Invoke(options[i], "ClearTransferableGroup");
        else Invoke(options[i], "set_IsSelected", true);
        yield return CommandUtil.Settle(1.5f);
        if (!withParams) { ctx.Result = applied; yield break; }
        // Selection may have rebuilt the tab VM tree: re-resolve it before reading parameters.
        var refreshed = Categories.FindTab(Categories.FindSelectorByShort(section + ".PresetsSelectorViewModel"), tab) ?? category;
        var payload = Parameters.Payload(section, tab, refreshed);
        payload["name"] = applied;
        ctx.Result = payload;
    }

    // Public list form: preview exactly the requested preset indexes (chosen from `presets`).
    // Address out of range is a caller bug -> the whole call fails.
    public static IEnumerator PreviewList(CommandDispatcher.Context ctx)
    {
        var section = CommandUtil.ResolveSectionArg(ctx, 0, "usage: preset_preview_list <section> <tab> <index,index,...> [file|inline|both]");
        var tab = CommandUtil.Arg(ctx, 1);
        var list = CommandUtil.Arg(ctx, 2);
        if (section == null) yield break;
        if (tab == null || list == null)
        { ctx.Error = "usage: preset_preview_list <section> <tab> <index,index,...> [file|inline|both]"; yield break; }

        var indices = new List<int>();
        foreach (var part in list.Split(','))
        {
            var p = part.Trim();
            if (!int.TryParse(p, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0)
            { ctx.Error = $"preset_preview_list: bad index '{p}'"; yield break; }
            if (!indices.Contains(v)) indices.Add(v);
        }
        if (indices.Count == 0) { ctx.Error = "preset_preview_list: empty index list"; yield break; }
        if (indices.Count > 12) { ctx.Error = "preset_preview_list: at most 12 indexes"; yield break; }

        var mode = (CommandUtil.Arg(ctx, 3) ?? "file").ToLowerInvariant();
        if (!CommandUtil.IsPreviewMode(mode)) { ctx.Error = "preset_preview_list: mode must be file|inline|both|b64"; yield break; }
        yield return RenderPreviews(ctx, section, tab, indices, mode, strict: true, cmd: "preset_preview_list");
    }

    // Public range form: preview `count` consecutive presets from `start`. Any index outside the
    // available presets (or count > 12) is a caller bug -> the whole call fails; nothing is trimmed.
    public static IEnumerator PreviewRange(CommandDispatcher.Context ctx)
    {
        var section = CommandUtil.ResolveSectionArg(ctx, 0, "usage: preset_preview_range <section> <tab> <start> [count] [file|inline|both|b64]");
        var tab = CommandUtil.Arg(ctx, 1);
        if (section == null) yield break;
        if (tab == null)
        { ctx.Error = "usage: preset_preview_range <section> <tab> <start> [count] [file|inline|both|b64]"; yield break; }
        if (!int.TryParse(CommandUtil.Arg(ctx, 2), out var start) || start < 0)
        { ctx.Error = "preset_preview_range: start must be an integer >= 0"; yield break; }
        int count = 1;
        if (CommandUtil.Arg(ctx, 3) != null && (!int.TryParse(CommandUtil.Arg(ctx, 3), out count) || count < 1 || count > 12))
        { ctx.Error = "preset_preview_range: count must be 1..12"; yield break; }
        var mode = (CommandUtil.Arg(ctx, 4) ?? "file").ToLowerInvariant();
        if (!CommandUtil.IsPreviewMode(mode)) { ctx.Error = "preset_preview_range: mode must be file|inline|both|b64"; yield break; }

        var indices = new List<int>();
        for (int i = 0; i < count; i++) indices.Add(start + i);
        yield return RenderPreviews(ctx, section, tab, indices, mode, strict: true, cmd: "preset_preview_range");
    }

    private static IEnumerator RenderPreviews(CommandDispatcher.Context ctx, string section, string tab, List<int> indices, string mode, bool strict, string cmd)
    {
        bool write = mode == "file" || mode == "both";
        bool b64 = mode == "b64";

        yield return CommandUtil.EnterSection(section);
        var selector = Categories.FindSelectorByShort(section + ".PresetsSelectorViewModel");
        var category = Categories.FindTab(selector, tab);
        if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
        var options = Icons.Options(section + ".PresetsSelectorViewModel", tab);

        var wanted = new List<int>();
        foreach (var i in indices)
        {
            if (i >= options.Count)
            {
                if (strict) { ctx.Error = $"{cmd}: index {i} out of range"; yield break; }
                continue;
            }
            wanted.Add(i);
        }
        if (strict && indices.Count > 0 && wanted.Count == 0)
        { ctx.Error = $"{cmd}: no indexes in range"; yield break; }
        yield return CommandUtil.Pause(0.4f);

        var items = new List<object>();
        Icons.BeginPreviews();
        foreach (var i in wanted)
        {
            var option = options[i];
            var resourceName = Get(Get(option, "Model"), "resourceName") as string;
            var name = Presets.DisplayName(option);
            if ((TypeName(option) ?? "").Contains("ClearOption") || string.IsNullOrEmpty(resourceName))
            {
                items.Add(new Dictionary<string, object> { ["index"] = i, ["name"] = name, ["ok"] = false, ["error"] = "no preview (clear)" });
                continue;
            }
            var png = Thumbnails.Get(resourceName);
            if (png == null)
            {
                // Fallback: the preset ViewModel pipeline (custom items / non-standard names).
                Icons.Prepare(option);
                object preview = null;
                yield return CommandUtil.WaitUntil(() => { preview = Get(option, "Preview"); return preview != null; }, 6f);
                if (preview != null)
                {
                    var task = Invoke(preview, "Create");
                    yield return CommandUtil.WaitUntil(() => Get(task, "IsCompleted") is bool b && b, 5f);
                    png = Icons.TextureToPng(Get(task, "Result"));
                }
            }
            if (png == null) { items.Add(new Dictionary<string, object> { ["index"] = i, ["name"] = name, ["ok"] = false, ["error"] = "preview not available" }); continue; }

            Icons.AddPreview(new Icons.PreviewItem { Index = i, Name = name, Png = png });
            string file = null;
            if (write)
            {
                file = $"preset_{i:D3}.png";
                File.WriteAllBytes(Path.Combine(BepInEx.Paths.GameRootPath, file), png);
            }
            var item = new Dictionary<string, object> { ["index"] = i, ["name"] = name, ["ok"] = true, ["file"] = file, ["bytes"] = png.Length };
            if (b64) item["data"] = Convert.ToBase64String(png);
            items.Add(item);
        }
        ctx.Result = items;
    }
}
