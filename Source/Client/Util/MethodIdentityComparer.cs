using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Multiplayer.Client.Util;

// Keys dictionaries by what a method is, not by which MethodBase object describes it: metadata token plus module
// identify the definition, the declaring type's full name and the method's generic arguments tell instantiations
// apart, and the reflected type doesn't matter. The hash is plain arithmetic on metadata tokens. On this runtime
// RuntimeMethodInfo.GetHashCode goes through System.HashCode.Combine, and until Oct 20 Multiplayer replaced that
// function's result while a session was active, so a table filled at startup lost its keys once a game was hosted
// or joined (SyncActions and Settlement.GetFloatMenuOptions: the caravan menu ran unsynced and desynced the client).
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
        return unchecked(m.MetadataToken * 397) ^ (m.DeclaringType?.MetadataToken ?? 0);
    }

    private static string GenericArgs(MethodBase m) =>
        m.IsGenericMethod ? string.Join(",", m.GetGenericArguments().Select(t => t.FullName ?? t.Name)) : "";
}
