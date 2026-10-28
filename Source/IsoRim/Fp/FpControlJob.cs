using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace IsoRim.Fp
{
    // The simulation side of a controlled pawn (fp-play-spec 5.2-5.5, 5.7): one long control job that walks the pawn by
    // the synced movement intent, the think-tree node that hands it out, the cell-step rule, the CheckForJobOverride
    // prefix and the Game constructor postfix. All of it runs in the pawn's map tick on every player's machine and reads
    // only synced state.
    //
    // Why one long job and not a Goto per cell: every job end runs the think tree, ten jobs in ten ticks trip the game's
    // error recovery, and Multiplayer would send a whole exposed Job per step. The job is player-forced, so damage,
    // opportunistic hauls and the main think tree's overrides leave it alone; mental states, being downed and burning
    // still win (they sit above the control node in the tree).

    [DefOf]
    public static class FpJobDefOf
    {
        public static JobDef IsoRimFp_Control;   // Source/IsoRim/Defs/IsoRimFp_Control.xml

        static FpJobDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(FpJobDefOf));
        }
    }

    public class JobDriver_FpControl : JobDriver
    {
        // Movement state (spec 5.5), saved with the job so a reload continues the same walk on every machine.
        private IntVec3 lastBase = IntVec3.Invalid;   // the cell the last step was planned from
        private byte lastDir = FpDir.Stop;            // the sector that step was planned for
        private int stepCount;                        // steps planned so far: odd sectors alternate by its parity

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        // One toil that never completes by itself. Its tickAction runs every tick, whatever the variable tick rate (the
        // interval actions only run every UpdateRateTicks). The toil handles facing, so the rotation tracker leaves the
        // pawn alone (no drafted south turn, no turn to a job target); the walk sets the facing itself.
        protected override IEnumerable<Toil> MakeNewToils()
        {
            Toil control = ToilMaker.MakeToil("FpControl");
            control.defaultCompleteMode = ToilCompleteMode.Never;
            control.handlingFacing = true;
            control.tickAction = ControlTick;
            yield return control;
        }

        // The pather arriving or failing is part of walking cell by cell, never the end of the job.
        public override void Notify_PatherArrived()
        {
        }

        public override void Notify_PatherFailed()
        {
        }

        public override bool IsContinuation(Job j)
        {
            return j.def == FpControlJob.Def;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref lastBase, "lastBase", IntVec3.Invalid);
            Scribe_Values.Look(ref lastDir, "lastDir", FpDir.Stop);
            Scribe_Values.Look(ref stepCount, "stepCount", 0);
        }

        // Spec 5.4, every tick.
        private void ControlTick()
        {
            FpHits.Hit("Control job tick");
            FpEntry e = FpRegistry.EntryOf(pawn);
            if (e == null || e.controller == FpIds.None || e.comp == null || e.comp.map != pawn.Map)
            {
                EndJobWith(JobCondition.Succeeded);
                return;
            }
            if (pawn.jobs.jobQueue.Count > 0)
            {
                // An order queued behind control (Shift, the game's QueueOrder key, with the left button or a menu option,
                // from this player or another): the queue goes first, as TryTakeOrderedJob does for an unqueued order.
                // JobGiver_FpControl gives way while the queue holds anything and gives control back once it is empty.
                EndJobWith(JobCondition.InterruptForced);
                return;
            }
            if (pawn.IsHashIntervalTick(60) && (pawn.GetLord() != null || FpControlJob.AutopilotDue(e, pawn)))
            {
                EndJobWith(JobCondition.Succeeded);   // the think tree gives the lord's or the needs' job; control resumes after
                return;
            }
            if (pawn.Downed || pawn.InMentalState)
            {
                return;   // the map's tick releases the pawn at the end of this tick; pathing now would log an error
            }
            Move(e.dir);
            FaceWalk();
        }

        // Spec 5.5. The pather walks one cell at a time towards a destination one cell ahead; the next cell is requested
        // once the pawn is at least half-way into the current one (StartPath resets a step that has only just begun, so
        // re-requesting on entering a cell would cost a tick per cell and stutter). A new direction is requested at once.
        private void Move(byte d)
        {
            Pawn_PathFollower pather = pawn.pather;
            if (d == FpDir.Stop)
            {
                if (pather.Moving && pather.Destination.Cell != pather.nextCell)
                {
                    pather.StartPath(pather.nextCell, PathEndMode.OnCell);   // finish the step in progress
                }
                lastBase = IntVec3.Invalid;
                lastDir = FpDir.Stop;
                return;
            }
            if (!pather.Moving)
            {
                Issue(pawn.Position, d);
                return;
            }
            bool nearlyIn = pather.nextCellCostLeft <= 0.5f * pather.nextCellCostTotal;
            if (d != lastDir)
            {
                Issue(pather.nextCell, d);
            }
            else if (pather.Destination.Cell == pather.nextCell && nearlyIn && lastBase != pather.nextCell)
            {
                Issue(pather.nextCell, d);   // keep one cell of headroom
            }
        }

        private void Issue(IntVec3 from, byte d)
        {
            Pawn_PathFollower pather = pawn.pather;
            lastBase = from;
            lastDir = d;
            IntVec3 want = FpStep.Choose(pawn, from, d, stepCount);
            if (want.IsValid)
            {
                stepCount++;
                pather.StartPath(want, PathEndMode.OnCell);
            }
            else if (pather.Moving && pather.Destination.Cell != pather.nextCell)
            {
                pather.StartPath(pather.nextCell, PathEndMode.OnCell);   // blocked: stop after the current step
            }
        }

        // While walking, face the cell being entered, the way the rotation tracker would (east or west for a diagonal).
        // Through FaceCell, so IsoRim's drawn-facing memory (MpFacing, which hooks it) sees the turn. Written only on a
        // change.
        private void FaceWalk()
        {
            Pawn_PathFollower pather = pawn.pather;
            if (!pather.Moving)
            {
                return;
            }
            IntVec3 next = pather.nextCell;
            IntVec3 at = pawn.Position;
            if (!next.IsValid || next == at)
            {
                return;
            }
            int dx = next.x - at.x;
            int dz = next.z - at.z;
            Rot4 want = dx > 0 ? Rot4.East : dx < 0 ? Rot4.West : dz > 0 ? Rot4.North : Rot4.South;
            if (pawn.Rotation != want)
            {
                pawn.rotationTracker.FaceCell(next);
            }
        }
    }

    // Spec 5.3: in the humanlike think tree right after the mental states (IsoRimFp_ControlInsert), before queued jobs,
    // drafted orders, lords, needs and work. Queued orders (an LMB order) therefore run first and control resumes when
    // they end.
    //
    // With the AutoNeeds flag (M2) this node also hands out the needs autopilot's jobs: a colonist who must eat, sleep or
    // go to bed for a doctor gets the game's own job for it from here, and the control job comes back when that job
    // ends. The node gives them itself rather than leaving the pawn to the rest of the tree, because the rest of the tree
    // would put a colonist whose need can't be met (no food, no bed, no doctor) on its work and joy jobs, out of the
    // player's hands for as long as the need lasts, and a drafted one on the drafted wait.
    public class JobGiver_FpControl : ThinkNode_JobGiver
    {
        // Each job is tagged, so the pawn's last job tag (saved, the same on every machine) tells whether the needs
        // autopilot has it (FpControlJob.NeedsAutopilot): the autopilot's jobs carry the game's needs tags, the control job
        // carries Misc, as an ordered job does.
        public override ThinkResult TryIssueJobPackage(Pawn pawn, JobIssueParams jobParams)
        {
            Job job = Choose(pawn, out JobTag? tag);
            return job == null ? ThinkResult.NoJob : new ThinkResult(job, this, tag);
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            return Choose(pawn, out _);
        }

        private static Job Choose(Pawn pawn, out JobTag? tag)
        {
            tag = null;
            if (FpRegistry.ControlledCount == 0)
            {
                return null;
            }
            JobDef def = FpControlJob.Def;
            FpEntry e = FpRegistry.EntryOf(pawn);
            if (def == null || e == null || e.controller == FpIds.None || e.comp == null || e.comp.map != pawn.Map)
            {
                return null;
            }
            if (pawn.jobs.jobQueue.Count > 0 || pawn.GetLord() != null)
            {
                return null;
            }
            if (FpControlJob.AutoNeedsOn(e, pawn))
            {
                Job cur = pawn.CurJob;
                if (cur != null && FpControlJob.KeepsNeedsJob(pawn, cur))
                {
                    // An override check during the autopilot's meal or rest: the same job object means carry on, with no
                    // restart (the game's JobGiver_KeepLyingDown does the same).
                    FpHits.Hit("AutoNeeds job kept");
                    return cur;
                }
                Job needs = FpControlJob.NeedsJob(pawn, out JobTag needsTag);
                if (needs != null)
                {
                    FpHits.Hit("AutoNeeds job given");
                    tag = needsTag;
                    return needs;
                }
            }
            Job job = JobMaker.MakeJob(def);
            job.playerForced = true;
            job.expiryInterval = -1;
            job.locomotionUrgency = LocomotionUrgency.Jog;   // the game's normal walking speed, never faster
            tag = JobTag.Misc;
            return job;
        }
    }

    // The next cell of a controlled pawn's walk (spec 5.5).
    public static class FpStep
    {
        // IntVec3.Invalid when blocked. The sector's step first; a blocked diagonal slides along x, then z; an odd sector
        // then tries its other neighbour. The order is fixed, so every machine picks the same cell.
        public static IntVec3 Choose(Pawn pawn, IntVec3 from, byte d, int n)
        {
            if (d > 15 || pawn?.Map == null)
            {
                return IntVec3.Invalid;
            }
            FpMath.StepOf(d, n, out int dx, out int dz);
            IntVec3 c = new IntVec3(from.x + dx, 0, from.z + dz);
            if (Passable(pawn, from, c))
            {
                return c;
            }
            if (dx != 0 && dz != 0)
            {
                c = new IntVec3(from.x + dx, 0, from.z);
                if (Passable(pawn, from, c))
                {
                    return c;
                }
                c = new IntVec3(from.x, 0, from.z + dz);
                if (Passable(pawn, from, c))
                {
                    return c;
                }
            }
            if ((d & 1) == 1)
            {
                FpMath.StepOf(d, n + 1, out int ox, out int oz);
                c = new IntVec3(from.x + ox, 0, from.z + oz);
                if (Passable(pawn, from, c))
                {
                    return c;
                }
            }
            return IntVec3.Invalid;
        }

        // In bounds, walkable for this pawn, not fogged, no corner cut past an unwalkable cell (the path finder's own
        // diagonal rule, PathUtility.BlocksDiagonalMovement, is the same test), and no door the pawn can't open.
        private static bool Passable(Pawn pawn, IntVec3 from, IntVec3 to)
        {
            Map map = pawn.Map;
            if (!to.InBounds(map) || !to.WalkableBy(map, pawn) || to.Fogged(map))
            {
                return false;
            }
            int dx = to.x - from.x;
            int dz = to.z - from.z;
            if (dx != 0 && dz != 0
                && (!new IntVec3(from.x + dx, 0, from.z).WalkableBy(map, pawn) || !new IntVec3(from.x, 0, from.z + dz).WalkableBy(map, pawn)))
            {
                return false;
            }
            Building_Door door = to.GetDoor(map);
            return door == null || door.PawnCanOpen(pawn);
        }
    }

    public static class FpControlJob
    {
        // Main think trees with the control node (through the IsoRimFp_ControlInsert tag or directly). Defs only, the
        // same on every machine; lookups only.
        private static readonly Dictionary<ThinkTreeDef, bool> slotByTree = new Dictionary<ThinkTreeDef, bool>();

        // The control job's def; null if the def file didn't load.
        public static JobDef Def
        {
            get { return FpJobDefOf.IsoRimFp_Control; }
        }

        // The pawn is in its control job now (UI and simulation).
        public static bool InControlJob(Pawn p)
        {
            JobDef def = Def;
            return def != null && p?.CurJob != null && p.CurJob.def == def;
        }

        // ---------------------------------------------------------------- AutoNeeds (M2, spec 5.3 and 5.4)
        //
        // The autopilot starts when a need is urgent: urgently hungry or starving, exhausted (the last rest category,
        // under 1%), or HealthAIUtility.ShouldSeekMedicalRestUrgent (a wound or illness that needs tending, an operation
        // due). The control job checks every 60 ticks (AutopilotDue) and ends; JobGiver_FpControl then gives the game's
        // own job for the need (NeedsJob). The pawn is back under control when that job ends; a rest in bed ends once the
        // pawn is up again (KeepsNeedsJob). Everything here reads synced state, so every machine decides the same.

        // The game's needs job givers, outside any tree (as WorkGiver_PatientGoToBedRecuperate keeps its own): their
        // jobs are what the colonist's own AI would take for that need. Made on first use; they hold no state.
        private static JobGiver_GetFood getFood;
        private static JobGiver_GetRest getRest;

        // The controller asked for the autopilot (FpFlags.AutoNeeds, from their settings through FpSetControl). Never
        // while drafted: the game's drafted colonists don't see to their needs either, and the player drafted them to
        // fight (drafting is also how a player calls a colonist back from the autopilot early).
        internal static bool AutoNeedsOn(FpEntry e, Pawn p)
        {
            return e != null && (e.flags & FpFlags.AutoNeeds) != 0 && p?.needs != null && !p.Drafted;
        }

        // The needs autopilot has the pawn now: the HUD says so and a movement key doesn't cancel it (FpState.AutopilotKey,
        // FpSetIntent). True while the pawn does a job the autopilot gave for a need. Synced state only and no side
        // effects, so the UI may call it.
        internal static bool NeedsAutopilot(FpEntry e, Pawn p)
        {
            return AutoNeedsOn(e, p) && OnNeedsJob(p, p.CurJob);
        }

        // A job the AI chose (not player-forced, not the control job) that started with one of the game's needs tags:
        // the autopilot's own jobs (JobGiver_FpControl tags them), or a need the game itself put first.
        private static bool OnNeedsJob(Pawn p, Job j)
        {
            if (j == null || j.playerForced || j.def == Def || p.mindState == null)
            {
                return false;
            }
            JobTag t = p.mindState.lastJobTag;
            return t == JobTag.SatisfyingNeeds || t == JobTag.RestingForMedicalReasons;
        }

        // An override check (the game's CheckForJobOverride: on damage, and every 211 ticks while lying down) found the
        // pawn on the autopilot's job: true to carry on. A meal, the walk to it and whatever else a need started end by
        // themselves. Lying down is the exception: the game keeps a sleeper in bed above the control node
        // (ThinkNode_ConditionalMustKeepLyingDown) and lets them up through an override check once they are awake, so
        // lying down is kept only while its reason lasts (still exhausted, or a doctor can still come or the bleeding is
        // urgent, as the game's Patient work asks) and hunger doesn't come first. Then NeedsJob or control takes over.
        internal static bool KeepsNeedsJob(Pawn p, Job cur)
        {
            if (!OnNeedsJob(p, cur))
            {
                return false;
            }
            if (cur.def != JobDefOf.LayDown)
            {
                return true;
            }
            if (UrgentlyHungry(p))
            {
                return false;
            }
            return Exhausted(p) || MedicalUrgent(p)
                && (HealthAIUtility.ShouldBeTendedNowByPlayerUrgent(p) || WorkGiver_PatientGoToBedTreatment.AnyAvailableDoctorFor(p));
        }

        // Urgently hungry or starving, and not a patient in bed who waits to be fed until starving
        // (JobGiver_GetFood.GetPriority).
        private static bool UrgentlyHungry(Pawn p)
        {
            Need_Food food = p.needs.food;
            return food != null && food.CurCategory >= HungerCategory.UrgentlyHungry
                && (food.CurCategory == HungerCategory.Starving || !FoodUtility.ShouldBeFedBySomeone(p));
        }

        // The last rest category: under 1% (Need_Rest.CurCategory).
        private static bool Exhausted(Pawn p)
        {
            Need_Rest rest = p.needs.rest;
            return rest != null && rest.CurCategory == RestCategory.Exhausted;
        }

        // A wound or illness that needs tending, an operation due (HealthAIUtility.ShouldSeekMedicalRestUrgent), for a
        // colonist who works. Not in labour: the game's work giver (JobGiver_Work) gives nothing then, and the birth
        // ritual's lord takes over.
        private static bool MedicalUrgent(Pawn p)
        {
            return p.workSettings != null && p.workSettings.EverWork && !p.health.hediffSet.InLabor()
                && HealthAIUtility.ShouldSeekMedicalRestUrgent(p);
        }

        // Spec 5.4 step 2, every 60 ticks in the control job: a need is urgent and the game has a job for it, so the control
        // job ends and the think tree hands that job out (JobGiver_FpControl asks the same givers again in the same tick;
        // this check's job goes back to the pool). When no food, bed or doctor can be had the pawn stays under control
        // (the HUD's need warnings say why) instead of ending and restarting the control job every 60 ticks.
        internal static bool AutopilotDue(FpEntry e, Pawn p)
        {
            if (!AutoNeedsOn(e, p) || p.Downed || p.InMentalState)
            {
                return false;
            }
            Job job = NeedsJob(p, out _);
            if (job == null)
            {
                return false;
            }
            JobMaker.ReturnToPool(job);
            return true;
        }

        // The game's job for the pawn's most pressing urgent need, or null when nothing is urgent or the game has no job
        // for it. The givers are asked in the order the humanlike think tree reaches them: emergency treatment (bleeding
        // out), urgent hunger, treatment by a doctor, exhaustion, each under the conditions the tree puts on it. Simulation
        // only (the map tick or the think tree): the givers search the map and make jobs, which take job ids.
        internal static Job NeedsJob(Pawn p, out JobTag tag)
        {
            tag = JobTag.SatisfyingNeeds;
            bool medical = MedicalUrgent(p);
            bool hungry = UrgentlyHungry(p);
            // Only a pawn that could fall asleep goes to bed (JobGiver_GetRest.GetPriority); a starving one can't.
            bool exhausted = Exhausted(p) && Find.TickManager.TicksGame >= p.mindState.canSleepTick && RestUtility.CanFallAsleep(p);
            if (!medical && !hungry && !exhausted)
            {
                return null;
            }
            Job job;
            if (medical && (job = PatientJob(p, p.workSettings.WorkGiversInOrderEmergency)) != null)
            {
                tag = JobTag.RestingForMedicalReasons;
                return job;
            }
            if (hungry)
            {
                if (getFood == null)
                {
                    getFood = new JobGiver_GetFood();
                }
                if ((job = getFood.TryIssueJobPackage(p, default).Job) != null)
                {
                    return job;
                }
            }
            if (medical && (job = PatientJob(p, p.workSettings.WorkGiversInOrderNormal)) != null)
            {
                tag = JobTag.RestingForMedicalReasons;
                return job;
            }
            if (exhausted)
            {
                if (getRest == null)
                {
                    getRest = new JobGiver_GetRest();
                }
                if ((job = getRest.TryIssueJobPackage(p, default).Job) != null)
                {
                    return job;
                }
            }
            return null;
        }

        // The game's Patient work, with JobGiver_Work's own tests for a work giver: go to bed for emergency treatment, or for
        // treatment when a doctor can come (WorkGiver_PatientGoToBedTreatment). Only while the colonist's Patient work is
        // on: the work lists hold the givers of active work types only.
        private static Job PatientJob(Pawn p, List<WorkGiver> givers)
        {
            for (int i = 0; i < givers.Count; i++)
            {
                WorkGiver g = givers[i];
                if (!(g is WorkGiver_PatientGoToBedTreatment) && !(g is WorkGiver_PatientGoToBedEmergencyTreatment))
                {
                    continue;
                }
                if (p.WorkTagIsDisabled(g.def.workTags) || g.def.workType != null && p.WorkTypeIsDisabled(g.def.workType)
                    || g.ShouldSkip(p) || g.MissingRequiredCapacity(p) != null)
                {
                    continue;
                }
                Job job = g.NonScanJob(p);
                if (job != null)
                {
                    return job;
                }
            }
            return null;
        }

        // The pawn's main think tree reaches a JobGiver_FpControl, so the control job can be given at all (spec 5.1). A
        // tag insert (ThinkNode_SubtreesByTag) isn't part of the tree's own nodes: the game looks the inserted trees up by
        // tag when it runs, so this does the same.
        internal static bool HasControlSlot(Pawn p)
        {
            ThinkTreeDef tree = p?.thinker?.MainThinkTree;
            if (tree?.thinkRoot == null)
            {
                return false;
            }
            if (!slotByTree.TryGetValue(tree, out bool has))
            {
                has = Reaches(tree.thinkRoot, 0);
                slotByTree[tree] = has;
            }
            return has;
        }

        private static bool Reaches(ThinkNode root, int depth)
        {
            foreach (ThinkNode node in root.ThisAndChildrenRecursive)
            {
                if (node is JobGiver_FpControl)
                {
                    return true;
                }
                if (depth < 4 && node is ThinkNode_SubtreesByTag byTag && byTag.insertTag != null)
                {
                    foreach (ThinkTreeDef inserted in DefDatabase<ThinkTreeDef>.AllDefsListForReading)
                    {
                        if (inserted.insertTag == byTag.insertTag && inserted.thinkRoot != null && Reaches(inserted.thinkRoot, depth + 1))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // Both targets are large (CheckForJobOverride 189 bytes of IL, the Game constructor 383), so the JIT can't inline
        // them into a caller compiled before the patch. Both are patched through their declaring types.
        internal static void Install(Harmony h)
        {
            FpHooks.Patch(h, "control: no job override while controlled",
                AccessTools.DeclaredMethod(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.CheckForJobOverride)),
                prefix: new HarmonyMethod(typeof(FpControlJob), nameof(CheckForJobOverride_Prefix)));
            FpHooks.Patch(h, "control: index cleared for each game", AccessTools.Constructor(typeof(Game)),
                postfix: new HarmonyMethod(typeof(FpControlJob), nameof(GameCtor_Postfix)));
        }

        // Spec 5.7: a controlled pawn on a player-forced job (the control job, or an order given in first person) keeps
        // it; the game would otherwise swap it for whatever the main think tree prefers (DoRecipeWork asks every 1000
        // ticks). Jobs the AI picked while the pawn is on autopilot aren't player-forced and stay overridable.
        private static bool CheckForJobOverride_Prefix(Pawn ___pawn)
        {
            if (FpRegistry.ControlledCount == 0)
            {
                return true;
            }
            FpEntry e = FpRegistry.EntryOf(___pawn);
            if (e == null || e.controller == FpIds.None || ___pawn.CurJob?.playerForced != true)
            {
                return true;
            }
            FpHits.Hit("CheckForJobOverride skipped");
            return false;
        }

        // A new or loaded game: nothing of the previous game stays indexed. May run on a loading thread; Clear only swaps
        // in empty containers.
        private static void GameCtor_Postfix()
        {
            FpHits.Hit("Game ctor: control index cleared");
            FpRegistry.Clear();
        }
    }
}
