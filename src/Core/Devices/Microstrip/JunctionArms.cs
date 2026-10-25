using System.Numerics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// The arm lines of a microstrip junction (MTEE, MCROSS — brief DFR26-5 R-dfr26-5-3): pin k reaches the
/// junction model's own port k through an MLIN section running from the pin to the junction's reference
/// plane. The junction's closed forms (Gupta, Garg &amp; Chadha) are referred to planes at the crossing arm's
/// edge, so an arm drawn <c>L</c> long from the junction centre carries <c>L − (crossing width)/2</c> of
/// line before the junction begins.
///
/// <para>The sections are the MLIN model itself (<see cref="MicrostripLineModel.LineAt"/> stamped through
/// <see cref="TLineModel.StampUniformLine"/>, exactly what an MLIN instance stamps) — never a second line
/// model. They and the junction are assembled in a small local nodal block whose internal nodes (the
/// junction's own ports) are then Kron-reduced away, so the instance still stamps one P×P block on its
/// pins and allocates nothing. Eliminating a node locally is exact for a LINEAR block evaluated at one
/// frequency, which is all this is; the no-local-elimination rule in <c>src/Core/CLAUDE.md</c> concerns
/// nonlinear devices' internal nodes, whose voltages carry harmonics of their own.</para>
/// </summary>
internal static class JunctionArms
{
    /// <summary>An arm whose line to the reference plane is at most this long has none (1 nm, below any
    /// layout database unit) — a length typed as <c>L3 = W/2</c> lands within rounding of zero, not on it.</summary>
    public const double ZeroLengthMeters = 1e-9;

    /// <summary>The line section of one arm, or null when the arm has no length to the reference plane
    /// (that pin then IS the junction's port).</summary>
    public static MicrostripLineModel? Section(double lengthMeters, double wMeters, double hMeters,
        double tMeters, double epsR, double sigma, double tanD, double roughness, string instancePath)
        => lengthMeters > ZeroLengthMeters
            ? new MicrostripLineModel(wMeters, lengthMeters, hMeters, tMeters, epsR, sigma, tanD, instancePath, roughness)
            : null;

    /// <summary>
    /// Stamps the junction (<paramref name="stampJunction"/>, handed a context and the node of each of its
    /// ports) behind the arm sections onto <paramref name="pins"/>. An arm with no section connects its pin
    /// straight to the junction.
    /// </summary>
    public static void Stamp(IMnaContext mna, IReadOnlyList<int> pins, IReadOnlyList<MicrostripLineModel?> arms,
        double omega, Action<IMnaContext, int[]> stampJunction)
    {
        int p = pins.Count;
        var junctionPorts = new int[p];
        int size = p;
        for (int k = 0; k < p; k++)
            junctionPorts[k] = arms[k] is null ? k + 1 : ++size;

        var block = new LocalAdmittanceBlock(size);
        stampJunction(block, junctionPorts);
        double freqHz = omega / (2.0 * Math.PI);
        for (int k = 0; k < p; k++)
            if (arms[k] is { } arm)
                StampLine(block, k + 1, junctionPorts[k], arm, freqHz);

        block.EliminateFrom(p);
        for (int r = 0; r < p; r++)
            for (int c = 0; c < p; c++)
                mna.AddBlockAdmittance(pins[r], pins[c], block[r, c]);
    }

    /// <summary>One arm section between two nodes, as an MLIN stamps itself; a null section is a tie.</summary>
    public static void StampLine(IMnaContext mna, int a, int b, MicrostripLineModel? line, double freqHz)
    {
        if (line is null)
        {
            StampTie(mna, a, b);
            return;
        }
        var (z0, gammaLength) = line.LineAt(freqHz);
        TLineModel.StampUniformLine(mna, a, b, new Complex(z0, 0.0), gammaLength);
    }

    /// <summary>
    /// Two pins that are one point of copper, as an exact tie: a zero-volt branch (V_a − V_b = 0). A large
    /// conductance would do the same job inexactly — eliminating a floating pin behind 1e9 S cancels 1e9
    /// against 1e9 and leaves ~1e-7 S of rounding on the line it hangs from.
    /// </summary>
    public static void StampTie(IMnaContext mna, int a, int b)
    {
        int branch = mna.AddBranch();
        mna.AddBranchCurrent(branch, a, b);
        mna.AddConstraint(branch, a, Complex.One);
        mna.AddConstraint(branch, b, -Complex.One);
    }

    /// <summary>Every arm's queued validity findings, for the junction's own drain.</summary>
    public static IEnumerable<(string Key, string Message)> DrainSections(IEnumerable<MicrostripLineModel?> arms)
        => arms.Where(a => a is not null).SelectMany(a => a!.DrainWarnings());

    /// <summary>
    /// A dense nodal admittance block on local nodes 1..N (0 is ground, dropped as the engine's own matrix
    /// drops it). Only the admittance groups are meaningful here: nothing assembled into it allocates a
    /// branch.
    /// </summary>
    private sealed class LocalAdmittanceBlock(int size) : IMnaContext
    {
        private readonly Complex[,] _y = new Complex[size, size];
        private int _size = size;

        public Complex this[int r, int c] => _y[r, c];

        public void AddAdmittance(int nodeA, int nodeB, Complex y)
        {
            int a = nodeA - 1, b = nodeB - 1;
            if (a >= 0) _y[a, a] += y;
            if (b >= 0) _y[b, b] += y;
            if (a >= 0 && b >= 0) { _y[a, b] -= y; _y[b, a] -= y; }
        }

        public void AddBlockAdmittance(int rowNode, int colNode, Complex y)
        {
            int r = rowNode - 1, c = colNode - 1;
            if (r >= 0 && c >= 0) _y[r, c] += y;
        }

        /// <summary>Kron-reduces away every node at index ≥ <paramref name="keep"/>, last first; the
        /// remaining leading block is the port admittance seen with those nodes left floating.</summary>
        public void EliminateFrom(int keep)
        {
            for (int k = _size - 1; k >= keep; k--)
            {
                var pivot = _y[k, k];
                for (int i = 0; i < k; i++)
                {
                    if (_y[i, k] == Complex.Zero) continue;
                    var f = _y[i, k] / pivot;
                    for (int j = 0; j < k; j++)
                        _y[i, j] -= f * _y[k, j];
                }
            }
            _size = keep;
        }

        public int AddBranch() => throw Unsupported();
        public void AddBranchCurrent(int branch, int nodeFrom, int nodeTo) => throw Unsupported();
        public void AddConstraint(int branch, int node, Complex coeff) => throw Unsupported();
        public void AddNodeBranchCoupling(int node, int branch, Complex coeff) => throw Unsupported();
        public void AddBranchConstraint(int branch, int otherBranch, Complex coeff) => throw Unsupported();
        public void AddCurrentInjection(int node, Complex j) => throw Unsupported();
        public void AddSourceValue(int branch, Complex value) => throw Unsupported();

        private static NotSupportedException Unsupported()
            => new("A junction's local arm block holds admittances only.");
    }
}
