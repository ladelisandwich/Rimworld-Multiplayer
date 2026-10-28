namespace IsoRim.Fp
{
    // STUB (M1.0, integrator). Track E owns this file and builds it in M3 (fp-play-spec 10.7): the skill-check ring, the
    // result pop, the progress bar, and in M6 the friend icon. Local only.
    public enum FpRingState : byte { Idle, Warn, Needle, Result }

    public static class FpSkillCheck
    {
        public static FpRingState State
        {
            get { return FpRingState.Idle; }
        }

        // Warn, Needle, or Result within tuning.resultGrace seconds.
        public static bool WantsKey
        {
            get { return false; }
        }

        public static float ArcStartDeg
        {
            get { return 0f; }
        }

        public static float ArcWidthDeg
        {
            get { return 0f; }
        }

        public static float GreatWidthDeg
        {
            get { return 0f; }
        }

        // Harness.
        public static float NeedleDeg
        {
            get { return 0f; }
        }

        // From FpInput.OnRootUpdate.
        internal static void Update()
        {
        }

        // From the FpHud postfix, every event type.
        internal static void OnGUI()
        {
        }
    }
}
