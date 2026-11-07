using System.Collections;
using System.Collections.Generic;
using System.Text;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

/// <summary>Custom-library discovery must include the engine's add/import tiles even in an empty
/// library, through both the managed rewrap and the forced native array fallback.</summary>
public sealed class CustomCollectionTest : VroidTest
{
    protected override IEnumerator Run()
    {
        yield return Open();
        var rows = new List<object>();
        bool ok = true;
        Interop.ForceNativeArrayFallback = false;
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

            // Repeat the discovery with the managed T[] rewrap disabled: the native fallback must
            // return the same library and the same add/import tiles.
            int nativeBefore = Interop.NativeArrayFallbackHits;
            List<object> nativeItems = null;
            bool nativeAdd = false, nativeImport = false;
            Interop.ForceNativeArrayFallback = true;
            try
            {
                nativeItems = Presets.CustomItems(node);
                nativeAdd = Presets.CustomItemOfKind(node, "add") != null;
                nativeImport = Presets.CustomItemOfKind(node, "import") != null;
            }
            finally { Interop.ForceNativeArrayFallback = false; }
            int nativeHits = Interop.NativeArrayFallbackHits - nativeBefore;
            bool nativeOk = nativeAdd && nativeImport && nativeHits >= 3
                            && Names(nativeItems) == Names(items);

            ok &= enter.Error == null && hasAdd && hasImport && nativeOk;
            var row = new Dictionary<string, object>
            {
                ["section"] = entry.Item1, ["category"] = entry.Item3,
                ["collection_type"] = TypeName(collection),
                ["proxy_type"] = collection?.GetType().FullName,
                ["count"] = Get(collection, "Count"),
                ["has_add"] = hasAdd, ["has_import"] = hasImport,
                ["native_ok"] = nativeOk, ["native_hits"] = nativeHits,
                ["items"] = items,
                ["items_native"] = nativeItems,
            };
            rows.Add(row);
        }
        WriteResult("customcollectiontest_result.json", new Dictionary<string, object> { ["ok"] = ok, ["categories"] = rows });
        Report("customcollectiontest", ok, rows);
    }

    private static string Names(List<object> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            if (item is not Dictionary<string, object> row) continue;
            if (sb.Length > 0) sb.Append('|');
            sb.Append(row["name"] as string);
        }
        return sb.ToString();
    }
}
