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
    // AffectFloor, SmoothWall and RemoveBuilding in M4.
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

        // A job-keyed tally's key (spec 10.6): never a thing id (those are 0 or more).
        internal static int JobKey(Job job)
        {
            return -(job.loadID + 1);
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
            Add(() => new FpBillAdapter());
            ModLog.Note("[IsoRim] First person: skill-check adapters: " + string.Join(", ", all.ConvertAll(a => a.Name)));
        }

        private static void Add(Func<FpWorkAdapterBase> make)
        {
            try
            {
                all.Add(make());
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim] First person: a skill-check adapter failed its startup lookups and is off: " + e);
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

        // Thing.MaxTickIntervalRate (Thing.cs:606), which Pawn doesn't override.
        private const int MaxIntervalTicks = 15;

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
}
