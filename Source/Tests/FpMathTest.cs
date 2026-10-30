using IsoRim.Fp;

namespace Tests;

// First-person play's pure functions (Source/IsoRim/Fp/FpMath.cs, linked into this project): the integer ones run in
// the shared simulation, so their exact values matter; the expected values are the spec's (fp-play-spec 3.9, 10.2).
[TestFixture]
public class FpMathTest
{
    [TestCase(3, 2, 2)]
    [TestCase(1, 2, 1)]
    [TestCase(-3, 2, -2)]
    [TestCase(5, 3, 2)]
    [TestCase(0, 7, 0)]
    [TestCase(-1, 2, -1)]
    [TestCase(4, 2, 2)]
    public void RoundDiv(int a, int b, int expected)
    {
        Assert.That(FpMath.RoundDiv(a, b), Is.EqualTo(expected));
    }

    [TestCase(1, 0, 0, 2)]
    [TestCase(0, 1, 0, 1)]
    [TestCase(0, 0, 1, -2)]
    [TestCase(3, 0, 0, 4)]
    [TestCase(0, 3, 0, 2)]
    [TestCase(0, 0, 3, -4)]
    [TestCase(4, 13, 3, 2)]
    [TestCase(1, 9, 10, -1)]
    [TestCase(0, 0, 0, 0)]
    [TestCase(50, 0, 0, 4)]
    [TestCase(0, 0, 50, -4)]
    public void Form(int great, int hit, int miss, int expected)
    {
        Assert.That(FpMath.Form(great, hit, miss), Is.EqualTo(expected));
        Assert.That(FpMath.Form(great, hit, miss, 0), Is.EqualTo(expected));
    }

    // Diluted by the checks the piece was planned to get (FpTally.expected): checks never answered count as zeros.
    [TestCase(2, 0, 0, 6, 1)]     // two Greats of six planned: RoundDiv(8, 6) = 1, not +4
    [TestCase(2, 0, 0, 2, 4)]     // all planned checks answered: as without
    [TestCase(2, 0, 0, 1, 4)]     // fewer planned than answered (re-planned on a later job): the answers count
    [TestCase(1, 0, 0, 0, 2)]     // nothing planned: at least two, as without
    [TestCase(0, 1, 0, 12, 0)]    // one Hit of twelve: RoundDiv(2, 12) = 0
    [TestCase(0, 0, 1, 6, -1)]    // one Miss of six: RoundDiv(-4, 6) = -1
    [TestCase(0, 0, 3, 4, -3)]    // RoundDiv(-12, 4) = -3
    [TestCase(6, 0, 0, 6, 4)]
    [TestCase(0, 0, 0, 6, 0)]     // nothing answered
    [TestCase(1, 4, 1, 12, 1)]    // RoundDiv(2 * 4, 12) = 1
    public void FormExpected(int great, int hit, int miss, int expected, int form)
    {
        Assert.That(FpMath.Form(great, hit, miss, expected), Is.EqualTo(form));
    }

    [TestCase(0f, 0)]
    [TestCase(11.2f, 0)]
    [TestCase(11.3f, 1)]
    [TestCase(22.5f, 1)]
    [TestCase(90f, 4)]
    [TestCase(180f, 8)]
    [TestCase(348.7f, 15)]
    [TestCase(348.8f, 0)]
    [TestCase(349f, 0)]
    [TestCase(359.9f, 0)]
    [TestCase(-10f, 0)]
    [TestCase(-12f, 15)]
    [TestCase(720f + 45f, 2)]
    public void SectorOfWithoutCurrent(float deg, int expected)
    {
        Assert.That(FpMath.SectorOf(deg, FpMath.Stop, 4f), Is.EqualTo((byte)expected));
    }

    [Test]
    public void SectorOfKeepsCurrentWithinHysteresis()
    {
        // Sector 0 spans -11.25..11.25; with 4 degrees of hysteresis it is kept up to 15.25 either way.
        Assert.That(FpMath.SectorOf(15f, 0, 4f), Is.EqualTo((byte)0));
        Assert.That(FpMath.SectorOf(-15f, 0, 4f), Is.EqualTo((byte)0));
        Assert.That(FpMath.SectorOf(15.5f, 0, 4f), Is.EqualTo((byte)1));
        Assert.That(FpMath.SectorOf(-15.5f, 0, 4f), Is.EqualTo((byte)15));
        // Across the 0/360 seam from sector 15 (centre 337.5).
        Assert.That(FpMath.SectorOf(352f, 15, 4f), Is.EqualTo((byte)15));
        Assert.That(FpMath.SectorOf(353f, 15, 4f), Is.EqualTo((byte)0));
        // A heading far from the current sector jumps straight to its own.
        Assert.That(FpMath.SectorOf(180f, 0, 4f), Is.EqualTo((byte)8));
    }

    [Test]
    public void StepOfEvenSectorsAreTheEightNeighbours()
    {
        int[,] expected = { { 0, 1 }, { 1, 1 }, { 1, 0 }, { 1, -1 }, { 0, -1 }, { -1, -1 }, { -1, 0 }, { -1, 1 } };
        for (int i = 0; i < 8; i++)
        {
            for (int n = 0; n < 3; n++)
            {
                FpMath.StepOf((byte)(i * 2), n, out int dx, out int dz);
                Assert.That((dx, dz), Is.EqualTo((expected[i, 0], expected[i, 1])), $"sector {i * 2}, step {n}");
            }
        }
    }

    [Test]
    public void StepOfOddSectorsAlternate()
    {
        // Sector 1 (NNE): N on even steps, NE on odd ones.
        FpMath.StepOf(1, 0, out int dx, out int dz);
        Assert.That((dx, dz), Is.EqualTo((0, 1)));
        FpMath.StepOf(1, 1, out dx, out dz);
        Assert.That((dx, dz), Is.EqualTo((1, 1)));
        // Sector 15 (NNW): NW on even steps, N (wrapped from 8) on odd ones.
        FpMath.StepOf(15, 0, out dx, out dz);
        Assert.That((dx, dz), Is.EqualTo((-1, 1)));
        FpMath.StepOf(15, 1, out dx, out dz);
        Assert.That((dx, dz), Is.EqualTo((0, 1)));
        // Sector 5 (ESE): E on even steps, SE on odd ones.
        FpMath.StepOf(5, 2, out dx, out dz);
        Assert.That((dx, dz), Is.EqualTo((1, 0)));
        FpMath.StepOf(5, 3, out dx, out dz);
        Assert.That((dx, dz), Is.EqualTo((1, -1)));
    }

    [Test]
    public void StepOfStopIsNoStep()
    {
        FpMath.StepOf(FpMath.Stop, 0, out int dx, out int dz);
        Assert.That((dx, dz), Is.EqualTo((0, 0)));
    }

    [Test]
    public void SixteenSectorStepsAverageTheirHeading()
    {
        // Two steps of an odd sector cover both neighbours: sector 3 (ENE, between NE and E) gives (2, 1).
        int sx = 0, sz = 0;
        for (int n = 0; n < 2; n++)
        {
            FpMath.StepOf(3, n, out int dx, out int dz);
            sx += dx;
            sz += dz;
        }
        Assert.That((sx, sz), Is.EqualTo((2, 1)));
    }

    [TestCase(-3, 1)]
    [TestCase(0, 1)]
    [TestCase(1, 1)]
    [TestCase(2, 3)]
    [TestCase(3, 6)]
    [TestCase(4, 15)]
    [TestCase(9, 15)]
    public void SpeedFactor(int timeSpeed, int expected)
    {
        Assert.That(FpMath.SpeedFactor(timeSpeed), Is.EqualTo(expected));
    }

    // The cadence of spec 10.2 at 1x with the default tuning (spacing 480, min remaining 150, end 900, max 12).
    [TestCase(300f, 1, 0, 1)]      // simple meal
    [TestCase(1300f, 1, 0, 3)]     // table
    [TestCase(15000f, 1, 0, 12)]   // large sculpture, capped
    [TestCase(80f, 1, 0, 0)]       // wall: too short
    [TestCase(1800f, 1, 0, 4)]     // granite
    [TestCase(1800f, 3, 0, 1)]     // granite at 3x
    [TestCase(1800f, 6, 0, 1)]     // granite at 6x: (1800 + 1440) / 2880
    [TestCase(1800f, 15, 0, 0)]    // granite at 15x: under 150 * 15 ticks left
    [TestCase(1300f, 1, 850, 0)]   // less than 100 per mille left before the end
    [TestCase(149f, 1, 0, 0)]
    [TestCase(150f, 1, 0, 1)]
    public void PlanChecks(float remTicks, int f, int p0, int expected)
    {
        Assert.That(FpMath.PlanChecks(remTicks, f, p0, 480, 150, 900, 12), Is.EqualTo(expected));
    }

    [Test]
    public void PlanChecksZeroSpacingPlansNothing()
    {
        Assert.That(FpMath.PlanChecks(5000f, 1, 0, 0, 150, 900, 12), Is.EqualTo(0));
    }

    [Test]
    public void DuePermilleStaysInItsSpan()
    {
        // 3 checks between 100 and 900: spans of 266; each check a quarter span or less from its span's middle.
        for (int k = 0; k < 3; k++)
        {
            for (int hash = -50; hash < 50; hash++)
            {
                int due = FpMath.DuePermille(k, 3, 100, 900, hash * 7919);
                int middle = 100 + 266 * k + 133;
                Assert.That(due, Is.InRange(middle - 66, middle + 67), $"k {k}, hash {hash}");
            }
        }
    }

    [Test]
    public void DuePermilleExactValues()
    {
        // span = 800 / 4 = 200; j = hash % 101 - 50
        Assert.That(FpMath.DuePermille(0, 4, 0, 800, 0), Is.EqualTo(0 + 100 - 50));
        Assert.That(FpMath.DuePermille(1, 4, 0, 800, 100), Is.EqualTo(200 + 100 + 50));
        Assert.That(FpMath.DuePermille(2, 4, 0, 800, 101), Is.EqualTo(400 + 100 - 50));
        // A negative hash is masked to its low 31 bits.
        Assert.That(FpMath.DuePermille(0, 4, 0, 800, -1), Is.EqualTo(100 + (0x7FFFFFFF % 101) - 50));
        // Nothing planned: due at the end.
        Assert.That(FpMath.DuePermille(0, 0, 100, 900, 5), Is.EqualTo(900));
    }

    private static FpRingNumbers Ring() => new FpRingNumbers
    {
        arcBase = 34f, arcPerLevel = 3f, arcMin = 24f, arcMax = 110f,
        greatBase = 6f, greatPerLevel = 0.5f, greatMin = 4f, greatMax = 22f,
        passionMinor = 1.1f, passionMajor = 1.2f, manipMin = 0.5f, inspiredMul = 1.25f,
    };

    [Test]
    public void ArcWidth()
    {
        var r = Ring();
        Assert.That(FpMath.ArcWidth(0, 0, 1f, false, r), Is.EqualTo(34f).Within(1e-4));
        Assert.That(FpMath.ArcWidth(10, 0, 1f, false, r), Is.EqualTo(64f).Within(1e-4));
        Assert.That(FpMath.ArcWidth(10, 1, 1f, false, r), Is.EqualTo(70.4f).Within(1e-4));
        Assert.That(FpMath.ArcWidth(10, 2, 1f, false, r), Is.EqualTo(76.8f).Within(1e-4));
        Assert.That(FpMath.ArcWidth(10, 0, 0.8f, false, r), Is.EqualTo(51.2f).Within(1e-4));
        Assert.That(FpMath.ArcWidth(10, 0, 0.1f, false, r), Is.EqualTo(32f).Within(1e-4));   // manipulation floor 0.5
        Assert.That(FpMath.ArcWidth(10, 0, 1.5f, false, r), Is.EqualTo(64f).Within(1e-4));   // capped at 1
        Assert.That(FpMath.ArcWidth(10, 0, 1f, true, r), Is.EqualTo(80f).Within(1e-4));
        Assert.That(FpMath.ArcWidth(20, 2, 1f, true, r), Is.EqualTo(110f).Within(1e-4));    // max
        Assert.That(FpMath.ArcWidth(0, 0, 0.5f, false, r), Is.EqualTo(24f).Within(1e-4));   // min
    }

    [Test]
    public void GreatWidth()
    {
        var r = Ring();
        Assert.That(FpMath.GreatWidth(0, 0, 1f, false, r), Is.EqualTo(6f).Within(1e-4));
        Assert.That(FpMath.GreatWidth(10, 0, 1f, false, r), Is.EqualTo(11f).Within(1e-4));
        Assert.That(FpMath.GreatWidth(10, 2, 1f, true, r), Is.EqualTo(16.5f).Within(1e-4));
        Assert.That(FpMath.GreatWidth(0, 0, 0.5f, false, r), Is.EqualTo(4f).Within(1e-4));
        Assert.That(FpMath.GreatWidth(20, 2, 1f, true, r), Is.EqualTo(22f).Within(1e-4));
    }

    [Test]
    public void Judge()
    {
        // Arc 150..210 (60 wide), great 175..185 (10 wide).
        Assert.That(FpMath.Judge(180f, 150f, 60f, 10f, 0f), Is.EqualTo((byte)3));
        Assert.That(FpMath.Judge(175f, 150f, 60f, 10f, 0f), Is.EqualTo((byte)3));
        Assert.That(FpMath.Judge(185f, 150f, 60f, 10f, 0f), Is.EqualTo((byte)3));
        Assert.That(FpMath.Judge(174.9f, 150f, 60f, 10f, 0f), Is.EqualTo((byte)2));
        Assert.That(FpMath.Judge(150f, 150f, 60f, 10f, 0f), Is.EqualTo((byte)2));
        Assert.That(FpMath.Judge(210f, 150f, 60f, 10f, 0f), Is.EqualTo((byte)2));
        Assert.That(FpMath.Judge(149.9f, 150f, 60f, 10f, 0f), Is.EqualTo((byte)1));
        Assert.That(FpMath.Judge(0f, 150f, 60f, 10f, 0f), Is.EqualTo((byte)1));
        // Half a frame widens both windows.
        Assert.That(FpMath.Judge(147f, 150f, 60f, 10f, 4f), Is.EqualTo((byte)2));
        Assert.That(FpMath.Judge(172f, 150f, 60f, 10f, 4f), Is.EqualTo((byte)3));
        // ...by at most 6 degrees.
        Assert.That(FpMath.Judge(143f, 150f, 60f, 10f, 20f), Is.EqualTo((byte)1));
        Assert.That(FpMath.Judge(144f, 150f, 60f, 10f, 20f), Is.EqualTo((byte)2));
    }
}
