using System.Collections;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace VroidStudioMcp;

/// <summary>Native array-backed interfaces must enumerate through both the managed rewrap and the
/// native fallback, without breaking list-backed interfaces.</summary>
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
        // A proxy whose generic argument matches the array element (covariance) and one whose
        // argument does not (a deliberately wrong view of the same array).
        var exceptions = new Il2CppReferenceArray<Il2CppSystem.Exception>(
            new Il2CppSystem.Exception[] { null, null, null });
        var covariantView = new Il2CppSystem.Collections.Generic.IReadOnlyCollection<Il2CppSystem.Object>(
            exceptions.Pointer);
        var mismatchedView = new Il2CppSystem.Collections.Generic.IReadOnlyCollection<Il2CppSystem.Exception>(
            array.Pointer);

        var checks = new Dictionary<string, object>();
        Interop.ForceNativeArrayFallback = false;
        try
        {
            // Managed paths: List<T> rewrap, T[] rewrap, direct array/indexer.
            int managed = Interop.ManagedArrayHits, native = Interop.NativeArrayFallbackHits;
            checks["array_interface"] = Same(Interop.EnumerateList(arrayView), first, last);
            checks["array_interface_managed"] =
                Interop.ManagedArrayHits == managed + 1 && Interop.NativeArrayFallbackHits == native;

            managed = Interop.ManagedArrayHits; native = Interop.NativeArrayFallbackHits;
            checks["list_interface"] = Same(Interop.EnumerateList(listView), first, last);
            checks["list_interface_managed_list"] =
                Interop.ManagedArrayHits == managed && Interop.NativeArrayFallbackHits == native;

            checks["direct_array"] = Same(Interop.EnumerateList(array), first, last);
            checks["empty_array_interface"] = Interop.EnumerateList(emptyView).Count == 0;
            checks["managed_array"] = Interop.EnumerateList(new object[] { first, null, last }).Count == 3;
            checks["null"] = Interop.EnumerateList(null).Count == 0;
            checks["covariant_interface"] = Interop.EnumerateList(covariantView).Count == 3;

            // Forced native fallback: the same array-backed interfaces must enumerate with the
            // managed T[] rewrap disabled. The mismatch is refused, so it does not count as a hit.
            managed = Interop.ManagedArrayHits; native = Interop.NativeArrayFallbackHits;
            Interop.ForceNativeArrayFallback = true;
            checks["array_interface_native"] = Same(Interop.EnumerateList(arrayView), first, last);
            checks["empty_array_native"] = Interop.EnumerateList(emptyView).Count == 0;
            checks["covariant_interface_native"] = Interop.EnumerateList(covariantView).Count == 3;
            checks["mismatched_interface_rejected"] = Interop.EnumerateList(mismatchedView).Count == 0;
            checks["native_fallback_hits"] = Interop.NativeArrayFallbackHits == native + 3;
            checks["native_fallback_managed_off"] = Interop.ManagedArrayHits == managed;
        }
        finally { Interop.ForceNativeArrayFallback = false; }

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
