using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CircuitRF.Design.Circuit;
using CircuitRF.Ui.Tuning;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>brief-tuneopt-3 R-to3-1/R-to3-3: one in flight, one pending, newest wins, never cancelled by a newer value.</summary>
public sealed class TuneSessionPolicyTests
{
    private sealed class FakeEvaluator
    {
        public readonly ManualResetEventSlim Gate = new(false);
        public readonly ConcurrentQueue<string> Evaluated = new();
        public int Cancelled;

        public RunResult Evaluate(IReadOnlyDictionary<string, string> v, IReadOnlyList<string>? _, CircuitRF.Engine.RunControl c)
        {
            Evaluated.Enqueue(v["R1.R"]);
            while (!Gate.Wait(5))
                if (c.Token.IsCancellationRequested) { Interlocked.Increment(ref Cancelled); return new RunResult(RunStatus.EngineError, "cancelled"); }
            return new RunResult(RunStatus.Success, "ok", grouped: TuningDisplayFixture.Results(1));
        }
    }

    private sealed class Sink : ITuneResultSink
    {
        public int Published, Committed, Dropped;
        public void Publish(DataSet data) => Interlocked.Increment(ref Published);
        public void Commit(DataSet data, IReadOnlyDictionary<string, string> values) => Committed++;
        public void Drop() => Dropped++;
        public void Snapshot() { }
        public void ClearSnapshot() { }
    }

    private static Dictionary<string, string> V(int i) => new() { ["R1.R"] = $"{i} Ohm" };

    private static void WaitIdle(TuneSession s, long evaluations) =>
        Assert.True(SpinWait.SpinUntil(() => s.Evaluations == evaluations && !s.IsEvaluating, 5000));

    [Fact]
    public void TenRequestsDuringOneEvaluation_RunTheInFlightOneAndTheNewest()
    {
        var eval = new FakeEvaluator();
        var sink = new Sink();
        using var s = new TuneSession(eval.Evaluate, sink, a => a());

        for (int i = 1; i <= 10; i++) s.Request(V(i));
        Assert.True(SpinWait.SpinUntil(() => eval.Evaluated.Count == 1, 5000));
        Assert.True(s.IsLagging);
        eval.Gate.Set();
        WaitIdle(s, 2);

        Assert.Equal(["1 Ohm", "10 Ohm"], eval.Evaluated.ToArray());
        Assert.Equal(0, eval.Cancelled);          // the newer values never cancelled the run in flight
        Assert.Equal(2, sink.Published);
        Assert.Equal("10 Ohm", s.DisplayedValues!["R1.R"]);
        Assert.False(s.IsLagging);
    }

    [Fact]
    public void Stop_CancelsTheEvaluationInFlightAndDropsThePending()
    {
        var eval = new FakeEvaluator();
        var sink = new Sink();
        var s = new TuneSession(eval.Evaluate, sink, a => a());

        s.Request(V(1));
        s.Request(V(2));
        Assert.True(SpinWait.SpinUntil(() => eval.Evaluated.Count == 1, 5000));
        s.Stop();
        WaitIdle(s, 1);

        Assert.Equal(1, eval.Cancelled);
        Assert.Equal(0, sink.Published);
        Assert.Equal(0, sink.Committed);          // nothing was ever displayed, so nothing is written
        Assert.Equal(1, sink.Dropped);
    }

    [Fact]
    public void RunOnRelease_EvaluatesOnlyTheFinalRequest()
    {
        var eval = new FakeEvaluator();
        eval.Gate.Set();
        using var s = new TuneSession(eval.Evaluate, new Sink(), a => a()) { RunOnRelease = true };

        s.Request(V(1));
        s.Request(V(2));
        s.Request(V(3), final: true);
        WaitIdle(s, 1);

        Assert.Equal(["3 Ohm"], eval.Evaluated.ToArray());
    }
}
