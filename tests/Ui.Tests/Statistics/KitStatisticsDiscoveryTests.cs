using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Pdk;
using CircuitRF.Design.Statistics;
using CircuitRF.Diagnostics;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>
/// R-ya3-5 — a Monte Carlo run with a yield goal and nothing to vary is refused; a kit axis whose statistical
/// section is not selected gets an Info note naming it; a selected one is named as what brought the statistics.
/// </summary>
public sealed class KitStatisticsDiscoveryTests : IDisposable
{
    private readonly string _kit = Path.Combine(Path.GetTempPath(), "crf-ya3d-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_kit, recursive: true); } catch { /* best effort */ }
    }

    private const string Bench = """
        Port:P1 in 0 Num=1 Z=50 Ohm
        R:R1 in 0 R=Rv Ohm
        analysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz
        goal G = dB(SP1.S(1,1)) analysis=SP1 le -10 use=yield
        """;

    [Fact]
    public void AYieldRunWithNothingToVary_IsRefused_AndAVarDistributionIsSomething()
    {
        Diagnostic? Refusal(string rv)
        {
            var (lib, tb) = new CnlReader().Read($"Rv = {rv}\n" + Bench);
            using var nl = new Elaborator(lib).Elaborate(tb);
            return KitStatistics.NothingVaries(tb.Tuning!, nl.StatisticalCalls);
        }

        Assert.Equal("yield.run.nothing-varies", Refusal("50")?.Id);
        Assert.Null(Refusal("agauss(50,2.5,1)"));
    }

    [Fact]
    public void AnUnselectedStatisticalSection_IsNamed_AndASelectedOneIsReported()
    {
        Directory.CreateDirectory(_kit);
        string corner = Path.Combine(_kit, "rCorners.lib");
        File.WriteAllText(corner, """
            .LIB r_typ
            .param rsh = 50
            .ENDL r_typ

            .LIB r_stat
            .param rsh_norm = 50
            .include r_stat.lib
            .ENDL r_stat
            """);
        File.WriteAllText(Path.Combine(_kit, "r_stat.lib"), ".param rsh = 'agauss(rsh_norm, 2.5, 1)'\n");

        var unselected = KitStatistics.Report([], axes: [new KitCornerAxisState("rCorners", corner, Selected: null)]);
        var note = Assert.Single(unselected.Notes);
        Assert.Equal(DiagnosticSeverity.Info, note.Severity);
        Assert.Contains("rCorners", note.Render());
        Assert.Contains("'r_stat'", note.Render());

        var section  = PdkCorners.SectionFor(corner, "r_stat", out _)!;
        var selected = KitStatistics.Report([], [section], [new KitCornerAxisState("rCorners", corner, "r_stat")]);
        Assert.Empty(selected.Notes);
        Assert.Equal(["r_stat (rCorners.lib)"], selected.Sections);
    }
}
