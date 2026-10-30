using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// brief-dfr31-6 — an inner plane whose side lies on the board edge, with the 0.109 µm jogs a crop leaves where it meets the
// artwork's own edge (the field board's dimensions, on a 2 × 2 mm four-layer board). Robust meshing cleaned the plane on its
// own and pulled that side off the edge: its face met the board's side face at 0.0127° and Gmsh stopped on "Invalid boundary
// mesh (overlapping facets)". In the Gmsh-counting collection: both tests start Gmsh.
[Collection(Em3dProcessCountersCollection.Name)]
public sealed class BoardEdgeSliverTests : IDisposable
{
    private const int Dbu = 1000;
    private readonly string _root = Directory.CreateTempSubdirectory("dfr31-6-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    /// <summary>The board, with Inner 2's plane drawn as <paramref name="plane"/> (DBU) against its right edge at 2 mm.</summary>
    private static (EmSetup Setup, EmLayoutSource Source) Board(long[] plane, bool robust)
    {
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(11, 0), X1 = 0, Y1 = 0, X2 = Um(2000), Y2 = Um(2000) });   // Outline
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = Um(400), Y1 = Um(800), X2 = Um(1200), Y2 = Um(1200), Net = "SIG" });
        view.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = 0, Y1 = 0, X2 = Um(2000), Y2 = Um(2000), Net = "GND" });
        view.Shapes.Add(new PolygonShape { Layer = new LayerKey(3, 0), Net = "GND", Xy = plane });
        var face = new EmAirBoxFace(500, null);
        var setup = new EmSetup
        {
            Name = "sliver", LayoutRef = "sliver/layout/sliver.clay",
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
            Problem3D = Em3dProblemType.Electrostatic,
            Terminals3D = [new("SIG", "SIG")],
            Ground3D = "GND",
            AirBox = new EmAirBox(face, face, face, face, face, face),
            RobustMeshing = robust,
        };
        return (setup, new EmLayoutSource("/nowhere/sliver.clay", view, tech, Dbu));
    }

    private PalaceStep Mesh(EmSetup setup, EmLayoutSource source, out Em3dGenerationResult g)
    {
        g = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(g.Ok, g.Refusal);
        var low = GmshGeoWriter.Write(g.Problem!, PalaceSettings.Resolve(setup.Palace));
        Assert.True(low.Ok, low.Refusal);
        return PalaceRun.Mesh(Path.Combine(_root, "mesh"), low, Gmsh(), null, default);
    }

    /// <summary>
    /// R-dfr31-6-2/3 — the plane's side on the board edge from y = 500 to 992.345 µm, a 0.109 µm jog at each end. Robust
    /// meshing moves the jogs onto the edge and keeps the side on it, so the plane is a rectangle and the board meshes; before
    /// the fix it removed the corner at the top of the side and Gmsh refused the board with the field's message.
    /// </summary>
    [GmshFact]
    public void APlaneOnTheBoardEdge_StaysOnIt_AndTheBoardMeshes()
    {
        long xe = Um(2000);
        var (setup, source) = Board([Um(1000), Um(500) - 29, xe - 109, Um(500) - 29, xe, Um(500), xe, Um(992.345),
                                     xe - 109, Um(992.375), Um(1000), Um(992.375)], robust: true);

        var step = Mesh(setup, source, out var g);

        Assert.True(step.Ok, step.Message);
        var plane = Assert.IsType<Em3dExtrudedPolygon>(g.Problem!.Solids.Single(s => s.Name == "Inner 2/GND").Primitive);
        Assert.Equal(4, plane.Outline.Count);
        Assert.Equal(2, plane.Outline.Count(q => q.X == 2e-3));
        Assert.Contains(g.Notes, n => n.Contains("and moved 2 that lay as close to the board's edge", StringComparison.Ordinal));
    }

    /// <summary>
    /// R-dfr31-6-4 — the same sliver drawn into the artwork (its side from the edge to 0.109 µm inside it) and meshed as drawn:
    /// Gmsh still refuses it, and the refusal names the plane and the board's side rather than two surface tags.
    /// </summary>
    [GmshFact]
    public void AForcedOverlap_NamesBothObjects()
    {
        long xe = Um(2000);
        var (setup, source) = Board([Um(1000), Um(500), xe, Um(500), xe - 109, Um(992.375), Um(1000), Um(992.375)], robust: false);

        var step = Mesh(setup, source, out _);

        Assert.False(step.Ok);
        // In the order Gmsh names the two surfaces.
        Assert.True(step.Message!.Contains("overlapping facets", StringComparison.Ordinal) &&
                    step.Message.Contains("The overlap is between ", StringComparison.Ordinal) &&
                    step.Message.Contains("'Inner 2/GND'", StringComparison.Ordinal) &&
                    step.Message.Contains("the face where 'Core' meets the surrounding air", StringComparison.Ordinal), step.Message);
    }

    private static string Gmsh() => SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh)).Installation!.Path;
}
