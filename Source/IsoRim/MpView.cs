using System;
using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Port-owned (outside the subtree, like MpBridge): what Multiplayer needs to show other players' cursors, drag boxes
    // and pings in this player's own 3D view, and to jump the 3D camera to them. Multiplayer binds these by name
    // (Source/Client/UI/IsoRimView.cs, type "IsoRim.MpView"): a standalone IsoRim has no such type, and Multiplayer then
    // keeps its flat drawing. Keep the public signatures in step with IsoRimView.
    //
    // Interface only: main thread, reads the map and this player's view, writes no game state, no Rand. Never called
    // from a tick or a synced command. Draw* only in the Update phase (Graphics.DrawMesh queues for this frame's picture;
    // from OnGUI it would be late or lost).
    //
    // "Map point": the game's x, z (z may be a row of an upper floor, Floors). "True point": x, height in cells, z on
    // the ground rows (what View3D projects).
    public static class MpView
    {
        private const float MarkLift = 0.03f;   // as MineMarks.MarkLift

        // The 3D view is drawing the current map.
        public static bool On
        {
            get { return View3D.Ready && Find.CurrentMap != null && WorldRendererUtility.DrawingMap; }
        }

        // Where this view draws a map point: moved onto its storey (Floors.ColumnFloor reads the layout per map, so it is
        // also right just after a map switch) and, with onSurface, on top of what stands there as this player sees it:
        // the block as cut down here with this player's wall height, else the building's top, else a bridge deck or ramp.
        public static Vector3 TruePoint(Map map, Vector3 at, bool onSurface)
        {
            if (!On || map == null)
            {
                return new Vector3(at.x, 0f, at.z);
            }
            IntVec3 c = new IntVec3(Mathf.FloorToInt(at.x), 0, Mathf.FloorToInt(at.z));
            float floorH = 0f;
            int shift = 0;
            if (Floors.ColumnFloor(map, c, out float h, out int z) > 0)
            {
                floorH = h;
                shift = z;
            }
            float top = 0f;
            if (onSurface && c.InBounds(map))
            {
                top = IsoCellMap.TopOf(map, c);
                if (shift == 0 && Bridges.Active)
                {
                    top = Mathf.Max(top, Bridges.HeightAt(map, at.x, at.z));
                }
            }
            return new Vector3(at.x, floorH + top, at.z - shift);
        }

        // UI position (as UI.MapToUIPosition) of a map point drawn on top of what stands there, and the size in UI
        // pixels of one cell there. False when it lies behind the camera or past the perspective view's reach.
        public static bool MapToUI(Map map, Vector3 at, out Vector2 ui, out float cellSize)
        {
            ui = default;
            cellSize = 0f;
            try
            {
                if (!On || map != Find.CurrentMap)
                {
                    return false;
                }
                Vector3 p = TruePoint(map, at, true);
                if (!TrueToUI(p, out ui))
                {
                    return false;
                }
                if (TrueToUI(p + Vector3.right, out Vector2 r))
                {
                    cellSize = Mathf.Max(cellSize, (r - ui).magnitude);
                }
                if (TrueToUI(p + Vector3.forward, out Vector2 f))
                {
                    cellSize = Mathf.Max(cellSize, (f - ui).magnitude);
                }
                return true;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] MP: placing another player's marker on screen failed: " + e, 93640001);
                return false;
            }
        }

        private static bool TrueToUI(Vector3 p, out Vector2 ui)
        {
            ui = default;
            if (View3D.Perspective)
            {
                // behind the camera the projection mirrors into the sky; past its reach only fog and sky are drawn
                float d = View3D.DepthOf(p);
                if (d <= 0.2f || d > View3D.CamDistance + View3D.ViewDistance)
                {
                    return false;
                }
            }
            Vector2 px = View3D.WorldToScreen(p);   // pixels from the bottom left
            float s = Prefs.UIScale;
            ui = new Vector2(px.x / s, Verse.UI.screenHeight - px.y / s);   // as Patches.LabelDrawPosFor_Prefix
            return true;
        }

        // The map point the local pointer shows, sent to the other players as this player's cursor or ping. With the
        // view off, UI.MouseMapPosition. In 3D, MouseMapPosition is the ground behind a wall top, a building or a head
        // (Floors.ScreenToMap), so the nearest 3D shape the line of sight meets is taken instead: a pawn's spot, or the
        // point of a thing's top under the pointer, kept inside the thing's cells.
        public static Vector3 PointerMapPoint()
        {
            if (Fp.FpHooks.PointerMapPoint(out Vector3 fpPoint)) return fpPoint;   // Port: first-person play
            Vector3 ground = Verse.UI.MouseMapPosition();
            try
            {
                Map map = Find.CurrentMap;
                if (!On)
                {
                    return ground;
                }
                List<Thing> hits = Interaction.PickUnderPointer(map);
                if (hits.Count == 0)
                {
                    return ground;
                }
                Thing t = hits[0];
                if (t is Pawn)
                {
                    Vector3 d = t.DrawPos;
                    return new Vector3(d.x, 0f, d.z);
                }
                Floors.ColumnFloor(map, t.Position, out float floorH, out int shift);   // 0, 0 on the ground
                Vector3 p = View3D.ScreenToHeight(Input.mousePosition, Interaction.PickHeight(t) + floorH);
                CellRect r = t.OccupiedRect();
                return new Vector3(Mathf.Clamp(p.x, r.minX + 0.02f, r.maxX + 0.98f), 0f, Mathf.Clamp(p.z + shift, r.minZ + 0.02f, r.maxZ + 0.98f));
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] MP: finding what the pointer shows failed: " + e, 93640002);
                return ground;
            }
        }

        // ---- drawing (Update phase)

        // A ring on top of what is at a map point (another player's cursor): bright where this player can see that spot,
        // faint through walls and roofs, never smaller than about 14 pixels across.
        public static void DrawMarker(Map map, Vector3 at, Color col)
        {
            try
            {
                if (!On || map != Find.CurrentMap || SeeThrough == null)
                {
                    return;
                }
                Vector3 p = TruePoint(map, at, true);
                float size = 0.7f;
                if (MapToUI(map, at, out _, out float cellPx) && cellPx > 0f)
                {
                    size = Mathf.Max(size, 14f / cellPx);
                }
                Matrix4x4 m = Matrix4x4.TRS(new Vector3(p.x, (p.y + MarkLift) / View3D.Squash, p.z), Quaternion.identity, new Vector3(size, 1f, size));
                Draw(Ring, m, SeeThrough, new Color(col.r, col.g, col.b, 0.35f));
                if (Tested != null)
                {
                    Draw(Ring, m, Tested, new Color(col.r, col.g, col.b, 0.95f));
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] MP: drawing another player's cursor failed: " + e, 93640003);
            }
        }

        // Another player's drag box between two map points, as IsoRim draws its own designation drag
        // (Interaction.DrawDragOutline) in that player's colour: an outline and a faint fill on the plane of a's storey,
        // seen through walls.
        public static void DrawBox(Map map, Vector3 a, Vector3 b, Color col)
        {
            try
            {
                if (!On || map != Find.CurrentMap || SeeThrough == null)
                {
                    return;
                }
                Vector3 pa = TruePoint(map, a, false);
                float bz = b.z - (a.z - pa.z);   // b moved by a's storey's row shift (0 on the ground)
                float x0 = Mathf.Min(pa.x, b.x);
                float x1 = Mathf.Max(pa.x, b.x);
                float z0 = Mathf.Min(pa.z, bz);
                float z1 = Mathf.Max(pa.z, bz);
                if (x1 - x0 < 0.01f && z1 - z0 < 0.01f)
                {
                    return;
                }
                float y = (pa.y + 0.015f) / View3D.Squash;
                float w = LineWidth * 1.5f;
                Color line = new Color(col.r, col.g, col.b, 0.9f);
                Rect(x0, z0, x1, z1, y, new Color(col.r, col.g, col.b, 0.1f));
                Rect(x0 - w, z0 - w, x1 + w, z0, y, line);
                Rect(x0 - w, z1, x1 + w, z1 + w, y, line);
                Rect(x0 - w, z0, x0, z1, y, line);
                Rect(x1, z0, x1 + w, z1, y, line);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] MP: drawing another player's drag box failed: " + e, 93640004);
            }
        }

        // ---- the camera

        // Moves the 3D camera so that what this view shows at a map point is in the middle of the screen. False when the
        // view is off, the point is on another map, or a gravship cutscene holds the camera; the caller then jumps the
        // game's way. First person is left first: it puts the camera back on its colonist every frame.
        public static bool JumpTo(Map map, Vector3 at)
        {
            try
            {
                CameraDriver driver = Find.CameraDriver;
                if (!On || driver == null || map != Find.CurrentMap || View3D.ControlsLocked)
                {
                    return false;
                }
                if (FirstPerson.On)
                {
                    FirstPerson.Leave(null);   // before FocusFor: Pitch reads first person while it is on
                }
                driver.JumpToCurrentMapLoc(FocusFor(map, at));
                return true;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] MP: moving the camera to another player failed: " + e, 93640005);
                return false;
            }
        }

        // The ground point the camera centres on (View3D.ApplyCamera's focus) so that a map point's surface is in the
        // middle of the screen: the line of sight through it slid down to the ground. It uses the settled direction
        // (Pitch, YawTarget), so a turn in progress can't skew it.
        private static Vector3 FocusFor(Map map, Vector3 at)
        {
            Vector3 p = TruePoint(map, at, true);
            Vector3 f = Quaternion.Euler(View3D.Pitch, View3D.YawTarget, 0f) * Vector3.forward;
            if (f.y < -1e-3f)
            {
                p += f * (p.y / -f.y);
            }
            IntVec3 size = map.Size;
            return new Vector3(Mathf.Clamp(p.x, 0f, size.x), 0f, Mathf.Clamp(p.z, 0f, size.z));
        }

        // ---- materials and meshes (copies of Interaction's private ones, so the subtree stays untouched)

        private static Material seeThrough, tested;
        private static Mesh quad, ring;
        private static MaterialPropertyBlock block;
        private static int colorId;

        // Unlit, no depth test: drawn over everything, as IsoRim's own brackets and drag box (Interaction.Overlay).
        private static Material SeeThrough
        {
            get
            {
                if (seeThrough == null && R3DMaterials.Solid != null)
                {
                    seeThrough = R3DMaterials.Make(R3DMaterials.OverlayDef, "IsoRimMp_SeeThrough", 3650);
                }
                return seeThrough;
            }
        }

        // Depth-tested, no depth write. Queue 3160: after the light overlay (3100, so not darkened at night), before
        // the fog lid (3175) and the roofs (3600), which hide it.
        private static Material Tested
        {
            get
            {
                if (tested == null && R3DMaterials.Solid != null)
                {
                    tested = R3DMaterials.Make(R3DMaterials.BlendDef, "IsoRimMp_Tested", 3160);
                }
                return tested;
            }
        }

        // A flat square one unit across, centred on the origin, both sides (as Interaction.Quad).
        private static Mesh Quad
        {
            get
            {
                if (quad == null)
                {
                    quad = new Mesh();
                    quad.SetVertices(new List<Vector3>
                    {
                        new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f)
                    });
                    Color32 w = new Color32(255, 255, 255, 255);
                    quad.SetColors(new List<Color32> { w, w, w, w });
                    quad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 }, 0);
                    quad.RecalculateBounds();
                    MeshGuard.Check(quad, "MP marker quad");
                }
                return quad;
            }
        }

        // A flat ring one unit across (the inner edge at 0.7 of it), both sides.
        private static Mesh Ring
        {
            get
            {
                if (ring == null)
                {
                    const int N = 32;
                    const float Inner = 0.35f;
                    const float Outer = 0.5f;
                    List<Vector3> v = new List<Vector3>();
                    List<Color32> c = new List<Color32>();
                    List<int> t = new List<int>();
                    Color32 w = new Color32(255, 255, 255, 255);
                    for (int i = 0; i < N; i++)
                    {
                        float a = i * Mathf.PI * 2f / N;
                        v.Add(new Vector3(Mathf.Cos(a) * Outer, 0f, Mathf.Sin(a) * Outer));
                        v.Add(new Vector3(Mathf.Cos(a) * Inner, 0f, Mathf.Sin(a) * Inner));
                        c.Add(w);
                        c.Add(w);
                    }
                    for (int i = 0; i < N; i++)
                    {
                        int o0 = 2 * i, i0 = 2 * i + 1;
                        int o1 = 2 * ((i + 1) % N), i1 = 2 * ((i + 1) % N) + 1;
                        t.AddRange(new[] { o0, i0, o1, i0, i1, o1 });   // one side
                        t.AddRange(new[] { o0, o1, i0, i0, o1, i1 });   // the other
                    }
                    ring = new Mesh();
                    ring.SetVertices(v);
                    ring.SetColors(c);
                    ring.SetTriangles(t, 0);
                    ring.RecalculateBounds();
                    MeshGuard.Check(ring, "MP marker ring");
                }
                return ring;
            }
        }

        // How thick lines on the ground are, so they stay about two pixels wide at any zoom (as Interaction.LineWidth).
        private static float LineWidth
        {
            get
            {
                Camera cam = Find.Camera;
                float size = cam != null ? cam.orthographicSize : 20f;
                return Mathf.Clamp(size * 2.4f / Mathf.Max(200f, Screen.height), 0.025f, 0.4f);
            }
        }

        private static void Rect(float x0, float z0, float x1, float z1, float y, Color col)
        {
            if (x1 <= x0 || z1 <= z0)
            {
                return;
            }
            Matrix4x4 m = Matrix4x4.TRS(new Vector3((x0 + x1) * 0.5f, y, (z0 + z1) * 0.5f), Quaternion.identity, new Vector3(x1 - x0, 1f, z1 - z0));
            Draw(Quad, m, SeeThrough, col);
        }

        private static void Draw(Mesh mesh, Matrix4x4 m, Material mat, Color col)
        {
            if (block == null)
            {
                block = new MaterialPropertyBlock();
                colorId = Shader.PropertyToID("_Color");
            }
            block.SetColor(colorId, col);
            Graphics.DrawMesh(mesh, m, mat, 0, null, 0, block);
        }
    }
}
