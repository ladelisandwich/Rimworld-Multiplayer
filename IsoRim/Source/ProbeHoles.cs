using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness keys for the chamfered holes in natural rock floors (CliffLook.cs, openings; cliff script, ProbeCliff.cs):
    //   findhole=storey,min,max,skip   an enclosed opening on that storey: 4-way connected cells without a floor or a block,
    //                            min..max cells, every cell round it on that storey with a floor or a block; the skip-th by
    //                            distance from the centre. Logged with its bounding box and a map of it; seamAt is set to the
    //                            ground cell of its bounding box's south-west corner (look=seam,..., seamline=).
    //   holerel=dx,dz,w,h,storey       a hole made there (relative to seamAt): on that storey the floor and what stands on it
    //                            removed (open air), on the storey below the natural rock and the roof removed; fog lifted
    //                            round it; the map redrawn. Several calls make other shapes (an L from two rectangles).
    public static partial class Probe
    {
        private static readonly Dictionary<string, IntVec3> markedSpots = new Dictionary<string, IntVec3>();

        private static bool HoleKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "findhole":
                    FindHole(map, I(a[0]), I(a[1]), I(a[2]), a.Length > 3 ? I(a[3]) : 0);
                    return true;
                case "holerel":
                    HoleRel(map, I(a[0]), I(a[1]), I(a[2]), I(a[3]), I(a[4]));
                    return true;
                case "holechamfer":
                    // holechamfer=0|1: the holes' corners chamfered (default) or square as before (A/B); the map redrawn
                    WallBoxes.HoleChamfer = a[0] != "0";
                    map.mapDrawer.RegenerateEverythingNow();
                    Log.Message("[IsoRim probe] hole chamfer " + WallBoxes.HoleChamfer + "; open air worked out " + WallBoxes.OpenAirBuilds + " times, "
                        + WallBoxes.OpenAirMs.ToString("0.00") + " ms in all");
                    return true;
                case "cornerdump":
                {
                    // cornerdump=dx0,dz0,dx1,dz1,storey: the map corners there (relative to seamAt, on that rock storey), logged (debug)
                    int storey = I(a[4]);
                    for (int z = seamAt.z + I(a[3]); z >= seamAt.z + I(a[1]); z--)
                    {
                        for (int x = seamAt.x + I(a[0]); x <= seamAt.x + I(a[2]); x++)
                        {
                            IntVec3 c = storey == 0 ? new IntVec3(x, 0, z) : Floors.Stacked(map, new IntVec3(x, 0, z), storey);
                            string s = WallBoxes.DebugCorner(map, c.x, c.z);
                            if (!s.Contains("kind 0"))
                            {
                                Log.Message("[IsoRim probe] cornerdump ground " + x + "," + z + ": " + s);
                            }
                        }
                    }
                    return true;
                }
                case "markspot":
                    // markspot=name / gospot=name: the found cell remembered and set again later (holes made since change what findspot finds)
                    markedSpots[a[0]] = seamAt;
                    return true;
                case "gospot":
                    seamAt = markedSpots.TryGetValue(a[0], out IntVec3 at) ? at : IntVec3.Invalid;
                    Log.Message("[IsoRim probe] gospot " + a[0] + ": " + seamAt);
                    return true;
                case "drawfog":
                    DebugViewSettings.drawFog = a[0] != "0";
                    Log.Message("[IsoRim probe] fog drawn " + DebugViewSettings.drawFog);
                    return true;
                case "findspot":
                    FindSpot(map, I(a[0]), I(a[1]), I(a[2]), a.Length > 3 && a[3] == "1");
                    return true;
            }
            return false;
        }

        private static bool HoleOpen(Map map, IntVec3 c)
        {
            float h;
            return c.IsValid && c.InBounds(map) && !Floors.HasFloor(map, c) && !Floors.FullBlock(map, c, out h);
        }

        private static void FindHole(Map map, int storey, int min, int max, int skip)
        {
            seamAt = IntVec3.Invalid;
            int rows = Floors.GroundRowsOf(map);
            if (rows <= 0 || storey < 1)
            {
                Log.Warning("[IsoRim probe] findhole: no floors");
                return;
            }
            bool[] seen = new bool[map.Size.x * rows];
            List<KeyValuePair<float, List<IntVec3>>> found = new List<KeyValuePair<float, List<IntVec3>>>();
            IntVec3 centre = new IntVec3(map.Size.x / 2, 0, rows / 2);
            int total = 0;
            for (int x = 1; x < map.Size.x - 1; x++)
            {
                for (int z = 1; z < rows - 1; z++)
                {
                    if (seen[z * map.Size.x + x] || !HoleOpen(map, Floors.Stacked(map, new IntVec3(x, 0, z), storey)))
                    {
                        continue;
                    }
                    List<IntVec3> comp = new List<IntVec3>();
                    Queue<IntVec3> q = new Queue<IntVec3>();
                    q.Enqueue(new IntVec3(x, 0, z));
                    seen[z * map.Size.x + x] = true;
                    bool enclosed = true;
                    while (q.Count > 0)
                    {
                        IntVec3 g = q.Dequeue();
                        comp.Add(g);
                        if (g.x <= 0 || g.z <= 0 || g.x >= map.Size.x - 1 || g.z >= rows - 1)
                        {
                            enclosed = false;
                        }
                        foreach (IntVec3 d in GenAdj.CardinalDirections)
                        {
                            IntVec3 n = g + d;
                            if (n.x < 0 || n.z < 0 || n.x >= map.Size.x || n.z >= rows || seen[n.z * map.Size.x + n.x]
                                || !HoleOpen(map, Floors.Stacked(map, n, storey)))
                            {
                                continue;
                            }
                            seen[n.z * map.Size.x + n.x] = true;
                            q.Enqueue(n);
                        }
                        if (comp.Count > max + 1)
                        {
                            enclosed = false;
                        }
                    }
                    if (!enclosed || comp.Count < min || comp.Count > max)
                    {
                        continue;
                    }
                    // the cells round it, diagonals too: none open (a diagonal touch to other open air is not a hole)
                    HashSet<IntVec3> set = new HashSet<IntVec3>(comp);
                    foreach (IntVec3 g in comp)
                    {
                        foreach (IntVec3 d in GenAdj.AdjacentCells)
                        {
                            if (!set.Contains(g + d) && HoleOpen(map, Floors.Stacked(map, g + d, storey)))
                            {
                                enclosed = false;
                            }
                        }
                    }
                    if (!enclosed)
                    {
                        continue;
                    }
                    total++;
                    float dist = (comp[0] - centre).LengthHorizontalSquared;
                    found.Add(new KeyValuePair<float, List<IntVec3>>(dist, comp));
                }
            }
            found.Sort((p, r) => p.Key.CompareTo(r.Key));
            Dictionary<int, int> bySize = new Dictionary<int, int>();
            foreach (var kv in found)
            {
                int n = kv.Value.Count;
                bySize[n] = bySize.TryGetValue(n, out int was) ? was + 1 : 1;
            }
            StringBuilder sizes = new StringBuilder();
            List<int> keys = new List<int>(bySize.Keys);
            keys.Sort();
            foreach (int k in keys)
            {
                sizes.Append(k).Append("x").Append(bySize[k]).Append(' ');
            }
            if (found.Count <= skip)
            {
                Log.Message("[IsoRim probe] findhole storey " + storey + " cells " + min + ".." + max + ": " + total + " found (sizes " + sizes + "), none at " + skip);
                return;
            }
            List<IntVec3> h = found[skip].Value;
            int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
            foreach (IntVec3 g in h)
            {
                x0 = Math.Min(x0, g.x); z0 = Math.Min(z0, g.z); x1 = Math.Max(x1, g.x); z1 = Math.Max(z1, g.z);
            }
            seamAt = new IntVec3(x0, 0, z0);
            HashSet<IntVec3> cells = new HashSet<IntVec3>(h);
            StringBuilder art = new StringBuilder();
            for (int z = z1 + 1; z >= z0 - 1; z--)
            {
                art.Append(" /");
                for (int x = x0 - 1; x <= x1 + 1; x++)
                {
                    IntVec3 below = Floors.Stacked(map, new IntVec3(x, 0, z), storey - 1);
                    Building b = below.IsValid && below.InBounds(map) ? below.GetEdifice(map) : null;
                    art.Append(cells.Contains(new IntVec3(x, 0, z)) ? (b != null ? 'B' : 'o') : (b != null && b.def.building != null && b.def.building.isNaturalRock ? '#' : '.'));
                }
            }
            IntVec3 up0 = Floors.Stacked(map, seamAt, storey);
            Log.Message("[IsoRim probe] findhole storey " + storey + " cells " + min + ".." + max + ": " + total + " found (sizes " + sizes + "), using #" + skip + ": "
                + h.Count + " cells, box " + x0 + "," + z0 + " to " + x1 + "," + z1 + (h.Count == (x1 - x0 + 1) * (z1 - z0 + 1) ? " (a rectangle)" : "")
                + ", fogged " + map.fogGrid.IsFogged(up0) + "; map (north up, o hole, # natural rock below, B a building below)" + art);
        }

        // findspot=storey,half,skip[,1]: the middle of a (2 half + 1)-square patch of plain mountain top on that storey (a floor,
        // nothing on it, natural rock under it), the patches taken apart from each other (none overlapping, one cell between),
        // nearest the centre first; with 1 the row south of the patch is open air (the plateau's edge). seamAt is set to it.
        private static void FindSpot(Map map, int storey, int half, int skip, bool edge)
        {
            seamAt = IntVec3.Invalid;
            int rows = Floors.GroundRowsOf(map);
            List<KeyValuePair<float, IntVec3>> found = new List<KeyValuePair<float, IntVec3>>();
            IntVec3 centre = new IntVec3(map.Size.x / 2, 0, rows / 2);
            Func<IntVec3, bool> top = g =>
            {
                IntVec3 c = Floors.Stacked(map, g, storey);
                IntVec3 b = Floors.Stacked(map, g, storey - 1);
                if (!c.IsValid || !c.InBounds(map) || !b.IsValid || !b.InBounds(map) || c.GetEdifice(map) != null || !Floors.HasFloor(map, c))
                {
                    return false;
                }
                Building r = b.GetEdifice(map);
                return r != null && r.def.building != null && r.def.building.isNaturalRock && !r.def.building.isResourceRock;
            };
            for (int x = half + 3; x < map.Size.x - half - 3; x++)
            {
                for (int z = half + 3; z < rows - half - 3; z++)
                {
                    bool ok = true;
                    for (int dx = -half; dx <= half && ok; dx++)
                    {
                        for (int dz = -half; dz <= half && ok; dz++)
                        {
                            ok = top(new IntVec3(x + dx, 0, z + dz));
                        }
                        if (ok && edge)
                        {
                            IntVec3 o = Floors.Stacked(map, new IntVec3(x + dx, 0, z - half - 1), storey);
                            ok = HoleOpen(map, o);
                        }
                    }
                    if (ok)
                    {
                        found.Add(new KeyValuePair<float, IntVec3>((new IntVec3(x, 0, z) - centre).LengthHorizontalSquared, new IntVec3(x, 0, z)));
                    }
                }
            }
            found.Sort((p, q) => p.Key.CompareTo(q.Key));
            List<IntVec3> apart = new List<IntVec3>();
            foreach (var kv in found)
            {
                bool far = true;
                foreach (IntVec3 p in apart)
                {
                    far &= Math.Max(Math.Abs(p.x - kv.Value.x), Math.Abs(p.z - kv.Value.z)) > 2 * half + 2;
                }
                if (far)
                {
                    apart.Add(kv.Value);
                }
            }
            if (apart.Count > skip)
            {
                seamAt = apart[skip];
            }
            Log.Message("[IsoRim probe] findspot storey " + storey + " half " + half + (edge ? " at an edge" : "") + ": " + apart.Count + " apart (" + found.Count + " in all), using " + seamAt
                + (seamAt.IsValid ? ", fogged " + map.fogGrid.IsFogged(Floors.Stacked(map, seamAt, storey)) : ""));
        }

        private static void HoleRel(Map map, int dx, int dz, int w, int h, int storey)
        {
            if (!seamAt.IsValid || storey < 1)
            {
                Log.Warning("[IsoRim probe] holerel: no found cell");
                return;
            }
            TerrainDef air = DefDatabase<TerrainDef>.GetNamedSilentFail("PZ_OpenAir");
            if (air == null)
            {
                Log.Warning("[IsoRim probe] holerel: no PZ_OpenAir");
                return;
            }
            int n = 0;
            foreach (IntVec3 g in new CellRect(seamAt.x + dx, seamAt.z + dz, w, h))
            {
                IntVec3 c = Floors.Stacked(map, g, storey);
                IntVec3 b = Floors.Stacked(map, g, storey - 1);
                if (!c.IsValid || !c.InBounds(map) || !b.IsValid || !b.InBounds(map))
                {
                    continue;
                }
                foreach (Thing t in new List<Thing>(c.GetThingList(map)))
                {
                    if (t is Building || t is Plant || t.def.category == ThingCategory.Item || t is Filth)
                    {
                        t.Destroy();
                    }
                }
                map.terrainGrid.SetTerrain(c, air);
                Building r = b.GetEdifice(map);
                if (r != null && r.def.building != null && r.def.building.isNaturalRock)
                {
                    r.Destroy();
                }
                map.roofGrid.SetRoof(b, null);
                foreach (IntVec3 u in new[] { c, b })
                {
                    foreach (IntVec3 o in GenAdj.CellsAdjacent8Way(new TargetInfo(u, map)))
                    {
                        if (o.InBounds(map))
                        {
                            map.fogGrid.Unfog(o);
                        }
                    }
                    map.fogGrid.Unfog(u);
                }
                n++;
            }
            map.mapDrawer.RegenerateEverythingNow();
            Log.Message("[IsoRim probe] holerel " + dx + "," + dz + "," + w + "," + h + " storey " + storey + ": " + n + " cells opened");
        }
    }
}
