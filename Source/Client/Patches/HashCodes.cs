using HarmonyLib;
using Multiplayer.Client.Util;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Verse;

namespace Multiplayer.Client.Patches
{
    [HarmonyPatch(typeof(WindManager), nameof(WindManager.BaseWindSpeedAt))]
    static class BaseWindSpeetGetHashCodeChange
    {
        static readonly MethodInfo OriginalGetHash = AccessTools.Method(typeof(object), nameof(GetHashCode));

        static readonly MethodInfo ChangedGetHash = AccessTools.Method(typeof(BaseWindSpeetGetHashCodeChange), nameof(GetMapHash));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var ci in insts)
            {
                if (ci.Calls(OriginalGetHash))
                    yield return new CodeInstruction(OpCodes.Call, ChangedGetHash);
                else
                    yield return ci;
            }
        }

        static int GetMapHash(Map map)
        {
            if (Multiplayer.Client == null)
                return map.GetHashCode();

            PatchHitCounter.Hit("WindManager.BaseWindSpeedAt map hash");
            return map.Tile;
        }
    }

    [HarmonyPatch(typeof(GlowGrid), MethodType.Constructor, typeof(Map))]
    static class GlowGridCtorPatch
    {
        private static AccessTools.FieldRef<GlowGrid, HashSet<CompGlower>> litGlowers =
            AccessTools.FieldRefAccess<GlowGrid, HashSet<CompGlower>>(nameof(GlowGrid.litGlowers));

        static void Postfix(GlowGrid __instance)
        {
            PatchHitCounter.Hit("GlowGrid litGlowers comparer");
            litGlowers(__instance) = new HashSet<CompGlower>(new CompGlowerEquality());
        }

        class CompGlowerEquality : IEqualityComparer<CompGlower>
        {
            public bool Equals(CompGlower x, CompGlower y) => x == y;
            public int GetHashCode(CompGlower obj) => obj.parent.thingIDNumber;
        }
    }

    [HarmonyPatch]
    static class PatchTargetInfoHashCodes
    {
        static MethodInfo Combine = AccessTools.Method(typeof(Gen), nameof(Gen.HashCombine)).MakeGenericMethod(typeof(Map));

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GlobalTargetInfo), nameof(GlobalTargetInfo.GetHashCode));
            yield return AccessTools.Method(typeof(TargetInfo), nameof(TargetInfo.GetHashCode));
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var inst in insts)
            {
                if (inst.operand as MethodInfo == Combine)
                    inst.operand = AccessTools.Method(typeof(PatchTargetInfoHashCodes), nameof(CombineHashes));

                yield return inst;
            }
        }

        static int CombineHashes(int seed, Map map)
        {
            PatchHitCounter.Hit("TargetInfo.GetHashCode map combine");
            return Gen.HashCombineInt(seed, map?.uniqueID ?? -1);
        }
    }

    // todo does this cause issues?
    [HarmonyPatch(typeof(Tradeable), nameof(Tradeable.GetHashCode))]
    static class TradeableHashCode
    {
        static bool Prefix() => false;

        static void Postfix(Tradeable __instance, ref int __result)
        {
            PatchHitCounter.Hit("Tradeable.GetHashCode");
            __result = RuntimeHelpers.GetHashCode(__instance);
        }
    }

    // System.HashCode seeds itself randomly in every process, so a value built with HashCode.Combine and used as a
    // random seed differs between players. Upstream (1.6, Mono) prefixed HashCode.Combine<int x2..8> itself and returned
    // a deterministic hash while a session was active. On .NET that switches the hash of every HashCode.Combine call
    // in the process when a session starts or ends, and MethodInfo.GetHashCode and ValueTuple.GetHashCode go through
    // HashCode.Combine there, so every hash table filled before hosting or joining stopped finding its keys (Oct 20:
    // SyncActions missed every world object caravan menu, so a client's caravan action ran unsynced and desynced it,
    // and the ritual sync hooks, keyed by a tuple, never matched). Only the call sites that use the value as a seed
    // need a deterministic hash (docs/determinism-scan.md); the others are lookup keys inside one process. So only
    // those calls are replaced, with the same deterministic functions as before.
    [HarmonyPatch]
    static class SeedHashCombinePatch
    {
        static readonly MethodInfo Combine2 = AccessTools.Method(typeof(SeedHashCombinePatch), nameof(SeedCombine2));
        static readonly MethodInfo Combine3 = AccessTools.Method(typeof(SeedHashCombinePatch), nameof(SeedCombine3));

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(Map), nameof(Map.NextGenSeed));
            yield return AccessTools.PropertyGetter(typeof(Gravship), nameof(Gravship.ConstantRandSeed));
            yield return AccessTools.Method(typeof(Building_Trap), nameof(Building_Trap.KnowsOfTrap));
            yield return AccessTools.Method(typeof(LayoutWorker), nameof(LayoutWorker.FillAllRooms));
        }

        public static string CounterName(MethodBase method) => $"HashCode.Combine seed in {method.DeclaringType?.Name}.{method.Name}";

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts, MethodBase original)
        {
            var counter = CounterName(original);
            var replaced = 0;

            foreach (var inst in insts)
            {
                if (inst.operand is MethodInfo { Name: nameof(HashCode.Combine) } method
                    && method.DeclaringType == typeof(HashCode)
                    && method.GetGenericArguments().All(t => t == typeof(int))
                    && (method.GetParameters().Length is 2 or 3))
                {
                    // The counter name goes on the stack as the replacement's last argument
                    yield return new CodeInstruction(OpCodes.Ldstr, counter).MoveLabelsFrom(inst).MoveBlocksFrom(inst);
                    yield return new CodeInstruction(OpCodes.Call, method.GetParameters().Length == 2 ? Combine2 : Combine3);
                    replaced++;
                    continue;
                }

                yield return inst;
            }

            if (replaced == 0)
                Log.Error($"Multiplayer: no HashCode.Combine<int> call found in {original.FullDescription()}, its seed isn't deterministic");
        }

        static int SeedCombine2(int value1, int value2, string counter)
        {
            if (Multiplayer.Client == null)
                return HashCode.Combine(value1, value2);

            PatchHitCounter.Hit(counter);
            return Gen.HashCombineInt(value1, value2);
        }

        static int SeedCombine3(int value1, int value2, int value3, string counter)
        {
            if (Multiplayer.Client == null)
                return HashCode.Combine(value1, value2, value3);

            PatchHitCounter.Hit(counter);
            return DeterministicHash.HashCombineInt(value1, value2, value3);
        }
    }
}
