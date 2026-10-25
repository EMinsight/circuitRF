using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// Brief DFR26-5 R-dfr26-5-3: MTEE and MCROSS simulate the arms they draw. Every claim here is exact — two
/// uniform lines cascaded are one line, and a junction behind lines is the hand-built circuit — so the gate is
/// 1e-9 in S over 0.1-10 GHz. The circuits are <c>testdata/microstrip-junction-arms/junction-arms.cnl</c>.
/// </summary>
public class JunctionArmTests
{
    private const double Z0 = 50.0;
    private const double Tol = 1e-9;
    private static readonly double[] Frequencies = [.. Enumerable.Range(0, 34).Select(i => 0.1e9 + i * 0.3e9)];

    private static readonly Lazy<ElaboratedNetlist> Netlist = new(() =>
    {
        var (lib, tb) = CnlReader.ReadFile(Fixture());
        return new Elaborator(lib).Elaborate(tb);
    });

    [Fact]
    public void Tee_WithNoBranchPastTheLine_IsTheMlinOfL1PlusL2()
    {
        var tee = (MicrostripTeeModel)Component("T1").Model;
        Assert.True(tee.IsStraightLine);
        AssertSameS(["s1a", "s1b"], ["T1"], ["s1x", "s1y"], ["TL1"]);
    }

    [Fact]
    public void Tee_WithARealBranch_IsItsJunctionBehindTheArmLines()
    {
        var tee = (MicrostripTeeModel)Component("T2").Model;
        Assert.Equal([3.25e-3, 5.25e-3, 3.5e-3], tee.ArmSectionLengths.Select(l => Math.Round(l, 12)));
        AssertSameS(["t2a", "t2b", "t2c"], ["T2"], ["h2a", "h2b", "h2c"], ["TA", "TB", "TC", "TJ"]);
    }

    [Fact]
    public void Cross_WithNoStubsPastTheLine_IsTheMlinOfL1PlusL3()
    {
        var cross = (MicrostripCrossModel)Component("X3").Model;
        Assert.Equal(1, cross.StraightLinePair);
        AssertSameS(["x3a", "x3c"], ["X3"], ["x3x", "x3y"], ["TL3"]);
    }

    [Fact]
    public void Cross_WithFourRealArms_IsItsJunctionBehindTheArmLines()
        => AssertSameS(["c4a", "c4b", "c4c", "c4d"], ["X4"], ["h4a", "h4b", "h4c", "h4d"], ["XA", "XB", "XC", "XD", "XJ"]);

    /// <summary>A part with no L (a file from before the lengths existed) is the junction alone, entry for entry,
    /// and says once that its arms are not simulated.</summary>
    [Fact]
    public void NoLengths_SimulatesTheJunctionAlone_AndSaysSo()
    {
        var oldTee = new MicrostripTeeModel(3e-3, 3e-3, 1.5e-3, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, "MTEE:TJ");
        var oldCross = new MicrostripCrossModel(3e-3, 1.5e-3, 3e-3, 1.5e-3, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, "MCROSS:XJ");
        foreach (var (name, legacy) in new (string, ComponentModel)[] { ("TJ", oldTee), ("XJ", oldCross) })
        {
            var ec = Component(name);
            var a = new CapturingMnaContext();
            ec.Stamp(a, 2 * Math.PI * 3e9);
            var reference = new CapturingMnaContext();
            new ElaboratedComponent(ec.ComponentType, ec.InstancePath, ec.Nodes, ec.Parameters, legacy).Stamp(reference, 2 * Math.PI * 3e9);
            Assert.Equal(reference.Entries, a.Entries);

            var warnings = ((IReportsWarnings)ec.Model).DrainWarnings();
            Assert.Single(warnings, w => w.Message.Contains("arms are not simulated"));
        }
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────

    private static ElaboratedComponent Component(string name)
        => Netlist.Value.Components.Single(c => c.InstancePath == name);

    /// <summary>The S-matrices of two sub-circuits, each seen at its own ports with every other node floating,
    /// agree entry for entry at every frequency.</summary>
    private static void AssertSameS(string[] portsA, string[] partsA, string[] portsB, string[] partsB)
    {
        foreach (double f in Frequencies)
        {
            var sa = SAt(portsA, partsA, f);
            var sb = SAt(portsB, partsB, f);
            for (int r = 0; r < sa.GetLength(0); r++)
                for (int c = 0; c < sa.GetLength(1); c++)
                    Assert.True((sa[r, c] - sb[r, c]).Magnitude < Tol,
                        $"S{r + 1}{c + 1} at {f / 1e9:G4} GHz: {sa[r, c]} vs {sb[r, c]}");
        }
    }

    private static Complex[,] SAt(string[] ports, string[] parts, double freqHz)
    {
        var mna = new CapturingMnaContext();
        foreach (var part in parts) Component(part).Stamp(mna, 2 * Math.PI * freqHz);

        // The only branch these parts allocate is an exact tie (JunctionArms.StampTie): fold its far node
        // into its near one, which is what the tie's constraint row says.
        var tiedTo = mna.BranchCurrents.ToDictionary(b => b.NodeTo, b => b.NodeFrom);
        Assert.Equal(mna.BranchCount, tiedTo.Count);
        int Node(int k) => tiedTo.TryGetValue(k, out var to) ? to : k;

        var nodes = Netlist.Value.Nodes;
        var portIdx = ports.Select(nodes.IndexOf).ToList();
        var internalIdx = mna.Entries.Keys.SelectMany(k => new[] { Node(k.Row), Node(k.Col) })
            .Where(n => n > 0 && !portIdx.Contains(n)).Distinct().ToList();
        var order = portIdx.Concat(internalIdx).ToList();
        int n = order.Count, p = portIdx.Count;
        var y = new Complex[n, n];
        foreach (var ((row, col), v) in mna.Entries)
        {
            int r = order.IndexOf(Node(row)), c = order.IndexOf(Node(col));
            if (r >= 0 && c >= 0) y[r, c] += v;
        }

        for (int k = n - 1; k >= p; k--)
            for (int i = 0; i < k; i++)
            {
                var factor = y[i, k] / y[k, k];
                for (int j = 0; j < k; j++) y[i, j] -= factor * y[k, j];
            }

        // S = (I + Z0·Y)⁻¹ (I − Z0·Y), by Gauss-Jordan on [I + Z0·Y | I − Z0·Y].
        var aug = new Complex[p, 2 * p];
        for (int r = 0; r < p; r++)
            for (int c = 0; c < p; c++)
            {
                var zy = Z0 * y[r, c];
                aug[r, c] = (r == c ? 1 : 0) + zy;
                aug[r, p + c] = (r == c ? 1 : 0) - zy;
            }
        for (int col = 0; col < p; col++)
        {
            int piv = Enumerable.Range(col, p - col).MaxBy(r => aug[r, col].Magnitude);
            for (int c = 0; c < 2 * p; c++) (aug[col, c], aug[piv, c]) = (aug[piv, c], aug[col, c]);
            var d = aug[col, col];
            for (int c = 0; c < 2 * p; c++) aug[col, c] /= d;
            for (int r = 0; r < p; r++)
            {
                if (r == col) continue;
                var m = aug[r, col];
                for (int c = 0; c < 2 * p; c++) aug[r, c] -= m * aug[col, c];
            }
        }
        var s = new Complex[p, p];
        for (int r = 0; r < p; r++)
            for (int c = 0; c < p; c++)
                s[r, c] = aug[r, p + c];
        return s;
    }

    private static string Fixture()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "testdata", "microstrip-junction-arms", "junction-arms.cnl");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("testdata/microstrip-junction-arms/junction-arms.cnl");
    }
}
