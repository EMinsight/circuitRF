using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Statistics;
using Xunit;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>
/// R-ya3-3 — process vs mismatch is a property of the SCOPE a distribution is evaluated in (yield overview D6):
/// a global VAR's draw is shared by every instance, a cell parameter default's is drawn per instance.
/// </summary>
public sealed class ProcessMismatchScopeTests
{
    // Rnom is a process draw (global); each instance's R default is a mismatch draw about it.
    private const string Netlist = """
        Rnom = agauss(100, 10, 1)
        define sub (a b)
        parameters R=agauss(Rnom,5,1)
        R:R1 a b R=R
        end sub
        sub:X1 n1 0
        sub:X2 n2 0
        """;

    private static (double X1, double X2, double Rnom, IReadOnlyList<StatisticalCall> Calls) Elaborate(
        bool process, bool mismatch)
    {
        var (lib, tb) = new CnlReader().Read(Netlist);
        using var nl = new Elaborator(lib)
        {
            Statistics = new ExpressionDraws(seed: 1, trial: 1, process: process, mismatch: mismatch),
        }.Elaborate(tb);
        double R(string path) => nl.Components.Single(c => c.InstancePath == path).Parameters["R"].AsReal();
        return (R("X1.R1"), R("X2.R1"), nl.ResolvedGlobals["Rnom"].AsReal(), nl.StatisticalCalls);
    }

    [Fact]
    public void InstancesShareTheProcessDraw_AndDifferInTheirMismatchDraw()
    {
        var both = Elaborate(process: true, mismatch: true);
        Assert.NotEqual(100.0, both.Rnom);
        Assert.NotEqual(both.X1, both.X2);
        Assert.Equal(
            [("Rnom", StatisticalKind.Process), ("X1.R", StatisticalKind.Mismatch), ("X2.R", StatisticalKind.Mismatch)],
            both.Calls.Select(c => (c.Stream, c.Kind)).OrderBy(c => c.Stream, StringComparer.Ordinal));

        // mismatch=0: both instances sit on the one shared process draw.
        var processOnly = Elaborate(process: true, mismatch: false);
        Assert.Equal(both.Rnom, processOnly.X1);
        Assert.Equal(both.Rnom, processOnly.X2);

        // process=0: the global is its nominal; each instance still draws its own mismatch.
        var mismatchOnly = Elaborate(process: false, mismatch: true);
        Assert.Equal(100.0, mismatchOnly.Rnom);
        Assert.Equal(both.X1 - both.Rnom, mismatchOnly.X1 - 100.0, 9);
    }
}
