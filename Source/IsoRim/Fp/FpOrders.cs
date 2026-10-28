using System.Runtime.CompilerServices;
using Multiplayer.API;
using Verse;

namespace IsoRim.Fp
{
    // STUB (M1.0, integrator). Track D owns this file and builds it in M4 (fp-play-spec 11.4): mining, harvesting and
    // cutting ordered by the left button on an undesignated rock or plant.
    public static class FpOrders
    {
        // An undesignated rock or plant: sends FpOrderWork.
        public static bool TryOrderWork(Pawn p, FpHit hit)
        {
            return false;
        }

        // "Mine granite", "Chop oak", "Harvest rice" or null.
        public static string OrderHint(Pawn p, FpHit hit)
        {
            return null;
        }

        // kind 0 Mine, 1 Harvest, 2 Cut
        [SyncMethod(cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpOrderWork(Pawn p, Thing target, byte kind)
        {
        }
    }
}
