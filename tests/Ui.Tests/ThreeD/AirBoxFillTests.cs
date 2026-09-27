// 3D editor round 3 — the air box is filled with Air by default; the user makes it a vacuum (or anything the technology
// defines) through the box's material, and both solvers fill the background with it.

using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class AirBoxFillTests
{
    [Fact]
    public void TheFill_IsAirByDefault_VacuumByChoice_AndAnUnknownMaterialIsRefused()
    {
        Assert.Equal("Air", C3dProblemAssembly.BoxFill(new C3dDocument()));
        Assert.Equal("Vacuum", C3dProblemAssembly.BoxFill(new C3dDocument { AirBoxMaterial = "Vacuum" }));

        var air = C3dProblemAssembly.AirFill(null, null, 25, out _)!.Value.Material;
        Assert.Equal(("Air", 1.0), (air.Name, air.Epsr));
        var vac = C3dProblemAssembly.AirFill("Vacuum", null, 25, out _)!.Value.Material;
        Assert.Equal(("Vacuum", 1.0), (vac.Name, vac.Epsr));
        Assert.Null(C3dProblemAssembly.AirFill("Unobtainium", null, 25, out string why));
        Assert.Contains("'Unobtainium'", why);
    }

    [Fact]
    public void BothWriters_FillTheBackgroundWithTheBoxsMaterial()
    {
        const double mm = 1e-3;
        var materials = new[]
        {
            new Em3dMaterial("Cu", 1, null, 0, 1, 5.8e7),
            new Em3dMaterial("Air", 1.0006, null, 0, 1, 0),
            new Em3dMaterial("Vacuum", 1, null, 0, 1, 0),
        };
        var solids = new[] { new Em3dSolid("pad", "Cu", Em3dRole.Conductor, new Em3dBox(new(-0.3 * mm, -0.3 * mm, 0.1 * mm), new(0.3 * mm, 0.3 * mm, 0.2 * mm)), 1) };
        var port = new Em3dPort(1, "port/1", "pad", "airbox/zmin", new(-0.3 * mm, -0.1 * mm, 0), new(-0.3 * mm, 0.1 * mm, 0.1 * mm),
                                new(0, 0, 1), 50, new Em3dReferencePlane(new(-0.3 * mm, 0, 0), new(0, 0, 1), 0));
        var a = Em3dBoundaryKind.Absorbing;
        var box = new Em3dAirBox(new(-1 * mm, -1 * mm, 0), new(1 * mm, 1 * mm, 1 * mm), new Em3dFaces(a, a, a, a, Em3dBoundaryKind.Pec, a))
        {
            Material = "Vacuum",
        };
        var p = new Em3dProblem(solids, [], materials, [port], box, new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 20);

        var geo = GmshGeoWriter.Write(p, PalaceSettings.Resolve(new CemPalace()));
        Assert.True(geo.Ok, geo.Refusal);
        Assert.Equal("Vacuum", geo.Groups.Single(g => g.Kind == Em3dGroupKind.Background).Material);

        var grid = FdtdGrid.Build(p, OpenEmsGridSettings.Default, long.MaxValue);
        var fdtd = CsxcadWriter.Write(p, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default);
        Assert.True(fdtd.Ok, fdtd.Refusal);
        Assert.Contains("<BackgroundMaterial Epsilon=\"1\"", fdtd.Model);

        var withAir = p with { Boundary = box with { Material = "Air" } };
        var fdtdAir = CsxcadWriter.Write(withAir, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default);
        Assert.Contains("<BackgroundMaterial Epsilon=\"1.0006\"", fdtdAir.Model);
        Assert.Equal("Air", GmshGeoWriter.Write(withAir, PalaceSettings.Resolve(new CemPalace())).Groups
                                         .Single(g => g.Kind == Em3dGroupKind.Background).Material);
    }
}
