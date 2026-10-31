using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine;
using RfCore;

namespace CircuitRF.Engine.Tests.Linear;

public class SnpStampTests
{
    private static string Hero1Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "testdata", "Hero1");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero1 not found");
    }

    // ── Gate test: load, interpolate off-grid, stamp, verify matrix entries ──

    [Fact]
    public void SnpModel_StampsZExpansion_GroundReferenced()
    {
        var filePath = Path.Combine(Hero1Dir(), "potentially_unstable_amp.s2p");
        double testHz = 1.05e9; // off-grid between the 1 GHz and 1.1 GHz file points

        // Independent reference: interpolate + convert to Z directly via RfCore
        var snpRef = TouchstoneIO.ReadFile(filePath);
        var interpRef = RFNetwork.Interpolate(snpRef, [testHz],
            InterpolationMethod.CubicSpline, InterpolationFormat.RealImag,
            MatrixType.S, OutOfRangePolicy.WarnClamp);
        var zRef = RFNetwork.SToZ(interpRef.Matrices[0], snpRef.Z0);

        // Build model + 2-node MNA (node 1 = port 1, node 2 = port 2, ground = 0)
        var model = new SnpModel(portCount: 2, absoluteFilePath: filePath);
        var mna   = new MnaSystem(nonGroundNodes: 2);
        var ec    = MakeEc(model, [1, 2], refNode: 0);

        model.Stamp(mna, ec, 2.0 * Math.PI * testHz);

        // Two branches must have been allocated
        Assert.Equal(2, mna.BranchCount);

        // Matrix index map (0-based):
        //   col/row 0 = node 1,  col/row 1 = node 2
        //   col/row 2 = branch 0 (port 1),  col/row 3 = branch 1 (port 2)

        // B block: KCL at each port node receives its branch current (+1)
        Assert.Equal(Complex.One, mna.GetEntry(row: 0, col: 2)); // node 1 ← branch 0
        Assert.Equal(Complex.One, mna.GetEntry(row: 1, col: 3)); // node 2 ← branch 1

        // C block: constraint rows reference each port-node voltage (+1)
        Assert.Equal(Complex.One, mna.GetEntry(row: 2, col: 0)); // branch 0 row, node 1 col
        Assert.Equal(Complex.One, mna.GetEntry(row: 3, col: 1)); // branch 1 row, node 2 col

        // Ground reference entries for constraint rows should be absent (node 0 dropped)
        Assert.Equal(Complex.Zero, mna.GetEntry(row: 2, col: -1)); // -1 is always zero
        Assert.Equal(Complex.Zero, mna.GetEntry(row: 3, col: -1));

        // D block: constraint rows contain -Z[k,j] for each branch pair
        const double Tol = 1e-10;
        for (int k = 0; k < 2; k++)
        for (int j = 0; j < 2; j++)
        {
            var expected = -zRef[k, j];
            var actual   = mna.GetEntry(row: 2 + k, col: 2 + j);
            Assert.True((actual - expected).Magnitude < Tol,
                $"D[{k},{j}]: expected {expected:G6}, got {actual:G6}, " +
                $"diff {(actual - expected).Magnitude:G3}");
        }
    }

    // ── Floating-reference (N+1 nets): reference node is NOT ground ───────────

    [Fact]
    public void SnpModel_StampsZExpansion_FloatingReference()
    {
        var filePath = Path.Combine(Hero1Dir(), "potentially_unstable_amp.s2p");
        double testHz = 1.5e9;

        var snpRef    = TouchstoneIO.ReadFile(filePath);
        var interpRef = RFNetwork.Interpolate(snpRef, [testHz],
            InterpolationMethod.CubicSpline, InterpolationFormat.RealImag,
            MatrixType.S, OutOfRangePolicy.WarnClamp);
        var zRef = RFNetwork.SToZ(interpRef.Matrices[0], snpRef.Z0);

        // 3 non-ground nodes: 1, 2 = ports; 3 = floating reference
        var model = new SnpModel(portCount: 2, absoluteFilePath: filePath);
        var mna   = new MnaSystem(nonGroundNodes: 3);
        var ec    = MakeEc(model, [1, 2], refNode: 3);

        model.Stamp(mna, ec, 2.0 * Math.PI * testHz);

        Assert.Equal(2, mna.BranchCount);

        // B block: port 1 KCL (row 0, branch 0 col 3) and port 2 KCL (row 1, branch 1 col 4)
        Assert.Equal(Complex.One, mna.GetEntry(0, 3));  // node 1 ← branch 0
        Assert.Equal(Complex.One, mna.GetEntry(1, 4));  // node 2 ← branch 1
        // Reference node KCL (row 2): receives the -1 return currents from both branches
        Assert.Equal(new Complex(-1, 0), mna.GetEntry(2, 3)); // ref ← branch 0 (return)
        Assert.Equal(new Complex(-1, 0), mna.GetEntry(2, 4)); // ref ← branch 1 (return)

        // C block: V_port = +1, V_ref = -1 in each constraint row
        Assert.Equal(Complex.One,           mna.GetEntry(3, 0)); // branch 0 row, node 1
        Assert.Equal(new Complex(-1, 0),    mna.GetEntry(3, 2)); // branch 0 row, ref node (col 2)
        Assert.Equal(Complex.One,           mna.GetEntry(4, 1)); // branch 1 row, node 2
        Assert.Equal(new Complex(-1, 0),    mna.GetEntry(4, 2)); // branch 1 row, ref node (col 2)

        // D block: -Z[k,j]
        const double Tol = 1e-10;
        for (int k = 0; k < 2; k++)
        for (int j = 0; j < 2; j++)
        {
            var expected = -zRef[k, j];
            var actual   = mna.GetEntry(3 + k, 3 + j);
            Assert.True((actual - expected).Magnitude < Tol,
                $"D[{k},{j}]: expected {expected:G6}, got {actual:G6}");
        }
    }

    // ── Interp Mode + Interp Domain: SnpModel must pass BOTH through to RFNetwork.Interpolate ──

    [Fact]
    public void SnpModel_MakimaMethod_MatchesIndependentRFNetworkInterpolate()
    {
        var filePath = Path.Combine(Hero1Dir(), "potentially_unstable_amp.s2p");
        double testHz = 1.05e9; // off-grid between the 1 GHz and 1.1 GHz file points

        var snpRef    = TouchstoneIO.ReadFile(filePath);
        var interpRef = RFNetwork.Interpolate(snpRef, [testHz],
            InterpolationMethod.Makima, InterpolationFormat.RealImag,
            MatrixType.S, OutOfRangePolicy.WarnClamp);
        var zRef = RFNetwork.SToZ(interpRef.Matrices[0], snpRef.Z0);

        var model = new SnpModel(portCount: 2, absoluteFilePath: filePath,
            interpMethod: InterpolationMethod.Makima);
        var mna = new MnaSystem(nonGroundNodes: 2);
        var ec  = MakeEc(model, [1, 2], refNode: 0);

        model.Stamp(mna, ec, 2.0 * Math.PI * testHz);

        const double Tol = 1e-10;
        for (int k = 0; k < 2; k++)
        for (int j = 0; j < 2; j++)
        {
            var expected = -zRef[k, j];
            var actual   = mna.GetEntry(row: 2 + k, col: 2 + j);
            Assert.True((actual - expected).Magnitude < Tol,
                $"D[{k},{j}]: expected {expected:G6}, got {actual:G6}");
        }
    }

    [Fact]
    public void SnpModel_MagPhaseDomain_MatchesIndependentRFNetworkInterpolate_AndDiffersFromRealImag()
    {
        var filePath = Path.Combine(Hero1Dir(), "potentially_unstable_amp.s2p");
        double testHz = 1.05e9;

        var snpRef = TouchstoneIO.ReadFile(filePath);
        var interpRefMagPhase = RFNetwork.Interpolate(snpRef, [testHz],
            InterpolationMethod.CubicSpline, InterpolationFormat.MagPhase,
            MatrixType.S, OutOfRangePolicy.WarnClamp);
        var zRefMagPhase = RFNetwork.SToZ(interpRefMagPhase.Matrices[0], snpRef.Z0);

        var interpRefRealImag = RFNetwork.Interpolate(snpRef, [testHz],
            InterpolationMethod.CubicSpline, InterpolationFormat.RealImag,
            MatrixType.S, OutOfRangePolicy.WarnClamp);
        var zRefRealImag = RFNetwork.SToZ(interpRefRealImag.Matrices[0], snpRef.Z0);

        // Sanity: off-grid, the two domains must actually disagree, or this test would pass
        // vacuously regardless of whether SnpModel threads the domain through at all.
        Assert.True((zRefMagPhase[0, 0] - zRefRealImag[0, 0]).Magnitude > 1e-6,
            "Test fixture invalid: RI and MA interpolation coincide at this frequency.");

        var model = new SnpModel(portCount: 2, absoluteFilePath: filePath,
            interpFormat: InterpolationFormat.MagPhase);
        var mna = new MnaSystem(nonGroundNodes: 2);
        var ec  = MakeEc(model, [1, 2], refNode: 0);

        model.Stamp(mna, ec, 2.0 * Math.PI * testHz);

        const double Tol = 1e-10;
        for (int k = 0; k < 2; k++)
        for (int j = 0; j < 2; j++)
        {
            var expected = -zRefMagPhase[k, j];
            var actual   = mna.GetEntry(row: 2 + k, col: 2 + j);
            Assert.True((actual - expected).Magnitude < Tol,
                $"D[{k},{j}]: expected (MagPhase) {expected:G6}, got {actual:G6}");
        }
    }

    // ── CnlReader round-trip: hero1.cnl parses SnP: line correctly ────────────

    [Fact]
    public void CnlReader_ParsesSnpLine_InHero1()
    {
        var cnlPath = Path.Combine(Hero1Dir(), "hero1.cnl");
        var (_, tb) = CircuitRF.Core.Netlist.CnlReader.ReadFile(cnlPath);

        var snpInst = tb.Instances.Single(i =>
            i.Reference.Equals("SnP", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("X1", snpInst.InstanceName);
        Assert.Equal(2, snpInst.NetBindings.Count);       // a1, a2
        Assert.Null(snpInst.RefNetBinding);               // ground-referenced
        Assert.Contains(snpInst.Overrides,
            ov => ov.Name.Equals("NumPorts", StringComparison.OrdinalIgnoreCase) &&
                  ov.Expression == "2");
        // File parameter must be an absolute path
        var fileOv = snpInst.Overrides.Single(
            ov => ov.Name.Equals("File", StringComparison.OrdinalIgnoreCase));
        Assert.True(Path.IsPathRooted(fileOv.Expression.Trim('"')),
            $"Expected absolute path, got: {fileOv.Expression}");
    }

    // ── Elaboration round-trip: hero1.cnl elaborates to an SnpModel ──────────

    [Fact]
    public void Elaborator_CreatesSnpModel_FromHero1()
    {
        var cnlPath = Path.Combine(Hero1Dir(), "hero1.cnl");
        var (lib, tb) = CircuitRF.Core.Netlist.CnlReader.ReadFile(cnlPath);
        var nl = new Elaborator(lib).Elaborate(tb);

        var snpEc = nl.Components.Single(
            ec => ec.ComponentType.Equals("SnP", StringComparison.OrdinalIgnoreCase));

        Assert.IsType<SnpModel>(snpEc.Model);
        Assert.Equal(0, snpEc.ReferenceNode);             // ground-referenced
        Assert.Equal(2, snpEc.Nodes.Length);
    }

    /// <summary>
    /// Designer round 32 — a part measured in SERIES (S11 + S21 = 1, the usual supplier capacitor file) has a singular
    /// I − S, so its Z stamp was rounding noise; where a sweep point fell exactly on a file sample it was garbage (a 3.9 pF
    /// part read |S11|² + |S21|² = 3.86). Such a block is stamped as its S, and reproduces the ideal series capacitor at a
    /// file sample and between samples alike.
    /// </summary>
    [Fact]
    public void SeriesMeasuredFile_OnAndBetweenItsOwnSamples_IsTheSeriesElement()
    {
        const double C = 3.9e-12, Z0 = 50;
        static Complex S21Of(double hz) => 2 * Z0 / (new Complex(0, -1 / (2 * Math.PI * hz * C)) + 2 * Z0);
        string path = Path.Combine(Path.GetTempPath(), $"crf-series-{Guid.NewGuid():N}.s2p");
        var lines = new List<string> { "# Hz S RI R 50" };
        for (int k = 0; k < 401; k++)
        {
            double hz = 1e8 + k * 21e6;
            Complex s21 = S21Of(hz), s11 = 1 - s21;
            string R(Complex z) => FormattableString.Invariant($"{z.Real:R} {z.Imaginary:R}");
            lines.Add(FormattableString.Invariant($"{hz:R} {R(s11)} {R(s21)} {R(s21)} {R(s11)}"));
        }
        File.WriteAllLines(path, lines);
        try
        {
            var (lib, tb) = new CircuitRF.Core.Netlist.CnlReader().Read($@"
Port:P1 n1 0 Num=1 Z=50 Ohm
Port:P2 n2 0 Num=2 Z=50 Ohm
SnP:X1 n1 n2 NumPorts=2 File=""{path}""
analysis SP type=sparam start=6e8 stop=7e8 npts=5
");
            double[] grid = [625e6, 637.5e6];                   // 625 MHz is the file's 26th sample; 637.5 MHz lies between
            var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), grid)["S"];
            for (int k = 0; k < grid.Length; k++)
                Assert.True(((Complex)s[k, 1, 0] - S21Of(grid[k])).Magnitude < 1e-6,
                            $"{grid[k] / 1e6} MHz: S21 {(Complex)s[k, 1, 0]} against {S21Of(grid[k])}");
        }
        finally { File.Delete(path); }
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static ElaboratedComponent MakeEc(ComponentModel model, int[] nodes, int refNode)
        => new("SnP", "X1", nodes,
               new Dictionary<string, Value>(StringComparer.Ordinal),
               model)
           { ReferenceNode = refNode };
}
