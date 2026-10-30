using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace IsoRim.Fp
{
    // The kinds of skilled work that get skill checks (fp-play-spec 10.1), one adapter per job driver family: how much work
    // the job has, how much is done, how to add or take some, where the hold point is and which tally a check counts in.
    // Bill (crafting, cooking, art, butchery, drugs and surgery) in M3; Construct, Mine, PlantWork, Sow, Repair,
    // AffectFloor, SmoothWall and RemoveBuilding in M4; ExtractTree, PlantSeed and FixBrokenDownBuilding added at the M4-M6
    // integration (the user asked for checks in all skilled work; spec 10.1 lists what is still left out and why).
    //
    // Everything here except the startup lookups runs in the simulation (the map tick and FpCheckResult) or is read by the
    // interface, and reads only synced state: the driver's own fields and the stats it reads itself.

    // An adapter plus what FpWork needs for a job that isn't running (FpWork.TallyKeyOf on a job that isn't the pawn's
    // current one, where there is no driver to ask).
    public abstract class FpWorkAdapterBase : IFpWorkAdapter
    {
        public abstract string Name { get; }

        public abstract bool Matches(JobDriver d);

        public abstract bool InWorkToil(JobDriver d);

        public abstract float Total(JobDriver d);

        public abstract float Done(JobDriver d);

        public abstract void Add(JobDriver d, float units);

        public abstract bool CanHold { get; }

        public abstract bool NearCompletion(JobDriver d);

        public abstract float UnitsPerTick(JobDriver d);

        public abstract int TallyKey(JobDriver d);

        public abstract Thing TallyTarget(JobDriver d);

        // The same type test as Matches, from a job's def (its driver class) and the job's own data.
        internal abstract bool MatchesJob(Job job);

        // The same key as TallyKey, from the job alone.
        internal abstract int KeyForJob(Job job);

        // FpWork calls this when tracking of a piece starts (spec 10.2), after it set the entry's checkJob and tallyKey and
        // before the plan reads Total and Done: Repair keeps the building's hit points there. Map tick only.
        internal virtual void BeginTracking(JobDriver d, FpEntry e)
        {
        }

        // A job-keyed tally's key (spec 10.6): never a thing id (those are 0 or more).
        internal static int JobKey(Job job)
        {
            return -(job.loadID + 1);
        }

        // Spec 10.6 for the thing-targeted adapters: job.targetA's thing, else the job (a floor's cell).
        internal static int KeyOfTarget(Job job)
        {
            Thing t = job?.targetA.Thing;
            return t != null ? t.thingIDNumber : JobKey(job);
        }

        // The job's driver class is T or derives from it (MatchesJob without a driver).
        internal static bool DriverIs<T>(Job job) where T : JobDriver
        {
            Type driver = job?.def?.driverClass;
            return driver != null && typeof(T).IsAssignableFrom(driver);
        }

        // Thing.MaxTickIntervalRate (Thing.cs:606), which Pawn doesn't override: the most ticks one interval of the
        // variable tick rate does at once.
        internal const int MaxIntervalTicks = 15;

        // ToilMaker.MakeToil's debug name for a toil made in a driver's own MakeNewToils (the caller's member name), which
        // is where every M4 driver makes its work toil.
        private const string MadeInMakeNewToils = "MakeNewToils";

        // The driver's skilled work toil (spec 10.1): made in its MakeNewToils, with an active skill and the tick action the
        // work runs in (tickIntervalAction, or tickAction for Repair). The move toils (Toils_Goto), the queue toils of plant
        // work and RemoveBuilding's finishing toil have no active skill; FpWork.AdapterFor also requires the skill.
        internal static bool InMadeWorkToil(JobDriver d, bool interval)
        {
            Toil t = FpAdapters.CurToil(d);
            return t != null && t.activeSkill != null && t.debugName == MadeInMakeNewToils
                && (interval ? t.tickIntervalAction != null : t.tickAction != null);
        }

        // The hold point of work counted in units (spec 10.1, and M3's rule for the bill adapter): within 3% of the end, or
        // within what one interval of the variable tick rate can do (a quarter more than the estimate, for small changes in
        // the work speed): the work toil runs once per interval, up to MaxIntervalTicks ticks at once, so a short job could
        // otherwise step from above the hold point straight to done under a check that was meant to hold it.
        protected bool NearEnd(JobDriver d, float left, float total)
        {
            return left <= total * 0.03f || left <= 1.25f * MaxIntervalTicks * UnitsPerTick(d);
        }

        // Work done plus units, kept in [0, 99%] of the total so a grant never completes the work (the work toil finishes
        // it on its own) and a miss never undoes more than was done. A clamp never moves the value against the grant: a
        // Great on work already past 99% leaves it where it is.
        internal static float AddDone(float done, float units, float total)
        {
            float after = done + units;
            if (units > 0f)
            {
                float cap = total * 0.99f;
                if (after > cap)
                {
                    after = done > cap ? done : cap;
                }
            }
            else if (after < 0f)
            {
                after = done < 0f ? done : 0f;
            }
            return after;
        }

        // The same for work counted down (workLeft): kept in [1%, 100%] of the total.
        internal static float SubLeft(float left, float units, float total)
        {
            float after = left - units;
            if (units > 0f)
            {
                float floor = total * 0.01f;
                if (after < floor)
                {
                    after = left < floor ? left : floor;
                }
            }
            else if (after > total)
            {
                after = left > total ? left : total;
            }
            return after;
        }

        internal static float Clamp(float v, float min, float max)
        {
            return v < min ? min : v > max ? max : v;
        }

        // Hit points are whole: units plus the entry's carried fraction (FpEntry.carry, spec 9.1), rounded half away from
        // zero; the fraction left over is carried to the next grant.
        internal static int RoundCarry(FpEntry e, float units, out float exact)
        {
            exact = units + (e?.carry ?? 0f);
            return exact >= 0f ? (int)(exact + 0.5f) : -(int)(-exact + 0.5f);
        }

        // A private field of a driver, or why the adapter can't run (FpAdapters.Add logs it and leaves the adapter out).
        internal static AccessTools.FieldRef<T, F> Field<T, F>(string name)
        {
            FieldInfo fi = AccessTools.DeclaredField(typeof(T), name);
            if (fi == null || fi.FieldType != typeof(F) || fi.IsStatic)
            {
                throw new MissingFieldException(typeof(T).FullName, name);
            }
            return AccessTools.FieldRefAccess<T, F>(fi);
        }

        // A protected property getter of a driver as a delegate (virtual: the override runs).
        internal static Func<T, F> Getter<T, F>(string name)
        {
            MethodInfo m = AccessTools.DeclaredPropertyGetter(typeof(T), name);
            if (m == null || m.ReturnType != typeof(F))
            {
                throw new MissingMemberException(typeof(T).FullName, name);
            }
            return AccessTools.MethodDelegate<Func<T, F>>(m);
        }
    }

    public static class FpAdapters
    {
        private static readonly List<FpWorkAdapterBase> all = new List<FpWorkAdapterBase>();
        private static bool initialized;

        // JobDriver's protected CurToil and HaveCurToil (JobDriver.cs:52, 69): the toil the driver is in. HaveCurToil first:
        // CurToil logs an error when its index is past the end, which it briefly is while a job finishes.
        private static Func<JobDriver, Toil> curToil;
        private static Func<JobDriver, bool> haveCurToil;

        // The adapters in a fixed order (the first match wins). Built once at startup (FpWork.Install); an adapter whose
        // lookups fail is left out with one error and the others still work.
        internal static void Init()
        {
            if (initialized)
            {
                return;
            }
            initialized = true;
            try
            {
                MethodInfo cur = AccessTools.DeclaredPropertyGetter(typeof(JobDriver), "CurToil");
                MethodInfo have = AccessTools.DeclaredPropertyGetter(typeof(JobDriver), "HaveCurToil");
                if (cur == null || have == null)
                {
                    Log.Error("[IsoRim] First person: JobDriver.CurToil or HaveCurToil not found; skill checks are off.");
                    return;
                }
                curToil = AccessTools.MethodDelegate<Func<JobDriver, Toil>>(cur);
                haveCurToil = AccessTools.MethodDelegate<Func<JobDriver, bool>>(have);
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim] First person: binding JobDriver's current toil failed; skill checks are off: " + e);
                curToil = null;
                haveCurToil = null;
                return;
            }
            Add("Bill", () => new FpBillAdapter());
            Add("Construct", () => new FpConstructAdapter());
            Add("Mine", () => new FpMineAdapter());
            Add("PlantWork", () => new FpPlantWorkAdapter());
            Add("Sow", () => new FpSowAdapter());
            Add("Repair", () => new FpRepairAdapter());
            Add("AffectFloor", () => new FpAffectFloorAdapter());
            Add("SmoothWall", () => new FpSmoothWallAdapter());
            Add("RemoveBuilding", () => new FpRemoveBuildingAdapter());
            Add("ExtractTree", () => new FpExtractTreeAdapter());
            Add("PlantSeed", () => new FpPlantSeedAdapter());
            Add("FixBrokenDown", () => new FpFixBrokenDownAdapter());
            ModLog.Note("[IsoRim] First person: skill-check adapters: " + Names);
        }

        // The adapters that passed their startup lookups, in order (the self-test and the startup note).
        internal static string Names
        {
            get { return string.Join(", ", all.ConvertAll(a => a.Name)); }
        }

        internal static bool Has(string name)
        {
            return all.Exists(a => a.Name == name);
        }

        private static void Add(string name, Func<FpWorkAdapterBase> make)
        {
            try
            {
                all.Add(make());
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim] First person: the " + name + " skill-check adapter failed its startup lookups and is off (no checks for that work): " + e);
            }
        }

        // The adapter for this driver's type, whatever toil it is in and whatever its skill (FpWork.AdapterFor adds those
        // tests). Null when no adapter knows the driver.
        internal static FpWorkAdapterBase ByDriver(JobDriver d)
        {
            if (!initialized)
            {
                Init();
            }
            if (d == null)
            {
                return null;
            }
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Matches(d))
                {
                    return all[i];
                }
            }
            return null;
        }

        // Spec 10.6 for a job that isn't running: the adapter's key by the job's driver class, else the job's own key.
        internal static int KeyForJob(Job job)
        {
            if (!initialized)
            {
                Init();
            }
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].MatchesJob(job))
                {
                    return all[i].KeyForJob(job);
                }
            }
            return FpWorkAdapterBase.JobKey(job);
        }

        // The toil the driver is in now, null between toils.
        internal static Toil CurToil(JobDriver d)
        {
            if (curToil == null || d == null || !haveCurToil(d))
            {
                return null;
            }
            return curToil(d);
        }
    }

    // Bills (spec 10.1): JobDriver_DoBill in its DoRecipeWork toil (Toils_Recipe.cs:43), which takes
    // `workSpeedStat x workTableSpeedStat` off the driver's public workLeft every interval and mirrors it to the unfinished
    // thing (Toils_Recipe.cs:90-127). Covers crafting, cooking, art, butchery, drugs and surgery (Bill_Medical).
    //
    // Not autonomous bills (Bill_Autonomous, the mech gestator's Bill_Mech family): their work in the formed state is a
    // flat 300 (Bill_Autonomous.GetWorkAmount), not the recipe's total, so this adapter's total would be wrong for them.
    public sealed class FpBillAdapter : FpWorkAdapterBase
    {
        // ToilMaker.MakeToil's debug name for the work toil (the caller's member name, Toils_Recipe.DoRecipeWork).
        private const string WorkToilName = "DoRecipeWork";

        public override string Name
        {
            get { return "Bill"; }
        }

        public override bool CanHold
        {
            get { return true; }
        }

        public override bool Matches(JobDriver d)
        {
            return d is JobDriver_DoBill && d.job?.bill != null && !(d.job.bill is Bill_Autonomous) && d.job.bill.recipe != null;
        }

        internal override bool MatchesJob(Job job)
        {
            Type driver = job?.def?.driverClass;
            return driver != null && typeof(JobDriver_DoBill).IsAssignableFrom(driver)
                && job.bill != null && !(job.bill is Bill_Autonomous) && job.bill.recipe != null;
        }

        public override bool InWorkToil(JobDriver d)
        {
            return FpAdapters.CurToil(d)?.debugName == WorkToilName;
        }

        // The recipe's work for the thing at B (the unfinished thing, or the last ingredient placed), as the work toil's
        // progress bar computes it (Toils_Recipe.cs:150) and the bill's start set workLeft from it.
        public override float Total(JobDriver d)
        {
            Job job = d.job;
            return job.bill.recipe.WorkAmountTotal(job.GetTarget(TargetIndex.B).Thing);
        }

        public override float Done(JobDriver d)
        {
            float total = Total(d);
            float done = total - ((JobDriver_DoBill)d).workLeft;
            return done < 0f ? 0f : done > total ? total : done;
        }

        // workLeft -= units, kept in [1%, 100%] of the total so a grant never completes the work (the work toil finishes it
        // on its own next interval) and never undoes more than was done. A clamp never moves the value against the grant:
        // a Great on work already past 99% leaves it where it is.
        public override void Add(JobDriver d, float units)
        {
            JobDriver_DoBill bill = (JobDriver_DoBill)d;
            float total = Total(d);
            float before = bill.workLeft;
            float after = before - units;
            if (units > 0f)
            {
                float floor = total * 0.01f;
                if (after < floor)
                {
                    after = before < floor ? before : floor;
                }
            }
            else if (after > total)
            {
                after = before > total ? before : total;
            }
            bill.workLeft = after;
            // The work toil copies workLeft to the unfinished thing every interval; this keeps the two equal in between, so
            // a save (or the piece's own inspect text) shows the same work.
            if (d.job.GetTarget(TargetIndex.B).Thing is UnfinishedThing uft && !uft.Destroyed && !uft.debugCompleted)
            {
                uft.workLeft = after;
            }
        }

        // Within 3% of the end (spec 10.1), or within the work one interval can do: the work toil runs once per interval of
        // the pawn's variable tick rate, up to MaxIntervalTicks ticks at once (Thing.DoTick), so a short job can step from
        // above 3% straight to done (a simple meal at 15 ticks per interval does 5% a step) and finish under a check that
        // was meant to hold it. The longest interval, not the current rate: the delta piles up while the rate changes. A
        // quarter more than the estimate covers small changes in the work speed. Only asked while a check is pending.
        public override bool NearCompletion(JobDriver d)
        {
            float left = ((JobDriver_DoBill)d).workLeft;
            return left <= Total(d) * 0.03f || left <= 1.25f * MaxIntervalTicks * UnitsPerTick(d);
        }

        // The work toil's own speed (Toils_Recipe.cs:105-115): the pawn's work speed stat, the bench's speed factor, and
        // the dev "fast crafting" switch (which the toil reads too).
        public override float UnitsPerTick(JobDriver d)
        {
            Job job = d.job;
            RecipeDef recipe = job.bill.recipe;
            float speed = recipe.workSpeedStat == null ? 1f : d.pawn.GetStatValue(recipe.workSpeedStat);
            if (recipe.workTableSpeedStat != null && job.GetTarget(TargetIndex.A).Thing is Building_WorkTable table)
            {
                speed *= table.GetStatValue(recipe.workTableSpeedStat);
            }
            if (DebugSettings.fastCrafting)
            {
                speed *= 30f;
            }
            return speed;
        }

        public override int TallyKey(JobDriver d)
        {
            return KeyForJob(d.job);
        }

        // The unfinished thing at B: every job on the same piece (resumed, or finished by another colonist) counts in one
        // tally. Otherwise the job: a meal, an operation or a butchering is one job.
        internal override int KeyForJob(Job job)
        {
            return job.GetTarget(TargetIndex.B).Thing is UnfinishedThing uft ? uft.thingIDNumber : JobKey(job);
        }

        public override Thing TallyTarget(JobDriver d)
        {
            return d.job.GetTarget(TargetIndex.B).Thing as UnfinishedThing;
        }
    }

    // Construction (spec 10.1): JobDriver_ConstructFinishFrame's build toil adds `ConstructionSpeed x 1.7 x the stuff's
    // ConstructionSpeedFactor` to the frame's public workDone every interval and completes it at WorkToBuild
    // (JobDriver_ConstructFinishFrame.cs:44-80). Each interval also rolls the construction failure from
    // ConstructSuccessChance (FpLevers moves it by form); a grant adds work without a roll, a miss takes work back and so
    // adds rolls. The quality of a finished building is rolled by Frame.CompleteConstruction through
    // QualityUtility.GenerateQualityCreatedByPawn while the frame's job is still the worker's, so the frame's tally reaches
    // it (FpLevers' quality patch). The tally is the frame's: every job on it counts in one.
    public sealed class FpConstructAdapter : FpWorkAdapterBase
    {
        public override string Name
        {
            get { return "Construct"; }
        }

        public override bool CanHold
        {
            get { return true; }
        }

        private static Frame FrameOf(JobDriver d)
        {
            return d.job?.targetA.Thing as Frame;
        }

        public override bool Matches(JobDriver d)
        {
            return d is JobDriver_ConstructFinishFrame;
        }

        internal override bool MatchesJob(Job job)
        {
            return DriverIs<JobDriver_ConstructFinishFrame>(job);
        }

        public override bool InWorkToil(JobDriver d)
        {
            return InMadeWorkToil(d, true) && FrameOf(d) is Frame f && !f.Destroyed;
        }

        // 0 without a frame: FpWork then sees no skilled work (the interface and the self-test read Total and Done for any
        // driver FpWork.AdapterFor accepts).
        public override float Total(JobDriver d)
        {
            Frame f = FrameOf(d);
            return f == null ? 0f : f.WorkToBuild;
        }

        public override float Done(JobDriver d)
        {
            Frame f = FrameOf(d);
            return f == null ? 0f : Clamp(f.workDone, 0f, Total(d));
        }

        public override void Add(JobDriver d, float units)
        {
            Frame f = FrameOf(d);
            f.workDone = AddDone(f.workDone, units, Total(d));
        }

        public override bool NearCompletion(JobDriver d)
        {
            float total = Total(d);
            return NearEnd(d, total - FrameOf(d).workDone, total);
        }

        // The build toil's own speed (JobDriver_ConstructFinishFrame.cs:53-57).
        public override float UnitsPerTick(JobDriver d)
        {
            Frame f = FrameOf(d);
            float speed = d.pawn.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7f;
            if (f?.Stuff != null)
            {
                speed *= f.Stuff.GetStatValueAbstract(StatDefOf.ConstructionSpeedFactor);
            }
            return speed;
        }

        public override int TallyKey(JobDriver d)
        {
            return KeyOfTarget(d.job);
        }

        internal override int KeyForJob(Job job)
        {
            return KeyOfTarget(job);
        }

        public override Thing TallyTarget(JobDriver d)
        {
            return FrameOf(d);
        }
    }

    // Mining (spec 10.1): JobDriver_Mine's toil hits the rock every round(100 / MiningSpeed) ticks for 80 hit points
    // (natural rock) or 40, and the hit that finds the rock at or under that destroys it (JobDriver_Mine.cs:46-133). The
    // work is counted in hit points: total = MaxHitPoints - one hit, done = hit points taken so far. A grant takes or
    // gives back hit points directly (never the last one: the next pick hit mines the rock out) and keeps the rock's yield
    // book (Mineable.yieldPct, which each mining hit adds to with the miner's MiningYield, Mineable.cs:17-27, 88-96) in step,
    // so a Great doesn't lose the ore it skipped and a miss doesn't pay the restored hit points twice. The tally is the
    // rock's.
    public sealed class FpMineAdapter : FpWorkAdapterBase
    {
        public override string Name
        {
            get { return "Mine"; }
        }

        public override bool CanHold
        {
            get { return true; }
        }

        private static Thing Rock(JobDriver d)
        {
            return d.job?.targetA.Thing;
        }

        // JobDriver_Mine.DoDamage's damage per pick hit.
        private static int Hit(Thing t)
        {
            return t.def.building != null && t.def.building.isNaturalRock ? 80 : 40;
        }

        public override bool Matches(JobDriver d)
        {
            return d is JobDriver_Mine;
        }

        internal override bool MatchesJob(Job job)
        {
            return DriverIs<JobDriver_Mine>(job);
        }

        public override bool InWorkToil(JobDriver d)
        {
            return InMadeWorkToil(d, true) && Rock(d) is Thing t && t.Spawned && t.def.useHitPoints;
        }

        public override float Total(JobDriver d)
        {
            Thing t = Rock(d);
            return t == null ? 0f : t.MaxHitPoints - Hit(t);
        }

        public override float Done(JobDriver d)
        {
            Thing t = Rock(d);
            return t == null ? 0f : Clamp(t.MaxHitPoints - t.HitPoints, 0f, Total(d));
        }

        // Hit points -= round(units + carry), kept in [1, max hit points] and never against the grant.
        public override void Add(JobDriver d, float units)
        {
            Thing t = Rock(d);
            FpEntry e = FpRegistry.EntryOf(d.pawn);
            int n = RoundCarry(e, units, out float exact);
            int hp = t.HitPoints;
            int max = t.MaxHitPoints;
            int lo = hp < 1 ? hp : 1;
            int hi = hp > max ? hp : max;
            int after = hp - n;
            bool clamped = after < lo || after > hi;
            after = after < lo ? lo : after > hi ? hi : after;
            if (e != null)
            {
                e.carry = clamped ? 0f : exact - n;
            }
            if (after == hp)
            {
                return;
            }
            // As Mineable.PreApplyDamage books a mining hit (before the hit points change): hit points taken add their share
            // of the yield, hit points given back take theirs out again.
            if (t is Mineable m && t.def.building?.mineableThing != null && t.def.building.mineableYieldWasteable)
            {
                m.Notify_TookMiningDamage(hp - after, d.pawn);
            }
            t.HitPoints = after;
        }

        // The next pick hit mines the rock out (JobDriver_Mine.DoDamage): hold before it while a check waits.
        public override bool NearCompletion(JobDriver d)
        {
            Thing t = Rock(d);
            return t.HitPoints <= Hit(t);
        }

        // Hit points per tick at the pick rate JobDriver_Mine.ResetTicksToPickHit sets (with its floor for non-colonists).
        public override float UnitsPerTick(JobDriver d)
        {
            float speed = d.pawn.GetStatValue(StatDefOf.MiningSpeed);
            if (speed < 0.6f && d.pawn.Faction != Faction.OfPlayer)
            {
                speed = 0.6f;
            }
            int ticks = speed > 0f ? (int)Math.Round(100f / speed) : 100000;
            return (float)Hit(Rock(d)) / Math.Max(1, ticks);
        }

        public override int TallyKey(JobDriver d)
        {
            return KeyOfTarget(d.job);
        }

        internal override int KeyForJob(Job job)
        {
            return KeyOfTarget(job);
        }

        public override Thing TallyTarget(JobDriver d)
        {
            return Rock(d);
        }
    }

    // Harvesting and cutting (spec 10.1): JobDriver_PlantWork (PlantHarvest, PlantCut and any subclass) adds
    // WorkDonePerTick to its private workDone every interval and collects the plant at its harvestWork, rolling the yield
    // from PlantHarvestYield (or DrugHarvestYield, which takes PlantHarvestYield as a factor) on the way
    // (JobDriver_PlantWork.cs:55-111). One job can work several plants in turn (its target queue): each plant is its own
    // piece, with its own plan and tally (FpWork starts tracking again when the tally key changes within the job).
    public sealed class FpPlantWorkAdapter : FpWorkAdapterBase
    {
        private readonly AccessTools.FieldRef<JobDriver_PlantWork, float> workDone = Field<JobDriver_PlantWork, float>("workDone");

        public override string Name
        {
            get { return "PlantWork"; }
        }

        public override bool CanHold
        {
            get { return true; }
        }

        private static Plant PlantOf(JobDriver d)
        {
            return d.job?.targetA.Thing as Plant;
        }

        public override bool Matches(JobDriver d)
        {
            return d is JobDriver_PlantWork;
        }

        internal override bool MatchesJob(Job job)
        {
            return DriverIs<JobDriver_PlantWork>(job);
        }

        public override bool InWorkToil(JobDriver d)
        {
            return InMadeWorkToil(d, true) && PlantOf(d) is Plant p && !p.Destroyed && p.def.plant != null;
        }

        public override float Total(JobDriver d)
        {
            Plant p = PlantOf(d);
            return p?.def.plant == null ? 0f : p.def.plant.harvestWork;
        }

        public override float Done(JobDriver d)
        {
            return Clamp(workDone((JobDriver_PlantWork)d), 0f, Total(d));
        }

        public override void Add(JobDriver d, float units)
        {
            ref float done = ref workDone((JobDriver_PlantWork)d);
            done = AddDone(done, units, Total(d));
        }

        public override bool NearCompletion(JobDriver d)
        {
            float total = Total(d);
            return NearEnd(d, total - workDone((JobDriver_PlantWork)d), total);
        }

        // The toil's own speed (the plant's growth makes young plants quicker to cut).
        public override float UnitsPerTick(JobDriver d)
        {
            return JobDriver_PlantWork.WorkDonePerTick(d.pawn, PlantOf(d));
        }

        public override int TallyKey(JobDriver d)
        {
            return KeyOfTarget(d.job);
        }

        internal override int KeyForJob(Job job)
        {
            return KeyOfTarget(job);
        }

        public override Thing TallyTarget(JobDriver d)
        {
            return PlantOf(d);
        }
    }

    // Sowing (spec 10.1): JobDriver_PlantSow spawns the plant when its sow toil starts (job target A becomes the plant)
    // and adds PlantWorkSpeed to its private sowWorkDone every interval until the plant's sowWork
    // (JobDriver_PlantSow.cs:39-70). The tally is the sown plant's, so it lives as long as the plant does: a later harvest of
    // that plant counts in the same tally, and its form reaches the harvest yield.
    public sealed class FpSowAdapter : FpWorkAdapterBase
    {
        private readonly AccessTools.FieldRef<JobDriver_PlantSow, float> sowWorkDone = Field<JobDriver_PlantSow, float>("sowWorkDone");

        public override string Name
        {
            get { return "Sow"; }
        }

        public override bool CanHold
        {
            get { return true; }
        }

        private static Plant PlantOf(JobDriver d)
        {
            return d.job?.targetA.Thing as Plant;
        }

        public override bool Matches(JobDriver d)
        {
            return d is JobDriver_PlantSow;
        }

        internal override bool MatchesJob(Job job)
        {
            return DriverIs<JobDriver_PlantSow>(job);
        }

        public override bool InWorkToil(JobDriver d)
        {
            return InMadeWorkToil(d, true) && PlantOf(d) is Plant p && !p.Destroyed && p.def.plant != null;
        }

        public override float Total(JobDriver d)
        {
            Plant p = PlantOf(d);
            return p?.def.plant == null ? 0f : p.def.plant.sowWork;
        }

        public override float Done(JobDriver d)
        {
            return Clamp(sowWorkDone((JobDriver_PlantSow)d), 0f, Total(d));
        }

        public override void Add(JobDriver d, float units)
        {
            ref float done = ref sowWorkDone((JobDriver_PlantSow)d);
            done = AddDone(done, units, Total(d));
        }

        public override bool NearCompletion(JobDriver d)
        {
            float total = Total(d);
            return NearEnd(d, total - sowWorkDone((JobDriver_PlantSow)d), total);
        }

        public override float UnitsPerTick(JobDriver d)
        {
            return d.pawn.GetStatValue(StatDefOf.PlantWorkSpeed);
        }

        public override int TallyKey(JobDriver d)
        {
            return KeyOfTarget(d.job);
        }

        internal override int KeyForJob(Job job)
        {
            return KeyOfTarget(job);
        }

        public override Thing TallyTarget(JobDriver d)
        {
            return PlantOf(d);
        }
    }

    // Repair (spec 10.1): JobDriver_Repair's toil works in its tickAction, every tick: after an 80-unit warm-up it adds
    // one hit point per 20 units of `ConstructionSpeed x 1.7` and ends the job at full hit points (JobDriver_Repair.cs:
    // 41-71). The work is counted in hit points from where they stood when tracking began (FpEntry.startHp, spec 9.1), the
    // fraction of a grant in FpEntry.carry. Never held: DriverTickInterval doesn't run this work, so a check due near the
    // end can be voided by the job finishing on its own (no effect either way). The tally is the building's.
    public sealed class FpRepairAdapter : FpWorkAdapterBase
    {
        public override string Name
        {
            get { return "Repair"; }
        }

        public override bool CanHold
        {
            get { return false; }
        }

        private static Building BuildingOf(JobDriver d)
        {
            return d.job?.targetA.Thing as Building;
        }

        // The entry tracking this very job (Total and Done count from its startHp), else null: before tracking starts the
        // work is counted from the hit points now.
        private static FpEntry Tracking(JobDriver d)
        {
            FpEntry e = FpRegistry.EntryOf(d.pawn);
            return e != null && d.job != null && e.checkJob == d.job.loadID ? e : null;
        }

        public override bool Matches(JobDriver d)
        {
            return d is JobDriver_Repair;
        }

        internal override bool MatchesJob(Job job)
        {
            return DriverIs<JobDriver_Repair>(job);
        }

        public override bool InWorkToil(JobDriver d)
        {
            return InMadeWorkToil(d, false) && BuildingOf(d) is Building b && b.Spawned && b.def.useHitPoints;
        }

        internal override void BeginTracking(JobDriver d, FpEntry e)
        {
            e.startHp = BuildingOf(d).HitPoints;
            e.carry = 0f;
        }

        public override float Total(JobDriver d)
        {
            Building b = BuildingOf(d);
            if (b == null)
            {
                return 0f;
            }
            FpEntry e = Tracking(d);
            return b.MaxHitPoints - (e != null ? e.startHp : b.HitPoints);
        }

        public override float Done(JobDriver d)
        {
            Building b = BuildingOf(d);
            FpEntry e = Tracking(d);
            return e == null || b == null ? 0f : Clamp(b.HitPoints - e.startHp, 0f, Total(d));
        }

        // Hit points += round(units + carry), at most max - 1 (the repair toil's own next point ends the job), at least
        // where tracking began, and never against the grant.
        public override void Add(JobDriver d, float units)
        {
            Building b = BuildingOf(d);
            FpEntry e = FpRegistry.EntryOf(d.pawn);
            int n = RoundCarry(e, units, out float exact);
            int hp = b.HitPoints;
            int start = e != null ? e.startHp : hp;
            int floor = start < 1 ? 1 : start;
            int lo = hp < floor ? hp : floor;
            int top = b.MaxHitPoints - 1;
            int hi = hp > top ? hp : top;
            int after = hp + n;
            bool clamped = after < lo || after > hi;
            after = after < lo ? lo : after > hi ? hi : after;
            if (e != null)
            {
                e.carry = clamped ? 0f : exact - n;
            }
            if (after == hp)
            {
                return;
            }
            b.HitPoints = after;
            // As the repair toil does after each point (JobDriver_Repair.cs:65).
            b.Map?.listerBuildingsRepairable.Notify_BuildingRepaired(b);
        }

        public override bool NearCompletion(JobDriver d)
        {
            return false;
        }

        // Hit points per tick (one per 20 units of the toil's speed).
        public override float UnitsPerTick(JobDriver d)
        {
            return d.pawn.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7f / 20f;
        }

        public override int TallyKey(JobDriver d)
        {
            return KeyOfTarget(d.job);
        }

        internal override int KeyForJob(Job job)
        {
            return KeyOfTarget(job);
        }

        public override Thing TallyTarget(JobDriver d)
        {
            return BuildingOf(d);
        }
    }

    // Floor work (spec 10.1): JobDriver_AffectFloor and its subclasses (SmoothFloor, RemoveFloor, RemoveFoundation) set the
    // private workLeft to the protected BaseWorkAmount when the work toil starts and take `SpeedStat x 1.7` off it every
    // interval (JobDriver_AffectFloor.cs:28-58). The target is a cell, so the tally is the job's.
    public sealed class FpAffectFloorAdapter : FpWorkAdapterBase
    {
        private readonly AccessTools.FieldRef<JobDriver_AffectFloor, float> workLeft = Field<JobDriver_AffectFloor, float>("workLeft");
        private readonly Func<JobDriver_AffectFloor, int> baseWork = Getter<JobDriver_AffectFloor, int>("BaseWorkAmount");
        private readonly Func<JobDriver_AffectFloor, StatDef> speedStat = Getter<JobDriver_AffectFloor, StatDef>("SpeedStat");

        public override string Name
        {
            get { return "AffectFloor"; }
        }

        public override bool CanHold
        {
            get { return true; }
        }

        public override bool Matches(JobDriver d)
        {
            return d is JobDriver_AffectFloor;
        }

        internal override bool MatchesJob(Job job)
        {
            return DriverIs<JobDriver_AffectFloor>(job);
        }

        public override bool InWorkToil(JobDriver d)
        {
            return InMadeWorkToil(d, true);
        }

        public override float Total(JobDriver d)
        {
            return baseWork((JobDriver_AffectFloor)d);
        }

        public override float Done(JobDriver d)
        {
            float total = Total(d);
            return Clamp(total - workLeft((JobDriver_AffectFloor)d), 0f, total);
        }

        public override void Add(JobDriver d, float units)
        {
            ref float left = ref workLeft((JobDriver_AffectFloor)d);
            left = SubLeft(left, units, Total(d));
        }

        public override bool NearCompletion(JobDriver d)
        {
            return NearEnd(d, workLeft((JobDriver_AffectFloor)d), Total(d));
        }

        // The toil's own speed (JobDriver_AffectFloor.cs:35-36).
        public override float UnitsPerTick(JobDriver d)
        {
            StatDef stat = speedStat((JobDriver_AffectFloor)d);
            float speed = stat != null && !stat.Worker.IsDisabledFor(d.pawn) ? d.pawn.GetStatValue(stat) : 1f;
            return speed * 1.7f;
        }

        public override int TallyKey(JobDriver d)
        {
            return KeyOfTarget(d.job);
        }

        internal override int KeyForJob(Job job)
        {
            return KeyOfTarget(job);
        }

        public override Thing TallyTarget(JobDriver d)
        {
            return d.job?.targetA.Thing;
        }
    }

    // Smoothing a wall (spec 10.1): JobDriver_SmoothWall sets the private workLeft to BaseWorkAmount (6500) when the work
    // toil starts and takes `SmoothingSpeed x 1.7` off it every interval (JobDriver_SmoothWall.cs:29-48). The tally is the
    // wall's (smoothing replaces it, so the tally goes with it).
    public sealed class FpSmoothWallAdapter : FpWorkAdapterBase
    {
        private readonly AccessTools.FieldRef<JobDriver_SmoothWall, float> workLeft = Field<JobDriver_SmoothWall, float>("workLeft");
        private readonly Func<JobDriver_SmoothWall, int> baseWork = Getter<JobDriver_SmoothWall, int>("BaseWorkAmount");

        public override string Name
        {
            get { return "SmoothWall"; }
        }

        public override bool CanHold
        {
            get { return true; }
        }

        public override bool Matches(JobDriver d)
        {
            return d is JobDriver_SmoothWall;
        }

        internal override bool MatchesJob(Job job)
        {
            return DriverIs<JobDriver_SmoothWall>(job);
        }

        public override bool InWorkToil(JobDriver d)
        {
            return InMadeWorkToil(d, true) && d.job?.targetA.Thing is Thing t && !t.Destroyed;
        }

        public override float Total(JobDriver d)
        {
            return baseWork((JobDriver_SmoothWall)d);
        }

        public override float Done(JobDriver d)
        {
            float total = Total(d);
            return Clamp(total - workLeft((JobDriver_SmoothWall)d), 0f, total);
        }

        public override void Add(JobDriver d, float units)
        {
            ref float left = ref workLeft((JobDriver_SmoothWall)d);
            left = SubLeft(left, units, Total(d));
        }

        public override bool NearCompletion(JobDriver d)
        {
            return NearEnd(d, workLeft((JobDriver_SmoothWall)d), Total(d));
        }

        public override float UnitsPerTick(JobDriver d)
        {
            return d.pawn.GetStatValue(StatDefOf.SmoothingSpeed) * 1.7f;
        }

        public override int TallyKey(JobDriver d)
        {
            return KeyOfTarget(d.job);
        }

        internal override int KeyForJob(Job job)
        {
            return KeyOfTarget(job);
        }

        public override Thing TallyTarget(JobDriver d)
        {
            return d.job?.targetA.Thing;
        }
    }

    // Deconstructing, uninstalling and filling in (spec 10.1): JobDriver_RemoveBuilding and its subclasses set the private
    // totalNeededWork and workLeft when the work toil starts and take `ConstructionSpeed x 1.7` off workLeft every interval
    // (JobDriver_RemoveBuilding.cs:44-58). The tally is the building's.
    public sealed class FpRemoveBuildingAdapter : FpWorkAdapterBase
    {
        private readonly AccessTools.FieldRef<JobDriver_RemoveBuilding, float> workLeft = Field<JobDriver_RemoveBuilding, float>("workLeft");
        private readonly AccessTools.FieldRef<JobDriver_RemoveBuilding, float> totalNeededWork = Field<JobDriver_RemoveBuilding, float>("totalNeededWork");

        public override string Name
        {
            get { return "RemoveBuilding"; }
        }

        public override bool CanHold
        {
            get { return true; }
        }

        public override bool Matches(JobDriver d)
        {
            return d is JobDriver_RemoveBuilding;
        }

        internal override bool MatchesJob(Job job)
        {
            return DriverIs<JobDriver_RemoveBuilding>(job);
        }

        public override bool InWorkToil(JobDriver d)
        {
            return InMadeWorkToil(d, true) && d.job?.targetA.Thing is Thing t && !t.Destroyed;
        }

        public override float Total(JobDriver d)
        {
            return totalNeededWork((JobDriver_RemoveBuilding)d);
        }

        public override float Done(JobDriver d)
        {
            float total = Total(d);
            return Clamp(total - workLeft((JobDriver_RemoveBuilding)d), 0f, total);
        }

        public override void Add(JobDriver d, float units)
        {
            ref float left = ref workLeft((JobDriver_RemoveBuilding)d);
            left = SubLeft(left, units, Total(d));
        }

        public override bool NearCompletion(JobDriver d)
        {
            return NearEnd(d, workLeft((JobDriver_RemoveBuilding)d), Total(d));
        }

        public override float UnitsPerTick(JobDriver d)
        {
            return d.pawn.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7f;
        }

        public override int TallyKey(JobDriver d)
        {
            return KeyOfTarget(d.job);
        }

        internal override int KeyForJob(Job job)
        {
            return KeyOfTarget(job);
        }

        public override Thing TallyTarget(JobDriver d)
        {
            return d.job?.targetA.Thing;
        }
    }

    // Extracting a tree (integration): JobDriver_ExtractTree sets the private totalNeededWork and workLeft to the tree's
    // harvestWork when its work toil starts and takes JobDriver_PlantWork.WorkDonePerTick off workLeft every interval
    // (JobDriver_ExtractTree.cs:41-64); the next toil spawns the minified tree. The tally is the tree's.
    public sealed class FpExtractTreeAdapter : FpWorkAdapterBase
    {
        private readonly AccessTools.FieldRef<JobDriver_ExtractTree, float> workLeft = Field<JobDriver_ExtractTree, float>("workLeft");
        private readonly AccessTools.FieldRef<JobDriver_ExtractTree, float> totalNeededWork = Field<JobDriver_ExtractTree, float>("totalNeededWork");

        public override string Name
        {
            get { return "ExtractTree"; }
        }

        public override bool CanHold
        {
            get { return true; }
        }

        private static Plant TreeOf(JobDriver d)
        {
            return d.job?.targetA.Thing?.GetInnerIfMinified() as Plant;
        }

        public override bool Matches(JobDriver d)
        {
            return d is JobDriver_ExtractTree;
        }

        internal override bool MatchesJob(Job job)
        {
            return DriverIs<JobDriver_ExtractTree>(job);
        }

        public override bool InWorkToil(JobDriver d)
        {
            return InMadeWorkToil(d, true) && d.job?.targetA.Thing is Thing t && !t.Destroyed && TreeOf(d)?.def.plant != null;
        }

        public override float Total(JobDriver d)
        {
            return totalNeededWork((JobDriver_ExtractTree)d);
        }

        public override float Done(JobDriver d)
        {
            float total = Total(d);
            return Clamp(total - workLeft((JobDriver_ExtractTree)d), 0f, total);
        }

        public override void Add(JobDriver d, float units)
        {
            ref float left = ref workLeft((JobDriver_ExtractTree)d);
            left = SubLeft(left, units, Total(d));
        }

        public override bool NearCompletion(JobDriver d)
        {
            return NearEnd(d, workLeft((JobDriver_ExtractTree)d), Total(d));
        }

        // The toil's own speed (JobDriver_ExtractTree.cs:49).
        public override float UnitsPerTick(JobDriver d)
        {
            Plant tree = TreeOf(d);
            return tree == null ? 1f : JobDriver_PlantWork.WorkDonePerTick(d.pawn, tree);
        }

        public override int TallyKey(JobDriver d)
        {
            return KeyOfTarget(d.job);
        }

        internal override int KeyForJob(Job job)
        {
            return KeyOfTarget(job);
        }

        public override Thing TallyTarget(JobDriver d)
        {
            return d.job?.targetA.Thing;
        }
    }

    // Planting a seed (integration; Ideology's Gauranlen seed and the like, CompPlantable): JobDriver_PlantSeed adds
    // PlantWorkSpeed to its private sowWorkDone every interval of its plant toil until job.plantDefToSow's sowWork
    // (JobDriver_PlantSeed.cs:49-66), then plants. The seed is used up and the target is a cell, so the tally is the job's.
    public sealed class FpPlantSeedAdapter : FpWorkAdapterBase
    {
        private readonly AccessTools.FieldRef<JobDriver_PlantSeed, float> sowWorkDone = Field<JobDriver_PlantSeed, float>("sowWorkDone");

        public override string Name
        {
            get { return "PlantSeed"; }
        }

        public override bool CanHold
        {
            get { return true; }
        }

        public override bool Matches(JobDriver d)
        {
            return d is JobDriver_PlantSeed;
        }

        internal override bool MatchesJob(Job job)
        {
            return DriverIs<JobDriver_PlantSeed>(job);
        }

        public override bool InWorkToil(JobDriver d)
        {
            return InMadeWorkToil(d, true) && d.job?.plantDefToSow?.plant != null;
        }

        public override float Total(JobDriver d)
        {
            return d.job?.plantDefToSow?.plant == null ? 0f : d.job.plantDefToSow.plant.sowWork;
        }

        public override float Done(JobDriver d)
        {
            return Clamp(sowWorkDone((JobDriver_PlantSeed)d), 0f, Total(d));
        }

        public override void Add(JobDriver d, float units)
        {
            ref float done = ref sowWorkDone((JobDriver_PlantSeed)d);
            done = AddDone(done, units, Total(d));
        }

        public override bool NearCompletion(JobDriver d)
        {
            float total = Total(d);
            return NearEnd(d, total - sowWorkDone((JobDriver_PlantSeed)d), total);
        }

        public override float UnitsPerTick(JobDriver d)
        {
            return d.pawn.GetStatValue(StatDefOf.PlantWorkSpeed);
        }

        public override int TallyKey(JobDriver d)
        {
            return JobKey(d.job);
        }

        internal override int KeyForJob(Job job)
        {
            return JobKey(job);
        }

        public override Thing TallyTarget(JobDriver d)
        {
            return null;
        }
    }

    // Fixing a broken-down machine (integration): JobDriver_FixBrokenDownBuilding waits a fixed 1000 ticks
    // (Toils_General.Wait: a Delay toil whose countdown, the driver's public ticksLeftThisToil, runs in DriverTick every
    // tick) with Construction as its active skill, then rolls FixBrokenDownBuildingSuccessChance once
    // (JobDriver_FixBrokenDownBuilding.cs:31-52). The work is the countdown: a grant takes ticks off it, a miss puts some
    // back (whole ticks, the fraction carried in FpEntry.carry). Never held: DriverTickInterval doesn't run a Delay toil, so
    // as for Repair a check due near the end can be voided by the wait running out. No lever: the success roll is the
    // game's. The tally is the machine's.
    public sealed class FpFixBrokenDownAdapter : FpWorkAdapterBase
    {
        public override string Name
        {
            get { return "FixBrokenDown"; }
        }

        public override bool CanHold
        {
            get { return false; }
        }

        // The driver's wait toil: the one Delay toil with an active skill.
        private static Toil WaitToil(JobDriver d)
        {
            Toil t = FpAdapters.CurToil(d);
            return t != null && t.activeSkill != null && t.defaultCompleteMode == ToilCompleteMode.Delay && t.defaultDuration > 0 ? t : null;
        }

        public override bool Matches(JobDriver d)
        {
            return d is JobDriver_FixBrokenDownBuilding;
        }

        internal override bool MatchesJob(Job job)
        {
            return DriverIs<JobDriver_FixBrokenDownBuilding>(job);
        }

        public override bool InWorkToil(JobDriver d)
        {
            return WaitToil(d) != null && d.job?.targetA.Thing is Building b && b.Spawned;
        }

        // 0 outside the wait toil (FpWork then sees no skilled work).
        public override float Total(JobDriver d)
        {
            Toil t = WaitToil(d);
            return t == null ? 0f : t.defaultDuration;
        }

        public override float Done(JobDriver d)
        {
            float total = Total(d);
            return Clamp(total - d.ticksLeftThisToil, 0f, total);
        }

        // ticksLeftThisToil -= round(units + carry), kept in [max(1, 1% of the total), total] (at 0 the wait ends on its own
        // next tick) and never against the grant.
        public override void Add(JobDriver d, float units)
        {
            FpEntry e = FpRegistry.EntryOf(d.pawn);
            int total = (int)Total(d);
            int n = RoundCarry(e, units, out float exact);
            int left = d.ticksLeftThisToil;
            int floor = Math.Max(1, (total + 99) / 100);
            int lo = left < floor ? left : floor;
            int hi = left > total ? left : total;
            int after = left - n;
            bool clamped = after < lo || after > hi;
            after = after < lo ? lo : after > hi ? hi : after;
            if (e != null)
            {
                e.carry = clamped ? 0f : exact - n;
            }
            d.ticksLeftThisToil = after;
        }

        public override bool NearCompletion(JobDriver d)
        {
            return false;
        }

        public override float UnitsPerTick(JobDriver d)
        {
            return 1f;
        }

        public override int TallyKey(JobDriver d)
        {
            return KeyOfTarget(d.job);
        }

        internal override int KeyForJob(Job job)
        {
            return KeyOfTarget(job);
        }

        public override Thing TallyTarget(JobDriver d)
        {
            return d.job?.targetA.Thing;
        }
    }
}
