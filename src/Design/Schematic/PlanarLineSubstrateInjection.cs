using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// The stackup binding for CPWG and SLIN (brief-artsch-1 R-as1-3) — <see cref="MicrostripSubstrateInjection"/>'s
/// rule for the two new line kinds. At extraction (and for a hand-written <c>.cnl</c>, in
/// <see cref="CnlTechnologyBinding"/>) the instance's substrate is resolved from the schematic's technology and
/// injected as plain-number overrides; none of it is a declared parameter.
///
/// <list type="bullet">
/// <item><b>CPWG</b> binds exactly as MLIN does — the signal layer and the nearest reference conductor below
/// it (above when there is none below) — and takes the same five numbers, H/T/Er/Sigma/TanD. G is the
/// instance's own; a technology has no gap.</item>
/// <item><b>SLIN</b> binds the nearest reference conductors above AND below the signal layer
/// (<see cref="SubstrateResolver.ResolveStripline"/>): H1, H2, T, Er, Sigma, TanD. A layer with one plane is
/// a REFUSAL naming the missing one, never a silent MLIN.</item>
/// </list>
/// </summary>
public static class PlanarLineSubstrateInjection
{
    /// <summary>The overrides each kind is given — what a line stating all of them has stated its substrate as.</summary>
    public static IReadOnlyList<string> InjectedNames(SymbolKind kind) => kind switch
    {
        SymbolKind.Cpwg => ["H", "T", "Er", "Sigma", "TanD"],
        SymbolKind.Slin => ["H1", "H2", "T", "Er", "Sigma", "TanD"],
        _ => [],
    };

    /// <summary>The layer-choice parameters each kind takes: resolution inputs, never engine parameters.</summary>
    public static IReadOnlyList<string> LayerParams(SymbolKind kind) => kind switch
    {
        SymbolKind.Cpwg => ["SignalLayer", "GroundReference"],
        SymbolKind.Slin => ["SignalLayer"],
        _ => [],
    };

    public static bool IsPlanarLineKind(SymbolKind kind) => kind is SymbolKind.Cpwg or SymbolKind.Slin;

    /// <summary>Every component whose substrate is injected from a stackup layer pair: the microstrip family,
    /// CPWG and SLIN. Where a site means "a line on the technology's stackup" rather than "the microstrip
    /// closed forms", this is its test (the audit is in src/Design/RESOLVED.md, AS-1).</summary>
    public static bool IsStackupLineKind(SymbolKind kind)
        => MicrostripSubstrateInjection.IsMicrostripKind(kind) || IsPlanarLineKind(kind);

    /// <summary>What <see cref="Build"/> resolved.</summary>
    /// <param name="Overrides">The injected substrate; empty when nothing resolved.</param>
    /// <param name="Warnings">Said, and the line still simulates.</param>
    /// <param name="Refusal">Set when the technology resolves but cannot carry this line — a SLIN on a layer
    /// with one plane. The line must not simulate on a fallback substrate.</param>
    public sealed record Binding(IReadOnlyList<ParameterAssignment> Overrides, IReadOnlyList<string> Warnings, string? Refusal);

    /// <summary>
    /// The substrate for one CPWG or SLIN. No technology at all is not a refusal — the model's standalone
    /// fallback stands, with the reason as a warning, exactly as MLIN's does — but a technology that HAS a
    /// stackup and cannot give a SLIN two planes is.
    /// </summary>
    public static Binding Build(Technology? technology, SymbolKind kind, string? signalLayer, string? groundReference)
    {
        if (kind == SymbolKind.Cpwg)
        {
            var (s, failure, warnings) = SubstrateResolver.ResolveElectrical(
                technology, new PCellLayerSelection(signalLayer, groundReference));
            if (s is null) return new Binding([], [failure?.Reason ?? "no substrate resolved"], null);

            // The resolver's own stripline sentence names the microstrip model; a coplanar line with a plane on
            // both sides is the stripline-with-coplanar-ground D13 sends to TLIN, and CPWG models only one.
            var said = warnings.Where(w => !w.Contains("this is a stripline", StringComparison.Ordinal)).ToList();
            if (s.IsStripline)
                said.Add($"'{s.SignalConductorName}' has a ground-designated conductor both above and below it; CPWG " +
                         $"models the backing plane '{s.GroundConductorName}' only, and the other plane is not in the answer.");
            return new Binding(
            [
                new("H", Fmt(s.HeightMeters)), new("T", Fmt(s.ThicknessMeters)), new("Er", Fmt(s.RelativePermittivity)),
                new("Sigma", Fmt(s.ConductivitySPerM)), new("TanD", Fmt(s.LossTangent)),
            ], said, null);
        }

        if (kind == SymbolKind.Slin)
        {
            var (sl, failure, warnings) = SubstrateResolver.ResolveStripline(technology, signalLayer);
            if (sl is null)
                return technology is null
                    ? new Binding([], [failure?.Reason ?? "no technology resolved"], null)
                    : new Binding([], warnings, failure?.Reason ?? "the technology cannot carry a stripline");
            return new Binding(
            [
                new("H1", Fmt(sl.H1Meters)), new("H2", Fmt(sl.H2Meters)), new("T", Fmt(sl.ThicknessMeters)),
                new("Er", Fmt(sl.RelativePermittivity)), new("Sigma", Fmt(sl.ConductivitySPerM)),
                new("TanD", Fmt(sl.LossTangent)),
            ], warnings, null);
        }

        return new Binding([], [], null);
    }

    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
