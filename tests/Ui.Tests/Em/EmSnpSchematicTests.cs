using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests.Em;

/// <summary>
/// brief-dfr31-4 — Create SnP schematic after a finished EM run: the cell is made through <see cref="CellCreate"/>, simulates
/// back to the run's own s-parameters, puts each TermG on the side its port faces, and is never made twice.
/// </summary>
public class EmSnpSchematicTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-dfr31-4").FullName;
    public void Dispose() { try { Directory.Delete(_ws, true); } catch { /* best effort */ } }

    private string ResultsRoot => Path.Combine(_ws, "results");
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>Parallel 1 mm lines, 20 mm long, 0.5 mm apart — one line is a 2-port, three are a 6-port.</summary>
    private static EmLayoutSource Lines(int count)
    {
        var v = new LayoutView { DbuPerMicron = Dbu };
        for (int k = 0; k < count; k++)
            v.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = k * 1_500_000, X2 = 20_000_000, Y2 = k * 1_500_000 + 1_000_000 });
        return new("/x/Lines.clay", v, StarterTechnologies.Pcb2Layer(), Dbu);
    }

    private static EmSetup Setup(string name = "Thru") => new()
    {
        Name = name, LayoutRef = "Lines/layout/Lines.clay", CreateSnpSchematic = true,
        Frequency = new FrequencySpec("1", "5", 5, SweepKind.Linear, "GHz", "GHz"),
    };

    /// <summary>The schematic run as Simulate runs it: extracted, written to the workspace root, run.</summary>
    private RunResult Simulate(string schematicPath)
    {
        var (model, _, cell) = SchematicPersistence.LoadFromFile(schematicPath);
        var tb = NetExtractor.Extract(model, cell);
        Assert.Empty(tb.Conflicts);
        string cnl = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(cnl, CnlWriter.Write(tb.TestBench, "; dfr31-4"));
        var run = SchematicRunService.RunNetlist(cnl);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        return run;
    }

    private static void AssertReproduces(RunResult run, string snpPath)
    {
        var snp = RfCore.TouchstoneIO.ReadFile(snpPath);
        var s = run.DataSets[0]["S"];
        for (int f = 0; f < snp.Frequencies.Length; f++)
            for (int i = 0; i < snp.Ports; i++)
                for (int j = 0; j < snp.Ports; j++)
                {
                    var got = s.At("freq", f).At("i", i).At("j", j).ComplexValues[0];
                    Assert.True((got - snp.Matrices[f][i, j]).Magnitude < 1e-9,
                                $"S({i + 1},{j + 1}) at point {f}: {got} against {snp.Matrices[f][i, j]}");
                }
    }

    /// <summary>Gate 1: a 2-port run with the flag on makes the cell, and simulating it gives back the Touchstone.</summary>
    [Fact]
    public void ATwoPortRun_CreatesTheCell_AndItsSimulationReproducesTheTouchstone()
    {
        var em = EmRunService.Run(Setup(), Lines(1), ResultsRoot);
        Assert.Equal(EmRunStatus.Ok, em.Status);

        var made = em.SnpSchematic!;
        Assert.Equal(EmSnpSchematicOutcome.Created, made.Outcome);
        Assert.Equal("Created cell Thru_sim: an S2P of this run with 2 TermG, ready to simulate.", made.Sentence);
        Assert.Equal(Path.Combine(_ws, "Thru_sim", "schematic", "Thru_sim.csch"), made.SchematicPath);

        var (model, _, _) = SchematicPersistence.LoadFromFile(made.SchematicPath!);
        var snp = Assert.Single(model.Components, c => c.Symbol == SymbolKind.Snp);
        Assert.Equal("results/Thru.s2p", snp.Parameters.First(p => p.Name == "File").Expression);
        Assert.Equal(2, model.Components.Count(c => c.Symbol == SymbolKind.TermG));
        var sp = Assert.IsType<SParameterAnalysis>(Assert.Single(model.Analyses));
        Assert.Equal(5, sp.Freq.NumPoints);

        AssertReproduces(Simulate(made.SchematicPath!), em.SnpPath!);

        // The display the schematic's run opens, read back through the application's own reader.
        var cdd = JsonSerializer.Deserialize<DataDisplayConfig>(
            File.ReadAllText(Path.Combine(_ws, "Thru_sim.cdd")), DataDisplayJson.Options)!;
        Assert.Equal("Thru_sim.npy", cdd.SelectedDataSource);
        var traces = cdd.Tabs.Single().Plots.SelectMany(p => p.Traces).ToList();
        Assert.Equal(["S(2,1)", "S(1,1)", "S(2,2)"], traces.Select(t => $"S({t.OutputPort},{t.InputPort})"));
    }

    /// <summary>Gate 2: on a 6-port, each TermG is on the side its port faces, turned away from the block, and wired.</summary>
    [Fact]
    public void ASixPortResult_PutsEachTermGOnTheSideItsPortFaces()
    {
        var plain = Setup("Triple");
        plain.CreateSnpSchematic = false;   // the faces below are stated, not read off this layout's unlabelled ends
        var em = EmRunService.Run(plain, Lines(3), ResultsRoot);
        Assert.Equal(EmRunStatus.Ok, em.Status);
        Assert.EndsWith(".s6p", em.SnpPath!);

        var faces = new Dictionary<int, EmPortFace>
        {
            [1] = EmPortFace.Left, [2] = EmPortFace.Right, [3] = EmPortFace.Top,
            [4] = EmPortFace.Bottom, [5] = EmPortFace.Left, [6] = EmPortFace.Top,
        };
        var made = EmSnpSchematic.Create(_ws, Setup("Triple"), em.SnpPath!, faces);
        Assert.Equal(EmSnpSchematicOutcome.Created, made.Outcome);

        var (model, _, _) = SchematicPersistence.LoadFromFile(made.SchematicPath!);
        foreach (var (port, face) in faces)
        {
            var t = model.Components.Single(c => c.InstanceName == $"Term{port}");
            Assert.Equal(port.ToString(), t.Parameters.First(p => p.Name == "Num").Expression);
            // Where its ground end is: behind the pin, away from the block.
            var (gx, gy) = SchematicGeometry.LocalToWorld(0f, 270f, t.X, t.Y, t.Rotation, false);
            Assert.True(face switch
            {
                EmPortFace.Left   => gx < -1000 && t.Rotation == SymbolRotation.R90,
                EmPortFace.Right  => gx > 1000 && t.Rotation == SymbolRotation.R270,
                EmPortFace.Top    => gy < -1000 && t.Rotation == SymbolRotation.R180,
                _                 => gy > 1000 && t.Rotation == SymbolRotation.R0,
            }, $"Term{port} should face {face}: ground at ({gx}, {gy}), {t.Rotation}");
        }

        AssertReproduces(Simulate(made.SchematicPath!), em.SnpPath!);
    }

    /// <summary>Gate 3: a second run finds the cell already reading its file and writes nothing.</summary>
    [Fact]
    public void ASecondRun_NeitherDuplicatesNorOverwritesTheCell()
    {
        var first = EmRunService.Run(Setup(), Lines(1), ResultsRoot).SnpSchematic!;
        var csch = first.SchematicPath!;
        File.AppendAllText(csch, "\n");   // an edit of the user's: must survive the re-run byte for byte
        byte[] before = File.ReadAllBytes(csch);

        var second = EmRunService.Run(Setup(), Lines(1), ResultsRoot).SnpSchematic!;

        Assert.Equal(EmSnpSchematicOutcome.Current, second.Outcome);
        Assert.Equal(csch, second.SchematicPath);
        Assert.Equal(before, File.ReadAllBytes(csch));
        Assert.Single(Directory.GetDirectories(_ws), d => Path.GetFileName(d).StartsWith("Thru_sim", StringComparison.Ordinal));
    }

    /// <summary>A port faces away from the way its current flows in: R0 (+x̂ into the metal) is a port on the left end.</summary>
    [Fact]
    public void APortFacesOppositeItsDirection()
    {
        var v = new LayoutView { DbuPerMicron = Dbu };
        v.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 10_000_000, Y2 = 10_000_000 });
        LabelShape Port(string text, long x, long y, LayoutRotation d)
            => new() { Layer = new(1, 0), X = x, Y = y, Text = text, Height = 100 * Dbu, IsPort = true, PortDirection = d };
        v.Shapes.Add(Port("P1", 0, 5_000_000, LayoutRotation.R0));
        v.Shapes.Add(Port("P2", 10_000_000, 5_000_000, LayoutRotation.R180));
        v.Shapes.Add(Port("P3", 5_000_000, 0, LayoutRotation.R90));
        v.Shapes.Add(Port("P4", 5_000_000, 10_000_000, LayoutRotation.R270));

        var faces = EmSnpSchematic.FacesOf(new EmLayoutSource("/x/Sq.clay", v, StarterTechnologies.Pcb2Layer(), Dbu));

        Assert.Equal(EmPortFace.Left,   faces[1]);
        Assert.Equal(EmPortFace.Right,  faces[2]);
        Assert.Equal(EmPortFace.Bottom, faces[3]);
        Assert.Equal(EmPortFace.Top,    faces[4]);
    }

    /// <summary>The flag is written only when on, survives a round trip and a clone, and is no part of a 3D run's record.</summary>
    [Fact]
    public void TheFlag_IsOmittedAtItsDefault_AndRoundTrips()
    {
        Assert.DoesNotContain("CreateSnpSchematic", EmSetupPersistence.Serialize(new EmSetup()));
        var on = new EmSetup { CreateSnpSchematic = true };
        Assert.True(EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(on)).CreateSnpSchematic);
        Assert.True(on.Clone().CreateSnpSchematic);
        Assert.True(EmSetupPersistence.FromEmbedded(EmSetupPersistence.ToEmbedded(on)).CreateSnpSchematic);

        var doc = new CircuitRF.Design.ThreeD.C3dDocument { Setups = [EmSetupPersistence.ToEmbedded(new EmSetup { Name = "A" })] };
        string off = CircuitRF.Design.ThreeD.C3dPersistence.SerializeForRun(doc);
        doc.Setups = [EmSetupPersistence.ToEmbedded(new EmSetup { Name = "A", CreateSnpSchematic = true })];
        Assert.Equal(off, CircuitRF.Design.ThreeD.C3dPersistence.SerializeForRun(doc));
    }
}
