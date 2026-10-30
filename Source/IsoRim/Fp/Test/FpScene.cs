using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace IsoRim.Fp
{
    // The self-test's scene (fp-play-spec 15.2), built on the first Playing frame of a -quicktest -isorim3d game (solo and
    // host; the client finds it in the host's save with Adopt). Everything sits at fixed offsets from the first colonist
    // C1, north = +z. Every def name was checked against 1.7.4990's Core defs.
    //
    // Beyond the spec's list, to keep the scene still while scenarios run: every colonist's work priorities are cleared
    // (the AI would otherwise haul the steel, finish the table frame and harvest the rice before a scenario gets to them;
    // a scenario that needs a work type enables it with EnableWork; C1 and C2 keep hauling, for the left button's order on
    // the steel, with one empty stockpile cell next to the grenade's), C1 and C2 are undrafted and wait on their cells
    // (Stand), and C2 stands at (-2, -2). The map's wild animals are removed (one attacked the colonists in a run). The cleared ground reaches 20 cells south of C1 (the spec's square is 21 x 21):
    // north of C1 the stove and the wall line stop a walk within 7 cells, so the walking scenarios walk south. The game
    // speed is Normal and the camera is turned back to yaw 0.
    public static class FpScene
    {
        public static Pawn C1, C2;
        public static IntVec3 Origin = IntVec3.Invalid;    // C1's cell when the scene was built

        public static Building Stove;
        public static Thing Potatoes, Steel, Granite, DamagedWall, Oak, Rice, Grenades;
        public static Frame TableFrame;
        public static Pawn Pirate;
        public static readonly List<Building> WallLine = new List<Building>();

        // Offsets from Origin.
        public static readonly IntVec3 StoveAt = new IntVec3(0, 0, 3);
        public static readonly IntVec3 PotatoesAt = new IntVec3(2, 0, 0);
        public static readonly IntVec3 SteelAt = new IntVec3(-2, 0, 1);
        public const int WallLineZ = 8, WallLineMinX = -5, WallLineMaxX = 5;
        public static readonly IntVec3 GraniteAt = new IntVec3(4, 0, 4);
        public static readonly IntVec3 DamagedWallAt = new IntVec3(-4, 0, 6);
        public static readonly IntVec3 OakAt = new IntVec3(-6, 0, 2);
        public static readonly IntVec3 RiceAt = new IntVec3(6, 0, 2);
        public static readonly IntVec3 TableAt = new IntVec3(3, 0, -3);
        public static readonly IntVec3 PirateAt = new IntVec3(12, 0, 0);
        public static readonly IntVec3 GrenadesAt = new IntVec3(1, 0, -2);
        public static readonly IntVec3 SpareStoreAt = new IntVec3(1, 0, -3);   // the stockpile's empty cell
        public static readonly IntVec3 C2At = new IntVec3(-2, 0, -2);

        private const int HalfSize = 10;    // the cleared square is 21 x 21 round C1...
        private const int SouthClear = 20;  // ...and the ground is cleared 20 cells south of C1 for walking

        public static string Describe()
        {
            return "C1=" + (C1?.ThingID ?? "null") + " C2=" + (C2?.ThingID ?? "null") + " origin=" + Origin;
        }

        public static bool Build(Map map, out string error)
        {
            error = null;
            List<Pawn> colonists = Colonists(map);
            if (colonists.Count < 2)
            {
                error = "needs two free colonists on the map, found " + colonists.Count;
                return false;
            }
            C1 = colonists[0];
            C2 = colonists[1];

            // Room for the square and the pirate at +12, away from the map edge.
            IntVec3 o = C1.Position;
            o.x = Clamp(o.x, HalfSize + 2, map.Size.x - PirateAt.x - 3);
            o.z = Clamp(o.z, SouthClear + 2, map.Size.z - HalfSize - 3);
            Origin = o;

            CellRect square = CellRect.CenteredOn(o, HalfSize).ClipInsideMap(map);
            CellRect cleared = CellRect.FromLimits(o.x - HalfSize, o.z - SouthClear, o.x + HalfSize, o.z + HalfSize).ClipInsideMap(map);
            List<IntVec3> cells = cleared.Cells.ToList();
            cells.Add(o + PirateAt);
            // Steam geysers and the like are not destroyable; the game's own colony maker (Autotests_ColonyMaker) lifts
            // the guard the same way while it clears ground.
            Thing.allowDestroyNonDestroyable = true;
            try
            {
                foreach (IntVec3 c in cells)
                {
                    Clear(map, c);
                }
            }
            finally
            {
                Thing.allowDestroyNonDestroyable = false;
            }

            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())   // every colonist, C1 and C2 or not
            {
                if (p.workSettings != null && p.workSettings.EverWork)
                {
                    p.workSettings.DisableAll();
                }
                if (p.drafter != null)
                {
                    p.drafter.Drafted = false;
                }
            }
            // The map's wild animals go: a predator or a manhunter that turns on the colonists downs one mid-scenario (the
            // M4-M6 test-copy run, m4-repair: C2 bleeding, the third colonist fleeing, the doer downed at 57% of the repair),
            // and one wandering into the line of sight takes a click (m5-odds). Animals spawned later stay (rare in a run).
            foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(x => x.Faction == null && x.RaceProps != null && x.RaceProps.Animal).ToList())
            {
                a.Destroy(DestroyMode.Vanish);
            }
            Place(C1, o);
            Place(C2, o + C2At);
            Stand(C1);
            Stand(C2);
            // Anyone else standing where something is spawned steps out of the way.
            foreach (Pawn p in square.Cells.SelectMany(c => c.GetThingList(map).OfType<Pawn>()).ToList())
            {
                if (p != C1 && p != C2)
                {
                    Place(p, o + new IntVec3(-HalfSize + 1, 0, -HalfSize + 1));
                }
            }

            Faction player = Faction.OfPlayer;
            Stove = (Building)Spawn(map, "FueledStove", null, o + StoveAt, Rot4.North, player);
            CompRefuelable fuel = Stove.TryGetComp<CompRefuelable>();
            fuel?.Refuel(fuel.GetFuelCountToFullyRefuel());

            Potatoes = SpawnStack(map, "RawPotatoes", 20, o + PotatoesAt);
            Steel = SpawnStack(map, "Steel", 50, o + SteelAt);

            ThingDef granite = ThingDef.Named("BlocksGranite");
            WallLine.Clear();
            for (int x = WallLineMinX; x <= WallLineMaxX; x++)
            {
                WallLine.Add((Building)Spawn(map, "Wall", granite, o + new IntVec3(x, 0, WallLineZ), Rot4.North, player));
            }
            Granite = Spawn(map, "Granite", null, o + GraniteAt, Rot4.North, null);
            DamagedWall = Spawn(map, "Wall", granite, o + DamagedWallAt, Rot4.North, player);
            DamagedWall.HitPoints = DamagedWall.MaxHitPoints / 2;

            Oak = SpawnGrown(map, "Plant_TreeOak", o + OakAt);
            IntVec3 riceCell = o + RiceAt;
            Zone_Growing field = new Zone_Growing(map.zoneManager);
            map.zoneManager.RegisterZone(field);
            field.AddCell(riceCell);
            field.SetPlantDefToGrow(ThingDef.Named("Plant_Rice"));
            Rice = SpawnGrown(map, "Plant_Rice", riceCell);

            TableFrame = SpawnFrame(map, ThingDef.Named("Table1x2c"), ThingDefOf.WoodLog, o + TableAt, player);

            ThingWithComps rifle = (ThingWithComps)ThingMaker.MakeThing(ThingDef.Named("Gun_AssaultRifle"));
            C1.equipment.DestroyAllEquipment();
            C1.equipment.AddEquipment(rifle);

            Pirate = SpawnPirate(map, o + PirateAt, out error);
            if (Pirate == null)
            {
                return false;
            }

            IntVec3 grenadeCell = o + GrenadesAt;
            Zone_Stockpile store = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(store);
            store.AddCell(grenadeCell);
            Grenades = SpawnStack(map, "Weapon_GrenadeFrag", 1, grenadeCell);
            // The left button's order on the steel (m1-lmb, mp1-lmb) is the game's "Prioritize hauling", which needs an
            // empty stockpile cell and hauling assigned (at home the game offers no "Pick up" for a resource). C1 and C2
            // only, and they wait (Stand) until a scenario takes them, so nobody hauls on their own meanwhile.
            store.AddCell(o + SpareStoreAt);
            EnableWork(C1, WorkTypeDefOf.Hauling);
            EnableWork(C2, WorkTypeDefOf.Hauling);

            IsoRimMod.Settings.turn = 0;
            IsoRimMod.Settings.freeYaw = 0f;
            Find.CameraDriver?.JumpToCurrentMapLoc(o);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            Find.Selector.ClearSelection();
            return true;
        }

        // Client: the scene came with the host's save. Found by its stove (the one with the granite wall line 5 cells north).
        public static bool Adopt(Map map, out string error)
        {
            error = null;
            List<Pawn> colonists = Colonists(map);
            if (colonists.Count < 2)
            {
                error = "needs two free colonists on the map, found " + colonists.Count;
                return false;
            }
            C1 = colonists[0];
            C2 = colonists[1];
            ThingDef wall = ThingDef.Named("Wall");
            Stove = map.listerBuildings.allBuildingsColonist
                .Where(b => b.def.defName == "FueledStove")
                .OrderBy(b => b.thingIDNumber)
                .FirstOrDefault(b => (b.Position + new IntVec3(0, 0, WallLineZ - StoveAt.z)).GetFirstThing(map, wall) != null);
            if (Stove == null)
            {
                error = "no scene in this game (no fueled stove with the wall line north of it)";
                return false;
            }
            IntVec3 o = Stove.Position - StoveAt;
            Origin = o;
            Potatoes = First(map, o + PotatoesAt, "RawPotatoes");
            Steel = First(map, o + SteelAt, "Steel");
            WallLine.Clear();
            for (int x = WallLineMinX; x <= WallLineMaxX; x++)
            {
                if (First(map, o + new IntVec3(x, 0, WallLineZ), "Wall") is Building b)
                {
                    WallLine.Add(b);
                }
            }
            Granite = First(map, o + GraniteAt, "Granite");
            DamagedWall = First(map, o + DamagedWallAt, "Wall");
            Oak = First(map, o + OakAt, "Plant_TreeOak");
            Rice = First(map, o + RiceAt, "Plant_Rice");
            TableFrame = (o + TableAt).GetThingList(map).OfType<Frame>().FirstOrDefault();
            Pirate = (o + PirateAt).GetThingList(map).OfType<Pawn>().FirstOrDefault(p => p.Faction != Faction.OfPlayer);
            Grenades = First(map, o + GrenadesAt, "Weapon_GrenadeFrag");
            return true;
        }

        // C1 and C2 (spec 15.2): the free colonists by thingIDNumber, passing over any that can never haul while two others
        // can. The left button's order on the steel (m1-lmb, mp1-lmb) is the game's "Prioritize hauling", which such a
        // colonist never gets ("Will never do hauling"), and -quicktest colonists are random. Build and Adopt pick alike.
        // Among those, colonists who can cook come first (M2's stove scenarios cook with C1, mp2-cook with C2; a colonist who
        // can never cook failed their setup), the rest after them, each group by thingIDNumber.
        private static List<Pawn> Colonists(Map map)
        {
            List<Pawn> all = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
            List<Pawn> haulers = all.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)).ToList();
            List<Pawn> pick = haulers.Count >= 2 ? haulers : all;
            WorkTypeDef cooking = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking");
            return cooking == null ? pick : pick.OrderBy(p => p.WorkTypeIsDisabled(cooking) ? 1 : 0).ToList();
        }

        // Lets a colonist do one kind of work again (the scene clears every priority).
        public static void EnableWork(Pawn p, WorkTypeDef work, int priority = 1)
        {
            if (p?.workSettings != null && p.workSettings.EverWork && !p.WorkTypeIsDisabled(work))
            {
                p.workSettings.SetPriority(work, priority);
            }
        }

        // ---- building blocks

        private static int Clamp(int v, int min, int max)
        {
            return max < min ? v : v < min ? min : v > max ? max : v;
        }

        // An empty, walkable, explored, unroofed soil cell with no zone.
        private static void Clear(Map map, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return;
            }
            foreach (Thing t in c.GetThingList(map).ToList())
            {
                if (!(t is Pawn) && !t.Destroyed)
                {
                    t.Destroy(DestroyMode.Vanish);
                }
            }
            map.zoneManager.ZoneAt(c)?.RemoveCell(c);
            map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
            map.roofGrid.SetRoof(c, null);
            map.fogGrid.Unfog(c);
        }

        private static void Place(Pawn p, IntVec3 c)
        {
            if (p.Position == c)
            {
                return;
            }
            p.jobs?.StopAll();
            p.pather?.StopDead();
            p.Position = c;
            p.Notify_Teleported();
        }

        // An idle colonist wanders off at once, so the scene's offsets from C1 would hold only for the first few ticks: a long
        // AI wait (not player-forced) keeps C1 and C2 on their cells until a scenario takes control (FpSetControl ends an
        // AI job) or gives an order.
        private static void Stand(Pawn p)
        {
            Job wait = JobMaker.MakeJob(JobDefOf.Wait);
            wait.expiryInterval = StandTicks;
            p.jobs.StartJob(wait, JobCondition.InterruptForced);
        }

        private const int StandTicks = 60000;

        private static Thing Spawn(Map map, string defName, ThingDef stuff, IntVec3 c, Rot4 rot, Faction faction)
        {
            ThingDef def = ThingDef.Named(defName);
            Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff ?? GenStuff.DefaultStuffFor(def) : null);
            if (faction != null)
            {
                t.SetFactionDirect(faction);
            }
            return GenSpawn.Spawn(t, c, map, rot);
        }

        private static Thing SpawnStack(Map map, string defName, int count, IntVec3 c)
        {
            Thing t = ThingMaker.MakeThing(ThingDef.Named(defName));
            t.stackCount = count;
            return GenSpawn.Spawn(t, c, map);
        }

        private static Thing SpawnGrown(Map map, string defName, IntVec3 c)
        {
            Plant plant = (Plant)ThingMaker.MakeThing(ThingDef.Named(defName));
            plant.Growth = 1f;
            return GenSpawn.Spawn(plant, c, map);
        }

        // A blueprint-free frame with every resource already delivered: only the construction work is left.
        private static Frame SpawnFrame(Map map, ThingDef building, ThingDef stuff, IntVec3 c, Faction faction)
        {
            Frame frame = (Frame)ThingMaker.MakeThing(building.frameDef, stuff);
            frame.SetFactionDirect(faction);
            GenSpawn.Spawn(frame, c, map, Rot4.North);
            foreach (ThingDefCountClass need in building.CostListAdjusted(stuff))
            {
                int left = need.count;
                while (left > 0)
                {
                    Thing res = ThingMaker.MakeThing(need.thingDef);
                    res.stackCount = System.Math.Min(left, need.thingDef.stackLimit);
                    left -= res.stackCount;
                    frame.resourceContainer.TryAdd(res);
                }
            }
            return frame;
        }

        // An unarmed, downed (anaesthetised) hostile humanlike: a pirate, or any hostile humanlike faction without pirates.
        private static Pawn SpawnPirate(Map map, IntVec3 c, out string error)
        {
            error = null;
            Faction f = Find.FactionManager.FirstFactionOfDef(FactionDefOf.Pirate)
                ?? Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => !x.IsPlayer && !x.Hidden && x.def.humanlikeFaction && x.HostileTo(Faction.OfPlayer));
            // The pirate gang has no basicMemberKind in 1.7's Core defs: its own Pirate kind, else a humanlike kind from the
            // faction's raid groups.
            PawnKindDef kind = f == null ? null
                : f.def.basicMemberKind ?? (f.def == FactionDefOf.Pirate ? PawnKindDefOf.Pirate : f.RandomPawnKind());
            if (kind == null)
            {
                error = "no hostile humanlike faction for the pirate" + (f == null ? "" : " (" + f.def.defName + " has no humanlike pawn kind)");
                return null;
            }
            Pawn p = PawnGenerator.GeneratePawn(kind, f);
            p.equipment?.DestroyAllEquipment();
            p.inventory?.DestroyAll();
            GenSpawn.Spawn(p, c, map);
            HealthUtility.TryAnesthetize(p);
            if (!p.Downed)
            {
                // Not fatal for the scene: only the scenarios that use the pirate depend on it, and they check it.
                FpTestKit.Info("the pirate isn't downed after the anaesthetic");
            }
            return p;
        }

        private static Thing First(Map map, IntVec3 c, string defName)
        {
            return c.InBounds(map) ? c.GetThingList(map).FirstOrDefault(t => t.def.defName == defName) : null;
        }
    }
}
