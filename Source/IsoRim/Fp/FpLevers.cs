using HarmonyLib;
using RimWorld;

namespace IsoRim.Fp
{
    // STUB (M1.0, integrator). Track F owns this file (fp-play-spec 10.5): the form StatPart, the quality patches and the
    // StatPart injection; M3 for the bill levers, M4 for the rest.
    public class StatPart_FpForm : StatPart
    {
        public override void TransformValue(StatRequest req, ref float val)
        {
        }

        public override string ExplanationPart(StatRequest req)
        {
            return null;
        }
    }

    public static class FpLevers
    {
        internal static void Install(Harmony h)
        {
        }
    }
}
