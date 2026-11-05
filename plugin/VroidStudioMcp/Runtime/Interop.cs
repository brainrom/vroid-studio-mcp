using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace VroidStudioMcp;

public static class Interop
{
    public static object First(object[] a) => (a != null && a.Length > 0) ? a[0] : null;

    public static IEnumerable<object> Enumerate(object o) => EnumerateList(o);

    // Type lookup. HarmonyLib.AccessTools.TypeByName falls back to enumerating every type of every
    // loaded assembly *twice* per call and logs a giant ReflectionTypeLoadException warning for each
    // broken IL2CPP assembly (see docs/quirks.md). Types in this app are resolvable by name via
    // Assembly.GetType, so we do that and cache the result; the silent full scan is only a fallback.
    private static readonly ConcurrentDictionary<string, Type> _typeByName = new();
    private static Type[] _allTypesQuiet;

    public static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        if (asm == null) return Array.Empty<Type>();
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        catch { return Array.Empty<Type>(); }
    }

    /// <summary>All loadable types across loaded assemblies; scanned once, silently (no HarmonyX dump).</summary>
    public static Type[] AllTypesQuiet()
    {
        var cached = _allTypesQuiet;
        if (cached != null) return cached;
        var list = new List<Type>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) list.AddRange(SafeGetTypes(asm));
        return _allTypesQuiet = list.ToArray();
    }

    /// <summary>Resolve a type by full (or simple) name without triggering HarmonyX's expensive fallback.</summary>
    public static Type FindType(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_typeByName.TryGetValue(name, out var cached)) return cached;
        Type found = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { found = asm.GetType(name, false); } catch { found = null; }
            if (found != null) break;
        }
        if (found == null)
            found = AllTypesQuiet().FirstOrDefault(t => t.FullName == name || t.Name == name);
        if (found != null) _typeByName[name] = found;
        return found;
    }

    // re-wrap an il2cpp object as its concrete proxy type (for reflection on derived members)
    public static object RewrapTo(object o, Type target)
    {
        if (o is not Il2CppObjectBase) return null;
        try
        {
            var tryCast = typeof(Il2CppObjectBase).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            if (tryCast != null) return tryCast.MakeGenericMethod(target).Invoke(o, null);
        }
        catch { }
        return null;
    }
    public static Type FirstGenericArg(Type t)
    {
        try
        {
            if (t.IsGenericType) return t.GetGenericArguments()[0];
            foreach (var i in t.GetInterfaces())
                if (i.IsGenericType) return i.GetGenericArguments()[0];
        }
        catch { }
        return null;
    }
    public static List<object> IndexedRead(object o, BindingFlags BF)
    {
        try
        {
            var t = o.GetType();
            var cntP = t.GetProperty("Count", BF) ?? t.GetProperty("Length", BF);
            var idxM = t.GetProperty("Item", BF)?.GetGetMethod(true) ?? t.GetMethod("get_Item", BF);
            if (cntP == null || idxM == null) return null;
            int n;
            try { n = Convert.ToInt32(cntP.GetValue(o)); } catch { return null; }
            if (n <= 0) return new List<object>();
            var tmp = new List<object>(n);
            for (int i = 0; i < n; i++)
            {
                try { tmp.Add(idxM.Invoke(o, new object[] { i })); }
                catch { return null; }
            }
            return tmp;
        }
        catch { return null; }
    }
    public static List<object> EnumerateList(object o)
    {
        var res = new List<object>();
        if (o == null || o is string) return res;
        o = RealObj(o);
        if (!(o is Il2CppObjectBase) && o is IEnumerable e)
        {
            foreach (var x in e) res.Add(x);
            return res;
        }

        const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // strategy 0: re-wrap il2cpp generic collection into its concrete List<T> proxy
        if (o is Il2CppObjectBase)
        {
            var elem = FirstGenericArg(o.GetType());
            if (elem != null)
            {
                try
                {
                    var listType = typeof(Il2CppSystem.Collections.Generic.List<>).MakeGenericType(elem);
                    var casted = RewrapTo(o, listType);
                    if (casted != null && !ReferenceEquals(casted, o))
                    {
                        var sub = IndexedRead(casted, BF);
                        if (sub != null) return sub;
                    }
                }
                catch { }
            }
        }

        // strategy 1: Count/Length + indexer on the proxy we already have
        var s1 = IndexedRead(o, BF);
        if (s1 != null) return s1;

        // strategy 2: ToArray()
        var t = o.GetType();
        var toArr = t.GetMethod("ToArray", Type.EmptyTypes);
        if (toArr != null)
        {
            try
            {
                var arr = RealObj(toArr.Invoke(o, null));
                if (arr != null && !ReferenceEquals(arr, o))
                {
                    var sub = IndexedRead(arr, BF) ?? (EnumerateList(arr).Count > 0 ? EnumerateList(arr) : null);
                    if (sub != null) return sub;
                }
            }
            catch { }
        }
        return res;
    }
    public static bool Set(object o, string member, object value)
    {
        if (o == null) return false;
        try
        {
            var obj = RealObj(o);
            var t = obj.GetType();
            var p = t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.CanWrite) { p.SetValue(obj, value); return true; }
            var f = t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) { f.SetValue(obj, value); return true; }
        }
        catch { }
        return false;
    }
    public static object Get(object o, string member)
    {
        if (o == null) return null;
        try
        {
            var obj = RealObj(o);
            var t = obj.GetType();
            var p = t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null) return p.GetValue(obj);
            var f = t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(obj);
        }
        catch { }
        return null;
    }
    public static string TypeName(object o)
    {
        if (o == null) return null;
        var t = RealType(o);
        return t?.FullName ?? t?.Name;
    }
    public static object RealObj(object o)
    {
        if (o is not Il2CppObjectBase ob) return o;
        try
        {
            var t = RealType(o);
            if (t == null || o.GetType() == t) return o;
            if (!typeof(Il2CppObjectBase).IsAssignableFrom(t)) return o;
            var tryCast = typeof(Il2CppObjectBase).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            if (tryCast != null)
            {
                var r = tryCast.MakeGenericMethod(t).Invoke(o, null);
                if (r != null) return r;
            }
            var ctor = t.GetConstructor(new[] { typeof(IntPtr) });
            if (ctor != null) return ctor.Invoke(new object[] { ob.Pointer });
        }
        catch { }
        return o;
    }
    public static string NativeClassName(IntPtr ptr)
    {
        var klass = IL2CPP.il2cpp_object_get_class(ptr);
        var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
        var ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
    public static Type RealType(object o)
    {
        if (o == null) return null;
        try
        {
            if (o is Il2CppObjectBase ob)
            {
                var full = NativeClassName(ob.Pointer);
                if (!string.IsNullOrEmpty(full))
                {
                    var t = FindType(full);
                    if (t != null) return t;
                }
            }
        }
        catch { }
        return o.GetType();
    }
    public static string FormatArg(object a)
    {
        if (a == null) return "null";
        try
        {
            var t = RealType(a) ?? a.GetType();
            if (a is string || t.IsPrimitive || t.IsEnum) return a.ToString();
            return t.FullName ?? t.Name;
        }
        catch { return "<arg>"; }
    }
    public static string TryGetLabel(object instance)
    {
        if (instance == null) return null;
        try
        {
            var obj = RealObj(instance);
            var t = obj.GetType();
            foreach (var p in new[] { "Label", "TranslatedDisplayName", "DisplayName", "Name" })
            {
                var prop = t.GetProperty(p, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || prop.PropertyType != typeof(string)) continue;
                var v = prop.GetValue(obj) as string;
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        catch { }
        return null;
    }
    public static string StackTop(int depth = 8)
    {
        try
        {
            var st = new System.Diagnostics.StackTrace(false);
            var names = new List<string>();
            int i = 0;
            foreach (var f in st.GetFrames())
            {
                if (i++ >= depth) break;
                var m = f.GetMethod();
                names.Add($"{m?.DeclaringType?.Name}.{m?.Name}");
            }
            return string.Join(" < ", names);
        }
        catch { return "?"; }
    }
    public static string SafeStr(object o)
    {
        try { return o?.ToString(); } catch { return "<obj>"; }
    }
    public static string CategoryStr(object cat)
    {
        if (cat == null) return "null";
        var name = Get(cat, "Name") as string ?? Get(cat, "TranslatedDisplayName") as string;
        var tg = Get(cat, "TransferableGroupType") as string;
        return $"{name} ({tg})";
    }
    public static object CategoryInfo(object cat)
    {
        if (cat == null) return null;
        return new Dictionary<string, object>
        {
            ["type"] = TypeName(cat),
            ["name"] = Get(cat, "Name") as string ?? Get(cat, "TranslatedDisplayName") as string,
            ["tgtype"] = Get(cat, "TransferableGroupType") as string,
        };
    }
    public static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "x";
        var bad = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(Array.IndexOf(bad, ch) >= 0 || ch == '/' || ch == '\\' ? '_' : ch);
        return sb.ToString();
    }
    public static object Invoke(object instance, string method, params object[] args)
    {
        try
        {
            var obj = RealObj(instance);
            var cands = obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(x => x.Name == method).ToList();
            if (cands.Count == 0) return null;
            var m = cands.FirstOrDefault(x => x.GetParameters().Length == args.Length) ?? cands[0];
            return m.Invoke(obj, args);
        }
        catch (Exception e) { PluginLog.LogError($"{method} failed: {e.Message}"); return null; }
    }

    /// <summary>Invoke an instance method, returning the exception message instead of swallowing it.</summary>
    public static bool TryInvoke(object instance, string method, out object result, out string error, params object[] args)
    {
        result = null; error = null;
        if (instance == null) { error = "object is null"; return false; }
        try
        {
            var obj = RealObj(instance);
            var cands = obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(x => x.Name == method).ToList();
            if (cands.Count == 0) { error = $"method '{method}' not found"; return false; }
            var m = cands.FirstOrDefault(x => x.GetParameters().Length == args.Length) ?? cands[0];
            result = m.Invoke(obj, args);
            return true;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    /// <summary>Invoke a static method, returning the exception message instead of swallowing it.</summary>
    public static bool TryInvokeStatic(Type type, string method, out object result, out string error, params object[] args)
    {
        result = null; error = null;
        if (type == null) { error = "type is null"; return false; }
        try
        {
            var cands = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(x => x.Name == method).ToList();
            if (cands.Count == 0) { error = $"method '{method}' not found"; return false; }
            var m = cands.FirstOrDefault(x => x.GetParameters().Length == args.Length) ?? cands[0];
            result = m.Invoke(null, args);
            return true;
        }
        catch (Exception e) { error = e.Message; return false; }
    }
}
