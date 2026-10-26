using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only keys for downed pawns crawling (1.7). Real jobs, run with real ticks.
    //   crawldown=x,z[,bx,bz] the colonist in (x,z) gets both legs bruised to 1 hp (no bleeding): downed, awake, able to crawl; every
    //                     other colonist drafted (so nobody carries them); given the bed in (bx,bz) as theirs.
    //                     The game's own downed AI then crawls them to a bed (JobGiver_PatientGoToBed: LayDown) as it ticks;
    //                     run visuals= after the ticks: the facing follows the path only in the per-frame catch-up.
    //   crawluntil=N      ticks until every downed colonist (not in bed) crawls on its way
    //   crawlstop=1       every crawling colonist given the game's job for a downed pawn lying still (Wait_Downed)
    //   crawlcheck=1      logs every downed colonist: cell, facing, job, crawling, moving, bed, the game's body angle
    public static partial class Probe
    {
        private static bool CrawlKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "crawldown":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        return true;
                    }
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    HediffDef bruise = DefDatabase<HediffDef>.GetNamed("Bruise");
                    foreach (BodyPartRecord leg in p.RaceProps.body.AllParts.Where(x => x.def == BodyPartDefOf.Leg).ToList())
                    {
                        Hediff hurt = HediffMaker.MakeHediff(bruise, p, leg);
                        hurt.Severity = leg.def.GetMaxHealth(p) - 1f;   // legs all but gone, still there (and drawn)
                        p.health.AddHediff(hurt, leg);
                    }
                    if (p.needs != null && p.needs.rest != null)
                    {
                        p.needs.rest.CurLevel = 1f;   // awake: a sleeping pawn cannot crawl
                    }
                    Building_Bed own = a.Length > 3 ? map.thingGrid.ThingsListAt(new IntVec3(I(a[2]), 0, I(a[3]))).OfType<Building_Bed>().FirstOrDefault() : null;
                    if (own != null)
                    {
                        own.CompAssignableToPawn.TryAssignPawn(p);   // as the player assigns a bed: the one they crawl to
                    }
                    foreach (Pawn other in map.mapPawns.FreeColonistsSpawned.Where(x => !x.Downed && x.drafter != null))
                    {
                        other.drafter.Drafted = true;   // nobody else rescues them: they crawl by themselves
                    }
                    Log.Message("[IsoRim probe] crawldown " + p.LabelShort + ": downed " + p.Downed + ", can crawl " + p.health.CanCrawl);
                    return true;
                }
                case "crawluntil":
                {
                    // crawluntil=N: game ticks one at a time (at most N) until every downed colonist crawls on its way (not in the
                    // game's rest between crawls), the facing caught up each tick as the frames do.
                    int limit = Mathf.Clamp(I(a[0]), 1, 2000), done = 0;
                    CellRect all = CellRect.WholeMap(map);
                    while (done < limit && map.mapPawns.FreeColonistsSpawned.Any(x => x.Downed && !x.InBed() && !(x.Crawling && x.pather.Moving)))
                    {
                        Find.TickManager.DoSingleTick();
                        foreach (Pawn tp in map.mapPawns.AllPawnsSpawned.ToList())
                        {
                            tp.ProcessPostTickVisuals(1, all);
                        }
                        done++;
                    }
                    foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                    {
                        tp.Drawer.tweener.ResetTweenedPosToRoot();
                    }
                    Log.Message("[IsoRim probe] crawluntil: " + done + " ticks");
                    return true;
                }
                case "crawlstop":
                    foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.Where(x => x.Crawling).ToList())
                    {
                        p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait_Downed), JobCondition.InterruptForced);
                        Log.Message("[IsoRim probe] crawlstop " + p.LabelShort + ": job " + (p.CurJobDef != null ? p.CurJobDef.defName : "-") + ", crawling " + p.Crawling);
                    }
                    return true;
                case "crawlcheck":
                    foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.Where(x => x.Downed).ToList())
                    {
                        Building_Bed bed = p.CurrentBed();
                        Log.Message("[IsoRim probe] crawlcheck " + p.LabelShort + " at " + p.Position + " drawn " + p.DrawPos.ToString("F2") + " rot " + p.Rotation
                            + " job " + (p.CurJobDef != null ? p.CurJobDef.defName : "-") + " crawling " + p.Crawling + " moving " + (p.pather != null && p.pather.Moving)
                            + " posture " + p.GetPosture() + " bed " + (bed != null ? bed.def.defName + " " + bed.Position : "-")
                            + " body angle " + p.Drawer.renderer.BodyAngle(PawnRenderFlags.None).ToString("0"));
                    }
                    return true;
            }
            return false;
        }
    }
}
