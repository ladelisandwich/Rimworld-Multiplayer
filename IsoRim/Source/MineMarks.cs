using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Rock (and any mineable block: ore, collapsed rock) marked for mining (the game's Mine / MineVein cell designations)
    // stands out in 3D, where the game's flat mark on the ground is hidden by the tall block:
    //   - the whole block is tinted toward a clear green (Tint), the stone texture still showing through;
    //   - a faint light line runs along the top edges of the marked group (PrintOutline: only where the block beside is
    //     not marked too, so a marked area gets one outline round it, no inner lines);
    //   - the game's own mark (its Mine / MineVein texture) lies on the block's top instead of on the ground
    //     (MarkHeight; Patches.DesignationDraw_Prefix), over the fog lid where the rock is fogged.
    //
    // One place for the block: the block printers (WallBoxes.PrintBlock / PrintRock / PrintPlainSides) take their colour
    // through Tint and their top-edge line through PrintOutline. The game redraws nothing when a designation is added or
    // removed (its cell marks are drawn every frame), so the patches on DesignationManager.AddDesignation /
    // RemoveDesignation (Patches.MineDesignation_Postfix) mark the block of that cell and its four neighbours (their
    // outline) to be redrawn: the wall layer of their sections only (or the game's things layer for a block it prints, with
    // the mod's wall layer off). No cost per frame; one section redraw per changed section, when the game next draws it.
    public static class MineMarks
    {
        // The tint's colour: a clear, slightly cool green (the game's own mark, a faint terracotta square, lies on top of
        // the block as it is). Green reads on every stone (grey granite, white marble, sandstone, dark slate) and on the
        // ores, and keeps its hue at night.
        public static readonly Color MarkColour = new Color(0.30f, 1f, 0.45f, 1f);

        // How far the block's colour goes toward the mark's colour (0 = not at all, 1 = the mark's colour).
        public const float DefaultStrength = 0.4f;   // the stone and ore still read through it

        // Harness A/B (probe keys minetint=, minetintcol=): another strength (below 0: the default) or colour.
        public static float StrengthOverride = -1f;
        public static Color? ColourOverride;

        public static float Strength
        {
            get { return StrengthOverride >= 0f ? StrengthOverride : DefaultStrength; }
        }

        public static Color Colour
        {
            get { return ColourOverride ?? MarkColour; }
        }

        // Whether the block is a mineable whose cell is marked for mining.
        public static bool Marked(Thing t)
        {
            if (t == null || !t.Spawned || !t.def.mineable)
            {
                return false;
            }
            Map map = t.Map;
            DesignationManager dm = map != null ? map.designationManager : null;
            if (dm == null)
            {
                return false;
            }
            IntVec3 c = t.Position;
            return dm.DesignationAt(c, DesignationDefOf.Mine) != null || dm.DesignationAt(c, DesignationDefOf.MineVein) != null;
        }

        // The colour a block is drawn in: its own, or tinted toward the mark's colour when marked for mining.
        public static Color Tint(Thing t, Color own)
        {
            return Marked(t) ? Toward(own) : own;
        }

        // A colour tinted toward the mark's colour.
        public static Color Toward(Color own)
        {
            Color c = Color.Lerp(own, Colour, Strength);
            c.a = own.a;
            return c;
        }

        // Harness A/B (probe keys mineoutline=, minemarktop=): the outline and the mark on top off.
        public static bool OutlineOn = true;
        public static bool MarkOnTop = true;

        // The outline: how wide (cells), how far toward white from the block's tinted top colour (faint), how far over
        // the top it lies (cells).
        public const float LineWidth = 0.035f;
        public static float LineLight = 0.45f;
        private const float LineLift = 0.006f;

        // Whether the block in cell c is marked for mining.
        public static bool MarkedAt(Map map, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return false;
            }
            Building b = c.GetEdifice(map);
            return b != null && Marked(b);
        }

        // The outline's colour on a block whose top is drawn in (tinted) colour top.
        public static Color32 LineColour(Color top)
        {
            Color c = Color.Lerp(top, Color.white, LineLight);
            return new Color32((byte)Mathf.Clamp(c.r * 255f, 0f, 255f), (byte)Mathf.Clamp(c.g * 255f, 0f, 255f), (byte)Mathf.Clamp(c.b * 255f, 0f, 255f), 255);
        }

        private static readonly IntVec3[] Out = { IntVec3.South, IntVec3.North, IntVec3.West, IntVec3.East };
        private static readonly int[] EdgeFrom = { 0, 2, 1, 3 };   // per face (S, N, W, E): its corners (SW 0, NW 1, NE 2, SE 3) going round anticlockwise
        private static readonly int[] EdgeTo = { 3, 1, 0, 2 };
        private static readonly int[] FaceFrom = { 0, 2, 1, 3 };   // per corner: the face whose edge leaves it, and the one whose edge arrives at it
        private static readonly int[] FaceTo = { 2, 1, 3, 0 };
        private static readonly Vector3[] lineIn = new Vector3[4];
        private static readonly Vector3[] lineOut = new Vector3[4];
        private static readonly float[] offIn = new float[4];
        private static readonly float[] offOut = new float[4];
        private static readonly bool[] edge = new bool[4];

        // The faint top-edge line of a block marked for mining (t): along each edge of its top (top: the four corners SW,
        // NW, NE, SE in cells, y = height, as drawn) whose neighbour is not marked; rimmed[f] = a dark rim lies along that
        // edge already (the light line goes just inside it, rimWidth in). Where two marked neighbours meet round an unmarked
        // diagonal, a small square closes the group's corner. Written into sm (a plain vertex-coloured solid). Main thread
        // only (static scratch arrays), as the section layers are built.
        public static void PrintOutline(LayerSubMesh sm, Thing t, Vector3[] top, bool[] rimmed, float rimWidth, Color topColour, float squash)
        {
            if (t == null || !Marked(t))
            {
                return;
            }
            NoteTop(t, top);
            if (!OutlineOn || sm == null || sm.verts.Count + 32 > MeshWriter.MaxVerts)
            {
                return;
            }
            Map map = t.Map;
            IntVec3 c = t.Position;
            bool any = false;
            for (int f = 0; f < 4; f++)
            {
                edge[f] = !MarkedAt(map, c + Out[f]);
                any |= edge[f];
            }
            Color32 col = LineColour(topColour);
            if (any)
            {
                for (int f = 0; f < 4; f++)
                {
                    // where a band lies across the top: just inside the dark rim, or from the edge itself; an edge without a
                    // band keeps 0 (the band beside it runs on to it)
                    offOut[f] = edge[f] && rimmed[f] ? rimWidth : 0f;
                    offIn[f] = edge[f] ? offOut[f] + LineWidth : 0f;
                }
                for (int k = 0; k < 4; k++)
                {
                    lineOut[k] = Inset(top, k, offOut[FaceFrom[k]], offOut[FaceTo[k]]);
                    lineIn[k] = Inset(top, k, offIn[FaceFrom[k]], offIn[FaceTo[k]]);
                }
                for (int f = 0; f < 4; f++)
                {
                    if (!edge[f])
                    {
                        continue;
                    }
                    int a = EdgeFrom[f], b = EdgeTo[f];
                    MeshWriter.Quad(sm, W(lineOut[a], top, squash), W(lineIn[a], top, squash), W(lineIn[b], top, squash), W(lineOut[b], top, squash), col);
                }
            }
            // the inner corners of the group's outline: both neighbours marked, the diagonal between them not
            for (int k = 0; k < 4; k++)
            {
                int f1 = FaceFrom[k], f2 = FaceTo[k];
                if (edge[f1] || edge[f2] || MarkedAt(map, c + Out[f1] + Out[f2]))
                {
                    continue;
                }
                Vector3 p = top[k];
                Vector3 d1 = DirIn(f1), d2 = DirIn(f2);
                Vector3 q = p + d1 * LineWidth, r = p + (d1 + d2) * LineWidth, s = p + d2 * LineWidth;
                MeshWriter.Quad(sm, W(p, top, squash), W(q, top, squash), W(r, top, squash), W(s, top, squash), col);
            }
        }

        // Into the cell, across face f.
        private static Vector3 DirIn(int f)
        {
            IntVec3 o = Out[f];
            return new Vector3(-o.x, 0f, -o.z);
        }

        // Corner k of the top moved in so that it lies offFrom inside the edge leaving it and offTo inside the edge
        // arriving at it (the two edges' lines moved in, met: a mitre, also on chamfered tops).
        private static Vector3 Inset(Vector3[] top, int k, float offFrom, float offTo)
        {
            Vector3 p = top[k];
            if (offFrom <= 0f && offTo <= 0f)
            {
                return p;
            }
            Vector2 n1 = InNormal(top[k], top[EdgeTo[FaceFrom[k]]]);
            Vector2 n2 = InNormal(top[EdgeFrom[FaceTo[k]]], top[k]);
            float det = n1.x * n2.y - n1.y * n2.x;
            Vector2 x;
            if (Mathf.Abs(det) < 0.05f)
            {
                x = (n1.sqrMagnitude > 0f ? n1 : n2) * Mathf.Max(offFrom, offTo);
            }
            else
            {
                x = new Vector2((offFrom * n2.y - offTo * n1.y) / det, (n1.x * offTo - n2.x * offFrom) / det);
            }
            return new Vector3(p.x + x.x, p.y, p.z + x.y);
        }

        // The inward normal (x, z) of a top edge from a to b (the top goes round anticlockwise: inward is to the left).
        private static Vector2 InNormal(Vector3 a, Vector3 b)
        {
            Vector2 d = new Vector2(b.x - a.x, b.z - a.z);
            if (d.sqrMagnitude < 1e-8f)
            {
                return Vector2.zero;
            }
            d.Normalize();
            return new Vector2(-d.y, d.x);
        }

        // A point of the line on the top, in the squashed world: its height read off the top as drawn (the top quad is the
        // triangles SW-NW-NE and SW-NE-SE), just over it.
        private static Vector3 W(Vector3 p, Vector3[] top, float squash)
        {
            float y = HeightOn(top, p.x, p.z);
            return new Vector3(p.x, (y + LineLift) / squash, p.z);
        }

        private static float HeightOn(Vector3[] top, float x, float z)
        {
            float y;
            if (Bary(top[0], top[1], top[2], x, z, out y) || Bary(top[0], top[2], top[3], x, z, out y))
            {
                return y;
            }
            return Mathf.Max(Mathf.Max(top[0].y, top[1].y), Mathf.Max(top[2].y, top[3].y));
        }

        private static bool Bary(Vector3 a, Vector3 b, Vector3 c, float x, float z, out float y)
        {
            y = 0f;
            float det = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Mathf.Abs(det) < 1e-8f)
            {
                return false;
            }
            float l1 = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / det;
            float l2 = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / det;
            float l3 = 1f - l1 - l2;
            const float e = -0.02f;
            if (l1 < e || l2 < e || l3 < e)
            {
                return false;
            }
            y = l1 * a.y + l2 * b.y + l3 * c.y;
            return true;
        }

        // The top of each marked block as last built (cells: its corners' extent and highest corner), noted by the block
        // printers (PrintOutline) when its section is built, so the mark is laid on it each frame without working it out
        // again. Rugged and chamfered rock tops are smaller than the cell. Per map; a cell is noted again whenever its block
        // is rebuilt (marked, unmarked, a neighbour mined), and read only while the cell carries a mine mark.
        private struct TopBox
        {
            public float x0, z0, x1, z1, y;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Map, System.Collections.Generic.Dictionary<int, TopBox>> tops =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Map, System.Collections.Generic.Dictionary<int, TopBox>>();

        private static void NoteTop(Thing t, Vector3[] top)
        {
            Map map = t.Map;
            if (map == null)
            {
                return;
            }
            TopBox b = new TopBox { x0 = top[0].x, z0 = top[0].z, x1 = top[0].x, z1 = top[0].z, y = top[0].y };
            for (int k = 1; k < 4; k++)
            {
                b.x0 = Mathf.Min(b.x0, top[k].x);
                b.z0 = Mathf.Min(b.z0, top[k].z);
                b.x1 = Mathf.Max(b.x1, top[k].x);
                b.z1 = Mathf.Max(b.z1, top[k].z);
                b.y = Mathf.Max(b.y, top[k].y);
            }
            tops.GetOrCreateValue(map)[map.cellIndices.CellToIndex(t.Position)] = b;
        }

        // How far over the top the mark lies (cells): over the fog lid, which lies 0.012 over the tops.
        private const float MarkLift = 0.03f;

        // Where the game's mark for cell c is drawn (DrawHook.xform over the game's own matrix: a 1 x 1 plane on the cell's
        // centre): flat on the top of the block it marks, fitted to that top as built; over a fogged cell, over the fog lid
        // (the whole cell, at the height the lid lies on: IsoCellMap.TopOf); a cell without a built top (open ground, a block
        // the game draws), at the top of what stands there. Heights on the cell's own storey (stacked floors move it on).
        public static Matrix4x4 MarkMatrix(Map map, IntVec3 c)
        {
            float y;
            TopBox b;
            System.Collections.Generic.Dictionary<int, TopBox> d;
            if (!map.fogGrid.IsFogged(c) && tops.TryGetValue(map, out d) && d.TryGetValue(map.cellIndices.CellToIndex(c), out b) && MarkedAt(map, c))
            {
                y = (b.y + MarkLift) / View3D.Squash;
                return Matrix4x4.Translate(new Vector3((b.x0 + b.x1) * 0.5f, y, (b.z0 + b.z1) * 0.5f))
                    * Matrix4x4.Scale(new Vector3(b.x1 - b.x0, 1f, b.z1 - b.z0))
                    * Matrix4x4.Translate(new Vector3(-(c.x + 0.5f), 0f, -(c.z + 0.5f)));
            }
            y = (IsoCellMap.TopOf(map, c) + MarkLift) / View3D.Squash;
            return Matrix4x4.Translate(new Vector3(0f, y, 0f));
        }

        // Whether this designation's mark is drawn on the top of its block (a Mine / MineVein mark on a cell).
        public static bool OnTop(Designation des)
        {
            return MarkOnTop && des != null && !des.target.HasThing && (des.def == DesignationDefOf.Mine || des.def == DesignationDefOf.MineVein);
        }

        // A mine designation was added to or removed from cell c: its block is redrawn, and the blocks beside it (their
        // outline runs on round it or stops at it): the wall layer of their sections; or the game's things layer when a block
        // is printed there.
        public static void Changed(Map map, Designation des)
        {
            if (des == null || map == null || !View3D.Active || Current.ProgramState != ProgramState.Playing
                || (des.def != DesignationDefOf.Mine && des.def != DesignationDefOf.MineVein) || des.target.HasThing)
            {
                return;
            }
            try
            {
                IntVec3 c = des.target.Cell;
                if (!c.InBounds(map) || map.mapDrawer == null)
                {
                    return;
                }
                Redraw(map, c);
                for (int f = 0; f < 4; f++)
                {
                    Redraw(map, c + Out[f]);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Redrawing a block marked for mining failed: " + e, 93611290);
            }
        }

        private static void Redraw(Map map, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return;
            }
            Building b = c.GetEdifice(map);
            if (b == null || !b.def.mineable)
            {
                return;
            }
            if (SectionLayer_IsoRimWalls.Handles(b))
            {
                SectionLayer_IsoRimWalls.Mark(map, c);
            }
            else
            {
                map.mapDrawer.MapMeshDirty(c, MapMeshFlagDefOf.Things);
            }
        }
    }
}
