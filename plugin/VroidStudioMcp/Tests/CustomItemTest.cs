using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BepInEx;

namespace VroidStudioMcp;

/// <summary>End-to-end custom-item import/export: create a custom preset, export its stored
/// <c>*.vroidcustomitem</c> (unworn), re-import it under a new name, then delete both test items so the
/// user's library is unchanged. Repeats the export/import/delete loop for an existing Outfit item when
/// the category has one. The engine's "Imported successfully" modal is answered by the plugin; a
/// leaked dialog would stall the following commands and fail the test.</summary>
public sealed class CustomItemTest : VroidTest
{
    private static CommandDispatcher.Context Ctx(string method, params string[] args)
        => CommandDispatcher.TestContext(method, args);

    private sealed class Item
    {
        public string Name;
        public string File;
        public object Option;
        public int Id;
    }

    protected override IEnumerator Run()
    {
        yield return Open();
        yield return SwitchSection("HairEditor");

        var root = Path.Combine(BepInEx.Paths.GameRootPath, "customitem_test");
        Directory.CreateDirectory(root);
        var tag = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var hairBase = "McpTestBase_" + tag;
        var hairCopy = "McpTestCopy_" + tag;
        var hairFile = Path.Combine(root, "hair_export.vroidcustomitem");
        var outfitFile = Path.Combine(root, "outfit_export.vroidcustomitem");

        var hair = new Dictionary<string, object>();
        var outfit = new Dictionary<string, object>();

        // ---- Hair: create an empty custom item, export it (unworn) and import the copy ----------
        yield return EnterCategory("HairEditor", "Back");
        const string hairSelector = "HairEditor.PresetsSelectorViewModel";
        int hairBefore = CustomItemsCount(hairSelector, "Back");

        var create = Ctx("create_custom_preset", "Hairstyle", "Back", hairBase);
        yield return CommandDispatcher.TestRun(create);
        Item created = null;
        yield return WaitUntil(() => (created = FindCustom(hairSelector, "Back", hairBase)) != null, 20f);
        hair["created"] = created != null;
        if (created == null)
        {
            Fail("hair create", create.Error ?? "the created item did not appear");
            yield break;
        }

        var export = Ctx("export_custom_preset", "Hairstyle", "Back", created.Id.ToString(), hairFile, "false");
        yield return CommandDispatcher.TestRun(export);
        hair["exported"] = export.Error == null && IsCustomItemFile(hairFile);
        hair["export_ok"] = export.Error == null;
        hair["export_error"] = export.Error;

        var import = Ctx("import_custom_preset", "Hairstyle", "Back", hairFile, hairCopy, "later");
        yield return CommandDispatcher.TestRun(import);
        Item imported = null;
        yield return WaitUntil(() => (imported = FindCustom(hairSelector, "Back", hairCopy)) != null, 20f);
        hair["imported"] = import.Error == null && imported != null;
        hair["import_ok"] = import.Error == null;
        hair["import_error"] = import.Error;
        hair["renamed"] = import.Result is Dictionary<string, object> ir
                          && ir["renamed"] is bool rn && rn
                          && string.Equals(ir["name"] as string, hairCopy, StringComparison.Ordinal);
        hair["import_applied"] = import.Result is Dictionary<string, object> ira && ira["applied"] is bool ap && ap;
        hair["count_delta"] = CustomItemsCount(hairSelector, "Back") - hairBefore;

        // Delete the imported copy and the created base item; the library must be back to its size.
        int notify = CustomItemSignals.NotifyVersion;
        bool deletedCopy = imported != null && CustomItems.TryDelete(imported.Option, out _);
        if (deletedCopy)
            yield return WaitUntil(() => CustomItemSignals.NotifyVersion > notify
                                         && FindCustom(hairSelector, "Back", hairCopy) == null, 10f);
        notify = CustomItemSignals.NotifyVersion;
        bool deletedBase = CustomItems.TryDelete(created.Option, out _);
        if (deletedBase)
            yield return WaitUntil(() => CustomItemSignals.NotifyVersion > notify
                                         && FindCustom(hairSelector, "Back", hairBase) == null, 10f);
        hair["deleted"] = deletedCopy && deletedBase
                          && FindCustom(hairSelector, "Back", hairCopy) == null
                          && FindCustom(hairSelector, "Back", hairBase) == null;
        hair["restored_count"] = CustomItemsCount(hairSelector, "Back") == hairBefore;

        // ---- Outfit: export/import/delete a copy of an existing custom item, if present ----------
        yield return SwitchSection("ClothingEditor");
        yield return EnterCategory("ClothingEditor", "Bottoms");
        const string outfitSelector = "ClothingEditor.PresetsSelectorViewModel";
        var outfitItems = CustomItemsList(outfitSelector, "Bottoms");
        if (outfitItems.Count == 0)
        {
            outfit["skipped"] = "no custom Bottoms item";
        }
        else
        {
            var source = outfitItems[0];
            var outfitName = "McpTestOutfit_" + tag;
            var exp = Ctx("export_custom_preset", "Outfit", "Bottoms", source.Id.ToString(), outfitFile, "false");
            yield return CommandDispatcher.TestRun(exp);
            outfit["exported"] = exp.Error == null && IsCustomItemFile(outfitFile);
            outfit["export_error"] = exp.Error;

            var imp = Ctx("import_custom_preset", "Outfit", "Bottoms", outfitFile, outfitName, "later");
            yield return CommandDispatcher.TestRun(imp);
            Item copy = null;
            yield return WaitUntil(() => (copy = FindCustom(outfitSelector, "Bottoms", outfitName)) != null, 20f);
            outfit["imported"] = imp.Error == null && copy != null;
            outfit["import_error"] = imp.Error;
            outfit["renamed"] = imp.Result is Dictionary<string, object> oir
                                && oir["renamed"] is bool orn && orn
                                && string.Equals(oir["name"] as string, outfitName, StringComparison.Ordinal);
            notify = CustomItemSignals.NotifyVersion;
            bool del = copy != null && CustomItems.TryDelete(copy.Option, out _);
            if (del) yield return WaitUntil(() => CustomItemSignals.NotifyVersion > notify
                                                  && FindCustom(outfitSelector, "Bottoms", outfitName) == null, 10f);
            outfit["deleted"] = del;
        }

        bool ok = Bool(hair, "created") && Bool(hair, "exported") && Bool(hair, "imported")
                  && Bool(hair, "renamed") && !Bool(hair, "import_applied") && (int)hair["count_delta"] == 2
                  && Bool(hair, "deleted") && Bool(hair, "restored_count")
                  && (!outfit.ContainsKey("imported")
                      || (Bool(outfit, "exported") && Bool(outfit, "imported") && Bool(outfit, "renamed") && Bool(outfit, "deleted")));
        WriteResult("customitemtest_result.json", new Dictionary<string, object>
        { ["ok"] = ok, ["hair"] = hair, ["outfit"] = outfit });
        Report("customitemtest", ok, new Dictionary<string, object> { ["hair"] = hair, ["outfit"] = outfit });
    }

    private static bool Bool(Dictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) && v is bool b && b;

    private static bool IsCustomItemFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var zip = ZipFile.OpenRead(path);
            return zip.GetEntry("v1customitem/meta.json") != null;
        }
        catch { return false; }
    }

    private static List<Item> CustomItemsList(string selectorShort, string tab)
        => Presets.Entries(selectorShort, tab, "custom")
                  .Select(e => new Item
                  {
                      Name = CustomItems.DisplayName(e.Option) ?? e.Name,
                      File = e.File,
                      Option = e.Option,
                      Id = e.Id,
                  })
                  .ToList();

    private static Item FindCustom(string selectorShort, string tab, string name)
        => CustomItemsList(selectorShort, tab)
                  .FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));

    private static int CustomItemsCount(string selectorShort, string tab)
        => Presets.Entries(selectorShort, tab, "custom").Count;

    private void Fail(string stage, string detail)
    {
        WriteResult("customitemtest_result.json",
            new Dictionary<string, object> { ["ok"] = false, ["stage"] = stage, ["error"] = detail });
        Report("customitemtest", false, new Dictionary<string, object> { ["stage"] = stage, ["error"] = detail });
    }
}
