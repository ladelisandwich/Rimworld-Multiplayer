using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.AI;

namespace IsoRim.Fp
{
    // Skill checks in the simulation (fp-play-spec 10.1-10.4, 10.6): while a controlled colonist with checks on is in the
    // skilled toil of a job an adapter knows (FpAdapters), FpWork plans a few checks for the job, raises one at a time as
    // the work reaches its due point, holds the work just short of completion while a check waits, voids a check that got
    // no answer in time, and applies an answer (FpCheckResult) as a share of the work done since the previous one. The
    // results are kept per worked piece in tallies, whose form score drives the outcome levers (FpLevers).
    //
    // All of it runs in the pawn's map tick or in a command, on every player's machine, and reads only synced state: the
    // entry's fields (saved, FpEntry), the job driver, the map's synced game speed through a constant table, ticks, defs.
    // No Rand, no settings, no Input or Time. The ring that asks the player (FpSkillCheck) is local and only sends the
    // outcome.

    // One kind of skilled work (spec 10.1). Implementations live in FpAdapters.cs.
    public interface IFpWorkAdapter
    {
        string Name { get; }

        bool Matches(JobDriver d);           // type test only

        bool InWorkToil(JobDriver d);        // the skilled toil is the current toil

        float Total(JobDriver d);            // work units

        float Done(JobDriver d);             // 0..Total

        void Add(JobDriver d, float units);  // +/- units; result clamped to [0, Total * 0.99]; never completes the work

        bool CanHold { get; }

        bool NearCompletion(JobDriver d);    // the hold point (spec 10.4)

        float UnitsPerTick(JobDriver d);     // estimate from the stats the driver itself reads

        int TallyKey(JobDriver d);           // target thingIDNumber, or -(job.loadID + 1)

        Thing TallyTarget(JobDriver d);      // null for job-keyed
    }

    public static class FpWork
    {
        // The key of no job: no tally has it (thing ids are 0 or more, a job's key is -(loadID + 1)).
        internal const int NoKey = int.MinValue;

        // Driver types with a skilled toil that no adapter knows, logged once each in dev mode (logging only).
        private static readonly HashSet<Type> unknownLogged = new HashSet<Type>();

        // ---------------------------------------------------------------- queries

        // Null if none, or the skill is Shooting/Melee/Social/Animals/null.
        public static IFpWorkAdapter AdapterFor(JobDriver d)
        {
            FpWorkAdapterBase a = FpAdapters.ByDriver(d);
            return a != null && CheckedSkill(SkillOf(d)) ? a : null;
        }

        // d.ActiveSkill
        public static SkillDef SkillOf(JobDriver d)
        {
            return d?.ActiveSkill;
        }

        // Skills whose work gets checks: none for fighting, talking and animals (spec 10.1).
        private static bool CheckedSkill(SkillDef s)
        {
            return s != null && s != SkillDefOf.Shooting && s != SkillDefOf.Melee && s != SkillDefOf.Social && s != SkillDefOf.Animals;
        }

        // UI: tracked progress 0..1, -1 if not tracking.
        public static float Progress01(Pawn p)
        {
            FpEntry e = FpRegistry.EntryOf(p);
            if (e == null || e.checkJob == -1 || p.CurJob == null || p.CurJob.loadID != e.checkJob
                || !InSkilledWork(e, p, out JobDriver d, out IFpWorkAdapter a))
            {
                return -1f;
            }
            float total = a.Total(d);
            float f = a.Done(d) / total;
            return f < 0f ? 0f : f > 1f ? 1f : f;
        }

        // Spec 10.6: the tally a pawn's job counts in. The running job asks its adapter (a bill's unfinished thing, a
        // frame, a rock, a plant...); a job without an adapter, or a job-keyed one, is -(job.loadID + 1). Simulation and UI.
        public static int TallyKeyOf(Pawn p, Job job)
        {
            if (job == null)
            {
                return NoKey;
            }
            JobDriver d = p?.jobs?.curDriver;
            if (d != null && d.job == job)
            {
                FpWorkAdapterBase a = FpAdapters.ByDriver(d);
                return a != null ? a.TallyKey(d) : FpWorkAdapterBase.JobKey(job);
            }
            return FpAdapters.KeyForJob(job);
        }

        // The colonist works in skilled work now, as spec 10.1 defines it: checks on for this entry, a driver an adapter
        // knows, its skilled toil, a skill that gets checks, and some work to do.
        private static bool InSkilledWork(FpEntry e, Pawn p, out JobDriver d, out IFpWorkAdapter a)
        {
            d = null;
            a = null;
            if ((e.flags & FpFlags.Checks) == 0 || p?.jobs == null || p.CurJob == null)
            {
                return false;
            }
            d = p.jobs.curDriver;
            if (d == null || d.job != p.CurJob)
            {
                return false;
            }
            a = AdapterFor(d);
            if (a == null || !a.InWorkToil(d))
            {
                a = null;
                return false;
            }
            return a.Total(d) > 0f;
        }

        // ---------------------------------------------------------------- the result command

        // UI sender: in a session it only sends from the interface (from a tick or a command it would run on this machine
        // alone). True when the command went out (or ran, outside a session); false when Multiplayer can't take it now
        // (during a long event), so the caller keeps the result and sends it on a later frame, as FpSync's senders do.
        public static bool SendResult(Pawn p, int jobLoadId, int seq, byte outcome)
        {
            if (p == null || MpBridge.InSession && !MP.InInterface || outcome > FpOutcome.Great)
            {
                return false;
            }
            FpCheckResult(p, jobLoadId, seq, outcome);
            return true;
        }

        // The answer to a pending check (spec 10.4), from the pawn's controller only, for the check now pending on the job
        // now tracked. Anything else (a late answer, a check already voided, a replayed or forged command) counts as stale
        // and changes nothing. The outcome is all the command carries: the effect is computed here from synced state.
        [SyncMethod(cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpCheckResult(Pawn p, int jobLoadId, int seq, byte outcome)
        {
            int me = FpIds.Executing;
            FpEntry e = FpRegistry.EntryOf(p);
            string stale = StaleReason(p, e, me, jobLoadId, seq, outcome, out JobDriver d, out IFpWorkAdapter a);
            if (FpTestKit.Active)
            {
                FpTestKit.OnCommand(me, "FpCheckResult p=" + p.thingIDNumber + " job=" + jobLoadId + " seq=" + seq + " outcome=" + outcome
                    + " by=" + me + " " + (stale == null ? "accepted" : "stale (" + stale + ")"));
            }
            if (stale != null)
            {
                FpHits.Hit("Check stale");
                return;
            }
            Apply(e.comp, e, d, a, outcome, Find.TickManager.TicksGame);
            FpHits.Hit("Check accepted");
        }

        // Null when the result is valid, else why not (logging only).
        private static string StaleReason(Pawn p, FpEntry e, int me, int jobLoadId, int seq, byte outcome, out JobDriver d, out IFpWorkAdapter a)
        {
            d = null;
            a = null;
            if (me == FpIds.None || e == null || e.controller != me)
            {
                return "not the controller";
            }
            if (e.comp == null || e.comp.map != p.Map || !p.Spawned)
            {
                return "not on the entry's map";
            }
            if (p.CurJob == null || p.CurJob.loadID != jobLoadId || jobLoadId != e.checkJob)
            {
                return "another job";
            }
            if (seq == 0 || seq != e.pendingSeq)
            {
                return "no such check pending";
            }
            if (outcome > FpOutcome.Great)
            {
                return "bad outcome";
            }
            if (!InSkilledWork(e, p, out d, out a))
            {
                return "not in the skilled work";
            }
            return null;
        }

        // The effect of an outcome (spec 10.4): the work since the last result (or since tracking began) gets a share added
        // (Great, Hit) or taken (Miss, no answer); the tally of the piece counts it; the check is done.
        private static void Apply(FpMapComp comp, FpEntry e, JobDriver d, IFpWorkAdapter a, byte outcome, int now)
        {
            FpTuningDef T = FpTuning.Get;
            Pawn p = e.pawn;
            float total = a.Total(d);
            float before = a.Done(d);
            float segStart = e.segStart;
            float seg = Math.Max(0f, before - segStart);
            float grant = outcome == FpOutcome.Great ? T.grantGreat : outcome == FpOutcome.Hit ? T.grantHit : T.grantMiss;
            a.Add(d, seg * grant);
            float after = a.Done(d);
            e.segStart = after;
            FpTally t = comp.GetOrAddTally(e.tallyKey, a.TallyTarget(d), p, d.job.loadID);
            if (outcome == FpOutcome.Great)
            {
                t.great++;
            }
            else if (outcome == FpOutcome.Hit)
            {
                t.hit++;
            }
            else
            {
                t.miss++;
            }
            t.lastTick = now;
            float xp = T.xp != null && outcome < T.xp.Count ? T.xp[outcome] : 0f;
            if (xp > 0f && p.skills != null)
            {
                SkillDef skill = SkillOf(d);
                if (skill != null)
                {
                    p.skills.Learn(skill, xp);
                }
            }
            e.pendingSeq = 0;
            e.issued++;
            e.lastResolveTick = now;
            e.lastOutcome = outcome;
            e.lastOutcomeTick = now;
            FpRegistry.SetHeld(e, false);
            Diag.Applied(p, outcome, before, after, segStart, total, grant, now);
        }

        // ---------------------------------------------------------------- the map tick

        // Called by B for each controlled entry, every tick (spec 10.2), in the map's context: the map's own synced speed is
        // in Find.TickManager.CurTimeSpeed there (Multiplayer's TimeSnapshot), and the factor is a constant per speed.
        internal static void Tick(FpMapComp comp, FpEntry e)
        {
            Pawn p = e.pawn;
            int now = Find.TickManager.TicksGame;
            if (!InSkilledWork(e, p, out JobDriver d, out IFpWorkAdapter a))
            {
                if (e.checkJob != -1 && p.CurJob?.loadID != e.checkJob)
                {
                    LeftJob(comp, e, p, now);   // the job ended or changed
                    e.checkJob = -1;
                }
                else if (e.pendingSeq != 0)
                {
                    VoidPending(e, now);   // left the skilled toil (to the finishing toil, say) with a check open
                }
                FpRegistry.SetHeld(e, false);
                NoteUnknownDriver(e, p);
                return;
            }
            FpTuningDef T = FpTuning.Get;
            int f = FpMath.SpeedFactor((int)Find.TickManager.CurTimeSpeed);
            Job job = p.CurJob;
            int key = a.TallyKey(d);
            if (job.loadID != e.checkJob || key != e.tallyKey)
            {
                // Tracking starts: a new job, or the job's next piece (a harvest or cutting job works the plants of its
                // queue in turn, each its own piece). The checks are planned now, over the work still to do at this speed.
                if (e.checkJob != -1 && job.loadID != e.checkJob)
                {
                    LeftJob(comp, e, p, now);   // straight from another tracked job, with no tick between
                }
                VoidPending(e, now);   // the previous piece of this job: its toil was left, so nothing can be open
                e.checkJob = job.loadID;
                e.tallyKey = key;
                e.carry = 0f;
                (a as FpWorkAdapterBase)?.BeginTracking(d, e);   // Repair counts from the hit points now
                float total0 = a.Total(d);
                float done0 = a.Done(d);
                int p0 = (int)(1000f * done0 / total0);
                e.p0 = p0;
                e.segStart = done0;
                float rem = (total0 - done0) / Math.Max(a.UnitsPerTick(d), 0.01f);
                e.planned = FpMath.PlanChecks(rem, f, p0, T.checkSpacingTicks, T.minRemainingTicks, T.endDuePermille, T.maxChecks);
                e.issued = 0;
                if (e.planned > 0)
                {
                    // The piece's tally learns how many checks it gets (integration change, spec 10.4): its form is averaged
                    // over at least the answers so far plus this plan, so a check left unanswered (F5, checks turned off,
                    // a void, the AI finishing the piece) counts as a zero rather than leaving a good start locked in.
                    FpTally t = comp.GetOrAddTally(key, a.TallyTarget(d), p, job.loadID);
                    t.expected = Math.Max(t.expected, t.Count + e.planned);
                }
                Diag.Planned(p, job.loadID, rem, f, p0, e.planned, a.Name);
            }
            float total = a.Total(d);
            float done = a.Done(d);
            int P = (int)(1000f * done / total);
            if (e.pendingSeq == 0 && e.issued < e.planned)
            {
                if (P >= T.holdPermille)
                {
                    e.issued = e.planned;   // too late for the rest: no more checks this job
                }
                else
                {
                    int due = FpMath.DuePermille(e.issued, e.planned, e.p0, T.endDuePermille, Gen.HashCombineInt(job.loadID, e.issued));
                    if (P >= due && now - e.lastResolveTick >= T.minGapTicks * f)
                    {
                        e.pendingSeq = ++e.seq;
                        e.pendingTick = now;
                        e.deadlineTick = now + T.deadlineTicks * f;
                        // A worked piece's tally exists from its first check on, so a check abandoned with the job
                        // (LeftJob) can be counted without the driver that knew the piece.
                        Thing target = a.TallyTarget(d);
                        if (target != null)
                        {
                            comp.GetOrAddTally(e.tallyKey, target, p, job.loadID);
                        }
                        Diag.Issued(p, job.loadID, e.pendingSeq, P, due);
                    }
                }
            }
            if (e.pendingSeq != 0 && now >= e.deadlineTick)
            {
                // No answer in time: no effect, no tally change. The segment closes with it, so a later answer's share
                // covers only the work after this check: skipping checks can't stake one answer on all the work so far.
                e.segStart = done;
                VoidPending(e, now);
            }
            FpRegistry.SetHeld(e, e.pendingSeq != 0 && a.CanHold && a.NearCompletion(d));
        }

        // Spec 10.2: the open check is dropped without any effect and counts as one of the job's checks. Nothing when no
        // check is open (the held flag is cleared either way).
        private static void VoidPending(FpEntry e, int now)
        {
            if (e.pendingSeq != 0)
            {
                e.pendingSeq = 0;
                e.issued++;
                e.lastResolveTick = now;
            }
            FpRegistry.SetHeld(e, false);
        }

        // The controller walks away from (FpSetIntent), replaces (FpStartRecipe, FpWorkBill) or turns checks off during (the
        // flags of FpSetControl) a check the ring has shown: it counts as an unanswered one, work and tally (spec 10.4:
        // leaving doesn't escape a bad check). Called in the command, before the job ends, while the tracked driver is
        // current; nothing when no check is open. A check raised too recently for the ring to have shown it is voided.
        internal static void ForfeitPending(FpEntry e)
        {
            if (e == null || e.pendingSeq == 0)
            {
                return;
            }
            Pawn p = e.pawn;
            int now = Find.TickManager.TicksGame;
            if (e.comp != null && p != null && p.Spawned && p.Map == e.comp.map && p.CurJob != null && p.CurJob.loadID == e.checkJob
                && Shown(e, now) && InSkilledWork(e, p, out JobDriver d, out IFpWorkAdapter a))
            {
                Apply(e.comp, e, d, a, FpOutcome.MissTimeout, now);
                FpHits.Hit("Check forfeited");
            }
            else if (e.comp != null && p != null && e.checkJob != -1 && p.CurJob?.loadID != e.checkJob)
            {
                LeftJob(e.comp, e, p, now);   // the tracked job already went this tick (an order): as the map tick would count it
            }
            else
            {
                VoidPending(e, now);
            }
        }

        // The ring has had time to show the open check: its warning (tuning ringWarn) has run in game time. Before that
        // the player can't have seen it, so giving up the work then voids it instead of counting a miss.
        private static bool Shown(FpEntry e, int now)
        {
            int f = FpMath.SpeedFactor((int)Find.TickManager.CurTimeSpeed);
            return now - e.pendingTick >= (int)(FpTuning.Get.ringWarn * 60f) * f;
        }

        // The tracked job is gone (map tick). A check still open counts as a miss on the piece's tally when the controller
        // took the colonist off the work for something of their own: drafted, or given another order (a float menu
        // option, a queued order run early). The work itself went with the job, so only the tally changes. A job that
        // ended on its own (its bench gone, the ingredients gone, a fail condition) voids it, as does a check the ring
        // can't have shown yet. Walking away and the bill commands count the miss themselves first (ForfeitPending).
        private static void LeftJob(FpMapComp comp, FpEntry e, Pawn p, int now)
        {
            if (e.pendingSeq != 0 && Shown(e, now) && (p.Drafted || p.CurJob != null && p.CurJob.playerForced && !FpControlJob.InControlJob(p)))
            {
                FpTally t = comp.TallyByKey(e.tallyKey);
                if (t != null)
                {
                    t.miss++;
                    t.lastTick = now;
                }
                e.pendingSeq = 0;
                e.issued++;
                e.lastResolveTick = now;
                e.lastOutcome = FpOutcome.MissTimeout;
                e.lastOutcomeTick = now;
                FpHits.Hit("Check abandoned");
                if (FpTestKit.Active)
                {
                    FpTestKit.Info("check abandoned p=" + p.thingIDNumber + " job=" + e.checkJob + " -> " + (p.CurJob?.def.defName ?? "none")
                        + (p.Drafted ? " (drafted)" : "") + ", tally " + (t == null ? "none (job-keyed)" : t.great + "/" + t.hit + "/" + t.miss));
                }
            }
            VoidPending(e, now);
        }

        // Spec 5.6 step 1, from FpMapComp.Release (map context): a check still open when its controller lets go counts as a
        // miss while the pawn is still on the tracked job, so leaving doesn't escape a bad check; a forced or automatic
        // release (the pawn downed, the player gone), or a check the ring can't have shown yet, voids it.
        internal static void OnRelease(FpMapComp comp, FpEntry e, bool countAsMiss)
        {
            if (e == null)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            Pawn p = e.pawn;
            if (e.pendingSeq != 0 && countAsMiss && comp != null && p != null && p.Spawned && p.Map == comp.map
                && p.CurJob != null && p.CurJob.loadID == e.checkJob && Shown(e, now) && InSkilledWork(e, p, out JobDriver d, out IFpWorkAdapter a))
            {
                Apply(comp, e, d, a, FpOutcome.Miss, now);
            }
            else
            {
                VoidPending(e, now);
            }
            FpRegistry.SetHeld(e, false);
        }

        // Every 250 ticks (spec 10.6), in list order: a piece's tally goes once the piece is gone (used up when its product
        // was made, in the same tick the levers read the tally) and has had no result for 250 ticks; a job's tally goes once
        // its owner is on another job.
        internal static void PruneTallies(FpMapComp comp)
        {
            List<FpTally> list = comp.tallies;
            if (list.Count == 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            foreach (FpTally t in list.ToArray())
            {
                bool gone = t.target != null
                    ? t.target.Destroyed && now - t.lastTick > 250
                    : t.owner?.CurJob == null || t.owner.CurJob.loadID != t.jobLoadId;
                if (gone)
                {
                    comp.RemoveTally(t);
                }
            }
        }

        // Dev mode, once per driver type: a controlled colonist with checks on works a skilled toil no adapter knows
        // (spec 10.1: unknown drivers get no checks). Logging only.
        private static void NoteUnknownDriver(FpEntry e, Pawn p)
        {
            if (!Prefs.DevMode || (e.flags & FpFlags.Checks) == 0)
            {
                return;
            }
            JobDriver d = p?.jobs?.curDriver;
            if (d == null || !CheckedSkill(SkillOf(d)) || FpAdapters.ByDriver(d) != null || !unknownLogged.Add(d.GetType()))
            {
                return;
            }
            Log.Message("[IsoRim] First person: no skill checks for " + d.GetType().Name + " (" + SkillOf(d).defName + " work): no adapter knows it.");
        }

        // ---------------------------------------------------------------- the hold

        // The hold prefix (spec 10.3). DriverTickInterval has a try block, so the JIT never inlines it.
        internal static void Install(Harmony h)
        {
            FpAdapters.Init();
            FpHooks.Patch(h, "skill checks: hold the work near completion while a check waits",
                AccessTools.DeclaredMethod(typeof(JobDriver), nameof(JobDriver.DriverTickInterval), new[] { typeof(int) }),
                prefix: new HarmonyMethod(typeof(FpWork), nameof(DriverTickInterval_Prefix)));
        }

        // While a check waits near the end of the work, the driver's interval work (every skilled toil's progress) doesn't
        // run; its fixed tick (fail conditions, Delay toils) still does. Only the held entry's tracked job: a job that
        // changed since the map tick set the flag runs normally.
        private static bool DriverTickInterval_Prefix(JobDriver __instance)
        {
            if (FpRegistry.HeldCount == 0)
            {
                return true;
            }
            FpEntry e = FpRegistry.EntryOf(__instance.pawn);
            if (e == null || !e.held || __instance.job == null || __instance.job.loadID != e.checkJob)
            {
                return true;
            }
            FpHits.Hit("Hold skipped interval");
            return false;
        }

        // ---------------------------------------------------------------- self-test diagnostics

        // What the simulation last planned, raised and applied, for the self-test (FpTestM3) and its FPTEST INFO lines.
        // Written by simulation code, never read by it.
        internal static class Diag
        {
            public static int PlanPawn = -1, PlanJob = -1, PlanF, PlanP0, PlanPlanned;
            public static float PlanRemTicks;
            public static int IssuePawn = -1, IssueJob = -1, IssueSeq, IssueP, IssueDue;
            public static int AppliedPawn = -1, AppliedOutcome = -1, AppliedTick;
            public static float AppliedBefore, AppliedAfter, AppliedSegStart, AppliedTotal, AppliedGrant;

            public static void Planned(Pawn p, int job, float rem, int f, int p0, int planned, string adapter)
            {
                PlanPawn = p.thingIDNumber;
                PlanJob = job;
                PlanRemTicks = rem;
                PlanF = f;
                PlanP0 = p0;
                PlanPlanned = planned;
                if (FpTestKit.Active)
                {
                    FpTestKit.Info("plan p=" + p.thingIDNumber + " job=" + job + " adapter=" + adapter + " rem=" + rem.ToString("0.0", CultureInfo.InvariantCulture)
                        + " f=" + f + " p0=" + p0 + " planned=" + planned);
                }
            }

            public static void Issued(Pawn p, int job, int seq, int permille, int due)
            {
                IssuePawn = p.thingIDNumber;
                IssueJob = job;
                IssueSeq = seq;
                IssueP = permille;
                IssueDue = due;
                if (FpTestKit.Active)
                {
                    FpTestKit.Info("check p=" + p.thingIDNumber + " job=" + job + " seq=" + seq + " at=" + permille + " due=" + due);
                }
            }

            public static void Applied(Pawn p, byte outcome, float before, float after, float segStart, float total, float grant, int tick)
            {
                AppliedPawn = p.thingIDNumber;
                AppliedOutcome = outcome;
                AppliedBefore = before;
                AppliedAfter = after;
                AppliedSegStart = segStart;
                AppliedTotal = total;
                AppliedGrant = grant;
                AppliedTick = tick;
                if (FpTestKit.Active)
                {
                    CultureInfo inv = CultureInfo.InvariantCulture;
                    FpTestKit.Info("result p=" + p.thingIDNumber + " outcome=" + outcome + " done " + before.ToString("0.00", inv) + " -> "
                        + after.ToString("0.00", inv) + " of " + total.ToString("0.00", inv) + " (segment from " + segStart.ToString("0.00", inv)
                        + ", grant " + grant.ToString("0.00", inv) + ")");
                }
            }
        }
    }
}
