using System.Diagnostics;
using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Optimization;
using CircuitRF.Ui.Tests.OptimizerPanel;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>brief-tuneopt-11 §3: the <c>opt</c> verb as a process, the MCP <c>run analysis=optimize</c>, the
/// panel parity and the generated reference pages. The circuits are the closed-form L-section and pad of
/// <see cref="OptCircuits"/>, so every run is a few dozen one-point S-parameter solves.</summary>
internal static class OptCli
{
    public static string Dir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-opt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string Write(string dir, string name, string text)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    public static (int Exit, string StdOut, string StdErr) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    /// <summary>The <c>result.optimize</c> object of a <c>--json</c> document.</summary>
    public static JsonObject Report(string json) => JsonNode.Parse(json)!["result"]!["optimize"]!.AsObject();

    public static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(OptCli).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    /// <summary>The pad of <see cref="InfeasibleGoalTests"/>: |S21| ≥ 2 from a series resistor.</summary>
    public const string Pad = """
        Port:P1 in  0 Num=1 Z=50 Ohm
        R:R1    in  out R=10 Ohm
        Port:P2 out 0 Num=2 Z=50 Ohm
        analysis SP1 type=sparam start=1 stop=2 npts=11 Unit=GHz
        tune R1.R min=1 Ohm max=100 Ohm opt=1
        goal Gain = mag(SP1.S(2,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz ge 2
        optimize algorithm=simplex maxiter=30
        """;

    /// <summary>The L-section's load as a complex VAR, optimized by magnitude and phase (D18).</summary>
    public const string ComplexLoad = """
        ZL = 80+0j Ohm
        Port:P1 in  0 Num=1 Z=50 Ohm
        L:L1    in  out L=10 nH
        C:C1    out 0   C=1 pF
        Port:P2 out 0 Num=2 Z=ZL
        analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz
        tune mag(ZL) min=20 Ohm max=300 Ohm opt=1
        tune phase(ZL) min=-80 deg max=80 deg opt=1
        goal Match = mag(SP1.S(1,1)) analysis=SP1 le 0.0001
        optimize algorithm=lm
        """;
}

/// <summary>R-to11-1…3, R-to11-8: the verb as a process.</summary>
public sealed class OptCliVerbTests
{
    [Fact]
    public void LSection_ExitsZero_AtTheAnalyticValues()
    {
        string cnl = OptCli.Write(OptCli.Dir(), "lsec.cnl", OptCircuits.LSection("lm", "maxiter=300"));
        var (exit, stdout, stderr) = OptCli.Run("opt", cnl, "--json");
        Assert.True(exit == 0, stderr);

        var r = OptCli.Report(stdout);
        Assert.Equal("goalsMet", r["outcome"]!.GetValue<string>());
        double Si(string key) => TunableValue.TryParse(
            r["variables"]!.AsArray().Single(v => v!["key"]!.GetValue<string>() == key)!["best"]!.GetValue<string>(),
            out _, out _, out double si) ? si : double.NaN;
        Assert.Equal(OptCircuits.LAnalytic, Si("L1.L"), OptCircuits.LAnalytic * 0.01);
        Assert.Equal(OptCircuits.CAnalytic, Si("C1.C"), OptCircuits.CAnalytic * 0.01);

        // The final result only, unless each iteration is asked for.
        Assert.Null(r["perIteration"]);
        Assert.DoesNotContain("goals met", stderr);
        var (_, shown, shownErr) = OptCli.Run("opt", cnl, "--show-iterations", "--json");
        var each = OptCli.Report(shown)["perIteration"]!.AsArray();
        Assert.Equal(OptCli.Report(shown)["iterations"]!.GetValue<int>(), each.Count);
        Assert.Equal(each.Count, shownErr.Split('\n').Count(l => l.StartsWith("iter ", StringComparison.Ordinal)));
    }

    [Fact]
    public void AnUnreachableGoal_ExitsThree_NamingIt()
    {
        string cnl = OptCli.Write(OptCli.Dir(), "pad.cnl", OptCli.Pad);
        var (exit, stdout, _) = OptCli.Run("opt", cnl);
        Assert.Equal(3, exit);
        Assert.Matches(@"(?m)^  Gain\s+NO\s", stdout);
    }

    [Fact]
    public void SavePreset_OnASchematic_AddsOnePreset_AndChangesNothingElse()
    {
        string dir = OptCli.Dir();
        string cnl = OptCli.Write(dir, "lsec.cnl", OptCircuits.LSection("lm", "maxiter=300"));
        string csch = Path.Combine(dir, "lsec.csch");
        Assert.Equal(0, OptCli.Run("netlist", cnl, "--to-schematic", "-o", csch).Exit);
        string before = File.ReadAllText(csch);

        var (exit, stdout, stderr) = OptCli.Run("opt", csch, "--save-preset", "matched", "--json");
        Assert.True(exit == 0, stderr);

        var (model, view, cell) = SchematicPersistence.LoadFromFile(csch);
        var preset = Assert.Single(model.Tuning!.Presets);
        Assert.Equal("matched", preset.Name);
        foreach (var v in OptCli.Report(stdout)["variables"]!.AsArray())
            Assert.Equal(v!["best"]!.GetValue<string>(), preset.Values[v["key"]!.GetValue<string>()]);

        // Without the preset, the file is byte for byte what it was.
        model.Tuning.Presets.Clear();
        Assert.Equal(before, SchematicPersistence.Serialize(model, cell, view.PanX, view.PanY, view.Zoom));

        // A netlist is text the caller writes: the preset is a line to add, so the flag is refused.
        var refused = OptCli.Run("opt", cnl, "--save-preset", "x");
        Assert.Equal(1, refused.Exit);
        Assert.Contains("Add a preset line", refused.StdErr);
    }

    [Fact]
    public void AComplexLoad_PrintsTheWholeBestValue_AndVarsRefusesTheWholeKey()
    {
        string cnl = OptCli.Write(OptCli.Dir(), "zl.cnl", OptCli.ComplexLoad);
        var (exit, stdout, stderr) = OptCli.Run("opt", cnl, "--vars", "mag(ZL),phase(ZL)", "--json");
        Assert.True(exit == 0, stderr);
        var mag = OptCli.Report(stdout)["variables"]!.AsArray().Single(v => v!["key"]!.GetValue<string>() == "mag(ZL)")!;
        string whole = mag["whole"]!.GetValue<string>();
        Assert.True(ComplexValue.TryParse(whole, out _, out string unit, out _), whole);
        Assert.Equal("Ohm", unit);
        Assert.Equal("80+0j Ohm", mag["wholeStart"]!.GetValue<string>());

        // The text table says the same: one line per value, start → best.
        Assert.Contains($"ZL  80+0j Ohm → {whole}", OptCli.Run("opt", cnl).StdOut);

        var refused = OptCli.Run("opt", cnl, "--vars", "ZL");
        Assert.Equal(1, refused.Exit);
        Assert.Contains("real(ZL), imag(ZL), mag(ZL), phase(ZL)", refused.StdErr);
    }
}

/// <summary>R-to11-4: the verb and the Optimizer panel's headless view model run one code path — same
/// setup, same best values, cost and evaluation count, exactly. The circuit is the Optimization example's
/// L-section (TO-12), run with the algorithm and seed its schematic saves.</summary>
public sealed class OptParityTests
{
    [Fact]
    public async Task TheVerb_AndThePanel_FindTheSameBestPoint()
    {
        string csch = Examples.OptimizationExampleTests.Csch("LSectionMatch");
        string text = SchematicCircuit.CnlTextOf(csch);
        var (exit, stdout, stderr) = OptCli.Run("opt", csch, "--json");
        Assert.True(exit is 0 or 3, stderr);
        var verb = OptCli.Report(stdout);

        var setup = PreparedCircuit.FromText(text, null, null).Tb!.Tuning!;
        var f = new OptimizerPanelFixture(text, setup, Array.Empty<EditableComponent>());
        f.Panel.RunCommand.Execute(null);
        await f.Panel.RunTask!.WaitAsync(TimeSpan.FromSeconds(60));
        var panel = f.Panel.Result!;

        Assert.Equal(panel.BestCost, verb["bestCost"]!.GetValue<double>());
        Assert.Equal(panel.Evaluations, verb["evaluations"]!.GetValue<long>());
        foreach (var v in verb["variables"]!.AsArray())
            Assert.Equal(panel.BestValues[v!["key"]!.GetValue<string>()], v["best"]!.GetValue<string>());
    }
}

/// <summary>R-to11-5: <c>run analysis=optimize</c> over the protocol returns the verb's <c>--json</c>
/// object — the final result only, unless <c>showIterations</c> asks for each iteration, which also sends a
/// progress notification per iteration — and a cancelled call answers 130.</summary>
public sealed class OptMcpTests
{
    [Fact]
    public void RunOptimize_ReturnsTheJsonObject_ReportsProgress_AndCancels()
    {
        string root = OptCli.Dir();
        string cnl = OptCli.Write(root, "lsec.cnl", OptCircuits.LSection("lm", "maxiter=300"));
        var expected = OptCli.Report(OptCli.Run("opt", cnl, "--json").StdOut);

        using var server = new Serve(root);
        server.Request("initialize", new JsonObject());
        JsonObject Call(bool show) => new()
        {
            ["name"] = "run",
            ["arguments"] = show ? new JsonObject { ["analysis"] = "optimize", ["path"] = cnl, ["showIterations"] = true }
                                 : new JsonObject { ["analysis"] = "optimize", ["path"] = cnl },
            ["_meta"] = new JsonObject { ["progressToken"] = "opt-1" },
        };
        List<JsonObject> Progress() =>
            [.. server.Frames.Where(f => f["method"]?.GetValue<string>() == "notifications/progress")];

        var document = JsonNode.Parse(server.Await(server.Send("tools/call", Call(false)))["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(0, document["exitCode"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(expected, document["result"]!["optimize"]));
        Assert.Empty(Progress());                                              // the final result only

        int shown  = server.Send("tools/call", Call(true));
        int second = server.Send("tools/call", Call(false));
        server.Notify("notifications/cancelled", new JsonObject { ["requestId"] = second });

        var each = JsonNode.Parse(server.Await(shown)["result"]!["content"]![0]!["text"]!.GetValue<string>())!
            ["result"]!["optimize"]!["perIteration"]!.AsArray();
        var progress = Progress();
        Assert.Equal(each.Count, progress.Count);
        Assert.All(progress, p => Assert.Contains("goals met", p["params"]!["message"]!.GetValue<string>()));

        var stopped = JsonNode.Parse(server.Await(second)["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(130, stopped["exitCode"]!.GetValue<int>());
        Assert.Empty(stopped["outputs"]!.AsArray());
    }

    /// <summary>A bare <c>serve</c> process: requests out, every frame in.</summary>
    private sealed class Serve : IDisposable
    {
        private readonly Process _proc;
        private readonly System.Collections.Concurrent.BlockingCollection<JsonObject> _frames = new();
        private readonly List<JsonObject> _seen = [];
        private int _id;

        public Serve(string root)
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { OptCli.CliDll(), "serve", "--root", root }) psi.ArgumentList.Add(a);
            _proc = Process.Start(psi)!;
            Task.Factory.StartNew(() =>
            {
                string? line;
                while ((line = _proc.StandardOutput.ReadLine()) is not null)
                    if (JsonNode.Parse(line) is JsonObject o) { lock (_seen) _seen.Add(o); _frames.Add(o); }
                _frames.CompleteAdding();
            }, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(() => _proc.StandardError.ReadToEnd(), TaskCreationOptions.LongRunning);
        }

        public IReadOnlyList<JsonObject> Frames { get { lock (_seen) return [.. _seen]; } }

        public int Send(string method, JsonObject parameters)
        {
            int id = ++_id;
            Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            return id;
        }

        public void Notify(string method, JsonObject parameters)
            => Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

        public JsonObject Request(string method, JsonObject parameters) => Await(Send(method, parameters));

        public JsonObject Await(int id)
        {
            while (true)
            {
                Assert.True(_frames.TryTake(out var frame, TimeSpan.FromMinutes(2)), "the server fell silent");
                if (frame!["id"] is { } got && got.GetValue<int>() == id) return frame;
            }
        }

        private void Write(JsonObject message)
        {
            _proc.StandardInput.WriteLine(message.ToJsonString());
            _proc.StandardInput.Flush();
        }

        public void Dispose()
        {
            try { _proc.StandardInput.Close(); _proc.WaitForExit(10_000); } catch { /* gone */ }
            try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* gone */ }
        }
    }
}

/// <summary>R-to11-6: the goals page is generated — from the directive schema and from the Optimizer's
/// own template catalog — so a key or a template added there appears with nothing to edit. (The
/// optimizers page is <see cref="AlgorithmRegistryTests"/>'.)</summary>
public sealed class OptReferenceTests
{
    [Fact]
    public void TheGoalsPage_ListsEverySchemaKey_AndEveryTemplate()
    {
        string page = CircuitRF.Cli.Reference.RenderTuning(AnalysisDirectiveSchema.GoalsTopic);
        foreach (var k in AnalysisDirectiveSchema.TuningDirectives.Single(d => d.Keyword == "goal").Keys)
            Assert.Contains(k.Name, page);
        Assert.Contains("1/sqrt(points)", page);                                 // the normalization rule

        var (_, bench) = new CnlReader().Read(string.Join('\n', CircuitRF.Cli.Reference.FunctionsBench), "tb", null);
        var templates = GoalTemplates.For(bench);
        Assert.Contains(templates, t => t.Group == GoalTemplateGroup.WsProbe);
        Assert.Contains(templates, t => t.Id == "meas.Eff" && t.Analysis == "HB1");
        foreach (var t in templates)
        {
            Assert.Contains(t.Label, page);
            if (t.Make().Expression is { Length: > 0 } expr) Assert.Contains($"= {expr}", page);
        }

        string tuning = CircuitRF.Cli.Reference.RenderTuning(AnalysisDirectiveSchema.TuningTopic);
        foreach (var d in AnalysisDirectiveSchema.TuningDirectives.Where(d => d.Topic == AnalysisDirectiveSchema.TuningTopic))
            foreach (var k in d.Keys) Assert.Contains(k.Name, tuning);
        Assert.Contains("circuitrf opt", tuning);
    }
}
