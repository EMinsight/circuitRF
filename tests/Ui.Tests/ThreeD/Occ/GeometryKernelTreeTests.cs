using System.Globalization;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Ui.Tests.ThreeD.Occ;

/// <summary>brief-em3d-63 R-em3d63-5 — the tree the worker receives: resolved numbers in micrometres, circuitRF's face
/// names, and canonical bytes, so the same tree is the same hash on every platform.</summary>
public sealed class GeometryKernelTreeTests
{
    private static C3dBox Lid() => new()
    {
        Name = "lid", Material = "copper",
        Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(100_500, 200_000, 50_000),
        Placement = new C3dPlacement { Origin = new C3dPoint3(10_000, 0, 0), Rotate = [new C3dRotation { Axis = C3dAxis.Z, Deg = 90 }] },
    };

    [Fact]
    public void ABox_IsWrittenCanonically_InMicrometres_WithItsFaceNames()
    {
        var tree = GeometryKernelTree.From(Lid(), dbuPerMicron: 1000);

        const string expected = """
            {"tree":1,"root":{"kind":"box","name":"lid","faces":["xmin","xmax","ymin","ymax","zmin","zmax"],"transform":[0,-1,0,10,1,0,0,0,0,0,1,0],"min":[0,0,0],"size":[100.5,200,50]}}

            """;
        Assert.Equal(expected, tree.Json);
        Assert.Equal(C3dBox.FaceNameList, tree.FaceNames);
    }

    [Fact]
    public void TheBytesAndHash_DoNotDependOnTheCulture()
    {
        var invariant = GeometryKernelTree.From(Lid(), 1000);
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var german = GeometryKernelTree.From(Lid(), 1000);
            Assert.Equal(invariant.Hash, german.Hash);
            Assert.Equal(invariant.Json, german.Json);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void APrism_IsItsOutlineInItsOwnFrame_AndOneExtrusionVector()
    {
        var prism = new C3dPrism
        {
            Name = "slab", Plane = C3dPlane.XZ, Offset = 5_000, Height = -30_000, Shear = new C3dPoint2(1_000, 0),
            Outline = [new(0, 0), new(100_000, 0), new(100_000, 20_000)],
        };
        var tree = GeometryKernelTree.From(prism, 1000);

        // XZ: u is x, v is z, and the offset and height run along y.
        Assert.Contains("\"outline\":[[0,5,0],[100,5,0],[100,5,20]],\"holes\":[],\"extrude\":[1,-30,0]", tree.Json, StringComparison.Ordinal);
        Assert.Equal(["bottom", "top", "side0", "side1", "side2"], tree.FaceNames);
    }

    [Fact]
    public void ASheetOrAPolyline_IsNotATree()
    {
        Assert.False(GeometryKernelTree.CanHold(new C3dSheet { Name = "trace" }));
        Assert.Throws<ArgumentException>(() => GeometryKernelTree.From(new C3dPolyline { Name = "guide" }, 1000));
    }
}
