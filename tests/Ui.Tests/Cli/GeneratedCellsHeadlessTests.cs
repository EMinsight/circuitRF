// ================================================================
//  GeneratedCellsHeadlessTests.cs — brief-generated-cells-2-headless-regeneration.md §4.
//
//  A placed PCell's artwork lives in `.generated-cells/`, a cache nothing commits. Until this brief a
//  headless verb resolved one only if a GUI session had already written it, and otherwise dropped the
//  instance with a warning and went on to answer for a board with parts missing. These gates run the
//  CLI as a PROCESS — the exit code and "wrote nothing" are half of what is asserted — on the shape
//  that was reported: a board whose parts are built-in `smt:` land patterns, with the folder deleted.
//
//  The board is built so the land pattern is LOAD-BEARING: the rail's copper is cut under one pad,
//  and only that pad joins the two halves. A run that skipped the cell would therefore not be a
//  slightly different number; it would be a different circuit.
// ================================================================

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Layout.PCells;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

[Collection(PCellResolverCollection.Name)]
public sealed class GeneratedCellsHeadlessTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-gc2-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private const string Footprint = "smt:0805@N";

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);

    // ══ Gate 1 — the round-9 shape, with the folder deleted ═══════════════════════════════════════

    /// <summary>
    /// R-gc2-2. <c>rail</c> gives the same answer with the folder deleted as with it present, and —
    /// being a verb allowed to write — puts back the same bytes the application's own placement wrote.
    /// </summary>
    [Fact]
    public void Rail_WithTheFolderDeleted_GivesTheSameAnswer_AndRewritesTheSameCell()
    {
        var fx = Board();
        var present = RunCli("rail", fx.Crail, "--json");
        Assert.True(present.ExitCode == 0, present.StdErr);

        var written = Snapshot(fx.CellDir);
        Directory.Delete(fx.GeneratedRoot, recursive: true);

        var absent = RunCli("rail", fx.Crail, "--json");
        Assert.True(absent.ExitCode == 0, absent.StdErr);
        Assert.DoesNotContain("does not resolve", absent.StdErr, StringComparison.Ordinal);
        Assert.Equal(Result(present.StdOut), Result(absent.StdOut));

        // Written as the application writes it — the same folder, byte for byte.
        Assert.Equal(written, Snapshot(fx.CellDir));
    }

    /// <summary>
    /// R-gc2-2's read-only half. <c>render</c>, <c>check</c> and <c>lvs</c> see the part with the folder
    /// deleted exactly as with it present — and leave the folder deleted.
    /// </summary>
    [Fact]
    public void RenderCheckAndLvs_WithTheFolderDeleted_AnswerTheSame_AndWriteNothing()
    {
        var fx = Board();
        string svgPresent = Path.Combine(_root, "present.svg"), svgAbsent = Path.Combine(_root, "absent.svg");

        var renderPresent = RunCli("render", fx.Clay, "-o", svgPresent);
        var checkPresent  = RunCli("check", fx.Clay, "--json");
        var lvsPresent    = RunCli("lvs", fx.CellFolder, "--json");
        Assert.True(renderPresent.ExitCode == 0, renderPresent.StdErr);

        Directory.Delete(fx.GeneratedRoot, recursive: true);

        var renderAbsent = RunCli("render", fx.Clay, "-o", svgAbsent);
        var checkAbsent  = RunCli("check", fx.Clay, "--json");
        var lvsAbsent    = RunCli("lvs", fx.CellFolder, "--json");

        Assert.True(renderAbsent.ExitCode == 0, renderAbsent.StdErr);
        Assert.Equal(NormalizeSvg(File.ReadAllText(svgPresent)), NormalizeSvg(File.ReadAllText(svgAbsent)));

        Assert.Equal(checkPresent.ExitCode, checkAbsent.ExitCode);
        Assert.Equal(Findings(checkPresent.StdOut), Findings(checkAbsent.StdOut));

        Assert.Equal(lvsPresent.ExitCode, lvsAbsent.ExitCode);
        Assert.Equal(Result(lvsPresent.StdOut), Result(lvsAbsent.StdOut));

        Assert.False(Directory.Exists(fx.GeneratedRoot), "a read-only verb wrote the generated-cell folder");
    }

    // ══ R-gc2-3 — what cannot be rebuilt is a refusal ═════════════════════════════════════════════

    /// <summary>
    /// A placed cell that is missing and has no snapshot to rebuild from: <c>render</c> refuses with
    /// the cell named and exits 1; <c>check</c> reports it as an ERROR finding, not a note.
    /// </summary>
    [Fact]
    public void AMissingCellThatCannotBeRebuilt_IsARefusal_AndACheckError()
    {
        var fx = Board();
        var view = LayoutPersistence.LoadFromFile(fx.Clay);
        view.PCellSnapshots.Clear();
        LayoutPersistence.SaveToFile(fx.Clay, view);
        Directory.Delete(fx.GeneratedRoot, recursive: true);

        var render = RunCli("render", fx.Clay, "-o", Path.Combine(_root, "x.svg"), "--json");
        Assert.Equal(1, render.ExitCode);
        Assert.Contains(fx.CellName, render.StdErr, StringComparison.Ordinal);
        Assert.Contains("cli.generated-cell.unbuildable", render.StdOut, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "x.svg")));

        var check = RunCli("check", fx.Clay, "--json");
        Assert.Equal(1, check.ExitCode);
        Assert.Contains("check.generated-cell.unbuildable", check.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// R-gc2-3's GUI half: the application keeps its placeholder, and its Messages line for the same
    /// missing cell is the sentence the headless refusal prints.
    /// </summary>
    [Fact]
    public void TheApplicationsMessage_IsTheRefusalsSentence()
    {
        var fx = Board();
        var view = LayoutPersistence.LoadFromFile(fx.Clay);
        var snap = view.PCellSnapshots[fx.CellName];
        view.PCellSnapshots[fx.CellName] = snap with { GeneratorId = "NO-SUCH-GENERATOR" };
        Directory.Delete(fx.GeneratedRoot, recursive: true);

        var said = new List<string>();
        GeneratedCellsLifecycle.Regenerate(fx.Ws, view, _ => null, said.Add);

        using var run = GeneratedCellsRun.Prepare(view, fx.Clay);
        Assert.Equal(Assert.Single(run.Unbuildable).Sentence, Assert.Single(said));
    }

    // ══ Gate 2 — a kit's PCell, under trust ═══════════════════════════════════════════════════════

    /// <summary>
    /// With the folder deleted, a kit's cell is rebuilt only under trust. Without it the run is
    /// refused NAMING the kit and the flag; with <c>--trust-kit</c> the folder the CLI writes is the
    /// one the application's own placement wrote, byte for byte.
    /// </summary>
    [PythonFact]
    public void AKitCell_IsRefusedWithoutTrust_AndRebuiltIdenticallyWithIt()
    {
        var fx = Board(kit: true);
        var written = Snapshot(fx.CellDir);
        Directory.Delete(fx.GeneratedRoot, recursive: true);

        var refused = RunCli("render", fx.Clay, "-o", Path.Combine(_root, "k.svg"));
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains(PCellTrustStore.Normalize(fx.KitDir!), refused.StdErr, StringComparison.Ordinal);
        Assert.Contains("--trust-kit", refused.StdErr, StringComparison.Ordinal);

        var trusted = RunCli("netlist", fx.Clay, "--ipc", Path.Combine(_root, "board.ipc"), "--trust-kit", fx.KitDir!);
        Assert.True(trusted.ExitCode == 0, trusted.StdErr);
        Assert.Equal(written, Snapshot(fx.CellDir));
    }

    // ══ serve asks the PERSON, through MCP elicitation ═════════════════════════════════════════════

    /// <summary>
    /// A client that can ask is asked — once — and an Allow rebuilds the kit's cell. The second call
    /// in the same session is not asked again.
    /// </summary>
    [PythonFact]
    public void Serve_AsksTheUser_AndAnAllowRebuildsTheCell_OncePerSession()
    {
        var fx = Board(kit: true);
        Directory.Delete(fx.GeneratedRoot, recursive: true);

        using var serve = new ServeClient(_root, canAsk: true);
        var first  = serve.Render(fx.Clay, Path.Combine(_root, "a.svg"), answer: (true, "accept"));
        var second = serve.Render(fx.Clay, Path.Combine(_root, "b.svg"), answer: (true, "accept"));

        Assert.False(first.IsError, first.Document);
        Assert.False(second.IsError, second.Document);
        var question = Assert.Single(serve.Questions);
        Assert.Contains(PCellTrustStore.Normalize(fx.KitDir!), question, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fx.GeneratedRoot), "render is read-only through the server too");
    }

    /// <summary>A decline is a refusal naming the kit, and is not asked again in the session.</summary>
    [Fact]
    public void Serve_ADeclineIsARefusal_AndIsNotAskedAgain()
    {
        var fx = KitBoardNeverGenerated();

        using var serve = new ServeClient(_root, canAsk: true);
        var first  = serve.Render(fx.Clay, Path.Combine(_root, "a.svg"), answer: (false, "decline"));
        var second = serve.Render(fx.Clay, Path.Combine(_root, "b.svg"), answer: (false, "decline"));

        Assert.True(first.IsError);
        Assert.True(second.IsError);
        Assert.Single(serve.Questions);
        Assert.Contains("cli.generated-cell.unbuildable", second.Document, StringComparison.Ordinal);
        Assert.Contains("was not allowed to run when circuitRF asked", second.Document, StringComparison.Ordinal);
    }

    /// <summary>A client that did not declare elicitation is never sent the question.</summary>
    [Fact]
    public void Serve_AClientThatCannotAsk_IsNeverAsked()
    {
        var fx = KitBoardNeverGenerated();

        using var serve = new ServeClient(_root, canAsk: false);
        var run = serve.Render(fx.Clay, Path.Combine(_root, "a.svg"), answer: (true, "accept"));

        Assert.True(run.IsError);
        Assert.Empty(serve.Questions);
        Assert.Contains("cannot put the question to its user", run.Document, StringComparison.Ordinal);
    }

    // ── the board ─────────────────────────────────────────────────────────────

    private sealed record Fx(string Ws, string CellFolder, string Clay, string Crail, string CellDir, string? KitDir)
    {
        public string GeneratedRoot => Path.Combine(Ws, GeneratedCellStore.ReservedFolderName);
        public string CellName => Path.GetFileName(CellDir);
    }

    /// <summary>
    /// A two-layer strip whose TOP copper is cut under pad 1 of one placed land pattern — so only the
    /// pad joins the two halves — with the snapshot a placement records. <paramref name="kit"/> places
    /// a kit script's cell instead, drawn the same way.
    /// </summary>
    private Fx Board(bool kit = false)
    {
        string ws = Path.Combine(_root, "Board");
        Directory.CreateDirectory(Path.Combine(ws, "tech"));
        const string techRef = "tech/Board.ctech";
        var tech = Tech();
        TechPersistence.SaveToFile(Path.Combine(ws, "tech", "Board.ctech"), tech);
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = techRef });

        string cellFolder = CircuitRF.Design.Cells.CellFolder.CreateCellFolder(ws, "Panel");
        string layoutDir = Path.Combine(cellFolder, "layout");
        string clay = Path.Combine(layoutDir, "Panel.clay");

        string? kitDir = kit ? WriteKit(ws) : null;
        string generator = kit ? "PADCELL" : Footprint;
        var parameters = new Dictionary<string, PCellValue>();

        PCellWorkerResolver? resolver = null;
        if (kit)
        {
            resolver = new PCellWorkerResolver(ws,
                (_, _) => new PythonInterpreter(PythonRunner.Interpreter ?? "python3", [], "test", "supplied by the test"));
            PCellRegistry.AddResolver(resolver);
        }

        string cellDir;
        try
        {
            // What a placement does: GetOrCreate, then RecordSnapshot.
            cellDir = GeneratedCellStore.GetOrCreate(ws, generator, parameters, tech, techRef, PCellLayerSelection.Default);
        }
        finally
        {
            PCellRegistry.RemoveResolver(resolver);
            resolver?.Dispose();
        }

        var cell = LayoutPersistence.LoadFromFile(Path.Combine(cellDir, "layout", Path.GetFileName(cellDir) + ".clay"));
        var pad = cell.Shapes.OfType<RectShape>().FirstOrDefault(s => s.Pin == "1")
               ?? cell.Shapes.OfType<RectShape>().First(); // the kit's cell is one pad

        long ix = Mm(15), iy = Mm(0.2);
        long cut = (pad.X1 + pad.X2) / 2 + ix, halfGap = Mm(0.1);
        var view = new LayoutView { DbuPerMicron = Dbu, TechRef = "../../tech/Board.ctech" };
        view.Shapes.Add(new RectShape { Layer = pad.Layer, X1 = 0,             Y1 = 0, X2 = cut - halfGap, Y2 = Mm(0.4) });
        view.Shapes.Add(new RectShape { Layer = pad.Layer, X1 = cut + halfGap, Y1 = 0, X2 = Mm(30),        Y2 = Mm(0.4) });
        view.Shapes.Add(new RectShape { Layer = Bot, X1 = 0, Y1 = Mm(-2), X2 = Mm(30), Y2 = Mm(2) });
        view.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(layoutDir, cellDir), X = ix, Y = iy, Mag = 1.0, SchematicId = "C1",
        });
        GeneratedCellStore.RecordSnapshot(view, cellDir, generator, parameters, techRef, PCellLayerSelection.Default, ws);
        LayoutPersistence.SaveToFile(clay, view);

        // A schematic view too, so `lvs` compares the cell rather than skipping it.
        string schematicDir = Path.Combine(cellFolder, "schematic");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        var c1 = new EditableComponent { InstanceName = "C1", Symbol = SymbolKind.Capacitor };
        c1.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = Footprint });
        model.Components.Add(c1);
        SchematicPersistence.SaveToFile(Path.Combine(schematicDir, "Panel.csch"), model, "Panel");

        string crail = Path.Combine(cellFolder, "Panel.crail");
        var doc = new RailDocument { Name = "Panel", ArtworkCellRef = Path.GetRelativePath(cellFolder, clay) };
        var vdd = new RailSpec { Name = "VDD", ReferenceLayer = Bot };
        vdd.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Point = (Mm(0.2), Mm(0.2)) }, OpenCircuitVoltageV = 3.3, SeriesResistanceOhms = 0.05,
        });
        vdd.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Point = (Mm(29.8), Mm(0.2)) }, DcCurrentA = 0.5 });
        doc.Rails.Add(vdd);
        RailDocumentIo.SaveToFile(crail, doc);

        CellLayoutResolver.InvalidateUnder(ws);
        return new Fx(ws, cellFolder, clay, crail, cellDir, kitDir);
    }

    /// <summary>
    /// A layout placing a kit's cell that was never generated — the manifest is there and the script
    /// is never run, so this needs no Python: whatever the trust answer, a missing kit cell is asked
    /// about before anything starts.
    /// </summary>
    private Fx KitBoardNeverGenerated()
    {
        string ws = Path.Combine(_root, "Board");
        Directory.CreateDirectory(Path.Combine(ws, "tech"));
        TechPersistence.SaveToFile(Path.Combine(ws, "tech", "Board.ctech"), Tech());
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech/Board.ctech" });
        string kitDir = WriteKit(ws);

        string cellFolder = CircuitRF.Design.Cells.CellFolder.CreateCellFolder(ws, "Panel");
        string layoutDir = Path.Combine(cellFolder, "layout");
        string clay = Path.Combine(layoutDir, "Panel.clay");
        string cellDir = Path.Combine(ws, GeneratedCellStore.ReservedFolderName, "PADCELL_000000000000");

        var view = new LayoutView { DbuPerMicron = Dbu, TechRef = "../../tech/Board.ctech" };
        view.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(layoutDir, cellDir), Mag = 1.0 });
        GeneratedCellStore.RecordSnapshot(view, cellDir, "PADCELL", new Dictionary<string, PCellValue>(),
                                          "tech/Board.ctech", PCellLayerSelection.Default, ws);
        LayoutPersistence.SaveToFile(clay, view);
        return new Fx(ws, cellFolder, clay, "", cellDir, kitDir);
    }

    private static Technology Tech()
    {
        var tech = new Technology { Name = "Board" };
        tech.Layers =
        [
            new LayerDef { Key = Top, Name = "TOP", ZOrder = 0, Color = new Rgba(200, 80, 40, 255) },
            new LayerDef { Key = Bot, Name = "BOT", ZOrder = 1, Color = new Rgba(40, 90, 200, 255) },
        ];
        tech.Stackup.Layers =
        [
            new StackupLayer { Kind = StackupKind.Conductor, Name = "TOP", ThicknessDbu = 35 * Dbu, SigmaSm = 5.8e7, DrawingLayers = [Top] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = Mm(1.6), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT", ThicknessDbu = 35 * Dbu, SigmaSm = 5.8e7,
                DrawingLayers = [Bot], IsGroundReference = true,
            },
        ];
        return tech;
    }

    /// <summary>A kit whose one generator draws a single 1 mm x 1.4 mm pad on the signal layer.</summary>
    private static string WriteKit(string ws)
    {
        string dir = Path.Combine(ws, "padkit");
        Directory.CreateDirectory(dir);
        string package = PythonRunner.PackageRoot;
        File.WriteAllText(Path.Combine(dir, "main.py"), $"""
            import sys
            sys.path.insert(0, r'{package}')
            from circuitrf_pcell import Layer, Rect, Result, generator, run

            @generator("PADCELL", [])
            def padcell(params, tech):
                layer = tech.signal_layer or Layer(1, 0)
                return Result(shapes=[Rect(layer, -500000, -700000, 500000, 700000)], pins=[])

            run()
            """);
        File.WriteAllText(Path.Combine(dir, PCellGeneratorManifest.FileName),
            $$"""{ "schemaVersion": 1, "entry": "main.py", "pythonPath": [{{JsonSerializer.Serialize(package)}}] }""");
        return dir;
    }

    // ── comparing ─────────────────────────────────────────────────────────────

    /// <summary>Every file of a cell folder, relative path → bytes as text.</summary>
    private static SortedDictionary<string, string> Snapshot(string cellDir)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string f in Directory.EnumerateFiles(cellDir, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(cellDir, f).Replace('\\', '/')] = Convert.ToBase64String(File.ReadAllBytes(f));
        return files;
    }

    /// <summary>The document's result, which is the answer — without the elapsed time, which is not.</summary>
    private static string Result(string stdout)
    {
        var node = JsonNode.Parse(stdout)!["result"]!;
        Strip(node);
        return node.ToJsonString();

        static void Strip(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                foreach (string k in o.Select(p => p.Key).Where(k => k.Contains("elapsed", StringComparison.OrdinalIgnoreCase)
                                                                   || k.Contains("seconds", StringComparison.OrdinalIgnoreCase)).ToList())
                    o.Remove(k);
                foreach (var (_, v) in o) Strip(v);
            }
            else if (n is JsonArray a) foreach (var v in a) Strip(v);
        }
    }

    private static string Findings(string stdout)
        => JsonNode.Parse(stdout)!["diagnostics"]?.ToJsonString() ?? "";

    /// <summary>Skia numbers its clipPath ids from a process-wide counter — RenderCliVerbTests' one
    /// stated exclusion, applied here for its reason.</summary>
    private static string NormalizeSvg(string svg) => Regex.Replace(svg, @"clip\d+", "clip");

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = _root,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        // A state directory of the test's own, so no recorded decision on this machine can grant a kit.
        psi.Environment[UserStateDirectory.EnvironmentVariable] = Path.Combine(_root, "state");
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        var run = (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
        output.WriteLine($"$ circuitrf {string.Join(' ', args)} -> {run.ExitCode}\n{run.Item3}");
        return run;
    }

    /// <summary>
    /// A minimal MCP client over a real <c>circuitrf serve</c> process: it declares (or not) that it can
    /// ask its user, answers each <c>elicitation/create</c> with the answer the test chose, and records
    /// every question it was sent.
    /// </summary>
    private sealed class ServeClient : IDisposable
    {
        private readonly Process _proc;
        private readonly System.Collections.Concurrent.BlockingCollection<string> _frames = new();
        private readonly StringBuilder _stderr = new();
        private int _id;

        public List<string> Questions { get; } = [];

        public ServeClient(string root, bool canAsk)
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.Environment[UserStateDirectory.EnvironmentVariable] = Path.Combine(root, "state");
            psi.ArgumentList.Add(CliDll());
            psi.ArgumentList.Add("serve");
            psi.ArgumentList.Add("--root");
            psi.ArgumentList.Add(root);
            _proc = Process.Start(psi)!;

            Task.Factory.StartNew(() =>
            {
                string? line;
                while ((line = _proc.StandardOutput.ReadLine()) is not null) _frames.Add(line);
                _frames.CompleteAdding();
            }, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(() =>
            {
                string? line;
                while ((line = _proc.StandardError.ReadLine()) is not null) lock (_stderr) _stderr.AppendLine(line);
            }, TaskCreationOptions.LongRunning);

            var capabilities = new JsonObject();
            if (canAsk) capabilities["elicitation"] = new JsonObject();
            Call("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18", ["capabilities"] = capabilities,
                ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
            }, answer: default);
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        }

        public (bool IsError, string Document) Render(string clay, string svg, (bool Allow, string Action) answer)
        {
            var result = Call("tools/call", new JsonObject
            {
                ["name"] = "render",
                ["arguments"] = new JsonObject { ["path"] = clay, ["output"] = svg },
            }, answer);
            return (result["isError"]?.GetValue<bool>() == true, result["content"]![0]!["text"]!.GetValue<string>());
        }

        private JsonObject Call(string method, JsonObject parameters, (bool Allow, string Action) answer)
        {
            int id = ++_id;
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            while (true)
            {
                string err;
                lock (_stderr) err = _stderr.ToString();
                Assert.True(_frames.TryTake(out string? line, TimeSpan.FromMinutes(2)), $"serve fell silent.\n{err}");
                var frame = (JsonObject)JsonNode.Parse(line!)!;

                if (frame["method"]?.GetValue<string>() == "elicitation/create")
                {
                    Questions.Add(frame["params"]!["message"]!.GetValue<string>());
                    var reply = new JsonObject { ["action"] = answer.Action };
                    if (answer.Action == "accept") reply["content"] = new JsonObject { ["allow"] = answer.Allow };
                    Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = frame["id"]!.DeepClone(), ["result"] = reply });
                    continue;
                }
                if (frame["id"] is JsonValue v && v.TryGetValue<int>(out int got) && got == id)
                    return (JsonObject)frame["result"]!;
            }
        }

        private void Send(JsonObject message)
        {
            _proc.StandardInput.Write(message.ToJsonString());
            _proc.StandardInput.Write('\n');
            _proc.StandardInput.Flush();
        }

        public void Dispose()
        {
            try { _proc.StandardInput.Close(); } catch { /* gone */ }
            if (!_proc.WaitForExit(30_000)) try { _proc.Kill(entireProcessTree: true); } catch { /* gone */ }
            _proc.Dispose();
        }
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(GeneratedCellsHeadlessTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }
}
