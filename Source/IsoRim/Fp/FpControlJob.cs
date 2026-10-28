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
            if (pawn.IsHashIntervalTick(60) && (pawn.GetLord() != null || FpControlJob.NeedsAutopilot(e, pawn)))
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
    public class JobGiver_FpControl : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
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
            if (pawn.jobs.jobQueue.Count > 0 || pawn.GetLord() != null || FpControlJob.NeedsAutopilot(e, pawn))
            {
                return null;
            }
            Job job = JobMaker.MakeJob(def);
            job.playerForced = true;
            job.expiryInterval = -1;
            job.locomotionUrgency = LocomotionUrgency.Jog;   // the game's normal walking speed, never faster
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

        // M2 (AutoNeeds): true when the AutoNeeds flag is set and the pawn must eat, sleep or rest in bed now, so the
        // think tree's needs take over. Off in M1.
        internal static bool NeedsAutopilot(FpEntry e, Pawn p)
        {
            return false;
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
