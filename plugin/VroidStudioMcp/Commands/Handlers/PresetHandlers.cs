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
        if (action == "import" || action == "export")
        {
            // Same implementation as the public tool; only the argument positions differ.
            string[] args = action == "import"
                ? new[] { CommandUtil.Arg(ctx, 1), CommandUtil.Arg(ctx, 2), CommandUtil.Arg(ctx, 3), CommandUtil.Arg(ctx, 4), CommandUtil.Arg(ctx, 5) }
                : new[] { CommandUtil.Arg(ctx, 1), CommandUtil.Arg(ctx, 2), CommandUtil.Arg(ctx, 3), CommandUtil.Arg(ctx, 4), CommandUtil.Arg(ctx, 5), CommandUtil.Arg(ctx, 6) };
            var sub = new CommandDispatcher.Context
            {
                Method = action == "import" ? "import_custom_preset" : "export_custom_preset",
                Args = args,
            };
            yield return action == "import" ? CustomItemHandlers.Import(sub) : CustomItemHandlers.Export(sub);
            ctx.Result = sub.Result;
            if (sub.Error != null) ctx.Error = sub.Error;
            yield break;
        }
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
            if (!ArgParse.Int(index, "index", out var i, out var indexError, 0)) { ctx.Error = $"haircustom select: {indexError}"; yield break; }
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
            if (!ArgParse.Int(index, "index", out var i, out var indexError, 0)) { ctx.Error = $"haircustom rename: {indexError}"; yield break; }
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
            if (!ArgParse.Int(index, "index", out var i, out var indexError, 0)) { ctx.Error = $"haircustom save: {indexError}"; yield break; }
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
        ctx.Error = "usage: haircustom [list <section> <tab> | select <section> <tab> <index> | create <section> <tab> | save|saveasnew <section> <tab> <index> | import <section> <tab> <path> [name] [wear=now|later] | export <section> <tab> <index> <path> [save=true|false] [overwrite=true|false] | rename <section> <tab> <index> <name> | tab <section> <tab> <presets|custom>]";
    }

    public static IEnumerator List(CommandDispatcher.Context ctx) => ListPresets(ctx, false);

    // Public: create a named custom preset (the Custom sub-tab item) in one call. `name` is applied
    // through the dialog-free registry rename, so the new item gets a meaningful id immediately.
    public static IEnumerator CreateCustom(CommandDispatcher.Context ctx)
    {
        const string usage = "usage: create_custom_preset <section> <category> <name>";
        var section = CommandUtil.ResolveSectionArg(ctx, 0, usage);
        var tab = CommandUtil.Arg(ctx, 1);
        var name = CommandUtil.Arg(ctx, 2);
        if (section == null) yield break;
        if (tab == null || name == null) { ctx.Error = usage; yield break; }
        name = name.Trim();
        if (name.Length == 0) { ctx.Error = "create_custom_preset: name must not be empty"; yield break; }

        yield return CommandUtil.EnterSection(section);
        var selectorShort = section + ".PresetsSelectorViewModel";
        var category = Categories.FindTab(Categories.FindSelectorByShort(selectorShort), tab);
        if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
        var presetNode = Get(category, "PresetCategoryViewModel") ?? category;
        var add = Presets.CustomItemOfKind(presetNode, "add");
        if (add == null) { ctx.Error = $"create_custom_preset: '{tab}' has no Custom slot (Create New)"; yield break; }

        var before = Presets.CustomItemRefs(presetNode);
        int beforeCount = before.Count;
        Invoke(add, "AddItem");
        yield return CommandUtil.WaitUntil(() =>
        {
            var node = Categories.FindTab(Categories.FindSelectorByShort(selectorShort), tab);
            var pn = Get(node, "PresetCategoryViewModel") ?? node ?? presetNode;
            return Presets.CustomItemCount(pn) > beforeCount;
        }, 20f);
        yield return CommandUtil.Pause(0.5f);

        var cat2 = Categories.FindTab(Categories.FindSelectorByShort(selectorShort), tab) ?? category;
        var pn2 = Get(cat2, "PresetCategoryViewModel") ?? cat2 ?? presetNode;
        var item = Presets.NewCustomItem(pn2, before);
        if (item == null) { ctx.Error = "create_custom_preset: the new item did not appear (creation failed)"; yield break; }

        string auto = Get(item, "DisplayName") as string ?? TryGetLabel(item);
        bool renamed = false;
        if (!string.Equals(auto, name, StringComparison.Ordinal))
        {
            if (!Presets.RenameCustomItem(item, name, out var rerr))
            { ctx.Error = $"create_custom_preset: created but rename failed: {rerr}"; yield break; }
            renamed = true;
            yield return CommandUtil.Pause(0.5f);
        }
        // Rename rewrites the item's file and rebuilds the Custom list, so the captured VM goes stale:
        // re-resolve "the item that was not there before" and read the fresh instance.
        var cat3 = Categories.FindTab(Categories.FindSelectorByShort(selectorShort), tab) ?? cat2;
        var pn3 = Get(cat3, "PresetCategoryViewModel") ?? cat3 ?? pn2;
        var fresh = Presets.NewCustomItem(pn3, before) ?? item;
        var finalName = Get(fresh, "DisplayName") as string ?? name;

        int id = -1;
        foreach (var e in Presets.Entries(selectorShort, tab, "all"))
            if (ReferenceEquals(e.Option, fresh)) { id = e.Id; break; }

        ctx.Result = new Dictionary<string, object>
        {
            ["section"] = Sections.SectionLabel(section), ["category"] = tab, ["id"] = id,
            ["name"] = finalName, ["kind"] = "custom", ["renamed"] = renamed,
        };
    }

    // Dev command (TCP only, not exposed over MCP): like `presets` but also returns the raw
    // resourceName/itemId, which the labeler harness needs to key labels and detect gaps.
    public static IEnumerator ListFull(CommandDispatcher.Context ctx) => ListPresets(ctx, true);

    private static IEnumerator ListPresets(CommandDispatcher.Context ctx, bool full)
    {
        var section = CommandUtil.ResolveSectionArg(ctx, 0, full ? "usage: presets_full <section> <tab> [all|presets|custom]" : "usage: presets <section> <tab> [all|presets|custom]");
        var tab = CommandUtil.Arg(ctx, 1);
        if (section == null) yield break;
        if (tab == null) { ctx.Error = full ? "usage: presets_full <section> <tab> [all|presets|custom]" : "usage: presets <section> <tab> [all|presets|custom]"; yield break; }
        var source = (CommandUtil.Arg(ctx, 2) ?? "all").ToLowerInvariant();
        if (source != "all" && source != "presets" && source != "custom")
        { ctx.Error = "source must be all|presets|custom"; yield break; }
        yield return CommandUtil.EnterSection(section);
        var selector = Categories.FindSelectorByShort(section + ".PresetsSelectorViewModel");
        var category = Categories.FindTab(selector, tab);
        if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
        // One continuous list: custom items first, then the stock presets (Clear included). `index`
        // and `id` are the same continuous value used by select_preset / preset_preview_*.
        var entries = Presets.Entries(section + ".PresetsSelectorViewModel", tab, source);
        var list = new List<object>();
        foreach (var e in entries)
        {
            // The engine tracks the applied preset with PresetOptionViewModel.IsSelected (which
            // UpdateIsSelected sets from the active transferable groups). The Clear slot carries no
            // IsSelected, so its `selected` is null; a real preset is true when it is the applied one.
            var entry = new Dictionary<string, object>
            {
                ["index"] = e.Id, ["id"] = e.Id, ["name"] = e.Name, ["kind"] = e.Kind,
                ["selected"] = Get(e.Option, "IsSelected"),
            };
            if (full)
            {
                if (e.IsCustom) { entry["file"] = e.File; entry["customId"] = e.CustomId; }
                else { entry["resourceName"] = e.ResourceName; entry["itemId"] = e.ItemId; }
            }
            list.Add(entry);
        }
        ctx.Result = list;
    }

    public static IEnumerator Select(CommandDispatcher.Context ctx)
    {
        var section = CommandUtil.ResolveSectionArg(ctx, 0, "usage: select_preset <section> <tab> <id> [params]"); var tab = CommandUtil.Arg(ctx, 1); var index = CommandUtil.Arg(ctx, 2);
        if (section == null) yield break;
        if (tab == null || index == null) { ctx.Error = "usage: select_preset <section> <tab> <id> [params]"; yield break; }
        // Optional 4th arg `true`: return the parameter list alongside the resourceName. Mainly for
        // Outfit, where the applied item's sliders only exist after selection (saves a re-`enter`).
        bool withParams = string.Equals(CommandUtil.Arg(ctx, 3), "true", StringComparison.OrdinalIgnoreCase);
        yield return CommandUtil.EnterSection(section);
        var category = CommandUtil.EnterCategory(section, tab);
        if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
        yield return CommandUtil.Pause(0.6f);
        if (!ArgParse.Int(index, "id", out var i, out var idError, 0))
        { ctx.Error = $"select_preset: {idError}; use the id from presets()"; yield break; }
        var entry = Presets.EntryAt(section + ".PresetsSelectorViewModel", tab, i);
        if (entry == null) { ctx.Error = $"id {i} out of range (from `presets`)"; yield break; }
        // Public result is the applied preset's display name, never the raw resourceName.
        var applied = entry.Kind == "clear" ? "cleared" : entry.Name;
        Signals.Arm();
        if (entry.Kind == "clear") Invoke(entry.Option, "ClearTransferableGroup");
        else Invoke(entry.Option, "set_IsSelected", true);
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
            if (!ArgParse.Int(p, "index", out var v, out var idxError, 0))
            { ctx.Error = $"preset_preview_list: {idxError}"; yield break; }
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
        if (!ArgParse.Int(CommandUtil.Arg(ctx, 2), "start", out var start, out var startError, 0))
        { ctx.Error = $"preset_preview_range: {startError}"; yield break; }
        int count = 1;
        var countArg = CommandUtil.Arg(ctx, 3);
        if (countArg != null && !ArgParse.Int(countArg, "count", out count, out var countError, 1, 12))
        { ctx.Error = $"preset_preview_range: {countError}"; yield break; }
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
        var category = Categories.FindTab(Categories.FindSelectorByShort(section + ".PresetsSelectorViewModel"), tab);
        if (category == null) { ctx.Error = $"no tab '{tab}'{CommandUtil.TabHint(section)}"; yield break; }
        // Continuous ids: custom items first, then the stock presets (same list as `presets`).
        var entries = Presets.Entries(section + ".PresetsSelectorViewModel", tab, "all");

        var wanted = new List<int>();
        foreach (var i in indices)
        {
            if (i >= entries.Count)
            {
                if (strict) { ctx.Error = $"{cmd}: id {i} out of range"; yield break; }
                continue;
            }
            wanted.Add(i);
        }
        if (strict && indices.Count > 0 && wanted.Count == 0)
        { ctx.Error = $"{cmd}: no ids in range"; yield break; }
        yield return CommandUtil.Pause(0.4f);

        var items = new List<object>();
        Icons.BeginPreviews();
        foreach (var i in wanted)
        {
            var e = entries[i];
            var name = e.Name;
            if (e.Kind == "clear")
            {
                items.Add(new Dictionary<string, object> { ["index"] = i, ["id"] = i, ["name"] = name, ["kind"] = e.Kind, ["ok"] = false, ["error"] = "no preview (clear)" });
                continue;
            }

            byte[] png = null;
            if (e.IsCustom)
            {
                // Custom presets carry their own thumbnail source (same ITextureSource pattern as the
                // stock Preview fallback).
                Icons.Prepare(e.Option);
                object src = null;
                yield return CommandUtil.WaitUntil(() => { src = Get(e.Option, "ThumbnailSource"); return src != null; }, 6f);
                if (src != null)
                {
                    var task = Invoke(src, "Create");
                    yield return CommandUtil.WaitUntil(() => Get(task, "IsCompleted") is bool b && b, 5f);
                    png = Icons.TextureToPng(Get(task, "Result"));
                }
            }
            else
            {
                png = Thumbnails.Get(e.ResourceName);
                if (png == null)
                {
                    // Fallback: the preset ViewModel pipeline (non-standard resource names).
                    Icons.Prepare(e.Option);
                    object preview = null;
                    yield return CommandUtil.WaitUntil(() => { preview = Get(e.Option, "Preview"); return preview != null; }, 6f);
                    if (preview != null)
                    {
                        var task = Invoke(preview, "Create");
                        yield return CommandUtil.WaitUntil(() => Get(task, "IsCompleted") is bool b && b, 5f);
                        png = Icons.TextureToPng(Get(task, "Result"));
                    }
                }
            }
            if (png == null) { items.Add(new Dictionary<string, object> { ["index"] = i, ["id"] = i, ["name"] = name, ["kind"] = e.Kind, ["ok"] = false, ["error"] = "preview not available" }); continue; }

            Icons.AddPreview(new Icons.PreviewItem { Index = i, Name = name, Png = png });
            string file = null;
            if (write)
            {
                file = $"preset_{i:D3}.png";
                File.WriteAllBytes(Path.Combine(BepInEx.Paths.GameRootPath, file), png);
            }
            var item = new Dictionary<string, object> { ["index"] = i, ["id"] = i, ["name"] = name, ["kind"] = e.Kind, ["ok"] = true, ["file"] = file, ["bytes"] = png.Length };
            if (b64) item["data"] = Convert.ToBase64String(png);
            items.Add(item);
        }
        ctx.Result = items;
    }
}
