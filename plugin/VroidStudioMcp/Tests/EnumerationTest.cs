using System.Collections;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace VroidStudioMcp;

/// <summary>Native array-backed interfaces must enumerate without breaking list-backed interfaces.</summary>
public sealed class EnumerationTest : VroidTest
{
    protected override IEnumerator Run()
    {
        var first = new Il2CppSystem.Object();
        var last = new Il2CppSystem.Object();
        var array = new Il2CppReferenceArray<Il2CppSystem.Object>(new[] { first, null, last });
        var arrayView = array.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<Il2CppSystem.Object>>();
        var list = new Il2CppSystem.Collections.Generic.List<Il2CppSystem.Object>();
        list.Add(first); list.Add(null); list.Add(last);
        var listView = list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<Il2CppSystem.Object>>();
        var empty = new Il2CppReferenceArray<Il2CppSystem.Object>(0L);
        var emptyView = empty.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<Il2CppSystem.Object>>();
        var checks = new Dictionary<string, object>
        {
            ["array_interface"] = Same(Interop.EnumerateList(arrayView), first, last),
            ["list_interface"] = Same(Interop.EnumerateList(listView), first, last),
            ["direct_array"] = Same(Interop.EnumerateList(array), first, last),
            ["empty_array_interface"] = Interop.EnumerateList(emptyView).Count == 0,
            ["managed_array"] = Interop.EnumerateList(new object[] { first, null, last }).Count == 3,
            ["null"] = Interop.EnumerateList(null).Count == 0,
        };
        bool ok = true;
        foreach (var value in checks.Values) ok &= value is bool b && b;
        WriteResult("enumerationtest_result.json", new Dictionary<string, object> { ["ok"] = ok, ["checks"] = checks });
        Report("enumerationtest", ok, checks);
        yield break;
    }

    private static bool Same(List<object> values, Il2CppObjectBase first, Il2CppObjectBase last)
        => values.Count == 3 && values[0] is Il2CppObjectBase a && a.Pointer == first.Pointer
            && values[1] == null && values[2] is Il2CppObjectBase b && b.Pointer == last.Pointer;
}
