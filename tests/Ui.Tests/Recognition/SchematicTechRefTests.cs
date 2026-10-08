// brief-artsch-6-emit-and-target-cell.md §4 — a schematic's own technology (R-as6-1, D20): a .csch TechRef and a
// .cnl `technology` statement are resolved before the workspace default; without either nothing changes; a
// reference that does not resolve is a check error.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Cli;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class SchematicTechRefTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-as6-tech-" + Guid.NewGuid().ToString("N")[..12]);

    public SchematicTechRefTests()
    {
        // The workspace default: 1.6 mm of FR-4. The board's own: 0.5 mm of εr 3.5.
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "fr4.ctech"), Board(1_600, 4.4));
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech/fr4.ctech" });
        Directory.CreateDirectory(Path.Combine(_root, "Board"));
        TechPersistence.SaveToFile(Path.Combine(_root, "Board", "board.ctech"), Board(500, 3.5));
        Directory.CreateDirectory(SchematicDir);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private string SchematicDir => Path.Combine(_root, "Model", "schematic");

    internal static Technology Board(double coreUm, double epsr)
    {
        var tech = TwoLayer();
        tech.Stackup.Layers[2].ThicknessDbu = Um(coreUm);
        tech.Stackup.Layers[2].Epsr = epsr;
        return tech;
    }

    private static double Override(Instance inst, string name) => double.Parse(inst.Overrides.First(o => o.Name == name).Expression,
                                                                               System.Globalization.CultureInfo.InvariantCulture);

    private SchematicEditModel MlinSchematic(string? techRef)
    {
        var model = new SchematicEditModel { SchematicDirectory = SchematicDir, TechRef = techRef };
        var comp = new EditableComponent { InstanceName = "TL1", Symbol = SymbolKind.Mlin };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
            comp.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, Dimension = dp.Dimension });
        model.Components.Add(comp);
        return model;
    }

    [Fact]
    public void AMicrostripLineExtractsOnItsSchematicsTechRef_AndOnTheWorkspaceDefaultWithoutOne()
    {
        var own = NetExtractor.Extract(MlinSchematic("../../Board/board.ctech")).TestBench;
        var tl = own.Instances.Single(i => i.InstanceName == "TL1");
        Assert.Equal((0.5e-3, 3.5), (Override(tl, "H"), Override(tl, "Er")));
        // Relative to the workspace root, where the extracted text is read back from (and Simulate writes it).
        Assert.Equal("Board/board.ctech", own.Technology);

        // ...and a run reads it back from there: the schematic on disk prepares, bound to the board's substrate.
        string csch = Path.Combine(SchematicDir, "Model.csch");
        SchematicPersistence.SaveToFile(csch, MlinSchematic("../../Board/board.ctech"), "Model");
        var prepared = CircuitRF.Design.Circuit.PreparedCircuit.FromSchematic(csch);
        Assert.Null(prepared.ReadError);
        Assert.Equal(0.5e-3, Override(prepared.Tb!.Instances.Single(i => i.InstanceName == "TL1"), "H"), 12);

        var plain = NetExtractor.Extract(MlinSchematic(null)).TestBench;
        Assert.Equal((1.6e-3, 4.4), (Override(plain.Instances.Single(), "H"), Override(plain.Instances.Single(), "Er")));
        Assert.Null(plain.Technology);
    }

    [Fact]
    public void TheNetlistsTechnologyStatementBindsTheSameWayHeadlessly()
    {
        const string body = "Port:P1 a 0 Num=1 Z=50 Ohm\nMLIN:TL1 a b W=1 mm L=10 mm\nPort:P2 b 0 Num=2 Z=50 Ohm\n" +
                            "analysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz\n";
        string withRef = Path.Combine(SchematicDir, "own.cnl");
        File.WriteAllText(withRef, "technology \"../../Board/board.ctech\"\n" + body);
        string without = Path.Combine(SchematicDir, "plain.cnl");
        File.WriteAllText(without, body);

        var (_, own) = CnlTechnologyBinding.ReadFile(withRef);
        Assert.Equal(0.5e-3, Override(own.Instances.Single(i => i.InstanceName == "TL1"), "H"), 12);
        var (_, plain) = CnlTechnologyBinding.ReadFile(without);
        Assert.Equal(1.6e-3, Override(plain.Instances.Single(i => i.InstanceName == "TL1"), "H"), 12);

        // The writer says it back, and the reader reads its own spelling.
        Assert.StartsWith("technology \"../../Board/board.ctech\"", Core.Netlist.CnlWriter.Write(new Core.Netlist.CnlReader().Read(File.ReadAllText(withRef)).TestBench));
    }

    [Fact]
    public void AnUnresolvableTechnologyIsACheckError()
    {
        string csch = Path.Combine(SchematicDir, "Model.csch");
        var model = MlinSchematic(null);
        model.Analyses.Add(new SParameterAnalysis("SP1", new FrequencySpec("1", "2", 2, SweepKind.Linear, "GHz", "GHz")));
        SchematicPersistence.SaveToFile(csch, model, "Model");
        Assert.Equal(0, CheckExit(csch));

        model.TechRef = "../../Board/missing.ctech";
        SchematicPersistence.SaveToFile(csch, model, "Model");
        Assert.Equal(1, CheckExit(csch));

        string cnl = Path.Combine(SchematicDir, "missing.cnl");
        File.WriteAllText(cnl, "technology \"nowhere.ctech\"\nR:R1 a 0 R=50 Ohm\nanalysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz\n");
        Assert.Equal(1, CheckExit(cnl));
    }

    private static int CheckExit(string path)
    {
        JsonRun.Reset();
        return CliEntry.Run(["check", path, "--json"]);
    }
}
