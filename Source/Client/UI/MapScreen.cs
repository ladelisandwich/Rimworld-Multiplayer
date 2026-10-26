using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    // Map-to-screen helpers for Multiplayer's overlays (other players' cursors, drag boxes, pings) that also work under
    // the built-in IsoRim's turned, tilted or perspective camera. Plain MapToUIPosition follows any camera (it uses
    // Camera.WorldToScreenPoint), but mirrors points behind a perspective camera onto the screen, and a top-down screen
    // rectangle or UI.CurUICellSize stop meaning anything once the camera turns. In the vanilla top-down view these give
    // the same results as before.
    public static class MapScreen
    {
        // UI position of a map point; false when it lies behind the camera.
        public static bool TryMapToUI(Vector3 v, out Vector2 ui)
        {
            // UnityStubs' WorldToScreenPoint returns the depth as z, positive in front of the camera
            if (Find.Camera.WorldToScreenPoint(v).z <= 0f)
            {
                ui = default;
                return false;
            }

            ui = v.MapToUIPosition();
            return true;
        }

        // Size in UI pixels of one map cell at v (UI.CurUICellSize in the top-down view), 0 when v is behind the camera.
        public static float CellSizeAt(Vector3 v)
        {
            if (!TryMapToUI(v, out var at)) return 0f;

            float size = 0f;
            if (TryMapToUI(v + Vector3.right, out var right)) size = Mathf.Max(size, (right - at).magnitude);
            if (TryMapToUI(v + Vector3.forward, out var forward)) size = Mathf.Max(size, (forward - at).magnitude);
            return size;
        }

        // Outline of the ground rectangle between two map points (a drag box), drawn in GUI.color. Its four corners are
        // projected separately, so the box lies on the ground under any camera. Nothing is drawn if a corner is behind
        // the camera. While the projected box is still an upright screen rectangle (the vanilla top-down view) it is
        // drawn with Widgets.DrawBox exactly as before.
        public static void DrawGroundBox(Vector3 a, Vector3 b, float thickness)
        {
            var corners = new[]
            {
                new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, a.z),
                new Vector3(b.x, 0f, b.z), new Vector3(a.x, 0f, b.z)
            };
            var ui = new Vector2[4];
            for (int i = 0; i < 4; i++)
                if (!TryMapToUI(corners[i], out ui[i]))
                    return;

            const float tolerance = 0.5f;
            if (Mathf.Abs(ui[0].y - ui[1].y) < tolerance && Mathf.Abs(ui[1].x - ui[2].x) < tolerance)
            {
                Widgets.DrawBox(new Rect { min = Vector2.Min(ui[0], ui[2]), max = Vector2.Max(ui[0], ui[2]) }, (int)thickness);
                return;
            }

            // Widgets.DrawLine tints with its colour argument on top of GUI.color (UnityStubs multiplies the two), so
            // pass the colour once and draw with a white GUI.color
            var color = GUI.color;
            GUI.color = Color.white;
            try
            {
                for (int i = 0; i < 4; i++)
                    Widgets.DrawLine(ui[i], ui[(i + 1) % 4], color, thickness);
            }
            finally
            {
                GUI.color = color;
            }
        }
    }
}
