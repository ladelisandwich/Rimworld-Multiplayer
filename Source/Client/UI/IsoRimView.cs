using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    // Calls into the built-in IsoRim's IsoRim.MpView (Source/IsoRim/MpView.cs) to show other players in this player's
    // 3D view. Bound by name on first use, never referenced at compile time: IsoRim.dll is its own assembly loaded by
    // the game, and a standalone IsoRim (MultiplayerStatic logs that case) has no MpView. Unbound, On is false and
    // every caller keeps Multiplayer's flat drawing. Interface only, never from a tick or a synced command.
    public static class IsoRimView
    {
        private delegate bool MapToUIFn(Map map, Vector3 at, out Vector2 ui, out float cellSize);

        private static bool bound;
        private static Func<bool> on;
        private static MapToUIFn mapToUI;
        private static Func<Vector3> pointerMapPoint;
        private static Action<Map, Vector3, Color> drawMarker;
        private static Action<Map, Vector3, Vector3, Color> drawBox;
        private static Func<Map, Vector3, bool> jumpTo;

        private static void Bind()
        {
            bound = true;
            var type = GenTypes.GetTypeInAnyAssembly("IsoRim.MpView", "IsoRim");
            if (type == null) return;

            try
            {
                mapToUI = AccessTools.MethodDelegate<MapToUIFn>(AccessTools.DeclaredMethod(type, "MapToUI"));
                pointerMapPoint = AccessTools.MethodDelegate<Func<Vector3>>(AccessTools.DeclaredMethod(type, "PointerMapPoint"));
                drawMarker = AccessTools.MethodDelegate<Action<Map, Vector3, Color>>(AccessTools.DeclaredMethod(type, "DrawMarker"));
                drawBox = AccessTools.MethodDelegate<Action<Map, Vector3, Vector3, Color>>(AccessTools.DeclaredMethod(type, "DrawBox"));
                jumpTo = AccessTools.MethodDelegate<Func<Map, Vector3, bool>>(AccessTools.DeclaredMethod(type, "JumpTo"));
                on = AccessTools.MethodDelegate<Func<bool>>(AccessTools.DeclaredPropertyGetter(type, "On"));
            }
            catch (Exception e)
            {
                on = null;
                Log.Error($"Multiplayer: couldn't bind IsoRim.MpView, so other players are drawn flat in the 3D view: {e}");
            }
        }

        // The built-in IsoRim's 3D view is drawing the current map
        public static bool On
        {
            get
            {
                if (!bound) Bind();
                return on != null && on();
            }
        }

        // UI position of a map point on top of what this player sees there (wall top, building, floor), and the UI size
        // of one cell there. False when it's behind the camera.
        public static bool MapToUI(Map map, Vector3 at, out Vector2 ui, out float cellSize)
        {
            if (On) return mapToUI(map, at, out ui, out cellSize);
            ui = default;
            cellSize = 0f;
            return false;
        }

        // The map point the local pointer shows: in 3D the wall top, building or pawn under it instead of the ground
        // behind it (UI.MouseMapPosition)
        public static Vector3 PointerMapPoint() => On ? pointerMapPoint() : UI.MouseMapPosition();

        // Update phase only (MapInterface.MapInterfaceUpdate)
        public static void DrawMarker(Map map, Vector3 at, Color color)
        {
            if (On) drawMarker(map, at, color);
        }

        public static void DrawBox(Map map, Vector3 a, Vector3 b, Color color)
        {
            if (On) drawBox(map, a, b, color);
        }

        // Centres the 3D camera on what it shows at a map point of the current map, leaving first person. False when the
        // view is off or can't jump; the caller then jumps the game's way.
        public static bool JumpTo(Map map, Vector3 at) => On && jumpTo(map, at);
    }
}
