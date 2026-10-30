// ================================================================
//  CapacitanceExamplesAuthoring.cs — brief-dfr31-2: the setups of the two capacitance examples, made through the setup
//  panel's own view model (a terminal row, its Pick, a click on the metal, the ground picker), and Test Pad Capacitance's
//  boards. Skipped unless CRF_AUTHOR_CAPACITANCE=1, because it WRITES examples/Capacitance Extraction/Pad and Line/3d/ and
//  examples/Test Pad Capacitance/{As built,Fixed}/. Kept, not deleted, so the files can be made again after a format change,
//  and so the clicks the READMEs describe are the ones used.
//
//  Pad and Line's geometry is drawn in its .c3d and is read here; only its setups are made. Test Pad Capacitance's boards
//  are artwork as an imported board arrives: every pad drawn on its own layer, planes with their openings, no net on any
//  piece of copper, and a technology that marks no layer as ground. Each 3D view is New 3D View from Layout's.
// ================================================================

using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class CapacitanceExamplesAuthoring
{
    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_CAPACITANCE") != "1")
                Skip = "re-makes the capacitance examples' setups and boards only with CRF_AUTHOR_CAPACITANCE=1";
        }
    }

    private static string Examples => Path.Combine(PalaceBackendTests.RepoRoot(), "examples");

    // ── Capacitance Extraction ──────────────────────────────────────────────────────────────────

    [AuthorFact]
    public void PadAndLine()
    {
        string ws = Path.Combine(Examples, "Capacitance Extraction");
        string c3d = Path.Combine(ws, "Pad and Line", "3d", "Pad and Line.c3d");
        var doc = C3dPersistence.LoadFromFile(c3d);
        doc.Setups.Clear();
        var bodies = Bodies(doc, c3d, Path.Combine(ws, ".cws"));

        // A static setup on open board space: walls a zero-charge face (no field line ends on them), the line's two ends cut by
        // walls at the board's ends so the line is infinitely long, and the floor the ground plane's own face.
        const string Air = """
            "AirBox": { "XMin": { "PaddingUm": 0, "Boundary": "Pmc" }, "XMax": { "PaddingUm": 0, "Boundary": "Pmc" },
                        "YMin": { "PaddingUm": 2000, "Boundary": "Pmc" }, "YMax": { "PaddingUm": 2000, "Boundary": "Pmc" },
                        "ZMin": { "PaddingUm": 0, "Boundary": "Pec" }, "ZMax": { "PaddingUm": 5000, "Boundary": "Pmc" } }
            """;

        // Pad and line: both are terminals; the ground is the plane, picked from the ground list.
        doc.Setups.Add(Setup(c3d, "Pad and line", Air, bodies, [("pad", "pad"), ("line", "line")], ground: "ground"));
        // The pad alone, the line left as it is: in no terminal and not ground, so it floats (the default).
        doc.Setups.Add(Setup(c3d, "Pad, line floating", Air, bodies, [("pad", "pad")], ground: "ground"));
        C3dPersistence.SaveToFile(c3d, doc);
    }

    /// <summary>
    /// Pad and Line.cdd's table at its minimum size: each column fitted to its header and cells, and the box to the columns
    /// and the rows, as a double-click on the table's gripper sizes it. Needs Pad and line's result, so run that setup
    /// first (<c>circuitrf em "Pad and Line/3d/Pad and Line.c3d" --setup "Pad and line"</c>); the result is not shipped.
    /// </summary>
    [AuthorFact]
    public void PadAndLineTableAtItsMinimumSize()
    {
        string cdd = Path.Combine(Examples, "Capacitance Extraction", "Pad and Line.cdd");
        var config = JsonSerializer.Deserialize<CircuitRF.Render.DataDisplay.DataDisplayConfig>(
            File.ReadAllText(cdd), CircuitRF.Render.DataDisplay.DataDisplayJson.Options)!;
        var (sources, refusal) = CircuitRF.Cli.CddSources.Bind(cdd, config, config.Tabs, [0], []);
        Assert.True(refusal is null && sources is not null, "run Pad and line first: the display's result is missing");

        var node = JsonNode.Parse(File.ReadAllText(cdd))!;
        var plots = node["Tabs"]![0]!["Plots"]!.AsArray();
        for (int k = 0; k < config.Tabs[0].Plots.Count; k++)
        {
            var plot = CircuitRF.Render.DataDisplay.PlotConfigLoader.LoadPlot(config.Tabs[0].Plots[k], sources!);
            CircuitRF.Render.DataDisplay.PlotConfigLoader.ResolveDerived(plot, sources!);
            if (plot.PlotType != CircuitRF.Render.DataDisplay.PlotType.Table) continue;
            var traces = plots[k]!["Traces"]!.AsArray();
            var columns = CircuitRF.Render.DataDisplay.TableRenderer.BuildColumns(plot);
            for (int c = 0; c < columns.Count; c++)
            {
                double fit = Math.Ceiling(CircuitRF.Render.DataDisplay.TableRenderer.CalcFitWidth(plot, c, (1e4, 1e4)));
                int t = columns[c].FirstTraceIndex;
                if (columns[c].Kind == CircuitRF.Render.DataDisplay.TableColKind.XAxis)
                {
                    plot.Traces[t].XColumnWidth = fit;
                    traces[t]!["XColumnWidth"] = fit;
                }
                else
                {
                    plot.Traces[t].ColumnWidth = fit;
                    traces[t]!["ColumnWidth"] = fit;
                }
            }
            plots[k]!["Width"] = Math.Ceiling(CircuitRF.Render.DataDisplay.TableRenderer.TotalColumnWidth(plot));
            plots[k]!["Height"] = Math.Ceiling(CircuitRF.Render.DataDisplay.TableRenderer.RequiredCanvasHeight(plot, 1f) + 1.0);
        }
        File.WriteAllText(cdd, node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n");
    }

    // ── Test Pad Capacitance ────────────────────────────────────────────────────────────────────

    private const double BoardX = 6, BoardY = 4;               // mm, half extents
    private const double LineW = 0.38;                          // 50 Ω on Top Copper over Inner 1 (the line calculator's)
    private const double PadD = 1.5, ViaPad = 0.6, Drill = 0.3, Antipad = 1.0, PourClear = 2.5;
    private static readonly (double X, double Y)[] Stitching =
        [(-4.5, -2.5), (-1.5, -2.5), (1.5, -2.5), (4.5, -2.5), (-4.5, 2.5), (-1.5, 2.5), (1.5, 2.5), (4.5, 2.5)];

    private const int Top = 1, In1 = 10, In2 = 11, In3 = 12, In4 = 13, Bottom = 2, Outline = 8, DrillLayer = 7;

    [AuthorFact]
    public void TestPad()
    {
        string ws = Path.Combine(Examples, "Test Pad Capacitance");
        string cws = Path.Combine(ws, ".cws");
        Board(ws, "As built", innerPads: true, in4Opening: Antipad);
        Board(ws, "Fixed", innerPads: false, in4Opening: PourClear);
        Board(ws, "Line only", innerPads: false, in4Opening: Antipad, stub: false);

        const string StaticAir = """
            "AirBox": { "XMin": { "PaddingUm": 2000, "Boundary": "Pec" }, "XMax": { "PaddingUm": 2000, "Boundary": "Pec" },
                        "YMin": { "PaddingUm": 2000, "Boundary": "Pec" }, "YMax": { "PaddingUm": 2000, "Boundary": "Pec" },
                        "ZMin": { "PaddingUm": 2000, "Boundary": "Pec" }, "ZMax": { "PaddingUm": 2000, "Boundary": "Pec" } }
            """;
        foreach (string cell in new[] { "As built", "Fixed", "Line only" })
        {
            string cellDir = Path.Combine(ws, cell);
            string c3d = Path.Combine(cellDir, "3d", cell + ".c3d");
            Directory.CreateDirectory(Path.GetDirectoryName(c3d)!);
            var made = C3dHierarchy.NewFromLayout(cellDir, c3d, cws);
            Assert.True(made.Document is not null, made.Refusal);
            var doc = made.Document!;
            var bodies = Bodies(doc, c3d, cws);
            if (cell == "Line only")
            {
                // The as-built board with the stub cut away: the line's own capacitance, which the line in a schematic already is.
                doc.Setups.Add(Setup(c3d, "Line C", StaticAir, bodies, [("Line", LinePiece(bodies))], ground: null));
                C3dPersistence.SaveToFile(c3d, doc);
                continue;
            }
            string bottomPad = BottomPad(bodies);

            // The test pad's terminal is picked by clicking the bottom pad; the ground is "Every body not in a terminal".
            doc.Setups.Add(Setup(c3d, "Test pad C", StaticAir, bodies, [("TP", bottomPad)], ground: null));
            if (cell == "As built")
            {
                // The same, with the ground box left as it opens: every other piece of metal floats.
                doc.Setups.Add(Setup(c3d, "Test pad C, planes floating", StaticAir, bodies, [("TP", bottomPad)], ground: ""));
                AddDriven(doc);
            }
            doc.FieldPlots.Add(new C3dFieldPlot
            {
                Name = "E through the test pad", Setup = "Test pad C", Solution = new C3dFieldSolution { Terminal = "TP" },
                Quantity = "E", On = C3dFieldPlotOn.ClipPlane, Axis = C3dAxis.Y, Offset = 0,
            });
            C3dPersistence.SaveToFile(c3d, doc);
        }
    }

    /// <summary>The driven run of the as-built board: a lumped port at each end of the line, from the line down to Inner 1,
    /// Palace at Draft with two refinement passes, absorbing walls a millimetre beyond the board's ends.</summary>
    private static void AddDriven(C3dDocument doc)
    {
        long Um(double um) => (long)Math.Round(um * 1000);
        const double In1Top = 1347, TopBottom = 1567;          // µm above the stack's bottom
        var node = JsonNode.Parse(C3dPersistence.Serialize(doc))!;
        var ports = new JsonArray();
        foreach (var (n, x) in new[] { (1, -BoardX), (2, BoardX) })
            ports.Add(JsonNode.Parse($$"""
                { "Number": {{n}}, "Name": "P{{n}}", "Kind": "Lumped", "Plane": "YZ", "Offset": {{Um(x * 1000)}},
                  "Rect": { "Min": [{{Um(-LineW * 500)}}, {{Um(In1Top)}}], "Size": [{{Um(LineW * 1000)}}, {{Um(TopBottom - In1Top)}}] },
                  "Z0": "50" }
                """));
        node["Ports"] = ports;
        var withPorts = C3dPersistence.Deserialize(node.ToJsonString());
        doc.Ports = withPorts.Ports;
        var driven = EmSetupPersistence.Deserialize("""
            { "FormatVersion": 1, "Name": "Driven (Draft)", "Solver3D": "Palace",
              "Frequency": { "StartExpr": "0.5", "StopExpr": "10", "StepExpr": "", "NumPoints": 96, "Mode": "PointCount",
                             "Kind": "Linear", "StartUnit": "GHz", "StopUnit": "GHz", "StepUnit": "Hz" },
              "AirBox": { "XMin": { "PaddingUm": 1000, "Boundary": "Absorbing" }, "XMax": { "PaddingUm": 1000, "Boundary": "Absorbing" },
                          "YMin": { "PaddingUm": 3000, "Boundary": "Absorbing" }, "YMax": { "PaddingUm": 3000, "Boundary": "Absorbing" },
                          "ZMin": { "PaddingUm": 3000, "Boundary": "Absorbing" }, "ZMax": { "PaddingUm": 3000, "Boundary": "Absorbing" } },
              "Palace": { "Quality": "Draft", "AdaptiveMaxIterations": 2 } }
            """);
        doc.Setups.Add(EmSetupPersistence.ToEmbedded(driven));
    }

    private static void Board(string ws, string cell, bool innerPads, double in4Opening, bool stub = true)
    {
        string cellDir = Path.Combine(ws, cell);
        Directory.CreateDirectory(Path.Combine(cellDir, "layout"));
        File.WriteAllText(Path.Combine(cellDir, ".ccell"), """
            {
              "FormatVersion": 1,
              "Parameters": [],
              "IsTestBench": false,
              "Terminals": [],
              "NumPorts": 0
            }

            """);
        var shapes = new List<string>
        {
            Rect(Outline, -BoardX, -BoardY, BoardX, BoardY),
            Rect(Top, -BoardX, -LineW / 2, BoardX, LineW / 2),       // the line, through the via's pad
            Circle(Top, 0, 0, ViaPad / 2),
            Plane(Bottom, PourClear / 2),                             // the bottom ground pour, cleared round the pad
            Plane(In1, Antipad / 2), Plane(In2, Antipad / 2), Plane(In3, Antipad / 2), Plane(In4, in4Opening / 2),
        };
        if (stub)
        {
            shapes.Add(Via(0, 0));
            shapes.Add(Circle(Bottom, 0, 0, PadD / 2));                // the test pad
        }
        if (innerPads)
            foreach (int layer in new[] { In1, In2, In3, In4 })
                shapes.Add(Circle(layer, 0, 0, ViaPad / 2));          // a pad on the via, joined to nothing
        foreach (var (x, y) in Stitching)
        {
            shapes.Add(Via(x, y));
            shapes.Add(Circle(Top, x, y, ViaPad / 2));
        }
        string clay = $$"""
            { "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Mm", "SnapDbu": 10000,
              "Shapes": [ {{string.Join(",\n", shapes)}} ], "Instances": [] }
            """;
        LayoutPersistence.SaveToFile(Path.Combine(cellDir, "layout", cell + ".clay"), LayoutPersistence.Deserialize(clay));
    }

    private static long D(double mm) => (long)Math.Round(mm * 1e6);
    private static string L(int layer) => $$"""{ "Layer": {{layer}}, "Datatype": 0 }""";
    private static string Rect(int layer, double x1, double y1, double x2, double y2)
        => $$"""{ "$type": "Rect", "Layer": {{L(layer)}}, "X1": {{D(x1)}}, "Y1": {{D(y1)}}, "X2": {{D(x2)}}, "Y2": {{D(y2)}} }""";
    private static string Circle(int layer, double x, double y, double r)
        => $$"""{ "$type": "Circle", "Layer": {{L(layer)}}, "Cx": {{D(x)}}, "Cy": {{D(y)}}, "R": {{D(r)}} }""";
    private static string Via(double x, double y)
        => $$"""{ "$type": "Via", "Layer": {{L(DrillLayer)}}, "X": {{D(x)}}, "Y": {{D(y)}}, "PadSize": {{D(ViaPad)}}, "DrillSize": {{D(Drill)}} }""";

    /// <summary>A plane over the whole board with a round opening of radius <paramref name="r"/> mm at the via.</summary>
    private static string Plane(int layer, double r)
    {
        var hole = Enumerable.Range(0, 64).SelectMany(k =>
        {
            double a = 2 * Math.PI * k / 64;
            return new[] { D(r * Math.Cos(a)), D(r * Math.Sin(a)) };
        });
        return $$"""
            { "$type": "Poly", "Layer": {{L(layer)}},
              "Xy": [{{D(-BoardX)}}, {{D(-BoardY)}}, {{D(BoardX)}}, {{D(-BoardY)}}, {{D(BoardX)}}, {{D(BoardY)}}, {{D(-BoardX)}}, {{D(BoardY)}}],
              "Holes": [[{{string.Join(", ", hole)}}]] }
            """;
    }

    // ── the setup panel ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<Em3dBodyChoice> Bodies(C3dDocument doc, string c3d, string cws)
    {
        var e = new C3dElaborator().Elaborate(doc, c3d, cws);
        var bodies = Em3dBodyList.Of(doc, e);
        Assert.NotEmpty(bodies);
        return bodies;
    }

    /// <summary>The object a click on the line picks.</summary>
    private static string LinePiece(IReadOnlyList<Em3dBodyChoice> bodies)
        => bodies.SelectMany(b => b.Members).First(m => m.Contains("/Top Copper", StringComparison.Ordinal) &&
                                                          bodies.Single(b => b.Members.Contains(m)).Members.Count <= 2);

    /// <summary>The object a click on the test pad picks: the Bottom Copper piece whose body also holds the via.</summary>
    private static string BottomPad(IReadOnlyList<Em3dBodyChoice> bodies)
        => bodies.SelectMany(b => b.Members).Single(m => m.Contains("/Bottom Copper", StringComparison.Ordinal) &&
                                                          bodies.Single(b => b.Members.Contains(m)).Members.Count > 2 &&
                                                          bodies.Single(b => b.Members.Contains(m)).Members.Count < 12);

    /// <summary>
    /// One electrostatic setup made in the panel: a row per terminal, its Pick armed and the object clicked; the ground
    /// chosen from the ground list — an object's body, "Every body not in a terminal" (null), or left as it opens ("").
    /// </summary>
    private static JsonElement Setup(string c3d, string name, string air, IReadOnlyList<Em3dBodyChoice> bodies,
                                     (string Name, string Clicked)[] terminals, string? ground)
    {
        var setup = EmSetupPersistence.Deserialize($$"""
            { "FormatVersion": 1, "Name": "{{name}}", "Solver3D": "Palace", "Problem3D": "Electrostatic", {{air}},
              "Palace": { "Quality": "Standard" } }
            """);
        var vm = new EmSetupEditorViewModel(c3d, setup, embedded: true) { CanPickInView = true };
        vm.SetBodies(bodies);
        foreach (var (terminal, clicked) in terminals)
        {
            vm.AddTerminalCommand.Execute(null);
            var row = vm.TerminalRows[^1];
            row.Name = terminal;
            vm.PickTerminalCommand.Execute(row);
            Assert.True(vm.PickConductor(clicked), $"{name}: no body holds '{clicked}'");
        }
        if (ground is null) vm.PickGround(vm.GroundPicks.Single(p => p.Body is null));
        else if (ground.Length > 0) vm.PickGround(vm.GroundPicks.Single(p => p.Body?.Members.Contains(ground) == true));
        return EmSetupPersistence.ToEmbedded(vm.Working);
    }
}
