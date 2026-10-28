using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.AI;

namespace IsoRim.Fp
{
    // STUB (M1.0, integrator). Track F owns this file and builds it in M3 (fp-play-spec 10.1-10.4, 10.6): tracking skilled
    // work, scheduling checks, the deadline, the hold prefix, the result command, tally upkeep.

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
        // Null if none, or the skill is Shooting/Melee/Social/Animals/null.
        public static IFpWorkAdapter AdapterFor(JobDriver d)
        {
            return null;
        }

        // d.ActiveSkill
        public static SkillDef SkillOf(JobDriver d)
        {
            return null;
        }

        // UI: tracked progress 0..1, -1 if not tracking.
        public static float Progress01(Pawn p)
        {
            return -1f;
        }

        // Spec 10.6.
        public static int TallyKeyOf(Pawn p, Job job)
        {
            return 0;
        }

        // UI sender.
        public static void SendResult(Pawn p, int jobLoadId, int seq, byte outcome)
        {
        }

        [SyncMethod(cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpCheckResult(Pawn p, int jobLoadId, int seq, byte outcome)
        {
        }

        // Called by B for each controlled entry, every tick.
        internal static void Tick(FpMapComp comp, FpEntry e)
        {
        }

        internal static void OnRelease(FpMapComp comp, FpEntry e, bool countAsMiss)
        {
        }

        // Every 250 ticks.
        internal static void PruneTallies(FpMapComp comp)
        {
        }

        // The hold prefix.
        internal static void Install(Harmony h)
        {
        }
    }
}
