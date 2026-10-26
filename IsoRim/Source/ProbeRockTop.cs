using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness keys for the rock-top floor checks (isorim-pz-rocktop-floors-plan.md §6, cliff script, ProbeCliff.cs):
    //   findlip=skip[,half]      a mountain's edge lip on the ground: natural rock (not ore) without a thick roof, open
    //                            ground (unfogged, no edifice) to the south, thick-roofed rock with an open level-1 top (unfogged) over it to the
    //                            north for two cells, and the same
    //                            for half cells east and west (a straight strip); the skip-th by distance from the centre.
    //                            The rule reads only the ground, so it finds the same spot before and after the lip gets
    //                            its floor. seamAt is set to it (look=seam,..., lampat=, padrel=, seamline=).
    //   smoothrel=dx,dz,w,h,storey   the natural floors there smoothed (relative to the found cell)
    //   covercaps=0|1            rock caps under a shown floor hidden (default) or drawn (A/B)
    //   padrel=dx,dz,w,h,terrain,storey   flatpad relative to the found cell (a slab, carpet or smooth stone strip)
    //   seamline=dx0,dz0,dx1,dz1,storey,name   where the map-corner line from (seamAt + d0) to (seamAt + d1) shows on
    //                            screen at that storey's floor height (pixels from the top left of the shot), logged as
    //                            "SEAMLINE name x0 y0 x1 y1" for the offline pixel checks
    public static partial class Probe
    {
        private static bool RockTopKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "covercaps":
                    WallBoxes.HideCoveredCaps = a[0] != "0";
                    Log.Message("[IsoRim probe] covered caps hidden under shown floors " + WallBoxes.HideCoveredCaps);
                    return true;
                case "pickpx":
                {
                    // pickpx=x,y: the cell under that shot pixel (from the top left) and its column, logged (debug)
                    Vector3 m = Floors.ScreenToMap(new Vector2(F(a[0]), Screen.height - F(a[1])));
                    IntVec3 c = m.ToIntVec3();
                    int k = Floors.PointerFloorAt(new Vector2(F(a[0]), Screen.height - F(a[1])));
                    string col = "";
                    IntVec3 g = c.IsValid && Floors.ColumnFloor(map, c, out float ch, out int cz) > 0 ? Floors.Stacked(map, c, -Floors.ColumnFloor(map, c, out ch, out cz)) : c;
                    for (int s = 0; s <= 3; s++)
                    {
                        IntVec3 u = s == 0 ? g : Floors.Stacked(map, g, s);
                        if (u.IsValid && u.InBounds(map))
                        {
                            col += " | L" + s + " " + u + " " + u.GetTerrain(map).defName + " " + (u.GetEdifice(map)?.def.defName ?? "-") + " roof " + (u.GetRoof(map)?.defName ?? "-") + (map.fogGrid.IsFogged(u) ? " fog" : "");
                        }
                    }
                    Log.Message("[IsoRim probe] pickpx " + a[0] + "," + a[1] + ": " + m + " floor " + k + " ground " + g + col);
                    return true;
                }
                case "colat":
                {
                    // colat=x,z[,r]: the columns of ground cells within r (default 1) of (x, z), logged level by level (debug)
                    int r = a.Length > 2 ? I(a[2]) : 1;
                    for (int dz = r; dz >= -r; dz--)
                    {
                        for (int dx = -r; dx <= r; dx++)
                        {
                            IntVec3 g = new IntVec3(I(a[0]) + dx, 0, I(a[1]) + dz);
                            string col = "";
                            for (int s = 0; s <= 3; s++)
                            {
                                IntVec3 u = s == 0 ? g : Floors.Stacked(map, g, s);
                                if (u.IsValid && u.InBounds(map))
                                {
                                    col += " | L" + s + " " + u.GetTerrain(map).defName.Replace("_Rough", "~") + " " + (u.GetEdifice(map)?.def.defName ?? "-") + " " + (u.GetRoof(map)?.defName ?? "-") + (map.fogGrid.IsFogged(u) ? " fog" : "");
                                }
                            }
                            Log.Message("[IsoRim probe] colat " + g + col);
                        }
                    }
                    return true;
                }
                case "smoothrel":
                {
                    // smoothrel=dx,dz,w,h,storey: the natural floor there smoothed (its terrain's smoothed form), relative to the found cell
                    int storey = I(a[4]), n = 0;
                    foreach (IntVec3 g in new CellRect(seamAt.x + I(a[0]), seamAt.z + I(a[1]), I(a[2]), I(a[3])))
                    {
                        IntVec3 c = storey == 0 ? g : Floors.Stacked(map, g, storey);
                        TerrainDef sm = c.IsValid && c.InBounds(map) ? c.GetTerrain(map).smoothedTerrain : null;
                        if (sm != null)
                        {
                            map.terrainGrid.SetTerrain(c, sm);
                            n++;
                        }
                    }
                    Log.Message("[IsoRim probe] smoothrel: " + n + " cells smoothed");
                    return true;
                }
                case "findlip":
                    FindLip(map, I(a[0]), a.Length > 1 ? I(a[1]) : 2);
                    return true;
                case "padrel":
                    CliffStep("flatpad=" + (seamAt.x + I(a[0])) + "," + (seamAt.z + I(a[1])) + "," + a[2] + "," + a[3] + "," + a[4] + "," + a[5]);
                    return true;
                case "seamline":
                {
                    int storey = I(a[4]);
                    float h;
                    int zShift;
                    Floors.FloorPlane(storey, out h, out zShift);
                    Vector2 p0 = View3D.WorldToScreen(new Vector3(seamAt.x + F(a[0]), h, seamAt.z + F(a[1])));
                    Vector2 p1 = View3D.WorldToScreen(new Vector3(seamAt.x + F(a[2]), h, seamAt.z + F(a[3])));
                    Log.Message("[IsoRim probe] SEAMLINE " + a[5] + " " + p0.x.ToString("0.0") + " " + (Screen.height - p0.y).ToString("0.0") + " "
                        + p1.x.ToString("0.0") + " " + (Screen.height - p1.y).ToString("0.0") + " screen " + Screen.width + "x" + Screen.height);
                    return true;
                }
            }
            return HoleKey(key, a, map);
        }

        private static void FindLip(Map map, int skip, int half)
        {
            seamAt = IntVec3.Invalid;
            int rows = Floors.GroundRowsOf(map) > 0 ? Floors.GroundRowsOf(map) : map.Size.z;
            Func<IntVec3, bool> top = g =>
            {
                IntVec3 u = Floors.Stacked(map, g, 1);
                return u.IsValid && u.InBounds(map) && Floors.HasFloor(map, u) && u.GetEdifice(map) == null && !map.fogGrid.IsFogged(u);
            };
            Func<IntVec3, bool> lip = g =>
            {
                IntVec3 s = g + IntVec3.South, n = g + IntVec3.North;
                if (g.x < 1 || g.z < 1 || g.x >= map.Size.x - 1 || g.z >= rows - 1)
                {
                    return false;
                }
                Building b = g.GetEdifice(map), bn = n.GetEdifice(map);
                return b != null && b.def.building != null && b.def.building.isNaturalRock && !b.def.building.isResourceRock
                    && !(map.roofGrid.RoofAt(g)?.isThickRoof ?? false)
                    && s.GetEdifice(map) == null && !map.fogGrid.IsFogged(s) && s.GetTerrain(map).passability == Traversability.Standable && !s.GetTerrain(map).IsWater
                    && bn != null && bn.def.building != null && bn.def.building.isNaturalRock && (map.roofGrid.RoofAt(n)?.isThickRoof ?? false) && top(n) && top(n + IntVec3.North);
            };
            List<KeyValuePair<float, IntVec3>> found = new List<KeyValuePair<float, IntVec3>>();
            IntVec3 centre = new IntVec3(map.Size.x / 2, 0, rows / 2);
            for (int x = 10; x < map.Size.x - 10; x++)
            {
                for (int z = 10; z < rows - 10; z++)
                {
                    IntVec3 g = new IntVec3(x, 0, z);
                    bool ok = true;
                    for (int d = -half; d <= half && ok; d++)
                    {
                        ok = lip(g + new IntVec3(d, 0, 0));
                    }
                    if (ok)
                    {
                        found.Add(new KeyValuePair<float, IntVec3>((g - centre).LengthHorizontalSquared, g));
                    }
                }
            }
            found.Sort((p, q) => p.Key.CompareTo(q.Key));
            if (found.Count > skip)
            {
                seamAt = found[skip].Value;
            }
            IntVec3 up = seamAt.IsValid ? Floors.Stacked(map, seamAt, 1) : IntVec3.Invalid;
            Log.Message("[IsoRim probe] findlip half " + half + ": " + found.Count + " found, using " + seamAt
                + (seamAt.IsValid ? ", roof " + (map.roofGrid.RoofAt(seamAt)?.defName ?? "none") + ", floor above " + (up.IsValid && Floors.HasFloor(map, up)) + " (" + (up.IsValid && up.InBounds(map) ? up.GetTerrain(map).defName : "-") + ")" : ""));
        }
    }
}
