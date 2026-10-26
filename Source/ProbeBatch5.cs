using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only keys (see Probe) for the grass-card / batch-5 pass:
    //   meshstats=<label>            logs the map mesh of every block: per layer type sub-meshes, vertices and triangles,
    //                                the plant cards among them (card materials), and the grass plants on the map
    //   ghostpreview=<Def>,x,z,rot   the placement preview (Ghosts.DrawPreview, as under the pointer while placing) drawn
    //                                every frame at that cell and rotation, green; ghostpreview=none clears them
    //   aim=x,z,<deg>                the gun of the turret building in the cell turned to that angle
    //   grassab=models|cards         A/B of the grass cards in one run: the five grass defs bound again to their old 3D grass
    //                                models (as before 2026-10-16), or back to their entries (cards); the map is rebuilt
    public static partial class Probe
    {
        private struct GhostSpec
        {
            public ThingDef def;
            public IntVec3 cell;
            public Rot4 rot;
        }

        private static readonly List<GhostSpec> ghostSpecs = new List<GhostSpec>();

        // Called from Scene; true if the key was one of these.
        private static bool Batch5Key(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "meshstats":
                    MeshStats(map, a.Length > 0 ? a[0] : "");
                    return true;
                case "ghostpreview":
                    if (a[0] == "none")
                    {
                        ghostSpecs.Clear();
                        return true;
                    }
                    ghostSpecs.Add(new GhostSpec { def = DefDatabase<ThingDef>.GetNamed(a[0]), cell = new IntVec3(I(a[1]), 0, I(a[2])), rot = new Rot4(I(a[3])) });
                    return true;
                case "grassab":
                    GrassAB(map, a[0] == "models");
                    return true;
                case "aim":
                {
                    Building_Turret bt = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstThing<Building_Turret>(map);
                    TurretTop top = bt != null ? Traverse.Create(bt).Field("top").GetValue() as TurretTop : null;
                    if (top == null)
                    {
                        Log.Warning("[IsoRim probe] aim: no turret top at " + a[0] + "," + a[1]);
                        return true;
                    }
                    top.CurRotation = F(a[2]);
                    Log.Message("[IsoRim probe] aim " + bt.def.defName + " rot " + bt.Rotation + " gun " + top.CurRotation.ToString("0"));
                    return true;
                }
            }
            return false;
        }

        private static readonly Dictionary<ThingDef, ModelEntry> grassCards = new Dictionary<ThingDef, ModelEntry>();

        private static void GrassAB(Map map, bool models)
        {
            Dictionary<ThingDef, ModelEntry> byThing = (Dictionary<ThingDef, ModelEntry>)AccessTools.Field(typeof(ModelLibrary), "byThing").GetValue(null);
            System.Reflection.MethodInfo make = AccessTools.Method(typeof(ModelLibrary), "MakeEntry");
            ModContentPack pack = DefDatabase<ModelDef>.AllDefsListForReading.First(d => d.modContentPack != null).modContentPack;
            string[][] old =
            {
                new[] { "Plant_Grass", "grass_1", "grass_2", "grass_3", "grass_4" },
                new[] { "Plant_TallGrass", "tall_grass_1", "tall_grass_2", "tall_grass_3" },
                new[] { "Plant_YellowGrass", "plant_yellowgrass_a", "plant_yellowgrass_b", "plant_yellowgrass_c", "plant_yellowgrass_d" },
                new[] { "Plant_YellowTallGrass", "plant_yellowtallgrass_a", "plant_yellowtallgrass_b", "plant_yellowtallgrass_c" },
                new[] { "Plant_Nightgrass", "plant_nightgrass_a", "plant_nightgrass_b", "plant_nightgrass_c" },
            };
            foreach (string[] o in old)
            {
                ThingDef td = DefDatabase<ThingDef>.GetNamedSilentFail(o[0]);
                if (td == null)
                {
                    continue;
                }
                ModelEntry cur;
                if (byThing.TryGetValue(td, out cur) && !grassCards.ContainsKey(td))
                {
                    grassCards[td] = cur;
                }
                if (models)
                {
                    ModelDef md = new ModelDef { defName = "R3D_AB_" + o[0], thingDef = o[0], models = o.Skip(1).Select(n => "Models/" + n + ".obj").ToList(),
                        surfaceHeight = 0f, randomRotation = true, scaleJitter = 0.18f, growthScale = true, scatter = 0.5f, groundShadow = false, outline = 0f };
                    md.modContentPack = pack;
                    byThing[td] = (ModelEntry)make.Invoke(null, new object[] { md });
                }
                else if (grassCards.ContainsKey(td))
                {
                    byThing[td] = grassCards[td];
                }
            }
            ((System.Collections.IDictionary)AccessTools.Field(typeof(Forms), "cache").GetValue(null)).Clear();
            map.mapDrawer.RegenerateEverythingNow();
            Log.Message("[IsoRim probe] grassab " + (models ? "models" : "cards"));
        }

        private static void GhostTick()
        {
            for (int i = 0; i < ghostSpecs.Count; i++)
            {
                Ghosts.DrawPreview(ghostSpecs[i].def, ghostSpecs[i].cell, ghostSpecs[i].rot, new Color(0.5f, 1f, 0.5f, 0.5f));
            }
        }

        private static void MeshStats(Map map, string label)
        {
            Array secs = MapSections.SectionsOf(map);
            if (secs == null)
            {
                Log.Warning("[IsoRim probe] meshstats: map not drawn");
                return;
            }
            Dictionary<string, int[]> byLayer = new Dictionary<string, int[]>();
            int cardSubs = 0, cardVerts = 0, cardTris = 0;
            foreach (object s in secs)
            {
                List<SectionLayer> layers = Traverse.Create(s).Field("layers").GetValue() as List<SectionLayer>;
                if (layers == null)
                {
                    continue;
                }
                foreach (SectionLayer l in layers)
                {
                    string name = l.GetType().Name;
                    int[] v;
                    if (!byLayer.TryGetValue(name, out v))
                    {
                        v = new int[3];
                        byLayer[name] = v;
                    }
                    foreach (LayerSubMesh sm in l.subMeshes)
                    {
                        if (!sm.finalized || sm.mesh == null || sm.mesh.vertexCount == 0)
                        {
                            continue;
                        }
                        int tris = sm.mesh.triangles.Length / 3;
                        v[0]++;
                        v[1] += sm.mesh.vertexCount;
                        v[2] += tris;
                        if (sm.material != null && sm.material.name != null && sm.material.name.EndsWith("_Card"))
                        {
                            cardSubs++;
                            cardVerts += sm.mesh.vertexCount;
                            cardTris += tris;
                        }
                    }
                }
            }
            int grass = 0, plants = 0;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Plant))
            {
                plants++;
                if (t.def.defName.Contains("Grass") || t.def.defName == "Plant_Nightgrass")
                {
                    grass++;
                }
            }
            int tv = byLayer.Values.Sum(x => x[1]), tt = byLayer.Values.Sum(x => x[2]);
            Log.Message("[IsoRim probe] meshstats " + label + ": plants " + plants + " (grass " + grass + "); all layers " + tv + " vertices, " + tt
                + " triangles; plant cards " + cardSubs + " sub-meshes, " + cardVerts + " vertices, " + cardTris + " triangles; by layer: "
                + string.Join("; ", byLayer.Where(p => p.Value[1] > 0).OrderByDescending(p => p.Value[1]).Select(p => p.Key + " " + p.Value[0] + "/" + p.Value[1] + "/" + p.Value[2])));
        }
    }
}
