using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A placed wBond's <c>Temp</c> reaches the run through its metal's σ(T) table, a temperature outside
/// the table is clamped and warned about rather than refused, every shipped conductor is a usable wire
/// metal, and the Inspector shows no Footprint and states Temp in °C.
/// </summary>
public sealed class WBondTemperatureAndMaterialTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wbond-temp-{Guid.NewGuid():N}");

    public WBondTemperatureAndMaterialTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static WBondDesign OneArray()
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };
        for (int i = 0; i < 2; i++)
            array.Wires.Add(LoopShape.CreateSeedWire(
                Point3.Mils(0, 6 * i, 4), Point3.Mils(100, 6 * i, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", WBondUnits.ToNm(20.0, WBondUnit.Mil)));
        design.Arrays.Add(array);
        return design;
    }

    /// <summary>The run's series resistance at 1 MHz, where a 1 mil wire is far below one skin depth
    /// and R is its DC value to ~1e-5 — so R ∝ 1/σ.</summary>
    private (double R, IReadOnlyList<string> Warnings) Run(string temp)
    {
        var run = RunWith(c => c.Parameters.First(p => p.Name == "Temp").Expression = temp,
                          Path.Combine(_root, "Amp", "schematic"));
        return (SeriesR(run), run.Warnings);
    }

    /// <summary>Two Terms across a placed wBond, swept at 1 MHz, with the component configured by the caller.</summary>
    private RunResult RunWith(Action<EditableComponent> configure, string schematicDir)
    {
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        Directory.CreateDirectory(model.SchematicDirectory);

        var comp = WBondPlacement.BuildCarrying(OneArray(), "W1");
        comp.Parameters.First(p => p.Name == "IncludeCapacitance").Expression = "false";
        // These tests are about a FIXED temperature reaching the run; a new placement solves its own (brief-wbond-wire-temperature D4).
        comp.Parameters.First(p => p.Name == "FixedTemp").Expression = "true";
        configure(comp);
        model.Components.Add(comp);

        var (render, _) = model.BuildRenderModel();
        var ports = render.Components.Single().Ports;
        for (int i = 0; i < 2; i++)
        {
            double x = comp.X + ports[i].LocalX, y = comp.Y + ports[i].LocalY;
            var term = new EditableComponent { InstanceName = $"T{i + 1}", Symbol = SymbolKind.Term, X = x, Y = y + 200 };
            term.Parameters.Add(new EditableParameter { Name = "Num", Expression = $"{i + 1}" });
            term.Parameters.Add(new EditableParameter { Name = "Z", Expression = "50" });
            model.Components.Add(term);
            model.Components.Add(new EditableComponent { InstanceName = $"GND{i}", Symbol = SymbolKind.Ground, X = x, Y = y + 400 });
        }
        model.Analyses.Add(new SParameterAnalysis(
            "SP1", new FrequencySpec("1", "1", "1", SweepKind.Linear, "MHz", "MHz", "MHz")));

        var extracted = NetExtractor.Extract(model, "tb");
        Assert.Empty(extracted.Conflicts);
        string cnl = Path.Combine(_root, "netlist.cnl");
        File.WriteAllText(cnl, CnlWriter.Write(extracted.TestBench, extracted.Library));
        return SchematicRunService.RunNetlist(cnl, baseDirectory: _root);
    }

    private static double SeriesR(RunResult run)
    {
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        var s = Assert.Single(run.DataSets)["S"];
        int nf = s.Axes[0].Values.Length, np = s.Axes[1].Values.Length;
        var s21 = s.ComplexValues[((0 * nf + 0) * np + 1) * np + 0];
        return (2 * 50.0 * (Complex.One / s21 - Complex.One)).Real;
    }

    [Fact]
    public void TheInstanceTemp_ReachesTheRun_ThroughGoldsTable()
    {
        var (r25, _)  = Run("25");
        var (r125, _) = Run("125");

        // The table, not the α₂₀ formula — which would read 0.16 % differently at 125 °C.
        var gold = WireMaterials.Gold;
        Assert.Equal(gold.SigmaAt(25) / gold.SigmaAt(125), r125 / r25, 1e-4);
    }

    [Fact]
    public void ATempBelowTheTable_IsClampedAndWarned_NotRefused()
    {
        var (rCold, warnings) = Run("-55");
        var (r20, _)          = Run("20");

        Assert.Equal(r20, rCold);
        Assert.Contains(warnings, w => w.Contains("W1") && w.Contains("outside Gold's conductivity table"));
    }

    public static TheoryData<string> ShippedMetals() => [.. WireMaterials.Library.Select(m => m.Name)];

    [Theory]
    [MemberData(nameof(ShippedMetals))]
    public void EveryShippedConductor_CanBeTheWireMetal(string metal)
    {
        double R(string name)
        {
            var p = new Dictionary<string, Value>(StringComparer.Ordinal)
            {
                ["Design"] = new Value(WBondEmbedding.Encode(OneArray())),
                ["Material"] = new Value(name),
                ["IncludeCapacitance"] = new Value("false"),
            };
            var model = (WBondModel)ComponentModelFactory.TryCreate("wBond", p)!;
            Assert.All(model.Design.Arrays[0].Wires, w => Assert.Equal(name, w.Material));
            return model.ArrayImpedance(1e6)[0].Real;
        }

        double t = WireMaterials.DefaultOperatingTempC;
        double expected = WireMaterials.Gold.SigmaAt(t) / WireMaterials.ByName(metal)!.SigmaAt(t);
        Assert.Equal(expected, R(metal) / R("Gold"), 1e-3);
    }

    [Fact]
    public void TheInspector_ShowsNoFootprintOnAWBond_AndStatesTempInCelsius()
    {
        var model = new SchematicEditModel();
        var comp = WBondPlacement.BuildCarrying(OneArray(), "W1");
        comp.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = "smt:0402@N" });
        model.Components.Add(comp);

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);

        Assert.False(editor.ShowFootprintRow);
        Assert.Equal("125", comp.Parameters.Single(p => p.Name == "Temp").Expression);   // a new placement
        Assert.Null(comp.Footprint);          // a stale one is drawn, placed and exported by nothing
        var temp = editor.Rows.Single(r => r.Name == "Temp");
        Assert.Equal("°C", temp.FixedUnit);
        Assert.False(temp.ShowUnitCombo);
    }

    // ── Workspace materials: the library an instance names (New Material…) ───────────────────

    private string NewWorkspace(string name, string? technologyId = null)
        => WorkspaceCreate.Create(_root, name, technologyId).WorkspaceDir;

    private static void WriteLibrary(string path, params TechMaterial[] materials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        MaterialLibraryPersistence.SaveToFile(path, materials);
    }

    private static TechMaterial Alloy => new()
    {
        Name = "My Alloy 2", Sigma20 = 1.0e7, Alpha20 = 0.004,
        SigmaVsTemp = [new() { TempC = -60, Value = 1.3e7 }, new() { TempC = 200, Value = 0.6e7 }],
    };

    [Fact]
    public void NewMaterial_InABlankWorkspace_CreatesTheLibrary_ThenReusesIt()
    {
        string sch = Path.Combine(NewWorkspace("Blank"), "Amp", "schematic");
        var comp = WBondPlacement.BuildCarrying(OneArray(), "W1");

        var first = WBondMaterialLibrary.LocateOrCreate(comp, sch);
        var again = WBondMaterialLibrary.LocateOrCreate(comp, sch);

        Assert.True(first.Created);
        Assert.Null(first.TechnologyToRegisterIn);       // a blank workspace has no technology to name it in
        Assert.EndsWith(WBondMaterialLibrary.NewLibraryRelativePath.Replace('/', Path.DirectorySeparatorChar), first.Path);
        Assert.Equal(first.Path, again.Path);
        Assert.False(again.Created);
    }

    [Fact]
    public void NewMaterial_WithATechnology_SkipsThePristineGenericCopy_AndRegistersTheNewLibrary()
    {
        string ws = NewWorkspace("Board", WorkspaceCreate.DefaultTechnologyId);
        string sch = Path.Combine(ws, "Amp", "schematic");
        var comp = WBondPlacement.BuildCarrying(OneArray(), "W1");

        var located = WBondMaterialLibrary.LocateOrCreate(comp, sch);
        Assert.True(located.Created);
        Assert.False(WBondMaterialLibrary.IsPristineGeneric(located.Path));
        Assert.NotNull(located.TechnologyToRegisterIn);

        WBondMaterialLibrary.RegisterInTechnology(located.TechnologyToRegisterIn!, located.Path);
        var after = WBondMaterialLibrary.LocateOrCreate(comp, sch);
        Assert.Equal(located.Path, after.Path);
        Assert.Null(after.TechnologyToRegisterIn);
    }

    [Fact]
    public void AWorkspaceMetal_ReachesTheRun_AndAMissingLibraryIsRefusedNamingTheInstance()
    {
        string ws = NewWorkspace("Run");
        string sch = Path.Combine(ws, "Amp", "schematic");
        string lib = Path.Combine(ws, "tech", "mine.cmat");
        WriteLibrary(lib, Alloy);

        void UseAlloy(EditableComponent c)
        {
            c.Parameters.First(p => p.Name == "Material").Expression = "My Alloy 2";   // a name with spaces
            c.Parameters.Add(new EditableParameter { Name = WBondPlacement.MaterialLibraryParameter, Expression = "../../tech/mine.cmat" });
            c.Parameters.First(p => p.Name == "Temp").Expression = "85";
        }

        double rAlloy = SeriesR(RunWith(UseAlloy, sch));
        double rGold  = SeriesR(RunWith(c => c.Parameters.First(p => p.Name == "Temp").Expression = "85", sch));
        double sigmaAlloy = 1.3e7 + (0.6e7 - 1.3e7) * (85 + 60) / 260.0;
        Assert.Equal(WireMaterials.Gold.SigmaAt(85) / sigmaAlloy, rAlloy / rGold, 1e-3);

        File.Delete(lib);
        var refused = RunWith(UseAlloy, sch);
        Assert.NotEqual(RunStatus.Success, refused.Status);
        Assert.Contains("wBond 'W1'", refused.StatusMessage);
        Assert.Contains("mine.cmat", refused.StatusMessage);
        Assert.Contains("Restore that file", refused.StatusMessage);
    }

    [Fact]
    public void ALibraryRecordWithNoConductivity_IsRefusedAsNotAWire()
    {
        string ws = NewWorkspace("Dielectric");
        WriteLibrary(Path.Combine(ws, "tech", "mine.cmat"), new TechMaterial { Name = "Glass", Epsr = 6 });

        var run = RunWith(c =>
        {
            c.Parameters.First(p => p.Name == "Material").Expression = "Glass";
            c.Parameters.Add(new EditableParameter { Name = WBondPlacement.MaterialLibraryParameter, Expression = "../../tech/mine.cmat" });
        }, Path.Combine(ws, "Amp", "schematic"));

        Assert.NotEqual(RunStatus.Success, run.Status);
        Assert.Contains("no electrical conductivity", run.StatusMessage);
    }

    [Fact]
    public void TheInspector_ListsTheWorkspaceMetal_NamesItsLibraryWhenChosen_AndOffersNewMaterial()
    {
        string ws = NewWorkspace("Inspector");
        WriteLibrary(Path.Combine(ws, "tech", "mine.cmat"), Alloy);

        var model = new SchematicEditModel { SchematicDirectory = Path.Combine(ws, "Amp", "schematic") };
        var comp = WBondPlacement.BuildCarrying(OneArray(), "W1");
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);
        bool opened = false;
        vm.NewWBondMaterial = (_, c) => opened = ReferenceEquals(c, comp);
        var editor = new ParameterEditorViewModel { PostToUi = work => work() };
        editor.SetTargetDirect(vm, comp, showClose: false);

        Assert.Equal(ParameterEditorViewModel.NewMaterialRow, editor.WBondMaterialOptions[^1]);
        editor.WBondMaterialIndex = editor.WBondMaterialOptions.IndexOf("My Alloy 2");
        Assert.Equal("../../tech/mine.cmat", WBondPlacement.MaterialLibraryOf(comp));

        editor.WBondMaterialIndex = editor.WBondMaterialOptions.IndexOf("Gold");
        Assert.Null(WBondPlacement.MaterialLibraryOf(comp));   // a shipped metal clears the reference

        int before = editor.WBondMaterialIndex;
        editor.WBondMaterialIndex = editor.WBondMaterialOptions.Count - 1;
        Assert.True(opened);
        Assert.Equal(before, editor.WBondMaterialIndex);       // the action row is never left selected
    }

    /// <summary>A LINKED wBond whose layout gained an array has fewer schematic pins than the model needs. The run
    /// died with "Index was outside the bounds of the array"; it now refuses naming the instance and the arrays,
    /// and the remedy it names — Update Schematic from Layout — gives the symbol the missing pins.</summary>
    [Fact]
    public void AnArrayAddedInTheLayout_IsRefusedByName_AndUpdateSchematicFromLayoutGivesThePins()
    {
        string ws = NewWorkspace("Drift");
        string sch = Path.Combine(ws, "Amp", "schematic");
        string wbond = Path.Combine(ws, "Amp", "layout", "Amp.wBond");
        Directory.CreateDirectory(Path.GetDirectoryName(wbond)!);
        var twoArrays = OneArray();
        var g2 = new WireArray { Name = "G2" };
        g2.Wires.Add(LoopShape.CreateSeedWire(Point3.Mils(0, 40, 4), Point3.Mils(100, 40, 1),
            WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", WBondUnits.ToNm(20.0, WBondUnit.Mil)));
        twoArrays.Arrays.Add(g2);
        WBondIo.WriteFile(wbond, twoArrays);

        EditableComponent? placed = null;
        var run = RunWith(c => { WBondPlacement.LinkTo(c, wbond, sch); placed = c; }, sch);

        Assert.NotEqual(RunStatus.Success, run.Status);
        Assert.DoesNotContain("outside the bounds", run.StatusMessage);
        Assert.Contains("wBond 'W1' has 2 pin(s)", run.StatusMessage);
        Assert.Contains("G1, G2", run.StatusMessage);
        Assert.Contains("Update Schematic from Layout", run.StatusMessage);

        var model = new SchematicEditModel { SchematicDirectory = sch };
        model.Components.Add(placed!);
        WBondSchematicReconcile.Run(model, WBondIo.ReadFile(wbond)).Command!.Execute();
        Assert.Equal(4, model.BuildRenderModel().Item1.Components.Single().Ports.Count);
    }

    /// <summary>A blank Temp shows, greyed, the temperature the run will use — 125 even for wires saved when the
    /// default was 85 — as a bare number, the unit column already saying °C.</summary>
    [Fact]
    public void ABlankTemp_ShowsTheTemperatureInUse_As125()
    {
        var oldWires = OneArray();
        oldWires.OperatingTempC = 85.0;
        var comp = WBondPlacement.BuildCarrying(oldWires, "W1");
        comp.Parameters.First(p => p.Name == "Temp").Expression = "";
        var model = new SchematicEditModel();
        model.Components.Add(comp);

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);

        Assert.Equal("125", editor.Rows.Single(r => r.Name == "Temp").ExpressionPlaceholder);
    }
}
