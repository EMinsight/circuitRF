using CircuitRF.Core.Design;
using CircuitRF.Design.Statistics;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>YA-2 R-ya2-4: a draw is a pure function of (seed, trial, stream) — trial 37 is the same
/// alone as inside a parallel batch, and a new statistical entry moves no other entry's draws.</summary>
public sealed class StreamReproducibilityTests
{
    private static readonly string[] Keys = ["R1.R", "C1.C", "DUT:R3.R", "mag(ZL)"];

    [Theory]
    [InlineData(StatSampling.Random)]
    [InlineData(StatSampling.Lhs)]
    [InlineData(StatSampling.Sobol)]
    public void Trial37_Alone_EqualsTrial37_InAHundredTrialBatchOnEightThreads(StatSampling sampling)
    {
        var alone = new StatisticalSampler(Keys, sampling, seed: 7, trials: 100).Sample(37);

        var batch = new StatisticalSample[100];
        var sampler = new StatisticalSampler(Keys, sampling, seed: 7, trials: 100);
        Parallel.For(1, 101, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t => batch[t - 1] = sampler.Sample(t));

        Assert.Equal(37, batch[36].Trial);
        foreach (var key in Keys) Assert.Equal(alone.Z[key], batch[36].Z[key]);
    }

    [Fact]
    public void AddingAnUnrelatedEntry_LeavesEveryOtherRandomDrawUnchanged()
    {
        var before = new StatisticalSampler(["R1.R", "C1.C"], StatSampling.Random, seed: 1, trials: 100);
        var after  = new StatisticalSampler(["R1.R", "L9.L", "C1.C"], StatSampling.Random, seed: 1, trials: 100);
        for (int t = 1; t <= 20; t++)
        {
            Assert.Equal(before.Sample(t).Z["R1.R"], after.Sample(t).Z["R1.R"]);
            Assert.Equal(before.Sample(t).Z["C1.C"], after.Sample(t).Z["C1.C"]);
        }
    }
}
