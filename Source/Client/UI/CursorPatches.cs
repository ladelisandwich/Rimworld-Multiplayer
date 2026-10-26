using HarmonyLib;
using Multiplayer.Common;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    [HarmonyPatch(typeof(Targeter), nameof(Targeter.TargeterOnGUI))]
    static class DrawPlayerCursors
    {
        static void Postfix()
        {
            if (Multiplayer.Client == null || !Multiplayer.settings.showCursors || TickPatch.Simulating) return;

            var curMap = Find.CurrentMap.Index;

            foreach (var player in Multiplayer.session.players)
            {
                if (!ShowsCursorOf(player, curMap)) continue;

                var cursorMap = CursorNow(player);
                // On the wall top or building in the built-in IsoRim's 3D view; behind its perspective camera there's
                // nothing to draw
                if (!MapScreen.TrySurfaceToUI(Find.CurrentMap, cursorMap, out var pos, out _)) continue;

                if (Multiplayer.settings.transparentPlayerCursors)
                    GUI.color = player.color * new Color(1, 1, 1, 0.5f);
                else
                    GUI.color = player.color * new Color(1, 1, 1, 1);

                var icon = MultiplayerData.icons.ElementAtOrDefault(player.cursorIcon);
                var drawIcon = icon ?? CustomCursor.CursorTex;
                var iconRect = new Rect(pos, new Vector2(24f * drawIcon.width / drawIcon.height, 24f));

                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(pos, new Vector2(100, 30)).CenterOn(iconRect).Down(20f).Left(icon != null ? 0f : 5f), player.username);
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;

                if (icon != null && MultiplayerData.iconInfos[player.cursorIcon].hasStuff)
                    GUI.color = new Color(0.5f, 0.4f, 0.26f, 0.5f); // Stuff color for wood

                GUI.DrawTexture(iconRect, drawIcon);

                // In the built-in IsoRim's 3D view the box is drawn in the world (DrawPlayerMarkers3D)
                if (player.dragStart != PlayerInfo.Invalid && !IsoRimView.On)
                {
                    GUI.color = player.color * new Color(1, 1, 1, 0.2f);
                    // A rectangle on the ground, so it keeps its shape under a turned or tilted 3D camera
                    MapScreen.DrawGroundBox(player.dragStart, cursorMap, 2f);
                }

                GUI.color = Color.white;
            }
        }

        internal static bool ShowsCursorOf(PlayerInfo player, int mapIndex) =>
            player.username != Multiplayer.username &&
            player.map == mapIndex &&
            player.factionId == Multiplayer.RealPlayerFaction.loadID;

        internal static Vector3 CursorNow(PlayerInfo player) => Vector3.Lerp(
            player.lastCursor,
            player.cursor,
            (float)(Multiplayer.clock.ElapsedMillisDouble() - player.updatedAt) / 50f
        );
    }

    // Other players' cursors and drag boxes in the built-in IsoRim's 3D view, drawn in the world: a ring on the wall
    // top, building or ground each cursor is on, and drag boxes on the ground. World meshes have to be drawn in the
    // Update phase (MapInterfaceUpdate, as the selection brackets); icons and names stay in DrawPlayerCursors (OnGUI).
    [HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceUpdate))]
    static class DrawPlayerMarkers3D
    {
        static void Postfix()
        {
            if (Multiplayer.Client == null || !Multiplayer.settings.showCursors || TickPatch.Simulating) return;

            var map = Find.CurrentMap;
            if (map == null || !IsoRimView.On) return;

            foreach (var player in Multiplayer.session.players)
            {
                if (!DrawPlayerCursors.ShowsCursorOf(player, map.Index)) continue;

                var cursorMap = DrawPlayerCursors.CursorNow(player);
                IsoRimView.DrawMarker(map, cursorMap, player.color);

                if (player.dragStart != PlayerInfo.Invalid)
                    IsoRimView.DrawBox(map, player.dragStart, cursorMap, player.color);
            }
        }
    }

    [HarmonyPatch(typeof(SelectionDrawer), nameof(SelectionDrawer.DrawSelectionOverlays))]
    [StaticConstructorOnStartup]
    static class SelectionBoxPatch
    {
        static HashSet<int> drawnThisUpdate = new HashSet<int>();
        static Dictionary<object, float> selTimes = new Dictionary<object, float>();

        static void Postfix()
        {
            if (Multiplayer.Client == null || TickPatch.Simulating) return;

            foreach (var t in Find.Selector.SelectedObjects.OfType<Thing>())
                drawnThisUpdate.Add(t.thingIDNumber);

            var tempTimes = SelectionDrawer.selectTimes;
            try
            {
                SelectionDrawer.selectTimes = selTimes;

                foreach (var player in Multiplayer.session.players)
                {
                    if (player.factionId != Multiplayer.RealPlayerFaction.loadID) continue;

                    foreach (var sel in player.selectedThings)
                    {
                        if (!drawnThisUpdate.Add(sel.Key)) continue;
                        if (!Multiplayer.ThingsById.TryGetValue(sel.Key, out Thing thing)) continue;
                        if (thing.MapHeld != Find.CurrentMap) continue;

                        selTimes[thing] = sel.Value;
                        SelectionDrawer.DrawSelectionBracketFor(thing, player.selectionBracketMaterial);
                        selTimes.Clear();
                    }
                }
            }
            finally
            {
                SelectionDrawer.selectTimes = tempTimes;
            }

            drawnThisUpdate.Clear();
        }
    }

    [HarmonyPatch(typeof(InspectPaneFiller), nameof(InspectPaneFiller.DrawInspectStringFor))]
    static class DrawInspectPaneStringMarker
    {
        public static ISelectable drawingFor;

        static void Prefix(ISelectable sel) => drawingFor = sel;
        static void Postfix() => drawingFor = null;
    }

    [HarmonyPatch(typeof(InspectPaneFiller), nameof(InspectPaneFiller.DrawInspectString))]
    static class DrawInspectStringPatch
    {
        static void Prefix(ref string str)
        {
            if (Multiplayer.Client == null) return;
            if (!(DrawInspectPaneStringMarker.drawingFor is Thing thing)) return;

            List<string> players = new List<string>();

            foreach (var player in Multiplayer.session.players)
                if (player.selectedThings.ContainsKey(thing.thingIDNumber))
                    players.Add(player.username);

            if (players.Count > 0)
                str += $"\nSelected by: {players.Join()}";
        }
    }
}
