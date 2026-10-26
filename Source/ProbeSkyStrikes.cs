using System.Linq;
using RimWorld;
using Verse;

namespace IsoRim
{
    // Harness-only keys for strikes from the sky in 3D (SkyStrikes.cs). Spawned as the game's verbs, permits and weather
    // do; run real ticks after them (tick=N) to film a moment.
    //   strike=beam,x,z             a power beam (as Verb_PowerBeam: 600 ticks, a colonist as instigator)
    //   strike=bomb,x,z             a bombardment (as Verb_Bombardment; starts after its 60-tick warm-up)
    //   strike=salvo,x,z            the empire's orbital salvo permit (as RoyalTitlePermitWorker_OrbitalStrike with the
    //                               OrbitalSalvo permit's numbers); strike=knight,x,z the single orbital strike permit
    //   bolt=x,z[,delay]            a lightning strike on the cell (WeatherEvent_LightningStrike, or _Delayed by delay ticks)
    //   strike3d=0|1                strikes and lobbed shells in 3D (SkyStrikes.Enabled, default 1) or the game's drawing
    //   strikecheck=1               logs every strike, shell and bolt with its impact height (SkyStrikes.Report)
    //   shieldhits=0|1              shots and shells stopped by a shield meet its dome (ShieldHits.Enabled, default 1) or not
    //   domealpha=a                 shield bubbles drawn at opacity a, not the game's pulse (ShieldDomes.HarnessAlpha; -1 = off)
    //   shieldhitcheck=1           logs the shots on their way to a dome (ShieldHits.Report)
    public static partial class Probe
    {
        private static bool StrikeKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "strike3d":
                    SkyStrikes.Enabled = a[0] != "0";
                    return true;
                case "strikecheck":
                    SkyStrikes.Report(map);
                    return true;
                case "shieldhits":
                    ShieldHits.Enabled = a[0] != "0";
                    return true;
                case "domealpha":
                    ShieldDomes.HarnessAlpha = F(a[0]);
                    return true;
                case "shieldhitcheck":
                    ShieldHits.Report();
                    return true;
                case "bolt":
                {
                    IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
                    int delay = a.Length > 2 ? I(a[2]) : 0;
                    map.weatherManager.eventHandler.AddEvent(delay > 0 ? (WeatherEvent)new WeatherEvent_LightningStrikeDelayed(map, c, delay) : new WeatherEvent_LightningStrike(map, c));
                    Log.Message("[IsoRim probe] bolt at " + c + (delay > 0 ? " in " + delay + " ticks" : ""));
                    return true;
                }
                case "strike":
                {
                    IntVec3 c = new IntVec3(I(a[1]), 0, I(a[2]));
                    Pawn by = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                    OrbitalStrike s;
                    switch (a[0])
                    {
                        case "beam":
                            s = (OrbitalStrike)GenSpawn.Spawn(ThingDefOf.PowerBeam, c, map);
                            s.duration = 600;
                            break;
                        case "bomb":
                            s = (OrbitalStrike)GenSpawn.Spawn(ThingDefOf.Bombardment, c, map);
                            s.duration = 540;
                            break;
                        default:
                        {
                            RoyalTitlePermitDef permit = DefDatabase<RoyalTitlePermitDef>.GetNamed(a[0] == "salvo" ? "CallOrbitalSalvo" : "CallOrbitalStrike");
                            Bombardment b = (Bombardment)GenSpawn.Spawn(ThingDefOf.Bombardment, c, map);
                            b.impactAreaRadius = permit.royalAid.radius;
                            b.explosionRadiusRange = permit.royalAid.explosionRadiusRange;
                            b.bombIntervalTicks = permit.royalAid.intervalTicks;
                            b.randomFireRadius = 1;
                            b.explosionCount = permit.royalAid.explosionCount;
                            b.warmupTicks = permit.royalAid.warmupTicks;
                            s = b;
                            break;
                        }
                    }
                    s.instigator = by;
                    if (!(s is Bombardment))
                    {
                        s.StartStrike();   // a bombardment starts itself after its warm-up (Bombardment.Tick)
                    }
                    Log.Message("[IsoRim probe] strike " + a[0] + " (" + s.def.defName + ") at " + c + ", " + s.duration + " ticks");
                    return true;
                }
            }
            return false;
        }
    }
}
