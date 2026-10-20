using System;
using HarmonyLib;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Patches
{
    // The game's variable tick rate: a thing's TickInterval runs every UpdateRateTicks ticks, and the vanilla
    // value comes from the camera (1 to 5 in view, 15 off screen or on another map). In multiplayer every client
    // must use the same rate, so it's derived from which maps have players on them (AsyncTimeComp.VTR).
    [HarmonyPatch(typeof(GenTicks), nameof(GenTicks.GetCameraUpdateRate))]
    public static class VTRSyncPatch
    {
        static bool Prefix(Thing thing, ref int __result)
        {
            if (Multiplayer.Client == null)
                return true;

            PatchHitCounter.Hit("GenTicks.GetCameraUpdateRate synced");
            // Keep the synchronized update rate until animation timing can be
            // brought back in line with the vanilla value.
            __result = VTRSync.GetSynchronizedUpdateRate(thing);
            return false;
        }
    }

    // GetCameraUpdateRate is only ever called from the tiny Thing.UpdateRateTicks getter, which the .NET JIT can
    // inline it into, and then the patch above never runs: each client ticks a pawn at its own camera's rate.
    // Seen Oct 19 as the recurring desync on whichever map the other player wasn't looking at (a colonist in the
    // host's view entered a cell a tick apart from the client's copy). Thing.DoTick reads UpdateRateTicks through
    // a virtual call, which can't be inlined, so the getters themselves carry the synced rate.
    [HarmonyPatch(typeof(Thing), nameof(Thing.UpdateRateTicks), MethodType.Getter)]
    public static class VtrSyncThingPatch
    {
        static bool Prefix(ref int __result, Thing __instance)
        {
            if (Multiplayer.Client == null)
                return true;

            PatchHitCounter.Hit("Thing.UpdateRateTicks synced");
            __result = VTRSync.GetSynchronizedUpdateRate(__instance);
            return false;
        }
    }

    // Pawn's override calls base.UpdateRateTicks non-virtually (inlineable), so it gets its own copy of the rule:
    // animals always 15 (vanilla), everything else the synced rate
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.UpdateRateTicks), MethodType.Getter)]
    public static class VtrSyncPawnPatch
    {
        static bool Prefix(ref int __result, Pawn __instance)
        {
            if (Multiplayer.Client == null)
                return true;

            PatchHitCounter.Hit("Pawn.UpdateRateTicks synced");
            __result = __instance.RaceProps.Animal ? VTRSync.MaximumVtr : VTRSync.GetSynchronizedUpdateRate(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.UpdateRateTicks), MethodType.Getter)]
    public static class VtrSyncProjectilePatch
    {
        static bool Prefix(ref int __result, Projectile __instance)
        {
            if (Multiplayer.Client == null)
                return true;

            __result = __instance.Spawned ? VTRSync.GetSynchronizedUpdateRate(__instance) : VTRSync.MaximumVtr;
            return false;
        }
    }

    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.UpdateRateTicks), MethodType.Getter)]
    public static class VtrSyncWorldObjectPatch
    {
        static bool Prefix(ref int __result, WorldObject __instance)
        {
            if (Multiplayer.Client == null)
                return true;

            __result = Multiplayer.AsyncWorldTime.VTR;
            return false;
        }
    }

    static class VTRSync
    {
        // Special identifier for the world map (since it doesn't have a uniqueID like regular maps)
        public const int WorldMapId = -2;
        public const int InvalidMapId = -1;
        public static int lastMovedToMapId = InvalidMapId;
        public static int lastSentAtTick = -1;

        // Vtr rates
        public const int MaximumVtr = 15;
        public const int MinimumVtr = 1;

        public static int GetSynchronizedUpdateRate(Thing thing) => thing?.MapHeld?.AsyncTime()?.VTR ?? MaximumVtr;

        public static void SendViewedMapUpdate(int previous, int current)
        {
            if (Multiplayer.reloading)
                return;

            string warn = string.Empty;
            if (previous != lastMovedToMapId)
                warn = $" mismatch between expected previous map {previous} and last moved to map {lastMovedToMapId}";
            else if (previous == current) return;
            int currentTick = Find.TickManager?.TicksGame ?? 0;
            MpLog.Debug($"VTR MapSwitchPatch: {lastMovedToMapId}->{current} @ tick {currentTick}{warn}");
            Multiplayer.Client.SendCommand(CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(previous, current));
            lastMovedToMapId = current;
        }

        public static void Reset()
        {
            lastMovedToMapId = InvalidMapId;
            lastSentAtTick = -1;
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.CurrentMap), MethodType.Setter)]
    static class MapSwitchPatch
    {
        static void Prefix(Map value)
        {
            if (Multiplayer.Client == null) return;

            try
            {
                // WorldRenderModePatch will handle it
                if (VTRSync.lastMovedToMapId == VTRSync.WorldMapId) return;
                int previousMap = GetPreviousMapId();
                int newMap = value?.uniqueID ?? VTRSync.InvalidMapId;
                int currentTick = Find.TickManager?.TicksGame ?? 0;

                if (previousMap == newMap) return;
                if (VTRSync.lastMovedToMapId == newMap && currentTick == VTRSync.lastSentAtTick) return;

                VTRSync.SendViewedMapUpdate(previousMap, newMap);
                VTRSync.lastSentAtTick = currentTick;
            }
            catch (Exception ex)
            {
                MpLog.Error($"VTR MapSwitchPatch error: {ex}");
            }
        }

        private static int GetPreviousMapId()
        {
            bool currentMapIsRemovedAndWasLatestMap = Current.Game.currentMapIndex >= Find.Maps.Count;

            if (currentMapIsRemovedAndWasLatestMap)
            {
                return VTRSync.InvalidMapId;
            }

            return Find.CurrentMap?.uniqueID ?? VTRSync.InvalidMapId;
        }
    }

    [HarmonyPatch(typeof(WorldRendererUtility), nameof(WorldRendererUtility.CurrentWorldRenderMode), MethodType.Getter)]
    static class WorldRenderModePatch
    {
        private static WorldRenderMode lastRenderMode = WorldRenderMode.None;

        static void Postfix(WorldRenderMode __result)
        {
            if (Multiplayer.Client == null) return;

            try
            {
                // Detect transition to world map (Planet mode)
                if (__result == WorldRenderMode.Planet && lastRenderMode != WorldRenderMode.Planet)
                {
                    if (VTRSync.lastMovedToMapId != VTRSync.InvalidMapId && VTRSync.lastMovedToMapId != VTRSync.WorldMapId)
                    {
                        VTRSync.SendViewedMapUpdate(VTRSync.lastMovedToMapId, VTRSync.WorldMapId);
                    }

                    // On standalone with streaming, trigger a join point when leaving a map
                    // so each player can save independently without disturbing others
                    if (Multiplayer.session?.ConnectedToStandaloneServer == true && Multiplayer.GameComp.multifaction && Multiplayer.GameComp.asyncTime)
                        Autosaving.SendAutosavingRequest(JoinPointRequestReason.WorldTravel);
                }
                // Detect transition back to tile map
                else if (__result != WorldRenderMode.Planet && lastRenderMode == WorldRenderMode.Planet)
                {
                    var newMap = Find.CurrentMap?.uniqueID ?? VTRSync.InvalidMapId;
                    if (newMap != VTRSync.InvalidMapId && VTRSync.lastMovedToMapId == VTRSync.WorldMapId)
                    {
                        VTRSync.SendViewedMapUpdate(VTRSync.WorldMapId, newMap);
                    }
                }

                lastRenderMode = __result;
            }
            catch (Exception ex)
            {
                MpLog.Error($"WorldRenderModePatch error: {ex.Message}");
            }
        }
    }
}
