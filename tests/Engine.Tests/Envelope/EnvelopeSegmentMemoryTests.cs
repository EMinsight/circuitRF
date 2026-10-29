using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// Brief ENV-SEG: an envelope run's parallel segments warm up for five of the circuit's slowest time constants — what a
/// model declares (a trap's emission, a thermal pole) or the linear network's slowest pole — and a run that cannot hold
/// two segments that long runs as one. The seam error is each probe's worst overlap mismatch over its own RMS, and a run
/// whose external probe disagrees past <see cref="EnvelopeRunner.SeamWarnAbove"/> says so, naming it.
/// </summary>
public sealed class EnvelopeSegmentMemoryTests
{
    private static string Fixture(string fetSwitches) => $"""
        Pin = 12
        MOD_Source:SRC1 n1 0 Fc=2e9 Pavg=Pin dBm Z=50 Ohm Carrier[1]="NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1" Record=0.0005 Shaping=-80
        C:C1 Vin n1 C=100 pF
        L:L1 n2 Vin L=100 nH
        Vdc:V1 n2 0 Vdc=-0.8 V
        ComposedFET:X1 Vin Vout 0 File="{CmodelPath()}" N=8 {fetSwitches}
        L:L2 VDD Vout L=100 nH
        Vdc:V2 VDD 0 Vdc=8 V
        C:C2 Vout n5 C=100 pF
        R:RL n5 0 R=50 Ohm
        analysis ENV1 type=env Stop=200e-6 Accuracy=standard Segments=auto MaxHarm=5 Probes="Vin,X1.di,Vout"
        """;

    /// <summary>A gain that follows a bias node: port 1's conductance rises with <c>nb</c>, which port 2 charges with the
    /// rectified drive through <c>R ∥ C</c> — τ = <paramref name="c"/> × 1 kΩ.</summary>
    private static string BiasFeed(string c, string segments, string stop = "100e-6") => $"""
        MOD_Source:S1 n1 0 Fc=1e9 Pavg=0 Z=50 Record=0.5e-3 Carrier[1]="LTE;BW=5MHz;Mod=QPSK;Off=0;P=0;Seed=1"
        SDD:X1 n1 0 nb 0 I[1,0]=_v1*(0.01+0.02*_v2) I[2,0]=-0.005*_v1^2
        R:RB nb 0 R=1k
        C:CB nb 0 C={c}
        analysis ENV1 type=env Stop={stop} Accuracy=draft MaxHarm=1 Probes="nb,n1" {segments}
        """;

    private static (EnvelopeRunner.Plan plan, Func<ElaboratedNetlist> elaborate, ElaboratedNetlist first) Load(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var first = new Elaborator(lib).Elaborate(tb);
        bool used = false;
        var gate = new object();
        ElaboratedNetlist Elab() { lock (gate) { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return first; } }
        return (EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), first.ResolvedGlobals), Elab, first);
    }

    private static (DataSet ds, List<string> notes) Run(string cnl)
    {
        var (plan, elab, first) = Load(cnl);
        using (first)
        {
            var notes = new List<string>();
            return (EnvelopeRunner.Run(plan, elab, note: notes.Add), notes);
        }
    }

    [Fact]
    public void ATrapState_ForcesOneSegment_AndSaysWhichAndHowLong()
    {
        var (plan, elab, first) = Load(Fixture("DrainLag=true"));
        var notes = new List<string>();
        using (first)
        using (var prepared = EnvelopeRunner.Prepare(plan, elab, note: notes.Add))
        {
            Assert.Equal(1, prepared.Segments);
            Assert.Equal(5e-3, prepared.WarmS, 9);
            var note = Assert.Single(notes);
            Assert.Contains("X1's drain-lag state remembers 1 ms", note, StringComparison.Ordinal);
        }

        // No lag and a thermal pole of 25 °C/W × 10 nJ/°C = 0.25 µs: today's plan, unasked.
        (plan, elab, first) = Load(Fixture("DrainLag=false GateLag=false Cth=1e-8"));
        notes.Clear();
        using (first)
        using (var prepared = EnvelopeRunner.Prepare(plan, elab, note: notes.Add))
        {
            Assert.Equal(Math.Clamp(10, 1, Environment.ProcessorCount), prepared.Segments);
            Assert.Equal(5e-6, prepared.WarmS);
            Assert.Empty(notes);
        }
    }

    [Fact]
    public void ASlowBiasNetwork_RunsAsOneSegment_MatchingSequential_AndAForcedSplitWarnsNamingTheProbe()
    {
        var (auto, notes) = Run(BiasFeed("20n", "Segments=auto"));
        var (seq, _) = Run(BiasFeed("20n", "Segments=1"));

        Assert.Equal(1, auto["__EnvSegments"].RealValues[0]);
        Assert.Contains(notes, n => n.Contains("the network at node 'nb' remembers 20 µs", StringComparison.Ordinal));
        Assert.Equal(seq["VSD"].RealValues, auto["VSD"].RealValues);
        Assert.Equal(seq["V"].ComplexValues, auto["V"].ComplexValues);

        var (forced, forcedNotes) = Run(BiasFeed("20n", "Segments=4 Warmup=1e-6"));
        Assert.Equal(4, forced["__EnvSegments"].RealValues[0]);
        Assert.Contains(forcedNotes, n => n.Contains("seams", StringComparison.Ordinal) && n.Contains("probe 'n1'", StringComparison.Ordinal));
    }

    [Fact]
    public void ShortMemory_KeepsTodaysPlan()
    {
        // τ = 20 ns: 5 µs of warm-up, four segments of a 100 µs run on whole steps.
        var (plan, elab, first) = Load(BiasFeed("20p", "Segments=auto"));
        var notes = new List<string>();
        using (first)
        using (var prepared = EnvelopeRunner.Prepare(plan, elab, maxSegments: 4, note: notes.Add))
        {
            Assert.Equal(4, prepared.Segments);
            Assert.Equal(5e-6, prepared.WarmS);
            int total = (int)Math.Round(100e-6 / prepared.StepS);
            Assert.Equal([0, total / 4, total / 2, 3 * total / 4, total], prepared.Bound);
            Assert.Empty(notes);
        }
    }

    [Fact]
    public void OneSampleNearZero_DoesNotDominateTheSeamError()
    {
        // Harmonic 1 sweeps through zero mid-overlap; the two segments differ by 1e-6 of its amplitude everywhere.
        const int ov = 50;
        var prev = new Complex[ov][][];
        var cur = new Complex[ov][][];
        for (int i = 0; i < ov; i++)
        {
            var a = new Complex(Math.Cos(Math.PI * i / (ov - 1)), 0);       // 1 … −1, 0 at the middle
            if (i == ov / 2) a = new Complex(1e-9, 0);
            prev[i] = [[0, a]];
            cur[i] = [[0, a + 1e-6]];
        }

        var (all, external, probe) = EnvelopeRunner.Seam(prev, 0, cur, ov, 1, [false]);

        Assert.Equal(0, probe);
        Assert.True(all < 2e-6, $"seam error {all}");
        Assert.Equal(all, external);
    }

    private static string CmodelPath([CallerFilePath] string here = "")
    {
        var d = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) d = d.Parent;
        return Path.Combine(d!.FullName, "examples", "Model Composer", "synthetic-gan", "gan_4x125.cmodel");
    }
}
