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

    // A projectile flies, gets intercepted and hits in TickInterval, so at rate 15 a bullet showed ~3 positions of
    // its ~22-tick flight and landed up to 14 ticks late (Oct 27, "bullets only show 2 to 5 frames"). A spawned
    // projectile runs at the minimum rate on every map in a session: a constant, so the same on every client, cheap
    // (projectiles are few and short-lived), and smooth even on a map nobody is counted on yet, such as during the
    // command round trip after a map switch. Vanilla uses 1 in view and 15 elsewhere.
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.UpdateRateTicks), MethodType.Getter)]
    public static class VtrSyncProjectilePatch
    {
        static bool Prefix(ref int __result, Projectile __instance)
        {
            if (Multiplayer.Client == null)
                return true;

            __result = __instance.Spawned ? VTRSync.MinimumVtr : VTRSync.MaximumVtr;
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

        // Vtr rates
        public const int MaximumVtr = 15;
        public const int MinimumVtr = 1;

        public static int GetSynchronizedUpdateRate(Thing thing) => thing?.MapHeld?.AsyncTime()?.VTR ?? MaximumVtr;

        // Reports the map this player views (the world map or none included) whenever it differs from the last
        // report. Called once a frame from OnMainThread.Update while this client plays a live session. Polling
        // replaced the CurrentMap setter and world render mode patches (Oct 27): those reported only map-to-map
        // switches, never the map a player is on after hosting or a join (the loads ran with Multiplayer.reloading
        // set, or before the joiner caught up), so no player was ever counted and every map ran at rate 15
        // (bullets drawn at ~3 positions of their flight). A switch made and undone inside one command or frame
        // isn't seen. The server takes the previous map from its own record (ServerPlayingState) and drops a
        // repeat, so this value only needs to follow the view; the counts change only when the command runs.
        public static void UpdateViewedMap()
        {
            int current = WorldRendererUtility.WorldSelected ? WorldMapId : Find.CurrentMap?.uniqueID ?? InvalidMapId;
            if (current == lastMovedToMapId)
                return;

            MpLog.Debug($"VTR viewed map: {lastMovedToMapId}->{current} @ tick {Find.TickManager?.TicksGame ?? 0}");
            Multiplayer.Client.SendCommand(CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(lastMovedToMapId, current));
            lastMovedToMapId = current;
        }

        public static void Reset()
        {
            lastMovedToMapId = InvalidMapId;
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
                // Detect transition to world map (Planet mode). The player count report for the world map is
                // VTRSync.UpdateViewedMap's job (Oct 27).
                if (__result == WorldRenderMode.Planet && lastRenderMode != WorldRenderMode.Planet)
                {
                    // On standalone with streaming, trigger a join point when leaving a map
                    // so each player can save independently without disturbing others
                    if (Multiplayer.session?.ConnectedToStandaloneServer == true && Multiplayer.GameComp.multifaction && Multiplayer.GameComp.asyncTime)
                        Autosaving.SendAutosavingRequest(JoinPointRequestReason.WorldTravel);
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
