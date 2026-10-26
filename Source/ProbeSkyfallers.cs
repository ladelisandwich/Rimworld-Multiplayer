using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only keys for skyfallers in 3D (Skyfallers.cs). Everything is spawned through the game's own utilities, as
    // the game's incidents, scenario and launch command do; run real ticks after them (burstticks=1 burst=..., tick=).
    //   skyfall=pods,x,z            a new colonist and some steel dropped near the cell by drop pods, as a scenario start does
    //                               (DropPodUtility.DropThingGroupsNear, not instant)
    //   skyfall=meteor,x,z          a meteorite (the incident's contents, ThingSetMakerDefOf.Meteorite) falling on the cell
    //   skyfall=shuttle,x,z         a Royalty shuttle landing (ShuttleIncoming with a new Shuttle, as the debug action)
    //   skyfall=pshuttle,x,z,rot    an Odyssey passenger shuttle landing facing rot (PassengerShuttleIncoming)
    //   skyfall=podof,x,z,plain|mech|ancient  one drop pod (MakeDropPodAt): plain, mechanoid faction, or an arriving ancient launch pod
    //   skyfall=chunk,x,z           a ship chunk (ShipChunkIncoming), skyfall=crash,x,z a crashing shuttle (ShuttleCrashing)
    //   skyfall=def,<Skyfaller>,<Inner|->,x,z  any skyfaller def holding a new thing of that def (or nothing)
    //   podlaunch=x,z               the transport pod in the cell (with a fuelled pod launcher by it) gets some steel and is
    //                               launched to this map's own tile (CompLaunchable.TryLaunch: the leaving pod is vanilla's)
    //   skyuntil=N                  real ticks until the newest skyfaller is N ticks from impact (leaving: N ticks out), to film
    //                               the end of a fall closely
    //   sky3d=0|1                  skyfallers in 3D (Skyfallers.Enabled, default 1) or the game's flat drawing (before/after)
    //   skycheck=1                  logs every skyfaller on the map (Skyfallers.Report)
    public static partial class Probe
    {
        private static bool SkyKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "sky3d":
                    Skyfallers.Enabled = a[0] != "0";
                    return true;
                case "skycheck":
                    Skyfallers.Report(map);
                    return true;
                case "skyfall":
                    SkyFall(a, map);
                    return true;
                case "skyuntil":
                {
                    // skyuntil=N: real ticks until the newest skyfaller is N ticks from impact (a leaving one: N ticks out).
                    Skyfaller last = null;
                    foreach (Thing t in map.listerThings.AllThings)
                    {
                        if (t is Skyfaller sf && (last == null || sf.thingIDNumber > last.thingIDNumber))
                        {
                            last = sf;
                        }
                    }
                    int n = I(a[0]), done = 0;
                    while (last != null && !last.Destroyed && done < 900
                        && (last.def.skyfaller.reversed ? last.ticksToImpact < n : last.ticksToImpact > n))
                    {
                        Find.TickManager.DoSingleTick();
                        done++;
                    }
                    Log.Message("[IsoRim probe] skyuntil " + n + ": " + done + " ticks, " + (last != null ? last.def.defName + " ticks " + last.ticksToImpact : "none"));
                    return true;
                }
                case "podlaunch":
                    PodLaunch(new IntVec3(I(a[0]), 0, I(a[1])), map);
                    return true;
            }
            return false;
        }

        private static void SkyFall(string[] a, Map map)
        {
            string kind = a[0];
            if (kind == "def")
            {
                ThingDef sky = DefDatabase<ThingDef>.GetNamed(a[1]);
                Thing inner = a[2] != "-" ? ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(a[2]), GenStuff.DefaultStuffFor(DefDatabase<ThingDef>.GetNamed(a[2]))) : null;
                IntVec3 dc = new IntVec3(I(a[3]), 0, I(a[4]));
                Skyfaller s = inner != null ? SkyfallerMaker.SpawnSkyfaller(sky, inner, dc, map) : SkyfallerMaker.SpawnSkyfaller(sky, dc, map);
                Log.Message("[IsoRim probe] skyfall " + sky.defName + " at " + dc + " ticks " + s.ticksToImpact);
                return;
            }
            IntVec3 c = new IntVec3(I(a[1]), 0, I(a[2]));
            Skyfaller made = null;
            switch (kind)
            {
                case "pods":
                {
                    Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 50;
                    List<List<Thing>> groups = new List<List<Thing>> { new List<Thing> { p }, new List<Thing> { steel } };
                    DropPodUtility.DropThingGroupsNear(c, map, groups, 110, false, true, true, true, false);
                    break;
                }
                case "podof":
                {
                    // skyfall=podof,x,z,plain|mech|ancient: one drop pod with some steel (DropPodUtility.MakeDropPodAt), as the
                    // player's transport pods arrive (plain), a mechanoid drop (mech: Faction.OfMechanoids), or an ancient launch
                    // pod arriving (ancient: sentTransporterDef AncientTransportPod)
                    string what = a.Length > 3 ? a[3] : "plain";
                    ActiveTransporterInfo info = new ActiveTransporterInfo();
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 25;
                    info.innerContainer.TryAdd(steel);
                    if (what == "ancient")
                    {
                        info.sentTransporterDef = DefDatabase<ThingDef>.GetNamed("AncientTransportPod");
                    }
                    DropPodUtility.MakeDropPodAt(c, map, info, what == "mech" ? Faction.OfMechanoids : null);
                    break;
                }
                case "meteor":
                    made = SkyfallerMaker.SpawnSkyfaller(ThingDefOf.MeteoriteIncoming, ThingSetMakerDefOf.Meteorite.root.Generate(), c, map);
                    break;
                case "shuttle":
                    made = SkyfallerMaker.SpawnSkyfaller(ThingDefOf.ShuttleIncoming, ThingMaker.MakeThing(ThingDefOf.Shuttle), c, map);
                    break;
                case "pshuttle":
                {
                    Thing sh = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("PassengerShuttle"));
                    sh.SetFaction(Faction.OfPlayer);
                    sh.Rotation = new Rot4(a.Length > 3 ? I(a[3]) : 0);
                    made = SkyfallerMaker.SpawnSkyfaller(DefDatabase<ThingDef>.GetNamed("PassengerShuttleIncoming"), sh, c, map);
                    break;
                }
                case "chunk":
                    made = SkyfallerMaker.SpawnSkyfaller(ThingDefOf.ShipChunkIncoming, ThingDefOf.ShipChunk, c, map);
                    break;
                case "crash":
                    made = SkyfallerMaker.SpawnSkyfaller(ThingDefOf.ShuttleCrashing, ThingMaker.MakeThing(ThingDefOf.ShuttleCrashed), c, map);
                    break;
            }
            Log.Message("[IsoRim probe] skyfall " + kind + " at " + c + (made != null ? " " + made.def.defName + " ticks " + made.ticksToImpact : ""));
        }

        private static void PodLaunch(IntVec3 c, Map map)
        {
            Thing pod = null;
            foreach (Thing t in c.GetThingList(map))
            {
                if (t.TryGetComp<CompLaunchable>() != null)
                {
                    pod = t;
                }
            }
            if (pod == null)
            {
                Log.Warning("[IsoRim probe] podlaunch: no launchable at " + c);
                return;
            }
            CompTransporter tr = pod.TryGetComp<CompTransporter>();
            CompLaunchable launch = pod.TryGetComp<CompLaunchable>();
            tr.groupID = 9071;
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 20;
            tr.GetDirectlyHeldThings().TryAdd(steel);
            AcceptanceReport ok = launch.CanLaunch();
            Log.Message("[IsoRim probe] podlaunch " + pod.def.defName + " at " + c + ": can launch " + ok.Accepted + " " + ok.Reason);
            launch.TryLaunch(map.Tile, null);
        }
    }
}
