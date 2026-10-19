using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using HarmonyLib;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace Multiplayer.Client.Patches;

public static class TimestampFixer
{
    delegate ref int FieldGetter<in T>(T obj) where T : IExposable;

    private static Dictionary<Type, List<FieldGetter<IExposable>>> timestampFields = new();

    private static void Add<T>(FieldGetter<T> t) where T : IExposable
    {
        timestampFields.GetOrAddNew(typeof(T)).Add(obj => ref t((T)obj));
    }

    static TimestampFixer()
    {
        Add((Pawn_MindState mind) => ref mind.canSleepTick);
        Add((Pawn_MindState mind) => ref mind.canLovinTick);
        Add((Pawn_GuestTracker guest) => ref guest.ticksWhenAllowedToEscapeAgain);
        Add((Pawn_GuestTracker guest) => ref guest.lastPrisonBreakTicks);
    }

    public static int? currentOffset;

    public static void FixPawn(Pawn p, Map oldMap, Map newMap)
    {
        var oldTime = oldMap?.AsyncTime().mapTicks ?? Multiplayer.AsyncWorldTime.worldTicks;
        var newTime = newMap?.AsyncTime().mapTicks ?? Multiplayer.AsyncWorldTime.worldTicks;
        currentOffset = newTime - oldTime;

        MpLog.Debug($"Fixing pawn timestamps for {p} moving from {oldMap?.ToString() ?? "World"}:{oldTime} to {newMap?.ToString() ?? "World"}:{newTime}");

        try
        {
            // Auxiliary save which is used to visit the pawn's data
            VisitExposable(p);
        }
        finally
        {
            currentOffset = null;
        }
    }

    // Like ScribeSaver.DebugOutputFor, but with saveDestroyedThings: a pawn that was just killed is passed to the
    // world from Pawn.Destroy, and DebugOutputFor logs "Deep-saving destroyed thing" for it. The output isn't
    // needed, only the traversal that RegisterDeepSaved_ProcessExposable hooks.
    private static void VisitExposable(IExposable exposable)
    {
        if (Scribe.mode != LoadSaveMode.Inactive)
        {
            Log.Error($"Multiplayer: can't fix timestamps while Scribe mode is {Scribe.mode}");
            return;
        }

        try
        {
            using var writer = XmlWriter.Create(TextWriter.Null, new XmlWriterSettings { OmitXmlDeclaration = true });
            Scribe.saver.writer = writer;
            Scribe.mode = LoadSaveMode.Saving;
            Scribe.saver.savingForDebug = true;
            Scribe_Deep.Look(ref exposable, true, "saveable");
        }
        catch (Exception e)
        {
            Log.Error("Multiplayer: exception while fixing timestamps: " + e);
        }
        finally
        {
            Scribe.saver.ForceStop();
        }
    }

    public static void ProcessExposable(IExposable exposable)
    {
        if (timestampFields.TryGetValue(exposable.GetType(), out var fieldGetters))
            foreach (var del in fieldGetters)
                del(exposable) += currentOffset!.Value;
    }
}

[HarmonyPatch(typeof(DebugLoadIDsSavingErrorsChecker), nameof(DebugLoadIDsSavingErrorsChecker.RegisterDeepSaved))]
static class RegisterDeepSaved_ProcessExposable
{
    static void Prefix(object obj)
    {
        if (TimestampFixer.currentOffset != null && obj is IExposable exposable)
            TimestampFixer.ProcessExposable(exposable);
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
static class PawnDespawn_RememberMap
{
    static void Prefix(Pawn __instance)
    {
        if (Multiplayer.Client == null) return;
        __instance.GetComp<MultiplayerPawnComp>().lastMap = __instance.Map.uniqueID;
    }
}

[HarmonyPatch(typeof(WorldPawns), nameof(WorldPawns.RemovePawn))]
static class WorldPawnsRemovePawn_RememberTick
{
    static void Prefix(Pawn p)
    {
        if (Multiplayer.Client == null) return;
        p.GetComp<MultiplayerPawnComp>().worldPawnRemoveTick = Multiplayer.AsyncWorldTime.worldTicks;
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
static class PawnSpawn_FixTimestamp
{
    static void Postfix(Pawn __instance)
    {
        if (Multiplayer.Client == null) return;
        if (__instance.Map == null) return;

        var comp = __instance.GetComp<MultiplayerPawnComp>();
        if (comp.worldPawnRemoveTick != -1)
        {
            TimestampFixer.FixPawn(__instance, null, __instance.Map);
            comp.worldPawnRemoveTick = -1;
        }
    }
}

[HarmonyPatch(typeof(WorldPawns), nameof(WorldPawns.AddPawn))]
static class WorldPawnsAddPawn_FixTimestamp
{
    static void Prefix(Pawn p)
    {
        if (Multiplayer.Client == null) return;

        var lastMap = p.GetComp<MultiplayerPawnComp>().lastMap;
        if (lastMap != -1)
        {
            TimestampFixer.FixPawn(p, Find.Maps.FirstOrDefault(m => m.uniqueID == lastMap), null);
            p.GetComp<MultiplayerPawnComp>().lastMap = -1;
        }
    }
}
