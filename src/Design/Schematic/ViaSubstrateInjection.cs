using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// The via components' side of the substrate seam (brief-via-component.md R-viac-2): what
/// <see cref="MicrostripSubstrateInjection"/> is to MLIN. At extraction, a <c>VIA</c> or <c>VIAGND</c>
/// on a technology has its barrel length, materials and the planes it passes resolved from the stackup
/// (<see cref="SubstrateResolver.ResolveViaSpan"/>) and injected as plain SI overrides, so none of them is
/// typed twice.
///
/// <para><b>What the user states.</b> The layer names (<c>FromLayer</c>/<c>ToLayer</c>, or
/// <c>GroundLayer</c>) and the four dimensions <c>Drill</c>, <c>Pad</c>, <c>Antipad</c> and
/// <c>Plating</c>. Every one of them may be left empty, which means "follow the technology": the
/// layers default as <see cref="SubstrateResolver.ResolveViaSpan"/> says, the drill and pad come from the
/// technology's own via defaults, and the plating from the via layer's wall. What the technology does not
/// state takes circuitRF's default and is NAMED as a default in the message returned beside the
/// overrides (the R-L4d-7 precedent) — the antipad always, since no technology states one.</para>
/// </summary>
public static class ViaSubstrateInjection
{
    public const string FromLayerParam = "FromLayer";
    public const string ToLayerParam = "ToLayer";
    public const string GroundLayerParam = "GroundLayer";

    /// <summary>The layer-choice parameters — resolution inputs, never engine parameters.</summary>
    public static readonly IReadOnlySet<string> LayerParams =
        new HashSet<string>(StringComparer.Ordinal) { FromLayerParam, ToLayerParam, GroundLayerParam };

    /// <summary>The dimension parameters whose empty value means "follow the technology".</summary>
    public static readonly IReadOnlyList<string> DimensionParams = ["Drill", "Pad", "Antipad", "Plating"];

    public static bool IsViaKind(SymbolKind kind) => kind is SymbolKind.Via or SymbolKind.ViaGnd;

    /// <summary>
    /// <c>IncludeC</c> as the engine can read it. The row holds a WORD (<c>true</c>/<c>false</c>, the
    /// picker's spelling), and a bare word in an expression is a variable name: <c>IncludeC=true</c>
    /// elaborated as an unresolved variable and stopped the run. A word <see cref="BooleanParameter"/>
    /// accepts becomes <c>1</c>/<c>0</c>; anything else (an expression the user wrote) is left alone.
    /// </summary>
    public static string FlagExpression(string expression)
        => BooleanParameter.TryParse(new CircuitRF.Core.Expressions.Value(expression.Trim()), out bool b) ? (b ? "1" : "0") : expression;

    /// <summary>What a via instance resolves to: the overrides to append to its instance line, the
    /// sentences to post (defaults named, resolution warnings), and the span itself for a readout.</summary>
    public sealed record Injection(
        IReadOnlyList<ParameterAssignment> Overrides,
        IReadOnlyList<string> Messages,
        ResolvedViaSpan? Span);

    /// <summary>
    /// Builds the overrides for one via instance. <paramref name="parameters"/> are the instance's own;
    /// only the layer names and which dimensions are left empty are read from them.
    /// </summary>
    public static Injection Build(Technology? technology, SymbolKind kind, IEnumerable<EditableParameter> parameters)
    {
        var list = parameters as IReadOnlyList<EditableParameter> ?? parameters.ToList();
        string? Text(string name) => list.FirstOrDefault(p => p.Name == name)?.Expression is { } e && e.Trim().Length > 0
            ? e.Trim() : null;
        bool Empty(string name) => Text(name) is null;

        bool grounded = kind == SymbolKind.ViaGnd;
        var overrides = new List<ParameterAssignment>();
        var messages = new List<string>();
        var defaulted = new List<string>();

        var (span, failure, warnings) = SubstrateResolver.ResolveViaSpan(
            technology, Text(FromLayerParam), Text(grounded ? GroundLayerParam : ToLayerParam), grounded);
        messages.AddRange(warnings);

        if (span is null)
        {
            messages.Add($"{failure?.Reason ?? "no stackup resolved"} — the via uses its standalone defaults " +
                         $"({ComponentModelFactory.DefaultSubstrateHMeters * 1e3:G4} mm of εr {ComponentModelFactory.DefaultSubstrateEpsR:G3}, no planes crossed)");
            if (Empty("Drill")) defaulted.Add($"Drill = {Mm(ComponentModelFactory.DefaultViaDrillMeters)}");
            if (Empty("Pad")) defaulted.Add($"Pad = {Mm(ComponentModelFactory.DefaultViaPadMeters)}");
            if (Empty("Plating")) defaulted.Add($"Plating = {Um(ComponentModelFactory.DefaultViaPlatingMeters)}");
        }
        else
        {
            Add("H", span.LengthMeters);
            Add("Er", span.DielectricPermittivity);
            Add("ErMax", span.MaxRelativePermittivity);
            Add("Sigma", span.ConductivitySPerM);
            Add("Solid", span.Solid ? 1 : 0);
            AddPlanes("Planes", "Tp", "Erp", span.Planes);
            if (grounded)
            {
                Add("Tpad", span.DielectricThicknessMeters);
                Add("Erpad", span.DielectricPermittivity);
            }
            else
            {
                if (span.StubBeyondTo is { } sb)
                {
                    Add("Hstub", sb.LengthMeters);
                    AddPlanes("StubPlanes", "Tsp", "Ersp", sb.Planes);
                }
                if (span.StubBeyondFrom is { } sa)
                {
                    Add("HstubA", sa.LengthMeters);
                    AddPlanes("StubAPlanes", "TspA", "ErspA", sa.Planes);
                }
            }

            if (Empty("Drill"))
            {
                if (span.DrillMeters is { } d) Add("Drill", d);
                else defaulted.Add($"Drill = {Mm(ComponentModelFactory.DefaultViaDrillMeters)}");
            }
            if (Empty("Pad"))
            {
                if (span.PadMeters is { } p) Add("Pad", p);
                else defaulted.Add($"Pad = {Mm(ComponentModelFactory.DefaultViaPadMeters)}");
            }
            if (Empty("Plating"))
            {
                if (span.Solid) { }                        // a filled barrel has no wall to state
                else if (span.PlatingDefaulted) { Add("Plating", span.PlatingMeters); defaulted.Add($"Plating = {Um(span.PlatingMeters)}"); }
                else Add("Plating", span.PlatingMeters);
            }
        }

        // No technology states an antipad, so an empty one is always circuitRF's default.
        if (Empty("Antipad"))
            defaulted.Add($"Antipad = the pad + {Mm(ComponentModelFactory.DefaultViaAntipadRingMeters)}");

        if (defaulted.Count > 0)
        {
            bool one = defaulted.Count == 1;
            messages.Add((span is null ? $"With no technology to take {(one ? "it" : "them")} from, {(one ? "a default is" : "defaults are")} used: "
                                       : $"{(one ? "A default is" : "Defaults are")} used where the technology states none: ") +
                         string.Join("; ", defaulted) +
                         $". Set {(one ? "it" : "them")} on the component to use your own.");
        }

        return new Injection(overrides, messages, span);

        void Add(string name, double value) => overrides.Add(new ParameterAssignment(name, Fmt(value)));
        void AddPlanes(string count, string tKey, string erKey, IReadOnlyList<CrossedPlane> planes)
        {
            if (planes.Count == 0) return;
            Add(count, planes.Count);
            for (int i = 0; i < planes.Count; i++)
            {
                Add($"{tKey}{i + 1}", planes[i].ThicknessMeters);
                Add($"{erKey}{i + 1}", planes[i].RelativePermittivity);
            }
        }
    }

    /// <summary>The electrical numbers one via instance simulates with — what the parameter editor shows
    /// in place of the value a designer would otherwise have typed (brief-via-component.md R-viac-5).</summary>
    /// <param name="Inductance">The barrel's L, H.</param>
    /// <param name="DcResistance">The barrel's R at DC, Ω.</param>
    /// <param name="ResistanceAtLimit">Its R at <paramref name="ValidityFrequency"/>, Ω — the top of the
    /// band the lumped model is claimed for.</param>
    /// <param name="Capacitance">ΣC to the reference: the planes passed (and, for a via to ground, the pad), F.</param>
    /// <param name="StubCapacitance">What the stubs add, F; zero with no stub.</param>
    /// <param name="ValidityFrequency">Where the drill is a twentieth of a wavelength, Hz.</param>
    public sealed record Electrical(
        double Inductance, double DcResistance, double ResistanceAtLimit, double Capacitance,
        double StubCapacitance, double ValidityFrequency, ViaGeometry Geometry);

    /// <summary>
    /// Builds the instance's model exactly as a run would — the same injection, the instance's own typed
    /// dimensions — and reads its numbers back. A dimension written as an expression the readout cannot
    /// evaluate on its own (it names a variable) is left out, and <paramref name="note"/> says so: the run
    /// will use the variable's value, so the readout is then only approximate.
    /// </summary>
    public static Electrical? Evaluate(Technology? technology, SymbolKind kind, IEnumerable<EditableParameter> parameters,
                                       out string? note)
    {
        note = null;
        if (!IsViaKind(kind)) return null;
        var list = parameters as IReadOnlyList<EditableParameter> ?? parameters.ToList();
        var values = new Dictionary<string, CircuitRF.Core.Expressions.Value>(StringComparer.Ordinal);
        foreach (var o in Build(technology, kind, list).Overrides)
            if (double.TryParse(o.Expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                values[o.Name] = new CircuitRF.Core.Expressions.Value(v);

        var evaluator = new CircuitRF.Core.Expressions.Evaluator();
        var scope = new CircuitRF.Core.Expressions.Scope("global");
        var unevaluated = new List<string>();
        foreach (var p in list)
        {
            if (LayerParams.Contains(p.Name) || string.IsNullOrWhiteSpace(p.Expression)) continue;
            try
            {
                string unit = CircuitRF.Core.Expressions.UnitNormalizer.ToEngineUnit(p.Unit);
                string expr = p.Name == "IncludeC" ? FlagExpression(p.Expression) : p.Expression;
                values[p.Name] = evaluator.Eval(expr, scope, unit.Length > 0 ? unit : null);
            }
            catch { unevaluated.Add(p.Name); }
        }
        if (unevaluated.Count > 0)
            note = $"{string.Join(", ", unevaluated)} {(unevaluated.Count > 1 ? "are expressions" : "is an expression")} " +
                   "the readout cannot evaluate here, so its default is shown; the run uses the real value.";

        CircuitRF.Core.ComponentModel? model;
        try { model = ComponentModelFactory.TryCreate(ComponentTypeRegistry.EngineReference(kind), values); }
        catch (Exception ex) { note = ex.Message; return null; }

        (ViaGeometry g, double c) = model switch
        {
            ViaModel vm => (vm.Geometry, vm.Geometry.PlaneCapacitance),
            ViaGroundModel gm => (gm.Geometry, gm.TotalCapacitance),
            _ => (null!, 0.0),
        };
        if (g is null) return null;
        double fmax = g.ValidityFrequency;
        return new Electrical(g.Inductance, g.Resistance(0), g.Resistance(fmax), c,
            g.StubCapacitanceOf(g.StubPlanes) + g.StubCapacitanceOf(g.StubAPlanes), fmax, g);
    }

    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static string Mm(double metres) => (metres * 1e3).ToString("0.###", CultureInfo.InvariantCulture) + " mm";
    private static string Um(double metres) => (metres * 1e6).ToString("0.#", CultureInfo.InvariantCulture) + " µm";
}
