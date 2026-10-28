using System.Runtime.CompilerServices;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace IsoRim.Fp
{
    // STUB (M1.0, integrator). Track D owns this file and builds it in M2 (fp-play-spec 11.1-11.3): the picker test and
    // hint, the start-recipe and work-bill commands, the clean-up of first-person bills.
    public static class FpBills
    {
        // True if hit is an own bill giver; opens Dialog_FpRecipes.
        public static bool TryOpenPicker(Pawn p, FpHit hit)
        {
            return false;
        }

        // "Use electric stove…" or null.
        public static string PickerHint(Pawn p, FpHit hit)
        {
            return null;
        }

        public static void SendStartRecipe(Pawn p, Thing bench, RecipeDef r, int count)
        {
        }

        public static void SendWorkBill(Pawn p, Bill bill)
        {
        }

        [SyncMethod(cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpStartRecipe(Pawn p, Thing bench, RecipeDef recipe, int count)
        {
        }

        [SyncMethod(cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpWorkBill(Pawn p, Bill bill)
        {
        }

        // Every 60 ticks.
        internal static void Tick(FpMapComp comp)
        {
        }

        internal static void OnRelease(FpMapComp comp, FpEntry e)
        {
        }
    }
}
