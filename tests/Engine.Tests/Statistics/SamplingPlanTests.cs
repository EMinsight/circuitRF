using CircuitRF.Engine.Statistics;

namespace CircuitRF.Engine.Tests.Statistics;

/// <summary>YA-2 R-ya2-3: the Latin hypercube's stratification, the Sobol sequence against its
/// published points, and the scrambled sequence as a pure function of the seed.</summary>
public sealed class SamplingPlanTests
{
    private static readonly ulong[] Streams = [StatStreams.Id("R1.R"), StatStreams.Id("C1.C"), StatStreams.Id("mag(ZL)")];

    [Fact]
    public void LatinHypercube_PutsOneDrawInEachStratumOfEachStream()
    {
        const int n = 50;
        var plan = SamplingPlan.Create(SamplingMethod.LatinHypercube, 3, Streams, n);
        var seen = new bool[Streams.Length, n];
        for (int t = 1; t <= n; t++)
        {
            var z = plan.Normals(t);
            for (int s = 0; s < Streams.Length; s++)
            {
                int stratum = (int)Math.Floor(SpecialFunctions.NormalCdf(z[s]) * n);
                Assert.False(seen[s, stratum], $"stream {s}: stratum {stratum} drawn twice");
                seen[s, stratum] = true;
            }
        }
    }

    [Fact]
    public void Sobol_Unscrambled_MatchesThePublishedFirstPoints()
    {
        // The first ten points in three dimensions, as the authors of the direction numbers publish
        // them for their reference generator.
        double[,] published =
        {
            { 0, 0, 0 }, { 0.5, 0.5, 0.5 }, { 0.75, 0.25, 0.25 }, { 0.25, 0.75, 0.75 }, { 0.375, 0.375, 0.625 },
            { 0.875, 0.875, 0.125 }, { 0.625, 0.125, 0.875 }, { 0.125, 0.625, 0.375 }, { 0.1875, 0.3125, 0.9375 },
            { 0.6875, 0.8125, 0.4375 },
        };
        for (int i = 0; i < 10; i++)
            for (int d = 0; d < 3; d++)
                Assert.Equal(published[i, d], Sobol.Point(d, i));
        Assert.Equal(1111, Sobol.MaxDimensions);
    }

    [Fact]
    public void Sobol_Scrambled_IsAPureFunctionOfTheSeed()
    {
        var a = SamplingPlan.Create(SamplingMethod.Sobol, 11, Streams);
        var again = SamplingPlan.Create(SamplingMethod.Sobol, 11, Streams);
        var other = SamplingPlan.Create(SamplingMethod.Sobol, 12, Streams);
        for (int t = 64; t >= 1; t--)   // backwards: no point depends on the one before
        {
            Assert.Equal(a.Normals(t), again.Normals(t));
            Assert.NotEqual(a.Normals(t), other.Normals(t));
        }

        // The dimension a stream takes follows its id, not the order streams were listed in.
        var shuffled = SamplingPlan.Create(SamplingMethod.Sobol, 11, [Streams[2], Streams[0], Streams[1]]);
        var z = a.Normals(5);
        Assert.Equal([z[2], z[0], z[1]], shuffled.Normals(5));
    }
}
