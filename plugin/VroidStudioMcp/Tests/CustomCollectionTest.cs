using System.Collections;
using System.Collections.Generic;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

/// <summary>Custom-library discovery must include the engine's add/import tiles even in an empty library.</summary>
public sealed class CustomCollectionTest : VroidTest
{
    protected override IEnumerator Run()
    {
        yield return Open();
        var rows = new List<object>();
        bool ok = true;
        foreach (var entry in new[] { ("Outfit", "ClothingEditor", "Dresses"), ("Hairstyle", "HairEditor", "Back") })
        {
            var enter = CommandDispatcher.TestContext("enter", entry.Item1, entry.Item3);
            yield return CommandDispatcher.TestRun(enter);
            var category = Categories.FindTab(SelectorFor(entry.Item2), entry.Item3);
            var node = Get(category, "PresetCategoryViewModel") ?? category;
            var collection = Get(node, "_customItems") ?? Get(node, "CustomItemOptions");
            var items = Presets.CustomItems(node);
            bool hasAdd = Presets.CustomItemOfKind(node, "add") != null;
            bool hasImport = Presets.CustomItemOfKind(node, "import") != null;
            ok &= enter.Error == null && hasAdd && hasImport;
            var row = new Dictionary<string, object>
            {
                ["section"] = entry.Item1, ["category"] = entry.Item3,
                ["collection_type"] = TypeName(collection),
                ["proxy_type"] = collection?.GetType().FullName,
                ["count"] = Get(collection, "Count"),
                ["has_add"] = hasAdd, ["has_import"] = hasImport,
                ["items"] = items,
            };
            rows.Add(row);
        }
        WriteResult("customcollectiontest_result.json", new Dictionary<string, object> { ["ok"] = ok, ["categories"] = rows });
        Report("customcollectiontest", ok, rows);
    }
}
