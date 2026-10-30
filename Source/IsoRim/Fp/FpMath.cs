using System;

namespace IsoRim.Fp
{
    // The ring's numbers, copied from FpTuningDef (FpTuningDef.RingNumbers) so FpMath needs nothing from Verse and the
    // unit tests (Source/Tests, which link this file) can build them by hand.
    public struct FpRingNumbers
    {
        public float arcBase, arcPerLevel, arcMin, arcMax;
        public float greatBase, greatPerLevel, greatMin, greatMax;
        public float passionMinor, passionMajor, manipMin, inspiredMul;
    }

    // Every pure function of first-person play (spec 3.9): plain C#, no Verse, no Unity, no Pow, Exp or trig. The
    // integer ones run inside the simulation and must give the same answer on every player's machine; the float ones
    // (sectors, ring widths, judging) are only used on the local, per-player side.
    public static class FpMath
    {
        // The eight neighbours clockwise from north (north = +z): N NE E SE S SW W NW.
        public static readonly int[] Dir8X = { 0, 1, 1, 1, 0, -1, -1, -1 };
        public static readonly int[] Dir8Z = { 1, 1, 0, -1, -1, -1, 0, 1 };

        public const byte Stop = 255;   // FpDir.Stop: no movement sector
        public const float SectorDeg = 22.5f;

        // Integer division rounding half away from zero (b > 0): (3,2) -> 2, (-3,2) -> -2, (5,3) -> 2.
        public static int RoundDiv(int a, int b)
        {
            return a >= 0 ? (2 * a + b) / (2 * b) : -((-2 * a + b) / (2 * b));
        }

        // The form score F (-4..+4) of a tally: Greats count 2, Hits 1, Misses -2, averaged over the checks (at least two,
        // so a single Great is +2, not +4) and doubled. 0 with no checks.
        public static int Form(int great, int hit, int miss)
        {
            return Form(great, hit, miss, 0);
        }

        // The same, averaged over at least the checks the piece was planned to get (expected, FpTally.expected): a check
        // that was never answered (voided, skipped by stopping play or turning checks off, the piece finished without
        // first person) counts as a zero, so stopping after a good start can't lock in a good form (integration change,
        // spec 10.4). 0 with no checks answered.
        public static int Form(int great, int hit, int miss, int expected)
        {
            int n = great + hit + miss;
            if (n == 0)
            {
                return 0;
            }
            int f = RoundDiv(2 * (2 * great + hit - 2 * miss), Math.Max(Math.Max(n, expected), 2));
            return f < -4 ? -4 : f > 4 ? 4 : f;
        }

        // The movement sector (0..15, sector 0 centred on north, clockwise) of a world heading in degrees. Hysteresis: the
        // current sector is kept until the heading is more than hysteresisDeg past its edge, so a heading on a border
        // doesn't flicker between two sectors (each change is a command). current == Stop means none yet.
        public static byte SectorOf(float worldDeg, byte current, float hysteresisDeg)
        {
            float deg = Wrap360(worldDeg);
            if (current <= 15)
            {
                float d = Math.Abs(DeltaDeg(deg, current * SectorDeg));
                if (d <= SectorDeg / 2f + hysteresisDeg)
                {
                    return current;
                }
            }
            int s = (int)Math.Floor((deg + SectorDeg / 2f) / SectorDeg) % 16;
            return (byte)(s < 0 ? s + 16 : s);
        }

        // The cell step of a sector. An even sector is one of the eight neighbours; an odd sector (between two of them)
        // alternates between its two neighbours by the step number n, so walking it averages its true heading.
        // Stop (or any sector past 15) gives no step.
        public static void StepOf(byte sector, int n, out int dx, out int dz)
        {
            if (sector > 15)
            {
                dx = 0;
                dz = 0;
                return;
            }
            int i;
            if ((sector & 1) == 0)
            {
                i = sector / 2;
            }
            else
            {
                i = (n & 1) == 0 ? (sector - 1) / 2 : ((sector + 1) / 2) % 8;
            }
            dx = Dir8X[i];
            dz = Dir8Z[i];
        }

        // How many game ticks pass per real tick at a game speed (TimeSpeed: Paused, Normal, Fast, Superfast, Ultrafast),
        // so check spacing stays about the same in real time. A constant table: the game's TickRateMultiplier also
        // depends on slow-down and "nothing happening" caches (spec 1).
        public static int SpeedFactor(int timeSpeed)
        {
            int i = timeSpeed < 0 ? 0 : timeSpeed > 4 ? 4 : timeSpeed;
            return SpeedFactors[i];
        }

        private static readonly int[] SpeedFactors = { 1, 1, 3, 6, 15 };

        // How many skill checks a job gets (spec 10.2): one per `spacing` ticks of remaining work at 1x (scaled by the
        // speed factor f), at least one, at most maxChecks; none when too little work is left or too little of the
        // progress range (p0Permille to endPermille) remains.
        public static int PlanChecks(float remTicks, int f, int p0Permille, int spacing, int minRemaining, int endPermille, int maxChecks)
        {
            if (remTicks < minRemaining * f || endPermille - p0Permille < 100)
            {
                return 0;
            }
            int per = spacing * f;
            if (per <= 0)
            {
                return 0;
            }
            int n = ((int)remTicks + per / 2) / per;
            return n < 1 ? 1 : n > maxChecks ? maxChecks : n;
        }

        // The progress (per mille) at which check k of `planned` falls due: the range p0..end is cut into equal spans, and
        // each check lands in the middle of its span, moved by up to a quarter span either way by `hash` (from
        // Gen.HashCombineInt of the job and k: deterministic, never HashCode.Combine).
        public static int DuePermille(int k, int planned, int p0, int end, int hash)
        {
            if (planned <= 0)
            {
                return end;
            }
            int span = (end - p0) / planned;
            if (span < 0)
            {
                span = 0;
            }
            int j = (hash & 0x7FFFFFFF) % (span / 2 + 1) - span / 4;
            return p0 + span * k + span / 2 + j;
        }

        // The ring's hit arc in degrees (spec 10.7): wider with skill level and passion, narrower with poor manipulation,
        // wider when inspired. passion: 0 none, 1 minor, 2 or more major.
        public static float ArcWidth(int level, int passion, float manip, bool inspired, FpRingNumbers r)
        {
            return Width(r.arcBase, r.arcPerLevel, r.arcMin, r.arcMax, level, passion, manip, inspired, r);
        }

        // The "great" part in the middle of the arc, in degrees: the same multipliers on the great numbers.
        public static float GreatWidth(int level, int passion, float manip, bool inspired, FpRingNumbers r)
        {
            return Width(r.greatBase, r.greatPerLevel, r.greatMin, r.greatMax, level, passion, manip, inspired, r);
        }

        private static float Width(float b, float perLevel, float min, float max, int level, int passion, float manip, bool inspired, FpRingNumbers r)
        {
            float passionMul = passion >= 2 ? r.passionMajor : passion == 1 ? r.passionMinor : 1f;
            float m = manip < r.manipMin ? r.manipMin : manip > 1f ? 1f : manip;
            float w = (b + perLevel * level) * passionMul * m * (inspired ? r.inspiredMul : 1f);
            return w < min ? min : w > max ? max : w;
        }

        // The outcome of a press at pressDeg (FpOutcome: 3 Great, 2 Hit, 1 Miss). halfFrameDeg widens both windows by
        // half a frame of needle travel each side (capped at 6 degrees), so a low frame rate isn't punished.
        public static byte Judge(float pressDeg, float arcStart, float arcWidth, float greatWidth, float halfFrameDeg)
        {
            float hf = halfFrameDeg < 0f ? 0f : halfFrameDeg > 6f ? 6f : halfFrameDeg;
            float greatStart = arcStart + (arcWidth - greatWidth) / 2f;
            if (pressDeg >= greatStart - hf && pressDeg <= greatStart + greatWidth + hf)
            {
                return 3;
            }
            if (pressDeg >= arcStart - hf && pressDeg <= arcStart + arcWidth + hf)
            {
                return 2;
            }
            return 1;
        }

        // Degrees into 0..360.
        public static float Wrap360(float deg)
        {
            float d = deg % 360f;
            return d < 0f ? d + 360f : d;
        }

        // The signed shortest turn from a to b, -180..180.
        public static float DeltaDeg(float a, float b)
        {
            float d = Wrap360(b - a);
            return d > 180f ? d - 360f : d;
        }
    }
}
