using System.Collections.Generic;
using Verse;

namespace IsoRim.Fp
{
    // First-person play's gameplay numbers (fp-play-spec 13.2), one def: IsoRimFp_Tuning in
    // Source/IsoRim/Defs/IsoRimFp_Tuning.xml. A def, not a setting, because the simulation reads it: Multiplayer's join
    // check compares Defs, so every player runs the same numbers. The C# defaults equal the XML values; XML field names
    // equal these fields.
    public class FpTuningDef : Def
    {
        // Checks: ticks at 1x, multiplied by FpMath.SpeedFactor of the map's speed.
        public int checkSpacingTicks = 480;
        public int minRemainingTicks = 150;
        public int minGapTicks = 180;
        public int deadlineTicks = 600;
        public int maxChecks = 12;
        public int endDuePermille = 900;
        public int holdPermille = 970;

        // Grants: a share of the segment worked since the last check (spec 10.4); xp by outcome (index = FpOutcome).
        public float grantGreat = 0.40f;
        public float grantHit = 0.15f;
        public float grantMiss = -0.30f;
        public List<float> xp = new List<float> { 0f, 0f, 0f, 0f };

        // Levers by form F = -4..+4 (index F + 4, spec 10.5).
        public List<float> constructFail = new List<float> { 2.0f, 1.75f, 1.5f, 1.25f, 1f, 0.9f, 0.8f, 0.7f, 0.6f };
        public List<float> surgery = new List<float> { 0.80f, 0.85f, 0.90f, 0.95f, 1f, 1.02f, 1.04f, 1.06f, 1.08f };
        public List<float> foodPoison = new List<float> { 2.0f, 1.75f, 1.5f, 1.25f, 1f, 0.85f, 0.7f, 0.6f, 0.5f };
        public List<float> yield = new List<float> { 0.80f, 0.85f, 0.90f, 0.95f, 1f, 1.03f, 1.06f, 1.08f, 1.10f };

        // The ring (local; seconds and degrees).
        public float ringPeriod = 1.15f;
        public float ringWarn = 0.55f;
        public float resultGrace = 0.6f;
        public float arcBase = 34f;
        public float arcPerLevel = 3f;
        public float arcMin = 24f;
        public float arcMax = 110f;
        public float greatBase = 6f;
        public float greatPerLevel = 0.5f;
        public float greatMin = 4f;
        public float greatMax = 22f;
        public float passionMinor = 1.1f;
        public float passionMajor = 1.2f;
        public float manipMin = 0.5f;
        public float inspiredMul = 1.25f;
        public float arcStartMin = 110f;
        public float arcEndMax = 330f;

        // Interaction (cells).
        public float reachCells = 2.5f;
        public float interactionCellReach = 1.5f;
        public int pickMaxCells = 60;
        public float pawnPickRadius = 0.45f;
        public float ingredientRadius = 20f;
        public int maxBillStack = 15;

        // The ring numbers as FpMath takes them.
        public FpRingNumbers RingNumbers
        {
            get
            {
                return new FpRingNumbers
                {
                    arcBase = arcBase, arcPerLevel = arcPerLevel, arcMin = arcMin, arcMax = arcMax,
                    greatBase = greatBase, greatPerLevel = greatPerLevel, greatMin = greatMin, greatMax = greatMax,
                    passionMinor = passionMinor, passionMajor = passionMajor, manipMin = manipMin, inspiredMul = inspiredMul,
                };
            }
        }

        // A lever table's value for form f (-4..+4); 1 outside the range or for a short table.
        public static float Table(List<float> table, int form)
        {
            int i = form + 4;
            return table != null && i >= 0 && i < table.Count ? table[i] : 1f;
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (xp == null || xp.Count != 4)
            {
                yield return "xp needs 4 values (one per outcome)";
            }
            foreach (KeyValuePair<string, List<float>> t in new[]
            {
                new KeyValuePair<string, List<float>>("constructFail", constructFail),
                new KeyValuePair<string, List<float>>("surgery", surgery),
                new KeyValuePair<string, List<float>>("foodPoison", foodPoison),
                new KeyValuePair<string, List<float>>("yield", this.yield),
            })
            {
                if (t.Value == null || t.Value.Count != 9)
                {
                    yield return t.Key + " needs 9 values (form -4 to +4)";
                }
            }
            if (checkSpacingTicks <= 0 || maxChecks <= 0 || deadlineTicks <= 0)
            {
                yield return "checkSpacingTicks, maxChecks and deadlineTicks must be positive";
            }
            if (endDuePermille > holdPermille || holdPermille > 1000)
            {
                yield return "endDuePermille <= holdPermille <= 1000 is required";
            }
            if (ringPeriod <= 0f || arcMin > arcMax || greatMin > greatMax || arcStartMin > arcEndMax)
            {
                yield return "ring numbers out of order";
            }
        }
    }

    public static class FpTuning
    {
        private static FpTuningDef cached;

        // The one tuning def (looked up once; defs don't change after loading).
        public static FpTuningDef Get
        {
            get { return cached ?? (cached = DefDatabase<FpTuningDef>.GetNamed("IsoRimFp_Tuning")); }
        }
    }
}
