using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace IsoRim
{
    // Harness-only keys for the zoomed-out silhouettes (Silhouettes3D):
    //   silscene=1          the test scene (layout_silhouettes): a real raid and a real trade caravan walk in (600 ticks), then
    //                       a tree belt (x 58..96, z 70..71) and a granite mountain (x 80..96, z 80..96, thick roof) with two
    //                       closed caves, and pawns of every kind behind the trees (z 73), in front of them (z 67) and in the
    //                       caves (one of them fogged); a hostile and a player mini turret and a shuttle
    //   silpawn=x,z,<hostile|neutral|player|wild>,<PawnKindDef|->,<tag>  one more tagged pawn there (U<z>: floor 2)
    //   silshots=1          silhouettes are drawn in the harness shots (the game leaves them out in screenshot mode)
    //   silmode=all|hostiles|none  the game's "Highlight" option (Prefs.DotHighlightDisplayMode)
    //   silfp=x,z|off       first person with the person in the cell, or out of it again
    //   silcheck=1          before every harness screenshot: one log line with the camera's altitude and the silhouette alpha,
    //                       and one per pawn/highlighted thing (vanilla colour, whether a silhouette is due, its screen pixel)
    public static partial class Probe
    {
        private static bool silShotsPatched, silCheckPatched;
        private static bool inSil;
        private static readonly List<Thing> silThings = new List<Thing>();
        private static readonly Dictionary<Thing, string> silTags = new Dictionary<Thing, string>();

        private static bool SilKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "silscene":
                    SilScene(map);
                    return true;
                case "silpawn":
                {
                    // silpawn=x,z,<hostile|neutral|player|wild>,<PawnKindDef|->,<tag>: one more tagged pawn (U<z> = floor 2)
                    Faction hf = SilFaction(Faction.OfPirates, FactionDefOf.Pirate, FactionRelationKind.Hostile);
                    Faction f = a[2] == "hostile" ? hf : a[2] == "neutral" ? SilFaction(SilNeutral(), FactionDefOf.OutlanderCivil, FactionRelationKind.Neutral)
                        : a[2] == "player" ? Faction.OfPlayer : null;
                    PawnKindDef kind = a[3] != "-" ? DefDatabase<PawnKindDef>.GetNamed(a[3]) : f == Faction.OfPlayer ? PawnKindDefOf.Colonist : f == hf ? (hf.def.basicMemberKind ?? DefDatabase<PawnKindDef>.GetNamed("Mercenary_Gunner")) : f != null ? (f.def.basicMemberKind ?? DefDatabase<PawnKindDef>.GetNamed("Villager")) : PawnKindDefOf.Thrumbo;
                    Pawn sp = SilPawn(map, a.Length > 4 ? a[4] : "pawn", I(a[0]), I(a[1]), kind, f, Rot4.South);
                    if (sp != null && f == hf && sp.GetLord() == null)
                    {
                        LordMaker.MakeNewLord(hf, new LordJob_AssaultColony(hf), map, new List<Pawn> { sp });
                    }
                    return true;
                }
                case "silshots":
                    if (!silShotsPatched)
                    {
                        silShotsPatched = true;
                        Harmony h = new Harmony("peren.isorim17.probe.sil");
                        h.Patch(AccessTools.Method(typeof(Silhouettes3D), nameof(Silhouettes3D.DrawAll)),
                            prefix: new HarmonyMethod(typeof(Probe), nameof(SilOn)), finalizer: new HarmonyMethod(typeof(Probe), nameof(SilOff)));
                        h.Patch(AccessTools.Method(typeof(DynamicDrawManager), "DrawSilhouettes"),
                            prefix: new HarmonyMethod(typeof(Probe), nameof(SilOn)), finalizer: new HarmonyMethod(typeof(Probe), nameof(SilOff)));
                        h.Patch(AccessTools.PropertyGetter(typeof(ScreenshotModeHandler), nameof(ScreenshotModeHandler.Active)),
                            postfix: new HarmonyMethod(typeof(Probe), nameof(SilShot)));
                    }
                    return true;
                case "silmode":
                    Prefs.DotHighlightDisplayMode = a[0] == "none" ? DotHighlightDisplayMode.None
                        : a[0] == "hostiles" ? DotHighlightDisplayMode.HighlightHostiles : DotHighlightDisplayMode.HighlightAll;
                    Prefs.HighlightStyleMode = HighlightStyleMode.Silhouettes;
                    return true;
                case "silfp":
                {
                    FieldInfo pf = AccessTools.Field(typeof(FirstPerson), "pawn");
                    if (a[0] == "off")
                    {
                        FirstPerson.Leave(null);
                        return true;
                    }
                    Pawn fp = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstPawn(map);
                    if (fp == null)
                    {
                        Log.Warning("[IsoRim probe] silfp: no pawn at " + a[0] + "," + a[1]);
                        return true;
                    }
                    View3D.Active = true;
                    AccessTools.Field(typeof(FirstPerson), "headingSet").SetValue(null, false);
                    pf.SetValue(null, fp);
                    return true;
                }
                case "silcheck":
                    if (!silCheckPatched)
                    {
                        silCheckPatched = true;
                        Type runner = AccessTools.TypeByName("Capture17.CaptureRunner");
                        MethodInfo cap = runner != null ? AccessTools.Method(runner, "Capture") : null;
                        if (cap == null)
                        {
                            Log.Warning("[IsoRim probe] silcheck: Capture17.CaptureRunner.Capture not found");
                            return true;
                        }
                        new Harmony("peren.isorim17.probe.silcheck").Patch(cap, prefix: new HarmonyMethod(typeof(Probe), nameof(SilCapture)));
                    }
                    return true;
            }
            return false;
        }

        public static void SilOn()
        {
            inSil = true;
        }

        public static void SilOff()
        {
            inSil = false;
        }

        public static void SilShot(ref bool __result)
        {
            if (inSil)
            {
                __result = false;
            }
        }

        // ---- the scene

        private static Faction SilNeutral()
        {
            return Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => !f.IsPlayer && !f.Hidden && !f.temporary && f.def.humanlikeFaction
                && !f.HostileTo(Faction.OfPlayer));
        }

        // A faction of that kind: the world's, or (quick-test and ProjectZ test worlds may have none) a new one made the way
        // the game makes factions, with that relation to the player.
        private static Faction SilFaction(Faction have, FactionDef def, FactionRelationKind kind)
        {
            if (have != null)
            {
                return have;
            }
            Faction f = FactionGenerator.NewGeneratedFactionWithRelations(def,
                new List<FactionRelation> { new FactionRelation(Faction.OfPlayer, kind) { baseGoodwill = kind == FactionRelationKind.Hostile ? -100 : 0 } });
            Find.FactionManager.Add(f);
            Log.Message("[IsoRim probe] silscene: made faction " + f.Name + " (" + def.defName + ", " + kind + ")");
            return f;
        }

        private static Pawn SilPawn(Map map, string tag, int x, int z, PawnKindDef kind, Faction f, Rot4 rot)
        {
            if (kind == null)
            {
                Log.Warning("[IsoRim probe] silscene: no pawn kind for " + tag);
                return null;
            }
            Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, f));
            GenSpawn.Spawn(p, new IntVec3(x, 0, z), map, rot);
            p.Rotation = rot;
            p.jobs?.StopAll();
            p.pather?.StopDead();
            silTags[p] = tag;
            silThings.Add(p);
            return p;
        }

        private static void SilScene(Map map)
        {
            Faction pirates = SilFaction(Faction.OfPirates, FactionDefOf.Pirate, FactionRelationKind.Hostile);
            Faction neutral = SilFaction(SilNeutral(), FactionDefOf.OutlanderCivil, FactionRelationKind.Neutral);
            Log.Message("[IsoRim probe] silscene: hostile " + pirates + ", neutral " + neutral);

            // Real arrivals first: a raid and a trade caravan walk in from the north edge.
            try
            {
                IncidentParms raid = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                raid.faction = pirates;
                raid.points = 600f;
                raid.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
                raid.raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn;
                raid.spawnCenter = new IntVec3(70, 0, map.Size.z - 1);
                bool rok = IncidentDefOf.RaidEnemy.Worker.TryExecute(raid);
                IncidentParms trade = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
                trade.faction = neutral;
                trade.spawnCenter = new IntVec3(100, 0, map.Size.z - 1);
                bool tok = neutral != null && IncidentDefOf.TraderCaravanArrival.Worker.TryExecute(trade);
                Log.Message("[IsoRim probe] silscene: raid " + rok + ", caravan " + tok);
                for (int i = 0; i < 600; i++)
                {
                    Find.TickManager.DoSingleTick();
                }
                foreach (Pawn ap in map.mapPawns.AllPawnsSpawned)
                {
                    if (!silTags.ContainsKey(ap) && ap.Faction != Faction.OfPlayer && ap.Faction != null)
                    {
                        silTags[ap] = ap.Faction == pirates ? "raid" : "caravan";
                        silThings.Add(ap);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim probe] silscene: arrivals failed: " + e);
            }

            // The ground: cleared (colonists kept), trees, the mountain with its caves.
            CellRect area = CellRect.FromLimits(55, 55, 105, 100);
            foreach (IntVec3 c in area)
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (!(t is Pawn) && !t.Destroyed && t.def.destroyable)
                    {
                        t.Destroy(DestroyMode.Vanish);
                    }
                }
                map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
                map.snowGrid.SetDepth(c, 0f);
            }
            foreach (Pawn colonist in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (area.Contains(colonist.Position) && !silTags.ContainsKey(colonist))
                {
                    colonist.DeSpawn();
                    GenSpawn.Spawn(colonist, new IntVec3(75, 0, 58), map);   // out of the rows
                }
            }
            ThingDef oak = DefDatabase<ThingDef>.GetNamed("Plant_TreeOak");
            for (int x = 58; x <= 96; x++)
            {
                for (int z = 70; z <= 71; z++)
                {
                    Plant tree = (Plant)GenSpawn.Spawn(ThingMaker.MakeThing(oak), new IntVec3(x, 0, z), map);
                    tree.Growth = 1f;
                }
            }
            ThingDef granite = DefDatabase<ThingDef>.GetNamed("Granite");
            CellRect mountain = CellRect.FromLimits(80, 80, 96, 96);
            CellRect caveA = CellRect.FromLimits(87, 87, 88, 88);
            CellRect caveB = CellRect.FromLimits(92, 84, 93, 85);
            foreach (IntVec3 c in mountain)
            {
                if (!caveA.Contains(c) && !caveB.Contains(c))
                {
                    GenSpawn.Spawn(ThingMaker.MakeThing(granite), c, map);
                }
                map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
            }

            // Pawns behind the tree belt (z 73: hidden from the default camera, south-west) ...
            PawnKindDef raider = pirates.def.basicMemberKind ?? DefDatabase<PawnKindDef>.GetNamed("Mercenary_Gunner");   // 1.7 Pirate/Outlander defs have no basicMemberKind
            PawnKindDef visitor = neutral?.def.basicMemberKind ?? DefDatabase<PawnKindDef>.GetNamed("Villager");
            SilPawn(map, "raider", 62, 73, raider, pirates, Rot4.South);
            SilPawn(map, "mech", 65, 73, DefDatabase<PawnKindDef>.GetNamed("Mech_Lancer"), SilFaction(Faction.OfMechanoids, FactionDefOf.Mechanoid, FactionRelationKind.Hostile), Rot4.South);
            SilPawn(map, "visitor", 68, 73, visitor, neutral, Rot4.South);
            SilPawn(map, "wild", 71, 73, DefDatabase<PawnKindDef>.GetNamed("Deer"), null, Rot4.South);
            SilPawn(map, "pet", 74, 73, DefDatabase<PawnKindDef>.GetNamed("Husky"), Faction.OfPlayer, Rot4.South);
            Pawn hunter = SilPawn(map, "manhunter", 77, 73, DefDatabase<PawnKindDef>.GetNamed("Warg"), null, Rot4.South);
            hunter?.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, forced: true);
            Pawn prisoner = SilPawn(map, "prisoner", 83, 73, raider, pirates, Rot4.South);
            prisoner?.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
            Pawn invis = SilPawn(map, "invisible", 86, 73, raider, pirates, Rot4.South);
            invis?.health.AddHediff(HediffMaker.MakeHediff(DefDatabase<HediffDef>.GetNamed("PsychicInvisibility"), invis));
            SilPawn(map, "colonist", 89, 73, PawnKindDefOf.Colonist, Faction.OfPlayer, Rot4.South);
            if (ModsConfig.IdeologyActive)
            {
                Pawn slave = SilPawn(map, "slave", 92, 73, raider, pirates, Rot4.South);
                slave?.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Slave);
            }
            // ... in front of it (z 67: hidden when the camera is turned round, 225) ...
            SilPawn(map, "raiderF", 63, 67, raider, pirates, Rot4.North);
            SilPawn(map, "visitorF", 69, 67, visitor, neutral, Rot4.North);
            SilPawn(map, "colonistF", 75, 64, PawnKindDefOf.Colonist, Faction.OfPlayer, Rot4.North);   // the first-person eye (silfp=75,64)
            // ... and in the mountain: a closed cave, and a closed fogged cave.
            SilPawn(map, "cave", 87, 87, raider, pirates, Rot4.South);
            SilPawn(map, "fogged", 92, 84, raider, pirates, Rot4.South);
            map.fogGrid.Refog(caveB);

            // Raiders get a real assault lord (paused: they stand until ticks run).
            List<Pawn> attackers = silThings.OfType<Pawn>().Where(p => p.Spawned && p.Faction == pirates && !p.IsPrisoner && !p.IsSlave && p.GetLord() == null).ToList();
            if (attackers.Count > 0)
            {
                LordMaker.MakeNewLord(pirates, new LordJob_AssaultColony(pirates), map, attackers);
            }

            // Turrets (a hostile one has a silhouette, a player one not) and the shuttle (always, its own colour).
            Thing ht = GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Turret_MiniTurret"), ThingDefOf.Steel), new IntVec3(66, 0, 61), map);
            ht.SetFaction(pirates);
            Thing pt = GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Turret_MiniTurret"), ThingDefOf.Steel), new IntVec3(70, 0, 61), map);
            pt.SetFaction(Faction.OfPlayer);
            Thing shuttle = GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Shuttle")), new IntVec3(84, 0, 61), map);
            silTags[ht] = "turretHostile";
            silTags[pt] = "turretPlayer";
            silTags[shuttle] = "shuttle";
            silThings.Add(ht);
            silThings.Add(pt);
            silThings.Add(shuttle);
            Log.Message("[IsoRim probe] silscene: " + silThings.Count + " things tagged");
        }

        // ---- the check before every harness screenshot

        private static readonly MethodInfo silShould = AccessTools.Method(typeof(SilhouetteUtility), "ShouldHighlightInt");
        private static readonly MethodInfo silColor = AccessTools.Method(typeof(SilhouetteUtility), "GetColor");
        private static readonly MethodInfo silGraphic = AccessTools.Method(typeof(Silhouettes3D), "PawnGraphic");

        public static void SilCapture(string name)
        {
            try
            {
                SilReport(name);
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim probe] silcheck failed: " + e);
            }
        }

        private static void SilReport(string shot)
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            float max = Find.CameraDriver.config.sizeRange.max;
            float alpha = View3D.Ready ? Silhouettes3D.Alpha() : 0f;
            float perSize = Mathf.Sin(35.3f * Mathf.Deg2Rad) / View3D.DefaultTanHalf;
            float floorH;
            int zs;
            Floors.FloorPlane(Floors.PointerTop, out floorH, out zs);
            float eq = (View3D.Altitude - floorH) / perSize;
            bool vanilla = !View3D.Ready;
            float vanAlpha = 0f;
            if (vanilla)
            {
                float rs = Find.CameraDriver.ZoomRootSize;
                vanAlpha = rs >= max * 0.9f ? 0.9f * Mathf.Clamp01(Mathf.InverseLerp(max * 0.85f, max, rs)) : 0f;
            }
            Log.Message("[IsoRim sil] shot " + shot + " 3d=" + View3D.Ready + " persp=" + View3D.Perspective + " fp=" + FirstPerson.On
                + " pitch=" + View3D.Pitch.ToString("0.0") + " yaw=" + View3D.Yaw.ToString("0") + " size=" + Find.CameraDriver.RootSize.ToString("0.0")
                + " altitude=" + View3D.Altitude.ToString("0.0") + " floor=" + floorH.ToString("0.0") + " eqSize=" + eq.ToString("0.0")
                + " alpha=" + (vanilla ? vanAlpha : alpha).ToString("0.00") + " mode=" + Prefs.DotHighlightDisplayMode
                + " screen=" + Screen.width + "x" + Screen.height);
            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1);
            foreach (Thing t in silThings)
            {
                if (t == null || !t.Spawned)
                {
                    continue;
                }
                bool should = (bool)silShould.Invoke(null, new object[] { t });
                Color c = (Color)silColor.Invoke(null, new object[] { t });
                bool fog = !t.def.seeThroughFog && t.Fogged();
                bool rule = t.def.drawHighlight && !fog && should && view.Contains(t.Position) && Prefs.DotHighlightDisplayMode != DotHighlightDisplayMode.None;
                bool due = rule && (vanilla ? vanAlpha > 0f : alpha > 0f);
                Vector2 px;
                if (vanilla)
                {
                    Vector3 sp = Find.Camera.WorldToScreenPoint(t.DrawPos);
                    px = new Vector2(sp.x, Screen.height - sp.y);
                }
                else
                {
                    Pawn p = t as Pawn;
                    Graphic g = p != null ? (Graphic)silGraphic.Invoke(null, new object[] { p }) : t.Graphic;
                    float sz = 1f;
                    if (g != null)
                    {
                        Vector2 ds = g.drawSize;
                        float h = p == null && t.def.rotatable ? ds.x : ds.y;
                        sz = Find.CameraDriver.InverseFovScale.x * (h < 2.5f ? h + SilhouetteUtility.AdjustScale(h) : h);
                    }
                    Quaternion r = Quaternion.Euler(View3D.Pitch - 90f, View3D.Yaw, 0f);
                    float rise = sz * 0.5f * Mathf.Max(0f, (r * Vector3.forward).y);
                    Vector3 dp = t.DrawPos;
                    Vector3 off = Floors.ThingOffset(t);
                    Vector2 s = View3D.WorldToScreen(new Vector3(dp.x + off.x, rise + off.y, dp.z + off.z));
                    px = new Vector2(s.x, Screen.height - s.y);
                }
                string tag;
                silTags.TryGetValue(t, out tag);
                Pawn pw = t as Pawn;
                string rel = t.Faction == null ? "none" : t.Faction == Faction.OfPlayer ? "player" : t.Faction.HostileTo(Faction.OfPlayer) ? "hostile" : "neutral";
                Log.Message("[IsoRim sil]   " + tag + " " + t.def.defName + " at " + t.Position + " rel=" + rel
                    + (pw != null && pw.IsPrisoner ? " prisoner" : "") + (pw != null && pw.IsSlave ? " slave" : "")
                    + (pw?.MentalStateDef != null ? " mental=" + pw.MentalStateDef.defName : "")
                    + (pw != null && pw.IsHiddenFromPlayer() ? " hidden" : "") + (fog ? " fogged" : "")
                    + " should=" + should + " due=" + due
                    + " colour=" + c.r.ToString("0.00") + "," + c.g.ToString("0.00") + "," + c.b.ToString("0.00")
                    + " px=" + px.x.ToString("0") + "," + px.y.ToString("0"));
            }
        }
    }
}
