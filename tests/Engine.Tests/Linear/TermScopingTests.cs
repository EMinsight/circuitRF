using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Gates for Brief H — Term scoping and engine-stamping refinement.
///
/// Gate 1: a Term/Port at DC is its Re(Z) to its reference node — not shorted by its S-parameter
///         drive branch, and (since 2026-10-05) not open either. S-parameter analysis is unchanged.
/// Gate 2: Only top-level Terms become S-param ports; a Term buried inside an
///         instantiated sub-cell is inert, doesn't add a port, and emits a warning.
/// Gate 3: Linter — Terms in cells, stray top-level Pins, duplicate/missing Num.
/// </summary>
public class TermScopingTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (CircuitRF.Core.Elaboration.ElaboratedNetlist nl, CircuitRF.Engine.NonlinearDcEngine.DcResult dc)
        RunDc(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl     = new Elaborator(lib).Elaborate(tb);
        var result = NonlinearDcEngine.Run(nl);
        return (nl, result);
    }

    private static Complex Sij(RfCore.Data.DataSet ds, int r, int c, int fi = 0) =>
        (Complex)ds["S"][fi, r, c];

    // ── Gate 1a: a Term is its Z at DC (overturned 2026-10-05: it was inert) ────────────────────────────

    /// <summary>
    /// Circuit: V1=5V → R1=100Ω → n2; Port:P1 (Z=50Ω) at n2; R2=200Ω from n2 to ground.
    /// A Term terminates: at DC it is Re(Z) to its reference, never its S-parameter 0 V drive branch (a short)
    /// and no longer open — open, a current whose only return was a Term drove nothing and DC did not converge.
    /// V(n2) = 5 × (50‖200) / (100 + 50‖200) = 5 × 40/140.
    /// </summary>
    [Theory]
    [InlineData("Port:P1")]
    [InlineData("Term:T1")]
    public void DcAnalysis_ATermIsItsZ_NotAShortAndNotOpen(string term)
    {
        var (nl, dc) = RunDc($@"
V:V1   n1 0   V=5
R:R1   n1 n2  R=100 Ohm
{term}  n2 0  Num=1 Z=50 Ohm
R:R2   n2 0   R=200 Ohm
");
        Assert.True(dc.Converged, "DC solver did not converge");
        double v2 = dc.NodeVoltages[nl.Nodes.IndexOf("n2") - 1];
        Assert.Equal(5.0 * 40 / 140, v2, 1e-9);
    }

    /// <summary>A Term inside an instantiated sub-cell is ignored (the elaborator says so) — at DC as well.</summary>
    [Fact]
    public void DcAnalysis_ABuriedTerm_StaysOutOfTheCircuit()
    {
        var (nl, dc) = RunDc(@"
define Stage (p q)
  R:R1  p q  R=100 Ohm
  Port:BuriedTerm  q 0  Num=2 Z=50 Ohm
end

V:V1   n1 0   V=5
Stage:X1  n1 n2
R:R2   n2 0   R=200 Ohm
");
        Assert.True(dc.Converged, "DC solver did not converge");
        Assert.Equal(10.0 / 3, dc.NodeVoltages[nl.Nodes.IndexOf("n2") - 1], 1e-9);
    }

    /// <summary>
    /// Harmonic balance loads a Term too (2026-10-05): a current source into a Term and nothing else reads
    /// V = I·Z at the fundamental and V = Idc·Re(Z) at DC — the DC point NonlinearDcEngine seeds the solve from.
    /// The cubic SDD is only there to give the solve a nonlinear interface; at these levels it carries ~1e-10 A.
    /// </summary>
    [Fact]
    public void HarmonicBalance_ATermIsItsZ_AtEveryHarmonic_AndRealAtDc()
    {
        var (lib, tb) = new CnlReader().Read("""
            I_1Tone:I1  n 0  I=0.1  Idc=0.02  Freq=1e9
            Port:P1     n 0  Num=1  Z=50+25j
            SDD:D1      n 0  Ports=1  I[1,0]=1e-12*_v1^3
            analysis HB1 type=hb Tone=1e9 MaxHarm=3
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        var p  = CircuitRF.Engine.HarmonicBalance.HbEngine.Resolve(
            tb.Analyses.OfType<CircuitRF.Core.Design.HarmonicBalanceAnalysis>().First(), nl.ResolvedGlobals);
        var run = new CircuitRF.Engine.HarmonicBalance.HbEngine(nl, tb).Run(p);
        Assert.True(run.Converged);

        var v = run.DataSet["V"];
        int n = Array.IndexOf(v.Axes.First(a => a.Name == "node").Labels!, "n");
        var v0 = (Complex)v[n, 0];
        var v1 = (Complex)v[n, 1];
        Assert.Equal(0.02 * 50, v0.Real, 1e-6);
        Assert.True((v1 - 0.1 * new Complex(50, 25)).Magnitude < 1e-6 * v1.Magnitude, $"V1 = {v1}");
    }

    // ── Gate 1b: S-param path is unchanged for top-level Terms ────────────────

    [Fact]
    public void SParam_TopLevelTerm_UnchangedByRefactor()
    {
        // Matched 1-port: S11 = 0.  Regression against existing behaviour.
        var (lib, tb) = new CnlReader().Read(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
R:R1  n1 0  R=50 Ohm
");
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, [1e9]);

        var s11 = Sij(ds, 0, 0);
        Assert.True(s11.Magnitude < 1e-8, $"S11={s11:G4}, expected ≈ 0 (matched load)");
    }

    // ── Gate 2: Buried Terms are inert and warned ─────────────────────────────

    /// <summary>
    /// Sub-cell "Stage" has an internal Port:BuriedTerm that should be ignored. (It was called
    /// "Amp" until brief-sys-5 made that a PRIMITIVE reference. A primitive name shadows a user cell
    /// of the same spelling — true of every name in this family — and here it is a loud refusal
    /// rather than a silent substitution, because the net count disagrees.)
    /// Top testbench has Port:P1 (Num=1) only.
    /// Expected: 1-port S-matrix; a warning naming the buried Term.
    /// </summary>
    [Fact]
    public void SParam_BuriedTerm_IsInert_OnlyTopLevelPortCounted()
    {
        var cnl = @"
define Stage (p q)
  R:R1  p q  R=50 Ohm
  Port:BuriedTerm  p 0  Num=2 Z=50 Ohm
end

Port:P1  n1 0  Num=1 Z=50 Ohm
Stage:X1  n1 n2
R:RLoad  n2 0  R=50 Ohm
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        // Warning must mention the buried Term path.
        Assert.Contains(nl.Warnings,
            w => w.Contains("BuriedTerm", StringComparison.OrdinalIgnoreCase)
              || w.Contains("instantiated cell", StringComparison.OrdinalIgnoreCase));

        // S-param analysis should see exactly 1 port (P1), not 2.
        var ds = SParameterEngine.Run(nl, [1e9]);
        int portCount = ds["S"].Axes[1].Length;
        Assert.Equal(1, portCount);
    }

    /// <summary>
    /// Buried Term must not perturb the S-parameter result.
    /// Compare: top-level Port:P1 + sub-cell with BuriedTerm versus the same
    /// circuit without the buried Term at all — S11 must be identical.
    /// </summary>
    [Fact]
    public void SParam_BuriedTerm_DoesNotPerturbResult()
    {
        const string flatCnl = @"
Port:P1  n1 0  Num=1 Z=50 Ohm
R:R1  n1 n2  R=50 Ohm
R:RLoad  n2 0  R=50 Ohm
";
        const string hierarchicalCnl = @"
define Stage (p q)
  R:R1  p q  R=50 Ohm
  Port:BuriedTerm  p 0  Num=2 Z=50 Ohm
end

Port:P1  n1 0  Num=1 Z=50 Ohm
Stage:X1  n1 n2
R:RLoad  n2 0  R=50 Ohm
";
        var (lib1, tb1) = new CnlReader().Read(flatCnl);
        var nl1 = new Elaborator(lib1).Elaborate(tb1);
        var ds1 = SParameterEngine.Run(nl1, [1e9]);

        var (lib2, tb2) = new CnlReader().Read(hierarchicalCnl);
        var nl2 = new Elaborator(lib2).Elaborate(tb2);
        var ds2 = SParameterEngine.Run(nl2, [1e9]);

        double diff = (Sij(ds1, 0, 0) - Sij(ds2, 0, 0)).Magnitude;
        Assert.True(diff < 1e-10,
            $"S11 with buried Term ({Sij(ds2,0,0):G4}) ≠ S11 without ({Sij(ds1,0,0):G4}); " +
            $"buried Term must not perturb results.");
    }

    // ── Gate 3: Linter ────────────────────────────────────────────────────────

    [Fact]
    public void Linter_TermInSubCell_EmitsWarning()
    {
        var cnl = @"
define DUT (n1 n2)
  Port:TermInCell  n1 0  Num=1 Z=50 Ohm
  R:R1  n1 n2  R=50 Ohm
end

Port:P1  p1 0  Num=1 Z=50 Ohm
DUT:X1  p1 p2
R:Rload  p2 0  R=50 Ohm
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        Assert.Contains(nl.Warnings,
            w => w.Contains("TermInCell", StringComparison.OrdinalIgnoreCase)
              || w.Contains("instantiated cell", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Linter_TopLevelPin_EmitsWarning()
    {
        var cnl = @"
Port:P1  n1 0  Num=1 Z=50 Ohm
Pin:Pin1  n1  Num=1
R:R1  n1 0  R=50 Ohm
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        Assert.Contains(nl.Warnings,
            w => w.Contains("Pin1", StringComparison.OrdinalIgnoreCase)
              || w.Contains("top", StringComparison.OrdinalIgnoreCase)
              || w.Contains("testbench", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Linter_DuplicateNum_EmitsWarning()
    {
        var cnl = @"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=1 Z=50 Ohm
R:R1  n1 n2  R=50 Ohm
analysis SP type=sparam start=1 GHz stop=3 GHz step=1 GHz
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        Assert.Contains(nl.Warnings,
            w => w.Contains("Duplicate", StringComparison.OrdinalIgnoreCase)
              || w.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
              || (w.Contains("Num=1") && w.Contains("P1") && w.Contains("P2")));
    }

    [Fact]
    public void Linter_GapInNumSequence_EmitsWarning()
    {
        // Ports Num=1 and Num=3 — missing Num=2.
        var cnl = @"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P3  n3 0  Num=3 Z=50 Ohm
R:R13  n1 n3  R=50 Ohm
analysis SP type=sparam start=1 GHz stop=3 GHz step=1 GHz
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        Assert.Contains(nl.Warnings,
            w => w.Contains("Num=2") || w.Contains("missing"));
    }

    [Fact]
    public void Linter_CleanTestbench_NoWarnings()
    {
        // A well-formed 2-port testbench (with the S-param analysis that activates the lint) should
        // produce no warnings.
        var cnl = @"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=2 Z=50 Ohm
R:R1  n1 n2  R=50 Ohm
analysis SP type=sparam start=1 GHz stop=3 GHz step=1 GHz
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        Assert.Empty(nl.Warnings);
    }
}
