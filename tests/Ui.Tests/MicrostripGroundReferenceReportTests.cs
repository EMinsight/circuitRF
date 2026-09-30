using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Update Layout places a microstrip's LINE; the plane it returns through is the stackup's ground
/// reference and no generator draws it. An empty ground layer afterwards read as a missing plane, so
/// the run offers Draw Ground Pour for it — once, and only while nothing is drawn there — and the pour
/// covers the lines, cutting a clearance only round a via that passes THROUGH the plane.
/// </summary>
public sealed class MicrostripGroundReferenceReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-mlin-ground-note-" + Guid.NewGuid().ToString("N"));

    public MicrostripGroundReferenceReportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static EditableComponent Mlin(string name)
    {
        var comp = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Mlin };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
            comp.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, Dimension = dp.Dimension });
        return comp;
    }

    /// <summary>The two-layer starter with an inner ground plane between its coppers, so a through via PASSES the
    /// plane the lines return through, and the bottom copper is no longer ground.</summary>
    private static Technology WithInnerGround(out StackupLayer inner)
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var key = new LayerKey(10, 0);
        tech.Layers.Add(new LayerDef { Key = key, Name = "Inner Ground", Purpose = "drawing" });
        var layers = tech.Stackup.Layers;
        layers.Single(l => l.IsGroundReference).IsGroundReference = false;
        int fr4 = layers.FindIndex(l => l.Kind == StackupKind.Dielectric);
        layers[fr4].ThicknessDbu /= 2;
        inner = new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "Inner Ground", ThicknessDbu = 35_000, SigmaSm = 5.8e7,
            DrawingLayers = [key], IsGroundReference = true,
        };
        layers.Insert(fr4 + 1, inner);
        layers.Insert(fr4 + 2, new StackupLayer
        {
            Kind = StackupKind.Dielectric, Name = "FR-4 lower", ThicknessDbu = layers[fr4].ThicknessDbu, Epsr = 4.4, TanD = 0.02,
        });
        return tech;
    }

    [Fact]
    public void PlacedLines_OfferTheirUndrawnGroundAsAPour_UnderTheLines_WithAClearanceOnlyWhereAViaPassesThrough()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "Line");
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var tech = WithInnerGround(out var ground);

        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(Mlin("ML1"));
        model.Components.Add(Mlin("ML2"));
        var target = new LayoutView();

        var first = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null);
        Assert.Equal([ground.Name], first.UndrawnGrounds);
        Assert.DoesNotContain(first.Lines, l => l.Text.Contains("ground reference"));   // posted with its button instead
        first.Command!.Execute();

        // A through via inside the lines' region passes the inner plane: it gets a hole. The drill layer is the
        // starter's own via entry.
        var drill = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via).DrawingLayers[0];
        var lines = target.Instances.Select(i => CellHierarchy.InstanceBbox(i, layoutDir)).Aggregate((a, b) => a.Union(b));
        target.Shapes.Add(new ViaShape { Layer = drill, X = lines.MinX, Y = lines.MinY, PadSize = 600_000, DrillSize = 300_000 });

        var pour = Assert.Single(GroundPourPlanner.Plan(target, tech, layoutDir));
        Assert.Equal(ground.DrawingLayers[0], pour.DrawingLayer);
        Assert.Equal(2, pour.Lines);
        Assert.Equal(1, pour.ViaClearances);
        Assert.Equal(0, pour.ViasJoined);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(pour.Shapes));
        Assert.Single(poly.Holes!);
        long minX = poly.Xy.Where((_, i) => i % 2 == 0).Min(), maxX = poly.Xy.Where((_, i) => i % 2 == 0).Max();
        Assert.True(minX <= lines.MinX - pour.MarginDbu && maxX >= lines.MaxX + pour.MarginDbu);

        // Once a plane is drawn there, neither the offer nor the planner draws another over it.
        foreach (var s in pour.Shapes) target.Shapes.Add(s);
        model.Components.Add(Mlin("ML3"));
        var second = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null);
        Assert.Empty(second.UndrawnGrounds);
        Assert.Empty(GroundPourPlanner.Plan(target, tech, layoutDir));
    }

    [Fact]
    public void OnATwoLayerBoard_TheOuterGroundIsNotOffered_AndDrawnOnRequestAViaEndingOnItIsJoinedAndCounted()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "Line");
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var tech = StarterTechnologies.Pcb2Layer();   // its through via ends on Bottom Copper, the ground
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(Mlin("ML1"));
        var target = new LayoutView();
        var run = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null);
        Assert.Empty(run.UndrawnGrounds);   // an OUTER ground is not offered: simulation already takes it as solid
        run.Command!.Execute();

        var drill = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via).DrawingLayers[0];
        var line = CellHierarchy.InstanceBbox(target.Instances[0], layoutDir);
        target.Shapes.Add(new ViaShape { Layer = drill, X = line.MinX, Y = line.MinY, PadSize = 600_000, DrillSize = 300_000 });

        var pour = Assert.Single(GroundPourPlanner.Plan(target, tech, layoutDir));
        Assert.Equal((0, 1), (pour.ViaClearances, pour.ViasJoined));
        Assert.Null(Assert.IsType<PolygonShape>(Assert.Single(pour.Shapes)).Holes);
    }
}
