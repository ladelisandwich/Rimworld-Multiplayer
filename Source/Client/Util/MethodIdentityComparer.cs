using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Multiplayer.Client.Util;

// Keys dictionaries by what a method is, not by which MethodBase object describes it. On .NET, MethodBase.Equals
// for a non-generic method is reference equality, and in the game the object Harmony hands a patch as
// __originalMethod didn't match the one the dictionary had been keyed with (Oct 19: KeyNotFoundException in
// SyncActions for Settlement.GetFloatMenuOptions, which killed the world right-click menu on every settlement).
// Mono compared method handles. Metadata token plus module identifies the definition; the declaring type's full
// name and the method's generic arguments tell instantiations apart.
public sealed class MethodIdentityComparer : IEqualityComparer<MethodBase>
{
    public static readonly MethodIdentityComparer Instance = new();

    public bool Equals(MethodBase a, MethodBase b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a is DynamicMethod || b is DynamicMethod) return false; // only ever equal to itself
        if (a.MetadataToken != b.MetadataToken) return false;
        if (a.Module.ModuleVersionId != b.Module.ModuleVersionId) return false;
        if (a.DeclaringType?.FullName != b.DeclaringType?.FullName) return false;
        return GenericArgs(a) == GenericArgs(b);
    }

    public int GetHashCode(MethodBase m)
    {
        if (m is DynamicMethod) return m.GetHashCode();
        return HashCode.Combine(m.MetadataToken, StringComparer.Ordinal.GetHashCode(m.DeclaringType?.FullName ?? ""));
    }

    private static string GenericArgs(MethodBase m) =>
        m.IsGenericMethod ? string.Join(",", m.GetGenericArguments().Select(t => t.FullName ?? t.Name)) : "";
}
