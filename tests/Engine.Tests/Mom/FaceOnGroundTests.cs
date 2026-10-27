using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <see cref="EmMeshSettings.OmitFacesOnGround"/> — grounded copper standing ON the image plane, as
/// the trace-impedance check meets it when a stackup's bottom boundary is the plane and the bottom
/// layer's copper sits beside the trace (brief-impedance-8 follow-up). Meshed whole, the face on the
/// plane coincides with its own image and the system is singular. Omitted, the answer is the limit of
/// the same copper lifted off the plane; the mesh is the size an ordinary cut's is; and a problem with
/// no such face meshes exactly as without the setting.
/// </summary>
public class FaceOnGroundTests
{
    private static EmConductor Box(string name, double x0, double x1, double y0, double y1) =>
        new(name, [new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)], double.PositiveInfinity);

    /// <summary>A 97 µm stripline 0.27 mm over the plane under a wide plane above, with bottom copper
    /// pulled back 0.25 mm either side of it, standing <paramref name="lift"/> off the image plane.</summary>
    private static EmProblem Stripline(double lift, bool bumps = true)
    {
        var conductors = new List<EmConductor>
        {
            Box("signal", -48.5e-6, 48.5e-6, 270e-6, 288e-6),
            Box("above", -1.2e-3, 1.2e-3, 450e-6, 468e-6),
        };
        if (bumps)
        {
            conductors.Add(Box("left", -1.2e-3, -0.3e-3, lift, lift + 35e-6));
            conductors.Add(Box("right", 0.3e-3, 1.2e-3, lift, lift + 35e-6));
        }
        EmDielectricRegion[] regions =
        [
            new(0, 600e-6, new EmMaterial(4.4, 0.02, 1)),
            new(600e-6, double.PositiveInfinity, EmMaterial.Air),
        ];
        return new EmProblem(conductors, regions, new EmGroundPlane(0, double.PositiveInfinity), [], 97e-6);
    }

    private static double Z0(EmProblem p, EmMeshSettings settings)
    {
        var mesh = BoundaryMesher.Mesh(p, settings).Mesh;
        double c = ChargeSolver.MaxwellCapacitance(mesh)[0, 0].Real;
        double c0 = ChargeSolver.MaxwellCapacitance(ChargeSolver.AirFilled(mesh))[0, 0].Real;
        return 1.0 / (EmConstants.C0 * Math.Sqrt(c * c0));
    }

    private static readonly EmMeshSettings Omit = EmMeshSettings.Default with { OmitFacesOnGround = true };

    /// <summary>Omitted, the face on the plane gives the limit of the same copper lifted off it — the
    /// lifted answers close on it linearly in the lift (61.7759 Ω at 0.25 µm against 61.7767 Ω) —
    /// where meshed whole it gives no answer at all.</summary>
    [Fact]
    public void CopperOnThePlane_IsTheLimitOfLiftingItOff()
    {
        double onPlane = Z0(Stripline(0), Omit);
        double lifted = Z0(Stripline(0.25e-6), EmMeshSettings.Default);
        double whole = Z0(Stripline(0), EmMeshSettings.Default);

        Assert.Equal(lifted, onPlane, 0.002);
        Assert.False(double.IsFinite(whole), $"meshed whole, the face on the plane solved to {whole} Ω");
    }

    /// <summary>A counter, not a timer: with the face omitted no segment lies on the plane and the
    /// mesh is the size of the same cut without that copper (376 against 273 unknowns), where meshed
    /// whole the zero substrate height graded the interface to 1,013.</summary>
    [Fact]
    public void CopperOnThePlane_MeshesLikeAnOrdinaryCut()
    {
        var omitted = BoundaryMesher.Mesh(Stripline(0), Omit);
        int ordinary = BoundaryMesher.Mesh(Stripline(0, bumps: false), EmMeshSettings.Default).UnknownCount;

        Assert.DoesNotContain(omitted.Mesh.Segments, sg => sg.A.Y == 0 && sg.B.Y == 0);
        Assert.True(omitted.UnknownCount < 2 * ordinary, $"{omitted.UnknownCount} unknowns against {ordinary}");
        Assert.True(BoundaryMesher.Mesh(Stripline(0), EmMeshSettings.Default).UnknownCount > 2 * ordinary);
    }

    /// <summary>With nothing on the plane the setting changes nothing, segment for segment.</summary>
    [Fact]
    public void WithNothingOnThePlane_TheMeshIsUnchanged()
    {
        var p = Stripline(8e-6);
        var with = BoundaryMesher.Mesh(p, Omit).Mesh.Segments;
        var without = BoundaryMesher.Mesh(p, EmMeshSettings.Default).Mesh.Segments;

        Assert.Equal(without, with);
    }
}
