using System.Globalization;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

// ──────────────────────────────────────────────────────────────────────────────
//  What a VIA or VIAGND is simulated as — SHOWN (brief-via-component.md R-viac-5).
//
//  The designer's question was which R and L to type next to an MLIN. The component answers it from
//  the stackup, so the numbers it computes are what this panel prints: the barrel's L, its R at DC
//  and at the top of the band the lumped model is claimed for, the capacitance, the planes the barrel
//  passes and the frequency the model is valid to. Computed through ViaSubstrateInjection.Evaluate —
//  the same injection a run makes, and the same factory — never a re-derivation that could disagree
//  with what is simulated.
// ──────────────────────────────────────────────────────────────────────────────

public partial class ParameterEditorViewModel
{
    /// <summary>True for the two via components.</summary>
    public bool IsViaTarget => _target is not null && ViaSubstrateInjection.IsViaKind(_target.Symbol);

    /// <summary>Whether the technology picker shows: every component whose numbers come from the
    /// stackup rather than from its own rows.</summary>
    public bool ShowsTechnologyPicker => IsMicrostripTarget || IsViaTarget;

    /// <summary>The readout, one fact per line. Empty when this is not a via.</summary>
    public string ViaReadoutText { get; private set; } = "";

    /// <summary>A resolution warning or an approximation the readout had to make. Empty when there is
    /// nothing to say.</summary>
    public string ViaReadoutNote { get; private set; } = "";

    public bool HasViaReadoutNote => ViaReadoutNote.Length > 0;

    private void RefreshViaReadout()
    {
        OnPropertyChanged(nameof(IsViaTarget));
        OnPropertyChanged(nameof(ShowsTechnologyPicker));
        if (!IsViaTarget)
        {
            ViaReadoutText = "";
            ViaReadoutNote = "";
            NotifyViaReadout();
            return;
        }

        var tech = (_schematicVm is { } svmTech ? SchematicTechnology.Of(svmTech.EditModel) : null);
        var injection = ViaSubstrateInjection.Build(tech, _target!.Symbol, _target.Parameters);
        var e = ViaSubstrateInjection.Evaluate(tech, _target.Symbol, _target.Parameters, out string? note);
        ViaReadoutText = e is null ? "" : Describe(e, injection.Span, _target.Symbol == SymbolKind.ViaGnd);

        // The injection's own messages are what the run will post; the readout shows them now.
        var notes = new List<string>(injection.Messages);
        if (note is not null) notes.Add(note);
        ViaReadoutNote = string.Join("\n", notes);
        NotifyViaReadout();
    }

    /// <summary>The readout's lines. Units chosen for a via's own scale: nH, mΩ, pF, mm.</summary>
    internal static string Describe(ViaSubstrateInjection.Electrical e,
                                    CircuitRF.Design.Layout.PCells.ResolvedViaSpan? span, bool grounded)
    {
        var ci = CultureInfo.InvariantCulture;
        var lines = new List<string>();
        if (span is not null)
        {
            string stub = (span.StubBeyondTo, span.StubBeyondFrom) switch
            {
                ({ } b, null) => string.Format(ci, ", stub {0:0.###} mm to {1}", b.LengthMeters * 1e3, b.EndConductorName),
                (null, { } a) => string.Format(ci, ", stub {0:0.###} mm to {1}", a.LengthMeters * 1e3, a.EndConductorName),
                ({ } b, { } a) => string.Format(ci, ", stubs to {0} and {1}", b.EndConductorName, a.EndConductorName),
                _ => "",
            };
            lines.Add(string.Format(ci, "{0} → {1}: barrel {2:0.###} mm{3}",
                span.From.Name, span.To.Name, span.LengthMeters * 1e3, grounded ? "" : stub));
            lines.Add(span.Planes.Count == 0
                ? "Passes no ground plane"
                : "Passes " + string.Join(", ", span.Planes.Select(p => p.Name)));
        }
        lines.Add(string.Format(ci, "Drill {0:0.###} mm · pad {1:0.###} mm · antipad {2:0.###} mm",
            e.Geometry.Drill * 1e3, e.Geometry.Pad * 1e3, e.Geometry.Antipad * 1e3));
        lines.Add(string.Format(ci, "L {0:0.###} nH · R {1:0.###} mΩ at DC, {2:0.###} mΩ at {3:0.##} GHz",
            e.Inductance * 1e9, e.DcResistance * 1e3, e.ResistanceAtLimit * 1e3, e.ValidityFrequency / 1e9));
        string c = string.Format(ci, "C {0:0.###} pF{1}", e.Capacitance * 1e12, grounded ? " (pad and planes)" : "");
        if (e.StubCapacitance > 0) c += string.Format(ci, " · stub {0:0.###} pF", e.StubCapacitance * 1e12);
        lines.Add(c);
        lines.Add(string.Format(ci, "Lumped model valid to {0:0.##} GHz (λ/20 in εr {1:0.##})",
            e.ValidityFrequency / 1e9, e.Geometry.EpsRMax));
        return string.Join("\n", lines);
    }

    private void NotifyViaReadout()
    {
        OnPropertyChanged(nameof(ViaReadoutText));
        OnPropertyChanged(nameof(ViaReadoutNote));
        OnPropertyChanged(nameof(HasViaReadoutNote));
    }
}
