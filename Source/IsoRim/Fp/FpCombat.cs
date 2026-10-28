using System.Runtime.CompilerServices;
using Multiplayer.API;
using UnityEngine;
using Verse;

namespace IsoRim.Fp
{
    // STUB (M1.0, integrator). Track G owns this file and builds it in M5 (fp-play-spec 12): firing while drafted with
    // vanilla odds, the aim, the reticle.
    public static class FpCombat
    {
        // Drafted: true if it handled the click.
        public static bool TryPrimaryDown(Pawn p, FpHit hit)
        {
            return false;
        }

        public static void PrimaryUp()
        {
        }

        // Drafted hint ("Shoot raider 63%") or undrafted "R: raise weapon" on hostiles.
        public static string Hint(Pawn p, FpHit hit)
        {
            return null;
        }

        // Called by FpHud on Repaint while drafted.
        public static void ReticleOnGUI(Vector2 centre)
        {
        }

        [SyncMethod(cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpFire(Pawn p, LocalTargetInfo target, bool hold)
        {
        }

        [SyncMethod(cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpStopFire(Pawn p)
        {
        }
    }
}
