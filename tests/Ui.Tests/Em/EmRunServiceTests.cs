// brief-artsch-8-gui-command.md R-as8-6 — the unknown-ceiling refusal points at Create Schematic from Artwork, and no
// other refusal does. Both through EmRunService.Run, the function Simulate and `circuitrf em` call, on the committed
// port-calibration fixtures: one meshed past the ceiling on purpose (the refusal is taken before any fill, so it is
// cheap), one refused for its port clearance.

using System;
using System.IO;
using CircuitRF.Core.Design;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class EmRunServiceTests : IDisposable
{
    private readonly string _results = Path.Combine(Path.GetTempPath(), $"crf-as8-em-{Guid.NewGuid():N}", "results");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_results)!, true); } catch { /* best effort */ }
    }

    private static string Portcal()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return Path.Combine(dir, "testdata", "portcal");
    }

    private static (EmSetup Setup, EmLayoutSource Source) Fixture(string name)
    {
        string cem = Path.Combine(Portcal(), name, "em", name + ".cem");
        var setup = EmSetupPersistence.LoadFromFile(cem).Clone();
        setup.Frequency = new FrequencySpec("1", "1", 1, SweepKind.Linear, "GHz", "GHz");
        var r = EmSetupResolver.Resolve(cem, setup.LayoutRef, Path.Combine(Portcal(), ".cws"), new TechnologyCache());
        return (setup, r.Source!);
    }

    [Fact]
    public void TheUnknownCeilingRefusal_PointsAtCreateSchematicFromArtwork_AndAPortRefusalDoesNot()
    {
        var (setup, source) = Fixture("separated-pair");
        setup.PlanarMesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 400, EdgeMesh: true, EdgeCells: 3,
                                                  BoundaryCells: PlanarBoundaryCells.Staircase, MeshFrequencyHz: 60e9);
        var ceiling = EmRunService.Run(setup, source, _results);

        Assert.Equal(EmRunStatus.Refused, ceiling.Status);
        Assert.Equal("em.refused.mesh-ceiling", ceiling.Diagnostic!.Id);
        Assert.EndsWith(EmRunService.WithoutEmPointer, ceiling.Error);
        Assert.Contains("Design ▸ Create Schematic from Artwork", ceiling.Error);

        var (other, otherSource) = Fixture("offset-pair");
        var clearance = EmRunService.Run(other, otherSource, _results);
        Assert.Equal(EmRunStatus.Refused, clearance.Status);
        Assert.Equal("em.refused.port-clearance", clearance.Diagnostic!.Id);
        Assert.DoesNotContain("Create Schematic from Artwork", clearance.Error);
    }
}
