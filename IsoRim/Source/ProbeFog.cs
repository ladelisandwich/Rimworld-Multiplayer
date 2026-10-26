using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only keys for the fog clip (RoofClip, the fog):
    //   fogscan=1      logs the fogged hollows (open fogged ground in groups of 8 or more cells: bounds, middle, trees,
    //                  pawns, items, tallest top), the colonists, and the revealed rock nearest them that borders fog
    //   fogcheck=x,z   logs the cell's fogged bit and top as the shaders read them (IsoCellMap) and the game's fog grid
    public static partial class Probe
    {
        private static bool FogKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "fogscan":
                    FogScan(map);
                    return true;
                case "fogmap":
                {
                    // fogmap=x,z,r: a character map of the square round (x,z), north up: # fogged rock, R revealed rock,
                    // , fogged ground, . revealed ground, T / t tree (revealed / fogged), C colonist, p / q other pawn.
                    int cx = I(a[0]), cz = I(a[1]), rr = I(a[2]);
                    System.Text.StringBuilder sb = new System.Text.StringBuilder("[IsoRim fogC] map round " + cx + "," + cz + " (west " + (cx - rr) + ", north " + (cz + rr) + ")\n");
                    for (int z = cz + rr; z >= cz - rr; z--)
                    {
                        sb.Append(z.ToString("000")).Append(' ');
                        for (int x = cx - rr; x <= cx + rr; x++)
                        {
                            IntVec3 c = new IntVec3(x, 0, z);
                            if (!c.InBounds(map)) { sb.Append(' '); continue; }
                            bool fog = map.fogGrid.IsFogged(c);
                            Building b = c.GetEdifice(map);
                            Pawn p = c.GetFirstPawn(map);
                            Plant pl = c.GetPlant(map);
                            char ch = b != null && b.def.MakeFog ? (fog ? '#' : 'R') : (fog ? ',' : '.');
                            if (pl != null && pl.def.plant.IsTree) ch = fog ? 't' : 'T';
                            if (p != null) ch = p.IsColonist ? 'C' : (fog ? 'q' : 'p');
                            sb.Append(ch);
                        }
                        sb.Append('\n');
                    }
                    Log.Message(sb.ToString());
                    return true;
                }
                case "cellperf":
                {
                    // cellperf=reps: the whole cell map rebuilt reps times with the floor-above bit and reps times without, interleaved; medians logged
                    int reps = System.Math.Max(1, I(a[0]));
                    List<double> with = new List<double>(), without = new List<double>();
                    IsoCellMap.RebuildNow(map);
                    for (int r = 0; r < reps; r++)
                    {
                        IsoCellMap.CoverBits = false;
                        without.Add(IsoCellMap.RebuildNow(map));
                        IsoCellMap.CoverBits = true;
                        with.Add(IsoCellMap.RebuildNow(map));
                    }
                    with.Sort();
                    without.Sort();
                    Log.Message("[IsoRim probe] cellperf " + map.Size.x + "x" + map.Size.z + ", " + reps + " reps: full cell-map rebuild median " + with[reps / 2].ToString("0.00") + " ms with the floor-above bit, " + without[reps / 2].ToString("0.00") + " ms without (min " + with[0].ToString("0.00") + " / " + without[0].ToString("0.00") + ")");
                    return true;
                }
                case "lidpts":
                    LidPoints(map, I(a[0]), a.Length > 1 ? a[1] : "pts");
                    return true;
                case "fogcheck":
                {
                    IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
                    IsoCellMap cm = IsoCellMap.Existing(map);
                    Log.Message("[IsoRim fogC] cell " + c + ": fogged " + map.fogGrid.IsFogged(c) + ", top " + IsoCellMap.TopOf(map, c).ToString("0.000")
                        + ", fog clip " + RoofClip.FogOn + ", cell map " + (cm != null));
                    return true;
                }
            }
            return false;
        }

        private static void FogScan(Map map)
        {
            int n = map.cellIndices.NumGridCells;
            bool[] seen = new bool[n];
            List<IntVec3> group = new List<IntVec3>();
            Queue<IntVec3> q = new Queue<IntVec3>();
            int groups = 0;
            foreach (IntVec3 start in map.AllCells)
            {
                int si = map.cellIndices.CellToIndex(start);
                if (seen[si] || !OpenFogged(map, start))
                {
                    continue;
                }
                group.Clear();
                q.Enqueue(start);
                seen[si] = true;
                while (q.Count > 0)
                {
                    IntVec3 c = q.Dequeue();
                    group.Add(c);
                    for (int d = 0; d < 4; d++)
                    {
                        IntVec3 o = c + GenAdj.CardinalDirections[d];
                        if (!o.InBounds(map))
                        {
                            continue;
                        }
                        int oi = map.cellIndices.CellToIndex(o);
                        if (!seen[oi] && OpenFogged(map, o))
                        {
                            seen[oi] = true;
                            q.Enqueue(o);
                        }
                    }
                }
                if (group.Count < 8)
                {
                    continue;
                }
                int trees = 0, pawns = 0, items = 0, plants = 0;
                CellRect r = CellRect.FromLimits(group[0], group[0]);
                long sx = 0, sz = 0;
                foreach (IntVec3 c in group)
                {
                    r = r.Encapsulate(c);
                    sx += c.x;
                    sz += c.z;
                    foreach (Thing t in map.thingGrid.ThingsListAtFast(c))
                    {
                        if (t is Plant p)
                        {
                            if (p.def.plant.IsTree) trees++; else plants++;
                        }
                        else if (t is Pawn) pawns++;
                        else if (t.def.category == ThingCategory.Item) items++;
                    }
                }
                groups++;
                Log.Message("[IsoRim fogC] hollow " + groups + ": " + group.Count + " cells, bounds " + r.minX + "," + r.minZ + "-" + r.maxX + "," + r.maxZ
                    + ", middle " + (sx / group.Count) + "," + (sz / group.Count) + ", trees " + trees + ", plants " + plants + ", pawns " + pawns + ", items " + items);
            }
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                Log.Message("[IsoRim fogC] colonist " + p.LabelShort + " at " + p.Position);
            }
            // Revealed rock beside fog nearest the colonists (where a colonist can mine into the fog).
            Pawn first = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            if (first != null)
            {
                IntVec3 best = IntVec3.Invalid;
                float bd = float.MaxValue;
                foreach (IntVec3 c in map.AllCells)
                {
                    Building b = c.GetEdifice(map);
                    if (b == null || !b.def.mineable || map.fogGrid.IsFogged(c) || !map.reachability.CanReach(first.Position, c, PathEndMode.Touch, TraverseParms.For(first)))
                    {
                        continue;
                    }
                    bool fogNext = false;
                    for (int d = 0; d < 4; d++)
                    {
                        IntVec3 o = c + GenAdj.CardinalDirections[d];
                        if (o.InBounds(map) && map.fogGrid.IsFogged(o))
                        {
                            fogNext = true;
                        }
                    }
                    float dd = (c - first.Position).LengthHorizontalSquared;
                    if (fogNext && dd < bd)
                    {
                        bd = dd;
                        best = c;
                    }
                }
                Log.Message("[IsoRim fogC] minable rock by fog nearest " + first.LabelShort + ": " + best);
            }
        }

        private static bool OpenFogged(Map map, IntVec3 c)
        {
            if (!map.fogGrid.IsFogged(c))
            {
                return false;
            }
            Building b = c.GetEdifice(map);
            return b == null || !b.def.MakeFog;
        }
    }
}
