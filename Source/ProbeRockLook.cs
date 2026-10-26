using System;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness keys for the rock-cap texture and upper-floor night light work (cliff script, ProbeCliff.cs):
    //   lightov=0|1               the game's lighting overlay drawn or not (debug view setting), for A/B shots
    //   rebuildperf=reps,label    every block's wall, terrain and lighting overlay layers rebuilt reps times (one warm-up
    //                             pass first), each layer type timed per rep, ground blocks and upper-floor blocks apart;
    //                             logs the median and min of the per-rep totals
    //   buildfloors=x,z,size,n    a built house over ground rect (x, z, size): walls round it on the ground and on floors
    //                             1..n-1 (each with a gap in the south side), slabs (ProjectZ PZ_UpperFloor) on floors 1..n,
    //                             a lit torch lamp in the middle of the ground and of every floor; fog lifted over it
    //   lampat=dx,dz,storey       a lit torch lamp on that storey, (dx, dz) from the cell findcap/findtop/findseam found
    //   setseam=x,z               that ground cell as the found cell (look=seam,...)
    public static partial class Probe
    {
        private static bool RockLookKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "lightov":
                    DebugViewSettings.drawLightingOverlay = a[0] != "0";
                    Log.Message("[IsoRim probe] lighting overlay drawn " + DebugViewSettings.drawLightingOverlay);
                    return true;
                case "rebuildperf":
                    RebuildPerf(map, Math.Max(1, I(a[0])), a.Length > 1 ? a[1] : "");
                    return true;
                case "buildfloors":
                    BuildFloors(map, I(a[0]), I(a[1]), I(a[2]), I(a[3]));
                    return true;
                case "lampat":
                {
                    int storey = I(a[2]);
                    IntVec3 g = seamAt + new IntVec3(I(a[0]), 0, I(a[1]));
                    IntVec3 c = storey == 0 ? g : Floors.Stacked(map, g, storey);
                    Lamp(map, c);
                    return true;
                }
                case "rockcaps":
                    WallBoxes.TerrainCaps = a[0] != "0";
                    map.mapDrawer.RegenerateEverythingNow();
                    Log.Message("[IsoRim probe] terrain rock caps " + WallBoxes.TerrainCaps);
                    return true;
                case "setseam":
                    seamAt = new IntVec3(I(a[0]), 0, I(a[1]));
                    return true;
                case "rocksaround":
                {
                    // rocksaround=r: the natural rock defs within r of the found cell, with their natural terrain (debug)
                    Dictionary<string, int> n = new Dictionary<string, int>();
                    foreach (IntVec3 c in CellRect.CenteredOn(seamAt, I(a[0])))
                    {
                        Building b = c.InBounds(map) ? c.GetEdifice(map) : null;
                        if (b != null && b.def.building != null && b.def.building.isNaturalRock)
                        {
                            TerrainDef nt = b.def.building.naturalTerrain;
                            string k = b.def.defName + " -> " + (nt != null ? nt.defName + " " + nt.texturePath + " " + nt.DrawColor : "none");
                            Material tm = nt != null && nt.graphic != null ? nt.graphic.MatSingle : null;
                            if (tm != null)
                            {
                                k += ", shader " + tm.shader.name + ", main " + (tm.mainTexture != null ? tm.mainTexture.name + " " + tm.mainTexture.width + "x" + tm.mainTexture.height : "-")
                                    + ", ST " + tm.mainTextureScale + tm.mainTextureOffset + ", alphaAdd " + (tm.HasProperty("_AlphaAddTex") && tm.GetTexture("_AlphaAddTex") != null ? tm.GetTexture("_AlphaAddTex").name : "-") + ", polygon " + (tm.HasProperty("_PolygonTex") && tm.GetTexture("_PolygonTex") != null ? tm.GetTexture("_PolygonTex").name : "-");
                            }
                            int v;
                            n[k] = n.TryGetValue(k, out v) ? v + 1 : 1;
                        }
                    }
                    foreach (KeyValuePair<string, int> kv in n)
                    {
                        Log.Message("[IsoRim probe] rocksaround " + kv.Key + ": " + kv.Value);
                    }
                    return true;
                }
                case "flatpad":
                {
                    // flatpad=x,z,w,h,terrain,storey: plants and buildings cleared and the terrain set there, fog lifted
                    TerrainDef def = DefDatabase<TerrainDef>.GetNamed(a[4]);
                    int storey = I(a[5]);
                    foreach (IntVec3 g in new CellRect(I(a[0]), I(a[1]), I(a[2]), I(a[3])))
                    {
                        IntVec3 c = storey == 0 ? g : Floors.Stacked(map, g, storey);
                        if (!c.IsValid || !c.InBounds(map))
                        {
                            continue;
                        }
                        foreach (Thing t in new List<Thing>(c.GetThingList(map)))
                        {
                            if (t is Building || t is Plant || t.def.category == ThingCategory.Item)
                            {
                                t.Destroy();
                            }
                        }
                        map.terrainGrid.SetTerrain(c, def);
                        map.fogGrid.Unfog(c);
                    }
                    return true;
                }
                case "unfogaround":
                {
                    // unfogaround=r: fog lifted r cells round the found cell on every storey
                    int r = I(a[0]);
                    for (int k = 0; k <= Floors.MaxFloors; k++)
                    {
                        foreach (IntVec3 g in CellRect.CenteredOn(seamAt, r))
                        {
                            IntVec3 c = k == 0 ? g : Floors.Stacked(map, g, k);
                            if (c.IsValid && c.InBounds(map))
                            {
                                map.fogGrid.Unfog(c);
                            }
                        }
                        if (Floors.GroundRowsOf(map) <= 0)
                        {
                            break;
                        }
                    }
                    return true;
                }
                case "glowat":
                {
                    // glowat=dx,dz,storey: the glow texture's corner colour there, the floors vector and the sky (debug)
                    int storey = I(a[2]);
                    IntVec3 g = seamAt + new IntVec3(I(a[0]), 0, I(a[1]));
                    IntVec3 c = storey == 0 ? g : Floors.Stacked(map, g, storey);
                    IsoGlowMap gm = IsoGlowMap.For(map);
                    Color32[] corners = HarmonyLib.Traverse.Create(gm).Field("corners").GetValue<Color32[]>();
                    Color32 v = corners[c.z * gm.width + c.x];
                    Log.Message("[IsoRim probe] glowat " + c + ": corner " + v + ", glow grid " + map.glowGrid.VisualGlowAt(c) + ", roof " + c.GetRoof(map)
                        + ", floors " + Floors.GlowFloors() + ", sky " + IsoGlow.SkyFor(map) + ", terrain " + c.GetTerrain(map) + ", edifice " + c.GetEdifice(map));
                    return true;
                }
            }
            return RockTopKey(key, a, map);
        }

        private static void Lamp(Map map, IntVec3 c)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                Log.Warning("[IsoRim probe] lamp: cell " + c + " not on the map");
                return;
            }
            Building old = c.GetEdifice(map);
            if (old != null)
            {
                old.Destroy();
            }
            Thing t = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("TorchLamp"));
            t.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(t, c, map, Rot4.North);
            CompRefuelable fuel = t.TryGetComp<CompRefuelable>();
            if (fuel != null)
            {
                fuel.Refuel(fuel.Props.fuelCapacity - fuel.Fuel);
            }
            map.fogGrid.Unfog(c);
            Log.Message("[IsoRim probe] lit torch lamp at " + c);
        }

        private static void BuildFloors(Map map, int x0, int z0, int size, int n)
        {
            TerrainDef slab = DefDatabase<TerrainDef>.GetNamedSilentFail("PZ_UpperFloor");
            if (slab == null || Floors.GroundRowsOf(map) <= 0)
            {
                Log.Warning("[IsoRim probe] buildfloors: no ProjectZ floors here");
                return;
            }
            CellRect rect = new CellRect(x0, z0, size, size);
            ThingDef stuff = DefDatabase<ThingDef>.GetNamed("BlocksGranite");
            for (int k = 0; k <= n; k++)
            {
                foreach (IntVec3 g in rect)
                {
                    IntVec3 c = k == 0 ? g : Floors.Stacked(map, g, k);
                    if (!c.IsValid || !c.InBounds(map))
                    {
                        continue;
                    }
                    foreach (Thing t in new List<Thing>(c.GetThingList(map)))
                    {
                        if (t is Building || t is Plant)
                        {
                            t.Destroy();
                        }
                    }
                    if (k > 0)
                    {
                        map.terrainGrid.SetTerrain(c, slab);
                    }
                    bool edge = g.x == rect.minX || g.x == rect.maxX || g.z == rect.minZ || g.z == rect.maxZ;
                    bool gap = g.z == rect.minZ && Math.Abs(g.x - (rect.minX + rect.maxX) / 2) <= 1;
                    if (k < n && edge && !gap)
                    {
                        Thing w = ThingMaker.MakeThing(ThingDefOf.Wall, stuff);
                        w.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(w, c, map, Rot4.North);
                    }
                }
                IntVec3 mid = rect.CenterCell;
                Lamp(map, k == 0 ? mid : Floors.Stacked(map, mid, k));
            }
            foreach (IntVec3 g in rect.ExpandedBy(2))
            {
                for (int k = 0; k <= n; k++)
                {
                    IntVec3 c = k == 0 ? g : Floors.Stacked(map, g, k);
                    if (c.IsValid && c.InBounds(map))
                    {
                        map.fogGrid.Unfog(c);
                    }
                }
            }
            Log.Message("[IsoRim probe] buildfloors " + rect + " floors " + n);
        }

        private static void RebuildPerf(Map map, int reps, string label)
        {
            Section[,] sections = cliffSections(map.mapDrawer);
            if (sections == null)
            {
                return;
            }
            Type[] types = { typeof(SectionLayer_IsoRimWalls), typeof(SectionLayer_Terrain), typeof(SectionLayer_LightingOverlay) };
            string[] names = { "walls", "terrain", "lighting" };
            List<SectionLayer>[,] layers = new List<SectionLayer>[types.Length, 2];
            for (int i = 0; i < types.Length; i++)
            {
                layers[i, 0] = new List<SectionLayer>();
                layers[i, 1] = new List<SectionLayer>();
            }
            int rows = Floors.GroundRowsOf(map);
            foreach (Section s in sections)
            {
                int up = rows > 0 && s.botLeft.z >= rows ? 1 : 0;
                for (int i = 0; i < types.Length; i++)
                {
                    SectionLayer l = s.GetLayer(types[i]);
                    if (l != null)
                    {
                        layers[i, up].Add(l);
                    }
                }
            }
            for (int i = 0; i < types.Length; i++)
            {
                for (int u = 0; u < 2; u++)
                {
                    foreach (SectionLayer l in layers[i, u])
                    {
                        l.Regenerate();   // warm-up
                    }
                }
            }
            List<double>[,] ms = new List<double>[types.Length, 2];
            for (int r = 0; r < reps; r++)
            {
                for (int i = 0; i < types.Length; i++)
                {
                    for (int u = 0; u < 2; u++)
                    {
                        if (ms[i, u] == null)
                        {
                            ms[i, u] = new List<double>();
                        }
                        long t0 = Stopwatch.GetTimestamp();
                        foreach (SectionLayer l in layers[i, u])
                        {
                            l.Regenerate();
                        }
                        ms[i, u].Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                    }
                }
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[IsoRim probe] REBUILDPERF ").Append(label).Append(": map ").Append(map.Size).Append(", ").Append(reps).Append(" reps, median (min) ms per full rebuild:");
            for (int i = 0; i < types.Length; i++)
            {
                for (int u = 0; u < 2; u++)
                {
                    List<double> m = ms[i, u];
                    m.Sort();
                    sb.Append(' ').Append(names[i]).Append(u == 0 ? "/ground" : "/upper").Append('[').Append(layers[i, u].Count).Append("] ")
                        .Append(m[m.Count / 2].ToString("0.00")).Append(" (").Append(m[0].ToString("0.00")).Append(')');
                }
            }
            Log.Message(sb.ToString());
            map.mapDrawer.RegenerateEverythingNow();
        }
    }
}
