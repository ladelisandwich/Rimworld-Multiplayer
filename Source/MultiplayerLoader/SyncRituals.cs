using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Client.Util;
using RimWorld;
using Verse;

namespace MultiplayerLoader;

public static class SyncRituals
{
    private class HookTarget(ISyncMethodWithTargetType syncMethod, string name)
    {
        public readonly ISyncMethodWithTargetType syncMethod = syncMethod;
        public readonly string name = name;
        public int hits;
        public bool loggedHit;
    }

    // Keyed by the target's metadata token (shared by every instantiation of the generic base type)
    // and the exact widget type the call belongs to
    private static Dictionary<(int, Type), HookTarget> hookTargets = new();

    // The targets are methods of a generic base type and the prefix is applied to each closed instantiation.
    // Instantiations over reference types can share one compiled body, so a patch on one can also run for
    // the other, and the prefix can even run twice for one call. Dispatching on the instance's type means a
    // call is only ever synced by its own widget's SyncMethod, and __runOriginal stops a second run of the
    // prefix from syncing a call that an earlier run already synced.
    // Diagnostics for the dev-mode "Patch hit counts": runs of the prefix that matched no target (wrong instance
    // type or token), kept apart from runs that never happened at all
    public static int unmatchedRuns;
    private static HashSet<(int, Type)> loggedUnmatched = new();

    public static bool HookPrefix(object __instance, object[] __args, MethodBase __originalMethod, bool __runOriginal)
    {
        if (!__runOriginal)
            return false;

        var key = (__originalMethod.MetadataToken, __instance.GetType());
        if (!hookTargets.TryGetValue(key, out var target))
        {
            unmatchedRuns++;
            if (Prefs.DevMode && loggedUnmatched.Add(key))
                Log.Message($"MP ritual sync hook ran for {__originalMethod.DeclaringType}.{__originalMethod.Name} on {__instance.GetType()} but matched no target");
            return true;
        }

        target.hits++;
        if (Prefs.DevMode && !target.loggedHit)
        {
            target.loggedHit = true;
            Log.Message($"MP ritual sync hook hit: {target.name} ({target.syncMethod.TargetType.Name})");
        }

        return !target.syncMethod.DoSync(__instance, __args);
    }

    public static IEnumerable<(string name, int hits)> GetHitCounts() =>
        hookTargets.Select(kv => ($"{kv.Value.name} ({kv.Key.Item2.Name})", kv.Value.hits))
            .Append(("prefix runs that matched no target", unmatchedRuns));

    private static MethodInfo syncMethod = AccessTools.Method("Multiplayer.Client.Sync:Method");

    // Registers the SyncMethods and patches their targets with HookPrefix
    public static void Init(Harmony harmony)
    {
        // Same as MpPriority.MpFirst, the prepatched prefix used to run before all Harmony prefixes
        var prefix = new HarmonyMethod(typeof(SyncRituals), nameof(HookPrefix)) { priority = Priority.First + 1 };

        // PawnRoleSelectionWidgetBase<RitualRole> and <PsychicRitualRoleDef> share one compiled body for these methods
        // (reference type instantiations), and Harmony's replacement for that body is built for one instantiation
        // with its concrete types. Patching both left the psychic one in place, so dragging a pawn onto a role in an
        // ordinary ritual threw InvalidCastException (Grouping<string, RitualRole> to IEnumerable<PsychicRitualRoleDef>)
        // in the UI and again when the synced command ran (Oct 21). Psychic rituals need Anomaly, so without it only
        // the ritual widget is patched. With Anomaly both widgets can appear and neither patch is correct for the
        // other, so role changes are left unsynced there until the hooks dispatch per instantiation.
        if (ModsConfig.AnomalyActive)
        {
            Log.Error("Multiplayer: ritual role sync is off with Anomaly active (ritual and psychic ritual role widgets " +
                      "share code that can't be patched for both); role changes in ritual dialogs aren't synced");
            return;
        }

        void Register(Type baseType, string method, Type derivedType, Action<ISyncMethodWithTargetType> postProcess = null, string name = null)
        {
            if (derivedType == typeof(PawnPsychicRitualRoleSelectionWidget))
                return;

            // Sync.Method doesn't apply a Harmony patch and doesn't require the DeclaringType to be the target type
            var newSyncMethod = (ISyncMethodWithTargetType)syncMethod.Invoke(null, [derivedType, method, null]);
            postProcess?.Invoke(newSyncMethod);

            var closedBaseType = derivedType;
            while (!(closedBaseType.IsGenericType && closedBaseType.GetGenericTypeDefinition() == baseType))
                closedBaseType = closedBaseType.BaseType ?? throw new Exception($"{derivedType} doesn't derive from {baseType}");

            var target = AccessTools.DeclaredMethod(closedBaseType, method)
                ?? throw new Exception($"Couldn't find method {closedBaseType}::{method}");

            hookTargets.Add((target.MetadataToken, derivedType), new HookTarget(newSyncMethod, name ?? method));
            harmony.Patch(target, prefix: prefix);
        }

        // ======= PawnRitualRoleSelectionWidget =======

        var ritualRolesSerializer = Serializer.New<IEnumerable<RitualRole>, (PawnRitualRoleSelectionWidget roleSelectionWidget, string Key)>(
            RitualRoleWriter<PawnRitualRoleSelectionWidget, RitualRole>,
            RitualRoleReader<PawnRitualRoleSelectionWidget, RitualRole>
        );
        var psychicRitualRolesSerializer = Serializer.New<IEnumerable<PsychicRitualRoleDef>, (PawnPsychicRitualRoleSelectionWidget roleSelectionWidget, string Key)>(
            RitualRoleWriter<PawnPsychicRitualRoleSelectionWidget, PsychicRitualRoleDef>,
            RitualRoleReader<PawnPsychicRitualRoleSelectionWidget, PsychicRitualRoleDef>
        );

        Register(typeof(PawnRoleSelectionWidgetBase<>), nameof(PawnRoleSelectionWidgetBase<RitualRole>.TryAssignReplace),
            typeof(PawnRitualRoleSelectionWidget),
            m => m.TransformArgument(1, ritualRolesSerializer));
        Register(typeof(PawnRoleSelectionWidgetBase<>), nameof(PawnRoleSelectionWidgetBase<PsychicRitualRoleDef>.TryAssignReplace),
            typeof(PawnPsychicRitualRoleSelectionWidget),
            m => m.TransformArgument(1, psychicRitualRolesSerializer));

        Register(typeof(PawnRoleSelectionWidgetBase<>), nameof(PawnRoleSelectionWidgetBase<RitualRole>.TryAssignAnyRole),
            typeof(PawnRitualRoleSelectionWidget));
        Register(typeof(PawnRoleSelectionWidgetBase<>), nameof(PawnRoleSelectionWidgetBase<PsychicRitualRoleDef>.TryAssignAnyRole),
            typeof(PawnPsychicRitualRoleSelectionWidget));

        Register(typeof(PawnRoleSelectionWidgetBase<>), nameof(PawnRoleSelectionWidgetBase<RitualRole>.TryAssign),
            typeof(PawnRitualRoleSelectionWidget),
            m => m.TransformArgument(1, ritualRolesSerializer));
        Register(typeof(PawnRoleSelectionWidgetBase<>), nameof(PawnRoleSelectionWidgetBase<PsychicRitualRoleDef>.TryAssign),
            typeof(PawnPsychicRitualRoleSelectionWidget),
            m => m.TransformArgument(1, psychicRitualRolesSerializer));

        Register(
            typeof(PawnRoleSelectionWidgetBase<>),
            MpMethodUtil.GetLambda(
                typeof(PawnRoleSelectionWidgetBase<ILordJobRole>),
                nameof(PawnRoleSelectionWidgetBase<RitualRole>.DrawPawnListInternal),
                lambdaOrdinal: 7).Name,
            typeof(PawnRitualRoleSelectionWidget),
            name: "DrawPawnListInternal lambda 7"
        ); // Roles right click delegate (try assign spectate)
        Register(
            typeof(PawnRoleSelectionWidgetBase<>),
            MpMethodUtil.GetLambda(
                typeof(PawnRoleSelectionWidgetBase<ILordJobRole>),
                nameof(PawnRoleSelectionWidgetBase<PsychicRitualRoleDef>.DrawPawnListInternal),
                lambdaOrdinal: 7).Name,
            typeof(PawnPsychicRitualRoleSelectionWidget),
            name: "DrawPawnListInternal lambda 7"
        ); // Roles right click delegate (try assign spectate)

        Register(
            typeof(PawnRoleSelectionWidgetBase<>),
            MpMethodUtil.GetLambda(
                typeof(PawnRoleSelectionWidgetBase<ILordJobRole>),
                nameof(PawnRoleSelectionWidgetBase<RitualRole>.DrawPawnListInternal),
                lambdaOrdinal: 2).Name,
            typeof(PawnRitualRoleSelectionWidget),
            name: "DrawPawnListInternal lambda 2"
        ); // Not participating left click delegate (try assign any role or spectate)
        Register(
            typeof(PawnRoleSelectionWidgetBase<>),
            MpMethodUtil.GetLambda(
                typeof(PawnRoleSelectionWidgetBase<ILordJobRole>),
                nameof(PawnRoleSelectionWidgetBase<PsychicRitualRoleDef>.DrawPawnListInternal),
                lambdaOrdinal: 2).Name,
            typeof(PawnPsychicRitualRoleSelectionWidget),
            name: "DrawPawnListInternal lambda 2"
        ); // Not participating left click delegate (try assign any role or spectate)
    }

    private static (WidgetType roleSelectionWidget, string Key) RitualRoleWriter<WidgetType, RoleType>(IEnumerable<RoleType> roles, object target, object[] _)
        where WidgetType : PawnRoleSelectionWidgetBase<RoleType>
        where RoleType : class, ILordJobRole
    {
        var roleSelectionWidget = (WidgetType)target;
        return (roleSelectionWidget, roleSelectionWidget.assignments.RoleGroups().FirstOrDefault(g => g.SequenceEqual(roles))?.Key);
    }

    private static IEnumerable<RoleType> RitualRoleReader<WidgetType, RoleType>((WidgetType roleSelectionWidget, string Key) data)
        where WidgetType : PawnRoleSelectionWidgetBase<RoleType>
        where RoleType : class, ILordJobRole
    {
        return data.roleSelectionWidget.assignments.RoleGroups().FirstOrDefault(g => g.Key == data.Key);
    }
}
