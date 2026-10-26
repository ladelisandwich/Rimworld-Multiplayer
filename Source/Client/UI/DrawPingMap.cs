using System;
using HarmonyLib;
using Verse;

namespace Multiplayer.Client
{

    [HarmonyPatch(typeof(BeautyDrawer), nameof(BeautyDrawer.BeautyDrawerOnGUI))]
    static class DrawPingMap
    {
        static void Postfix()
        {
            if (Multiplayer.Client == null || TickPatch.Simulating) return;

            foreach (var ping in Multiplayer.session.locationPings.pings)
            {
                if (ping.mapId != Find.CurrentMap.uniqueID) continue;
                if (ping.PlayerInfo is not { } player) continue;
                // Sized at its own spot and skipped behind the camera, for the built-in IsoRim's 3D views
                if (!MapScreen.TryMapToUI(ping.mapLoc, out var at)) continue;

                var size = Math.Min(MapScreen.CellSizeAt(ping.mapLoc) * 4, 32f);
                ping.DrawAt(at, player.color, size);
            }
        }
    }
}
