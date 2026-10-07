using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CircuitRF.Engine;
using CircuitRF.Ui.Schematic;
using RfCore.Data;

namespace CircuitRF.Ui.Tests.Evaluation;

/// <summary>brief-tuneopt-2: a shipped example workspace, copied so a run can write beside it.</summary>
internal static class ExampleCopy
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    /// <summary>The workspace <paramref name="group"/> copied to a fresh temporary folder; returns its root.</summary>
    public static string Workspace(string group)
    {
        string src = Path.Combine(RepoRoot(), "examples", group);
        string dst = Path.Combine(Path.GetTempPath(), "crf-to2-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t);
        }
        return dst;
    }

    public static string Csch(string ws, string cell) => Path.Combine(ws, cell, "schematic", cell + ".csch");

    /// <summary>The run's grouped DataSet as the bytes Simulate writes to <c>results/</c>.</summary>
    public static byte[] RunNpy(DataSet? grouped, string dir)
    {
        var o = ResultsWriter.WriteRun(dir, "run-" + Guid.NewGuid().ToString("N")[..8], grouped);
        Assert.Null(o.Error);
        return File.ReadAllBytes(Assert.Single(o.Written));
    }
}

/// <summary>R-to2-1: Simulate's <c>run.npy</c> is the bytes it was before the run moved below the firewall.</summary>
public class CircuitEvaluationParityTests
{
    [Theory]
    [InlineData("S-Parameters", "Amplifier")]
    [InlineData("Harmonic Balance", "PowerAmplifier")]
    [InlineData("Loadpull", "Loadpull")]
    public void SimulateRunNpy_IsTheOldPathsBytes_AndTheOneCallServiceAgrees(string group, string cell)
    {
        string ws = ExampleCopy.Workspace(group);

        // What Simulate does: the netlist at the workspace root, prepared and executed.
        string cnl = Path.Combine(ws, "netlist.cnl");
        File.WriteAllText(cnl, SchematicCircuit.CnlTextOf(ExampleCopy.Csch(ws, cell)));
        var simulate = SchematicRunService.Execute(SchematicRunService.Prepare(cnl, ws), new RunControl());
        Assert.True(simulate.Status == RunStatus.Success, simulate.StatusMessage);
        byte[] bytes = ExampleCopy.RunNpy(simulate.GroupedResults, ws);

        // Captured from the pre-extraction SchematicRunService on the platforms listed; a different
        // libm or vector width moves the last bits, so a platform with no capture checks the agreement
        // below only.
        string rid = RuntimeInformation.RuntimeIdentifier;
        var capture = File.ReadAllLines(Path.Combine(ExampleCopy.RepoRoot(),
                                                     "testdata/circuit-evaluation/old-gui-path.sha256"))
            .Where(l => !l.StartsWith('#'))
            .Select(l => Regex.Split(l.Trim(), @"\s+"))
            .FirstOrDefault(p => p.Length == 4 && p[0] == rid && p[1] == cell);
        if (capture is not null)
        {
            Assert.Equal(long.Parse(capture[2]), bytes.LongLength);
            Assert.Equal(capture[3], Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }

        // The one-call form a tuner or the optimizer uses, from the same netlist: the same bytes.
        var evaluated = CircuitEvaluation.Evaluate(PreparedCircuit.FromFile(cnl, ws));
        Assert.True(evaluated.Status == RunStatus.Success, evaluated.StatusMessage);
        Assert.Equal(bytes, ExampleCopy.RunNpy(evaluated.GroupedResults, ws));
    }

    /// <summary>The scan: the measurement group is assembled in one place, below the firewall.</summary>
    [Fact]
    public void UiAndCli_AssembleNoMeasurementGroupOfTheirOwn()
    {
        string root = ExampleCopy.RepoRoot();
        var offenders = new List<string>();
        foreach (var dir in new[] { "src/Ui", "src/Cli" })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                string code = Regex.Replace(File.ReadAllText(f), @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
                if (code.Contains("new MeasurementEvaluator(")
                    || Regex.IsMatch(code, @"AddToGroup\(\s*(""measurements""|DataSet\.MeasurementsGroup|RfCore\.Data\.DataSet\.MeasurementsGroup)"))
                    offenders.Add(Path.GetRelativePath(root, f));
            }
        Assert.Empty(offenders);
    }
}

/// <summary>R-to2-4: a prepared circuit evaluated again with new tuned values reads nothing.</summary>
public class CircuitEvaluationReuseTests
{
    [Fact]
    public void SecondEvaluation_WithOtherTunedValues_ReadsNoFile()
    {
        string ws = ExampleCopy.Workspace("Hierarchy");
        var circuit = PreparedCircuit.FromSchematic(ExampleCopy.Csch(ws, "Bench"));
        Assert.Null(circuit.ReadError);
        int filesRead = circuit.FilesRead.Count;
        Assert.True(filesRead >= 3, "the bench and its two sub-cell schematics were resolved");

        // Nothing left on disk to read: a second read of anything would fail the run.
        Directory.Delete(ws, recursive: true);

        RunResult Eval(string dB) => CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest
        {
            Tunables    = new Dictionary<string, string> { ["Board:X1.dB"] = dB },
            Expressions = ["S21_dB", "nosuch"],
        });

        var a = Eval("3");
        var b = Eval("4");
        Assert.True(a.Status == RunStatus.Success, a.StatusMessage);
        Assert.True(b.Status == RunStatus.Success, b.StatusMessage);

        Assert.Equal(1, circuit.LibraryResolutions);
        Assert.Equal(filesRead, circuit.FilesRead.Count);
        Assert.Equal(2, circuit.Elaborations);

        // The tuned value moved the circuit (one more dB in the first pad), and the goal expression saw it.
        double s21a = a.Expressions[0].Value.GetValueOrDefault().AsCube().RealValues[0];
        double s21b = b.Expressions[0].Value.GetValueOrDefault().AsCube().RealValues[0];
        Assert.Equal(-1.0, s21b - s21a, 1);
        Assert.NotNull(a.Expressions[1].Error);
        Assert.Equal(new AnalysisConvergence("SP1", true), Assert.Single(a.Convergence));
    }
}

/// <summary>R-to2-5: concurrent evaluations of one prepared circuit equal sequential ones.</summary>
public class CircuitEvaluationConcurrencyTests
{
    [Fact]
    public void FourConcurrentEvaluations_EqualFourSequentialOnes()
    {
        string ws = ExampleCopy.Workspace("Hierarchy");
        var circuit = PreparedCircuit.FromSchematic(ExampleCopy.Csch(ws, "Bench"));
        Assert.True(circuit.IsReentrant, circuit.NotReentrantReason);

        string[] values = ["1", "2", "3", "4"];
        RunResult Eval(string dB) => CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest
        {
            Tunables = new Dictionary<string, string> { ["Board:X2.dB"] = dB },
        });

        var sequential = values.Select(Eval).ToArray();
        var concurrent = new RunResult[values.Length];
        Parallel.For(0, values.Length, new ParallelOptions { MaxDegreeOfParallelism = values.Length },
                     i => concurrent[i] = Eval(values[i]));

        for (int i = 0; i < values.Length; i++)
            Assert.Equal(ExampleCopy.RunNpy(sequential[i].GroupedResults, ws),
                         ExampleCopy.RunNpy(concurrent[i].GroupedResults, ws));
        Assert.NotEqual(ExampleCopy.RunNpy(sequential[0].GroupedResults, ws),
                        ExampleCopy.RunNpy(sequential[3].GroupedResults, ws));
    }

    [Fact]
    public void ADesignWithAnExternalDeviceWorker_ReportsItselfNotReentrant()
    {
        var circuit = PreparedCircuit.FromText(
            "ExtDevice:Q1  g  d  0  Provider=kit  Type=fet\nPort:P1  g  0  Num=1  Z=50 Ohm\n" +
            "analysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz\n",
            sourceDirectory: null, baseDirectory: null);
        Assert.Null(circuit.ReadError);
        Assert.False(circuit.IsReentrant);
        Assert.Contains("Q1", circuit.NotReentrantReason);
    }
}
