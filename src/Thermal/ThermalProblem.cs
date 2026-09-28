// brief-em3d-74 R-em3d74-1a / -2 — what the solver is given, and nothing else: a mesh, a conductivity per region, heat
// sources, and conditions on tagged faces. No material names, no geometry, no file: the lowering in src/Design/Thermal
// builds this from the mesh's tags alone (R-em3d74-4c).
//
// TEMPERATURES ARE °C at this boundary, as at every boundary a user or a file touches (overview §1i). Steady conduction
// with fixed-temperature, convection and flux conditions is invariant under a shift of the temperature scale, and nothing
// the solver does depends on absolute temperature (there is no radiation), so the solver needs no kelvin anywhere; a
// conductivity function is simply called with the temperature in °C.

namespace CircuitRF.Thermal;

/// <summary>A region's thermal conductivity, W/(m·K): a constant, or a function of temperature with its slope — isotropic, or
/// (brief-em3d-76 R-em3d76-2c) a DIAGONAL tensor: the scalar times <see cref="Axes"/> along x, y and z.</summary>
public sealed class ThermalConductivity
{
    private ThermalConductivity(double nominal, Func<double, (double K, double Slope)>? ofT, (double X, double Y, double Z)? axes = null)
    {
        if (!(nominal > 0) || !double.IsFinite(nominal)) throw new ArgumentOutOfRangeException(nameof(nominal));
        Nominal = nominal;
        OfT = ofT;
        Axes = axes ?? (1, 1, 1);
    }

    /// <summary>What the scalar conductivity is multiplied by along x, y and z: (1, 1, 1) for an isotropic material.</summary>
    public (double X, double Y, double Z) Axes { get; }

    public bool IsIsotropic => Axes == (1, 1, 1);

    /// <summary>The value a constant-k solve uses: the constant, or the function's nominal value.</summary>
    public double Nominal { get; }

    /// <summary>k and dk/dT at a temperature (°C); null for a constant.</summary>
    public Func<double, (double K, double Slope)>? OfT { get; }

    public bool IsConstant => OfT is null;

    public static ThermalConductivity Constant(double k) => new(k, null);

    /// <summary>brief-em3d-76 R-em3d76-2c — a constant diagonal tensor (k_x, k_y, k_z), W/(m·K): an anisotropic region such
    /// as a via field's effective block. <see cref="Nominal"/> is the largest of the three.</summary>
    public static ThermalConductivity Diagonal(double kx, double ky, double kz)
    {
        foreach (double k in new[] { kx, ky, kz })
            if (!(k > 0) || !double.IsFinite(k)) throw new ArgumentOutOfRangeException(nameof(kx), "every component positive and finite");
        double n = Math.Max(kx, Math.Max(ky, kz));
        return new(n, null, (kx / n, ky / n, kz / n));
    }

    /// <summary>A temperature-dependent conductivity; <paramref name="nominal"/> is what the first (constant-k) solve
    /// uses, and what the whole run uses when k(T) is switched off. With <paramref name="axes"/> it is a diagonal tensor whose
    /// components all follow the one function: k_i(T) = k(T) · axes_i.</summary>
    public static ThermalConductivity Varying(double nominal, Func<double, (double K, double Slope)> ofT,
                                              (double X, double Y, double Z)? axes = null)
    {
        ArgumentNullException.ThrowIfNull(ofT);
        if (axes is { } a && new[] { a.X, a.Y, a.Z }.Any(v => !(v > 0) || !double.IsFinite(v)))
            throw new ArgumentOutOfRangeException(nameof(axes), "every component positive and finite");
        return new(nominal, ofT, axes);
    }
}

/// <summary>Heat generated uniformly through a region, W/m³.</summary>
public readonly record struct VolumeSource(int Region, double PowerDensityWm3);

/// <summary>Heat entering through (or generated on) the triangles of a surface tag, W/m² — an embedded sheet or a face.</summary>
public readonly record struct SurfaceSource(int Tag, double FluxWm2);

/// <summary>A convection (Robin) condition: heat leaves as h·(T − T∞).</summary>
public readonly record struct ConvectionCondition(int Tag, double HWm2K, double AmbientC);

/// <summary>A fixed-temperature (Dirichlet) condition on every node of a surface tag.</summary>
public readonly record struct FixedTemperature(int Tag, double TempC);

/// <summary>
/// brief-em3d-76 R-em3d76-3a — a fixed temperature that VARIES over a surface tag: each of its nodes takes
/// <paramref name="TempAt"/> at its own position (metres). A submodel's cut faces are fixed to the global solution this way.
/// </summary>
public sealed record FixedField(int Tag, Func<double, double, double, double> TempAt);

/// <summary>
/// One steady conduction problem. Every face not named by a condition is insulated. At least one fixed-temperature or
/// convection condition is needed for a steady state; the solver refuses a problem without.
/// </summary>
public sealed class ThermalProblem
{
    public required ThermalMesh Mesh { get; init; }

    /// <summary>One per region, indexed by <see cref="ThermalMesh.TetRegion"/>.</summary>
    public required IReadOnlyList<ThermalConductivity> Conductivity { get; init; }

    public IReadOnlyList<VolumeSource> VolumeSources { get; init; } = [];
    public IReadOnlyList<SurfaceSource> SurfaceSources { get; init; } = [];
    public IReadOnlyList<ConvectionCondition> Convection { get; init; } = [];

    /// <summary>In order: a node on two fixed faces takes the first one's temperature.</summary>
    public IReadOnlyList<FixedTemperature> Fixed { get; init; } = [];

    /// <summary>Fixed fields, applied after <see cref="Fixed"/>: a node a fixed face already took keeps that face's value.</summary>
    public IReadOnlyList<FixedField> FixedFields { get; init; } = [];
}

/// <summary>Which linear solver a solve uses.</summary>
public enum ThermalSolverKind
{
    /// <summary>Chosen by the number of unknowns (<see cref="ThermalSolveOptions.DirectBelow"/>).</summary>
    Auto,
    /// <summary>A sparse Cholesky factorisation (LU for a Newton step's nonsymmetric Jacobian).</summary>
    Direct,
    /// <summary>Preconditioned conjugate gradients with smoothed-aggregation AMG (BiCGStab for a Newton step).</summary>
    Iterative,
}

/// <summary>How a problem is solved.</summary>
public sealed record ThermalSolveOptions
{
    public ThermalSolverKind Solver { get; init; } = ThermalSolverKind.Auto;

    /// <summary>
    /// Below this many unknowns <see cref="ThermalSolverKind.Auto"/> takes the direct solver. brief 72 Q5 measured CSparse's
    /// Cholesky and CG + smoothed-aggregation AMG level at ~2,500 unknowns on its S5 ladder, AMG pulling away above it
    /// (11× at 90k); 5,000 leaves the robust one the tiny meshes.
    /// </summary>
    public int DirectBelow { get; init; } = 5000;

    /// <summary>The iterative solver's stopping point: the relative residual ‖b − Ax‖ / ‖b‖.</summary>
    public double RelativeTolerance { get; init; } = 1e-10;

    public int MaxIterations { get; init; } = 2000;

    /// <summary>The AMG's strength-of-connection threshold; brief 72 Q6 found 0 (every connection strong) fastest.</summary>
    public double AmgTheta { get; init; }

    /// <summary>Follow each region's k(T) by Newton's method. Off: every region at its nominal k.</summary>
    public bool KOfT { get; init; } = true;

    /// <summary>brief-em3d-77 R-em3d77-4b — follow each conductor's σ(T) in conductive balance. Off: σ at 20 °C everywhere.
    /// Read only by the electrothermal solve.</summary>
    public bool SigmaOfT { get; init; } = true;

    /// <summary>Newton stops when the largest nodal update is below this fraction of the temperature span (and the
    /// residual has fallen by 1e-8).</summary>
    public double NewtonTolerance { get; init; } = 1e-6;

    public int NewtonMaxIterations { get; init; } = 30;

    /// <summary>Where Newton starts: the previous sweep point's field (warm start). Null: the constant-k solution.</summary>
    public double[]? InitialGuess { get; init; }

    /// <summary>The assembly's parallelism; null for the machine's. The assembled matrix does not depend on it.</summary>
    public int? MaxDegreeOfParallelism { get; init; }

    public CancellationToken Cancellation { get; init; }
}
