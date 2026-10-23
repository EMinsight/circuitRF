// ================================================================
//  EnvelopeBaseband.cs  —  a non-affine element across the baseband envelope
//
//  The baseband product (k = 0) recovers C from stamps at 1 Hz and 2 Hz,
//  which is exact for a stamp affine in jw.  A line's Y stamp is not: it
//  goes as 1/(jw L) there, so its slope at DC comes out ~1/(dlt^2 L) —
//  ~1e15 for a 25-ohm quarter-wave line at 1 GHz — and swamped the 0.02 S
//  terminations until the LU found no pivot (E = 90-450 deg failed; longer
//  lines, with more L, only just survived).  Such an element is HELD at
//  its DC stamp for the baseband product instead: exact at DC, where the
//  bias is, and the stamp the DC solve and the harmonic-balance start
//  already use, so the run still starts at its own equilibrium.  What is
//  lost is its frequency dependence across the baseband band, and the run
//  warns, naming it, when that is large enough to matter.
//
//  An element is held only when its own baseband tangent is not exact, so
//  R/L/C, the sources and a fitted SnP never are, and a lumped circuit's
//  run is unchanged bit for bit.
// ================================================================

using System.Globalization;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine.Envelope;

/// <summary>An element whose stamp is its own at every frequency except while <see cref="Holding"/>, when it is its DC stamp.</summary>
public sealed class BasebandHeldModel(ComponentModel inner) : ComponentModel, IReportsWarnings
{
    public ComponentModel Inner { get; } = inner;
    public bool Holding { get; set; }
    public override int PortCount => Inner.PortCount;
    public override ModelKind Kind => Inner.Kind;
    public override string[] TerminalNames => Inner.TerminalNames;
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) => Inner.Stamp(mna, c, Holding ? 0.0 : omega);
    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => Inner is IReportsWarnings rw ? rw.DrainWarnings() : [];
}

public static class EnvelopeBaseband
{
    /// <summary>A baseband tangent this far off (relative, terminated — <see cref="EnvelopeTangentCheck"/>'s measure) is not
    /// exact: round-off on an affine stamp is ~1e-12, a line's is ~1.</summary>
    public const double ExactBelow = 1e-6;

    /// <summary>The held elements of one run; <see cref="Holding"/> switches them all, <see cref="Dispose"/> puts them back.</summary>
    public sealed class Hold : IDisposable
    {
        private readonly ElaboratedNetlist _nl;
        private readonly List<(int Index, ElaboratedComponent Original, BasebandHeldModel Held)> _held = [];
        internal Hold(ElaboratedNetlist nl) => _nl = nl;
        internal void Add(int index, ElaboratedComponent original, BasebandHeldModel held, ElaboratedComponent replacement)
        {
            _held.Add((index, original, held));
            _nl.Components[index] = replacement;
        }
        public int Count => _held.Count;
        public bool Holding { set { foreach (var h in _held) h.Held.Holding = value; } }
        public void Dispose()
        {
            foreach (var (i, ec, _) in _held) _nl.Components[i] = ec;
            _held.Clear();
        }
    }

    /// <summary>
    /// Wraps every linear element whose baseband tangent is not exact, for one envelope run over a baseband band of
    /// ±<paramref name="bandRad"/>, and warns naming those whose DC stamp is off by more than
    /// <see cref="EnvelopeTangentCheck.WarnAbove"/> across it. Call it before the run's extractor is built (it replaces
    /// netlist entries) and before the products are stamped (stamping alone re-numbers a source's branch).
    /// </summary>
    public static Hold Apply(ElaboratedNetlist nl, double bandRad)
    {
        var hold = new Hold(nl);
        var costly = new List<(string Instance, double Error)>();
        for (int i = 0; i < nl.Components.Count; i++)
        {
            var ec = nl.Components[i];
            if (ec.IsNonlinear || ec.Model is ModSourceModel or IInductiveBranch or MutualInductanceModel or PortModel or TermModel
                    or VdcModel or ToneSourceModelBase or TunerModel or P1ToneModel or PnToneModel)
                continue;
            double tangent, held;
            try { (tangent, held) = EnvelopeTangentCheck.BasebandErrors(ec, bandRad); }
            catch (Exception) { continue; }     // an element that cannot be stamped alone is the run's to refuse
            if (tangent <= ExactBelow) continue;
            var model = new BasebandHeldModel(ec.Model);
            hold.Add(i, ec, model, new ElaboratedComponent(ec.ComponentType, ec.InstancePath, ec.Nodes, ec.Parameters, model)
            {
                ReferenceNode = ec.ReferenceNode,
                Multiplicity = ec.Multiplicity,
            });
            if (held > EnvelopeTangentCheck.WarnAbove) costly.Add((ec.InstancePath, held));
        }
        if (costly.Count > 0)
        {
            var inv = CultureInfo.InvariantCulture;
            string names = string.Join(", ", costly.OrderByDescending(x => x.Error).Select(x => string.Format(inv, "'{0}' ({1:0.#} %)", x.Instance, 100 * x.Error)));
            nl.AddWarningOnce("env-baseband", string.Format(inv,
                "envelope: {0} {1} held at its DC value across the ±{2:0.##} MHz baseband envelope, off by that much (RMS) across it; " +
                "the bias-path and baseband results through {3} are approximate",
                names, costly.Count == 1 ? "is" : "are", bandRad / (2 * Math.PI) / 1e6, costly.Count == 1 ? "it" : "them"));
        }
        return hold;
    }
}
