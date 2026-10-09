using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Devices.Planar;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Text;

namespace CircuitRF.Design.Schematic;

/// <summary>A parameter row a line-type swap set aside, whole: its expression and unit as written
/// (<see cref="EditableComponent.SwapRemembered"/>).</summary>
public readonly record struct RememberedParameter(string Expression, string Unit, bool ShowOnSchematic = true);

/// <summary>What a swap needs that the component does not carry.</summary>
/// <param name="Technology">The schematic's technology (<see cref="SchematicTechnology.Of"/>) — each line type
/// takes its own substrate from it; null is the models' standalone fallback board.</param>
/// <param name="FrequencyHz">F: the bench's top S-parameter frequency (<see cref="LineTypeSwap.BenchTopFrequencyHz"/>),
/// where a TLIN's Z and εeff are computed and a width is synthesised. 0 is the static line.</param>
/// <param name="GapMeters">A CPWG gap the user was ASKED for, used only when the component has neither a
/// measured nor a remembered one.</param>
public sealed record LineTypeSwapContext(Technology? Technology, double FrequencyHz = 0, double? GapMeters = null);

/// <summary>What <see cref="LineTypeSwap.Swap"/> returns: the new component, or why there is none.</summary>
public sealed record LineTypeSwapResult
{
    public EditableComponent? Component { get; init; }
    public string? Refusal { get; init; }
    public bool Ok => Component is not null;

    /// <summary>Refused ONLY for want of a CPWG gap — the GUI asks for one and swaps again with
    /// <see cref="LineTypeSwapContext.GapMeters"/>; headless it stays a refusal naming G.</summary>
    public bool NeedsGap { get; init; }

    /// <summary>True when the new line's W was synthesised from a TLIN's Z — the status line says so.</summary>
    public bool WidthSynthesised { get; init; }

    /// <summary>Things worth saying: a synthesised width, substrate overrides set aside, a static TLIN.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Swap Line Type (brief-artsch-11, overview D19): MLIN ↔ CPWG ↔ SLIN ↔ TLIN in place. Instance name, position,
/// wiring, flags and the artwork provenance are carried; W and L are held; Z0 follows from the new model. ONE
/// function, framework-free, so the GUI's context menu, the parameter editor's type combo and any later batch use
/// all swap the same way.
///
/// <para><b>Where a parameter comes from</b>, in order: the source's own row of that name (verbatim) → what an
/// earlier swap set aside (<see cref="EditableComponent.SwapRemembered"/>) → what was measured off the artwork →
/// derived (a CPWG gap asked for; a TLIN's Z/εeff computed from the source model at F; a width synthesised from a
/// TLIN's Z at F by the model the line calculator elaborates). Substrate values are never copied — each type takes
/// its own from the technology — so a stated per-instance override is carried only where the target has it.</para>
///
/// <para><b>Nothing is lost</b> (R-as11-3): a row the target lacks is set aside whole unless swapping straight
/// back would derive the very same text anyway — which is what makes a swap and its inverse the identity
/// without piling up memory a swap back would never use. Decided by running that inverse swap, not by a second
/// set of rules that could disagree with it.</para>
/// </summary>
public static class LineTypeSwap
{
    /// <summary>The four line types, in menu order.</summary>
    public static readonly IReadOnlyList<SymbolKind> LineKinds = [SymbolKind.Mlin, SymbolKind.Cpwg, SymbolKind.Slin, SymbolKind.Tline];

    public static bool IsLineKind(SymbolKind kind) => kind is SymbolKind.Mlin or SymbolKind.Cpwg or SymbolKind.Slin or SymbolKind.Tline;

    /// <summary>A component the command is offered on: a built-in line, not a cell instance.</summary>
    public static bool IsSwappable(EditableComponent? component) => component is { CellRef: null } && IsLineKind(component.Symbol);

    /// <summary>The menu row and type-combo text for a line type.</summary>
    public static string MenuName(SymbolKind kind) => kind switch
    {
        SymbolKind.Mlin  => "Microstrip (MLIN)",
        SymbolKind.Cpwg  => "Grounded Coplanar (CPWG)",
        SymbolKind.Slin  => "Stripline (SLIN)",
        SymbolKind.Tline => "Ideal Line (TLIN)",
        _ => ComponentTypeRegistry.DisplayName(kind),
    };

    private const double DbPerNeper = 8.685889638065035;

    /// <summary>A TLIN's own rows — what it states that no physical line has.</summary>
    private static readonly HashSet<string> TlinOwn = new(StringComparer.Ordinal) { "Z", "E", "F", "A", "Eeff", "Ac", "Ad" };

    /// <summary>The geometry a TLIN's remembered values belong to: the line's W/L/G rows when they were set
    /// aside. A '@' is never in a parameter name, so these cannot collide with one.</summary>
    private const string TlinGeometryPrefix = "TLIN@";
    private static readonly string[] TlinGeometry = ["W", "L", "G"];

    /// <summary>Per-instance substrate overrides each physical type has (the injection's own names).</summary>
    private static IReadOnlyList<string> SubstrateNames(SymbolKind kind) => kind switch
    {
        SymbolKind.Mlin => ["H", "T", "Er", "Sigma", "TanD", "Roughness"],
        SymbolKind.Cpwg => [.. PlanarLineSubstrateInjection.InjectedNames(SymbolKind.Cpwg), "Roughness"],
        SymbolKind.Slin => [.. PlanarLineSubstrateInjection.InjectedNames(SymbolKind.Slin), "Roughness"],
        _ => [],
    };

    private static readonly HashSet<string> AllSubstrateNames =
        new(new[] { SymbolKind.Mlin, SymbolKind.Cpwg, SymbolKind.Slin }.SelectMany(SubstrateNames), StringComparer.Ordinal);

    /// <summary>The highest frequency the schematic's S-parameter analyses sweep, or 0 when there is none (or
    /// none that evaluates without the design's variables) — F for a swap, and the impedance readout's F.</summary>
    public static double BenchTopFrequencyHz(SchematicEditModel model)
    {
        double top = 0;
        foreach (var a in model.Analyses)
        {
            if (a is not SParameterAnalysis sp) continue;
            try { foreach (double f in sp.Expand()) if (f > top && double.IsFinite(f)) top = f; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException) { }
        }
        return top;
    }

    /// <summary>
    /// <paramref name="component"/> as a <paramref name="toType"/> line — a NEW component (the input is not
    /// touched), or a refusal. One undo step in the GUI replaces the old state with this one.
    /// </summary>
    public static LineTypeSwapResult Swap(EditableComponent component, SymbolKind toType, LineTypeSwapContext context)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(context);
        return Swap(component, toType, context, remember: true);
    }

    private static LineTypeSwapResult Swap(EditableComponent comp, SymbolKind to, LineTypeSwapContext ctx, bool remember)
    {
        string name = comp.InstanceName;
        SymbolKind from = comp.Symbol;
        if (!IsSwappable(comp))
            return Refused($"'{name}' is not a line. Swap Line Type takes MLIN, CPWG, SLIN and TLIN.");
        if (!IsLineKind(to))
            return Refused($"{ComponentTypeRegistry.DisplayName(to)} is not a line type. A line swaps to MLIN, CPWG, SLIN or TLIN.");
        if (from == to)
            return Refused($"'{name}' is already {ComponentTypeRegistry.DisplayName(to)}.");

        var notes = new List<string>();
        var left = new SortedDictionary<string, RememberedParameter>(comp.SwapRemembered, StringComparer.Ordinal);
        string lengthUnit = Find(comp.Parameters, "W")?.Unit is { Length: > 0 } wu ? wu
                          : Find(comp.Parameters, "L")?.Unit is { Length: > 0 } lu ? lu
                          : MicrostripSubstrateInjection.LengthUnitFor(ctx.Technology);

        var built = to == SymbolKind.Tline
            ? TlinRows(comp, ctx, left, notes)
            : PhysicalRows(comp, to, ctx, left, lengthUnit, notes);
        if (built.Refusal is not null)
            return new LineTypeSwapResult { Refusal = built.Refusal, NeedsGap = built.NeedsGap };
        var rows = built.Rows!;

        // The rows the target has that the source stated beyond its own geometry: a substrate override the
        // target also takes, and anything artwork-only (a footprint), which no line type owns.
        var targetSubstrate = SubstrateNames(to);
        foreach (var p in comp.Parameters)
        {
            if (rows.Any(r => r.Name == p.Name)) continue;
            if (ArtworkParameters.IsArtworkOnly(p.Name) || targetSubstrate.Contains(p.Name)) rows.Add(p.Clone());
        }
        foreach (var n in targetSubstrate)
            if (!rows.Any(r => r.Name == n) && left.Remove(n, out var kept))
                rows.Add(Restored(n, kept, IsLengthName(n) ? UnitDimension.Length : UnitDimension.None));

        var result = comp.Clone();
        result.Symbol = to;
        result.Parameters.Clear();
        result.Parameters.AddRange(rows);
        // Label offsets past the type and the name are per PARAMETER row; the rows changed, so they reset.
        while (result.LabelOffsets.Count > 2) result.LabelOffsets.RemoveAt(result.LabelOffsets.Count - 1);
        result.FromArtwork = comp.FromArtwork;
        result.ArtworkAnchor.AddRange(comp.ArtworkAnchor);
        foreach (var (k, v) in comp.ArtworkMeasured) result.ArtworkMeasured[k] = v;
        foreach (var (k, v) in left) result.SwapRemembered[k] = v;

        if (!remember)
            return new LineTypeSwapResult { Component = result, WidthSynthesised = built.Synthesised, Notes = notes };

        // R-as11-3: set aside what the target has no row for — unless the swap straight back derives the same
        // text, in which case remembering it would only be memory nothing reads.
        var dropped = comp.Parameters
            .Where(p => p.Expression.Trim().Length > 0 && !rows.Any(r => r.Name == p.Name))
            .ToList();
        if (dropped.Count > 0)
        {
            var back = Swap(result, from, ctx with { GapMeters = null }, remember: false).Component;
            foreach (var p in dropped)
            {
                var again = back?.Parameters.FirstOrDefault(r => r.Name == p.Name);
                if (again is not null && again.Expression == p.Expression && again.Unit == p.Unit) continue;
                result.SwapRemembered[p.Name] = new RememberedParameter(p.Expression, p.Unit, p.ShowOnSchematic);
            }
            // A TLIN's Z and εeff are for the line it was: tie them to the geometry they now sit beside, so a swap
            // back after the width is edited computes rather than restoring a stale impedance.
            if (from == SymbolKind.Tline && result.SwapRemembered.Keys.Any(TlinOwn.Contains))
                foreach (var g in TlinGeometry)
                    if (Find(result.Parameters, g) is { } r)
                        result.SwapRemembered[TlinGeometryPrefix + g] = new RememberedParameter(r.Expression, r.Unit);

            var setAside = dropped.Where(p => AllSubstrateNames.Contains(p.Name)).Select(p => p.Name).ToList();
            if (setAside.Count > 0)
                notes.Add($"'{name}' stated {string.Join(", ", setAside)}, which {ComponentTypeRegistry.DisplayName(to)} does not take; "
                        + "set aside, and restored on a swap back.");
        }

        return new LineTypeSwapResult { Component = result, WidthSynthesised = built.Synthesised, Notes = notes };
    }

    private sealed record Built(List<EditableParameter>? Rows, string? Refusal, bool NeedsGap = false, bool Synthesised = false);

    // ── MLIN / CPWG / SLIN ──────────────────────────────────────────────────────────────────────────────

    private static Built PhysicalRows(EditableComponent comp, SymbolKind to, LineTypeSwapContext ctx,
        SortedDictionary<string, RememberedParameter> left, string lengthUnit, List<string> notes)
    {
        string name = comp.InstanceName;
        var src = comp.Parameters;
        var template = ComponentTypeRegistry.DefaultParameters(to, 2);
        var rows = new List<EditableParameter>();
        foreach (var t in template)
            if (t.Name is "SignalLayer" or "GroundReference")
                rows.Add(Carried(src, left, t) ?? FromTemplate(t, t.Expression, t.Unit));

        string? Layer(string n) => Find(rows, n)?.Expression is { Length: > 0 } s ? s.Trim('"') : null;

        // A SLIN the stackup cannot give two planes is the injection's own refusal, and nothing changes.
        if (to == SymbolKind.Slin && ctx.Technology is not null
            && PlanarLineSubstrateInjection.Build(ctx.Technology, SymbolKind.Slin, Layer("SignalLayer"), null).Refusal is { } slinRefusal)
            return new Built(null, $"'{name}' cannot be a stripline: {slinRefusal}.");

        // G first: a synthesised width needs it.
        double? gM = null;
        EditableParameter? gRow = null;
        if (to == SymbolKind.Cpwg)
        {
            var t = template.First(p => p.Name == "G");
            gRow = Carried(src, left, t);
            if (gRow is null && MeasuredGap(comp) is { } measured)
                gRow = FromTemplate(t, Fmt(measured / Scale(lengthUnit)), lengthUnit);
            if (gRow is null && ctx.GapMeters is { } asked && asked > 0)
                gRow = FromTemplate(t, Fmt(asked / Scale(lengthUnit)), lengthUnit);
            if (gRow is null)
                return new Built(null, $"'{name}' has no measured or earlier gap, and a CPWG needs one: state G.", NeedsGap: true);
            gM = Si(gRow);
        }

        var stated = StatedSubstrate(src, to);
        LineCalcModel? ModelAt(double wM) =>
            LineModel(to, wM, gM, Layer("SignalLayer"), Layer("GroundReference"), stated, ctx.Technology, ctx.FrequencyHz).Model;

        // W: held; else remembered; else measured off the artwork; else synthesised from the TLIN's Z at F.
        var wTemplate = template.First(p => p.Name == "W");
        var wRow = Carried(src, left, wTemplate);
        if (wRow is null && comp.ArtworkMeasured.TryGetValue("W", out double wMeasured) && wMeasured > 0)
            wRow = FromTemplate(wTemplate, Fmt(wMeasured / Scale(lengthUnit)), lengthUnit);
        bool synthesised = false;
        if (wRow is null)
        {
            if (to == SymbolKind.Cpwg && gM is null)
                return new Built(null, $"'{name}': G is an expression, so no width can be synthesised for it. Enter a number in G.");
            if (Si(Find(src, "Z")) is not { } z || !(z > 0))
                return new Built(null, $"'{name}' has no W to keep and its Z is not a plain number, so no width can be synthesised. State Z as a number.");
            if (ModelAt(1e-3) is not { } probe)
                return new Built(null, $"'{name}': the {ComponentTypeRegistry.DisplayName(to)} model could not be built on this technology.");
            var reporter = new MicrostripValidityReporter(name);
            double hScale = Math.Max(1e-6, stated.Concat(InjectedSubstrate(to, Layer("SignalLayer"), Layer("GroundReference"), ctx.Technology))
                .Where(a => a.Name is "H" or "H1" or "H2").Select(a => NumericText.TryParseDouble(a.Expression, out double v) ? v : 0)
                .DefaultIfEmpty(ComponentModelFactory.DefaultSubstrateHMeters).Min());
            double w = PlanarLineSynthesis.SynthesizeWidth(x => ModelAt(x)?.Line.Z0 ?? double.NaN, z, hScale, probe.Component, reporter);
            var refusal = reporter.Drain();
            if (refusal.Count > 0)
                return new Built(null, $"'{name}': {string.Join(" ", refusal.Select(m => m.Message))}");
            wRow = FromTemplate(wTemplate, Fmt(w / Scale(lengthUnit)), lengthUnit);
            synthesised = true;
            notes.Add($"'{name}': W was synthesised for Z = {Fmt(z)} Ω{(ctx.FrequencyHz > 0 ? " at " + Ghz(ctx.FrequencyHz) : " (static)")}.");
        }

        // L: held; else the TLIN's E converted at its F with its εeff (the new model's at W when it has none).
        var lTemplate = template.First(p => p.Name == "L");
        var lRow = Carried(src, left, lTemplate);
        if (lRow is null)
        {
            if (Si(Find(src, "E")) is not { } eRad)
                return new Built(null, $"'{name}' has neither L nor a numeric E, so its length is unknown. State L or E as a number.");
            double fHz = Si(Find(src, "F")) ?? 1e9;
            double? eeff = left.TryGetValue("Eeff", out var re) && NumericText.TryParseDouble(re.Expression, out double rv) ? rv
                         : comp.ArtworkMeasured.TryGetValue("Eeff", out double me) ? me : null;
            if (eeff is null)
            {
                if (Si(wRow) is not { } wM || LineModel(to, wM, gM, Layer("SignalLayer"), Layer("GroundReference"), stated, ctx.Technology, fHz).Model is not { } at)
                    return new Built(null, $"'{name}': W is an expression, so E cannot be converted to a length. Enter a number in W.");
                eeff = at.Line.Eeff;
            }
            double lM = eRad * MicrostripLoss.SpeedOfLight / (2 * Math.PI * fHz * Math.Sqrt(eeff.Value));
            lRow = FromTemplate(lTemplate, Fmt(lM / Scale(lengthUnit)), lengthUnit);
        }

        // The registry's order: W, (G), L, then the layer choices.
        var ordered = new List<EditableParameter>();
        foreach (var t in template)
            ordered.Add(t.Name switch
            {
                "W" => wRow,
                "G" => gRow!,
                "L" => lRow,
                _ => rows.First(r => r.Name == t.Name),
            });
        return new Built(ordered, null, Synthesised: synthesised);
    }

    // ── TLIN, physical form ───────────────────────────────────────────────────────────────────────────

    private static Built TlinRows(EditableComponent comp, LineTypeSwapContext ctx,
        SortedDictionary<string, RememberedParameter> left, List<string> notes)
    {
        string name = comp.InstanceName;
        var src = comp.Parameters;
        SymbolKind from = comp.Symbol;
        if (Si(Find(src, "W")) is not { } wM)
            return new Built(null, $"'{name}': W is not a plain number, so its Z cannot be computed. Enter a number in W.");
        double? gM = null;
        if (from == SymbolKind.Cpwg && (gM = Si(Find(src, "G"))) is null)
            return new Built(null, $"'{name}': G is not a plain number, so its Z cannot be computed. Enter a number in G.");
        var lSource = Find(src, "L");
        if (lSource is null)
            return new Built(null, $"'{name}' has no L.");

        string? Layer(string n) => Find(src, n)?.Expression is { Length: > 0 } s ? s.Trim('"') : null;
        double f = ctx.FrequencyHz;
        var (model, why) = LineModel(from, wM, gM, Layer("SignalLayer"), from == SymbolKind.Cpwg || from == SymbolKind.Mlin ? Layer("GroundReference") : null,
                                     StatedSubstrate(src, from), ctx.Technology, f);
        if (model is null)
            return new Built(null, $"'{name}': {why}");
        var p = model.Line;

        var rows = new List<EditableParameter>
        {
            Row("Z", Fmt(p.Z0), "Ω", UnitDimension.Resistance, true),
            lSource.Clone(),
            Row("Eeff", Fmt(p.Eeff), "", UnitDimension.None, true),
        };
        if (f > 0)
        {
            rows.Add(Row("F", Fmt(f / 1e9), "GHz", UnitDimension.Frequency, true));
            rows.Add(Row("Ac", Fmt(p.ConductorLossNpPerM * DbPerNeper), "", UnitDimension.None, false));
            rows.Add(Row("Ad", Fmt(p.DielectricLossNpPerM * DbPerNeper), "", UnitDimension.None, false));
        }
        else
            notes.Add($"'{name}': no S-parameter analysis states a frequency, so Z and Eeff are the static line's and the TLIN is lossless.");

        // What an earlier swap from a TLIN set aside comes back — only onto the geometry it was set aside beside.
        var own = left.Keys.Where(TlinOwn.Contains).ToList();
        if (own.Count > 0)
        {
            bool sameLine = TlinGeometry.All(g =>
            {
                var row = Find(src, g);
                bool had = left.TryGetValue(TlinGeometryPrefix + g, out var at);
                return had ? row is not null && row.Expression == at.Expression && row.Unit == at.Unit : row is null;
            });
            if (sameLine)
            {
                if (left.ContainsKey("E"))
                {
                    // The angle form it was: Z, E, F and A, whichever it stated.
                    var angle = new List<EditableParameter>();
                    foreach (var (n, unit, dim, show) in new[]
                    {
                        ("Z", "Ω", UnitDimension.Resistance, true), ("E", "deg", UnitDimension.Angle, true),
                        ("F", "GHz", UnitDimension.Frequency, true), ("A", "", UnitDimension.None, false),
                    })
                    {
                        if (left.TryGetValue(n, out var kept)) angle.Add(Restored(n, kept, dim));
                        else if (n is "Z" or "F" && Find(rows, n) is { } computed) angle.Add(computed);
                    }
                    rows = angle;
                }
                else
                    for (int i = 0; i < rows.Count; i++)
                        if (left.TryGetValue(rows[i].Name, out var kept))
                            rows[i] = Restored(rows[i].Name, kept, rows[i].Dimension);
            }
            else
                notes.Add($"'{name}': the TLIN values set aside earlier were for another width or length; Z and Eeff are computed for the line as it is.");
            foreach (var n in own) left.Remove(n);
        }
        foreach (var g in TlinGeometry) left.Remove(TlinGeometryPrefix + g);
        return new Built(rows, null);
    }

    // ── the model, as the line calculator elaborates it ─────────────────────────────────────────────────

    /// <summary>The <paramref name="kind"/> line at width <paramref name="wM"/> on the substrate its extraction
    /// injects (after the instance's stated overrides, which the injection supersedes exactly as it does in a
    /// run), elaborated and asked for its line parameters at <paramref name="freqHz"/> —
    /// <see cref="LineCalculator.ModelAt"/>, so this, the line calculator and a run read one model.</summary>
    private static (LineCalcModel? Model, string? Refusal) LineModel(SymbolKind kind, double wM, double? gM,
        string? signalLayer, string? groundReference, IReadOnlyList<ParameterAssignment> stated, Technology? tech, double freqHz)
    {
        if (kind == SymbolKind.Slin && tech is not null
            && PlanarLineSubstrateInjection.Build(tech, kind, signalLayer, null).Refusal is { } refusal)
            return (null, refusal);
        var substrate = new List<ParameterAssignment>(stated);
        substrate.AddRange(InjectedSubstrate(kind, signalLayer, groundReference, tech));
        string component = kind switch { SymbolKind.Mlin => "MLIN", SymbolKind.Cpwg => "CPWG", _ => "SLIN" };
        return LineCalculator.ModelAt(component, wM, gM, substrate, freqHz);
    }

    private static IReadOnlyList<ParameterAssignment> InjectedSubstrate(SymbolKind kind, string? signalLayer, string? groundReference, Technology? tech)
        => kind == SymbolKind.Mlin
            ? MicrostripSubstrateInjection.BuildOverrides(tech, out _, signalLayer, groundReference)
            : PlanarLineSubstrateInjection.Build(tech, kind, signalLayer, kind == SymbolKind.Cpwg ? groundReference : null).Overrides;

    /// <summary>The instance's own numeric substrate overrides the <paramref name="kind"/> model takes, in SI.</summary>
    private static IReadOnlyList<ParameterAssignment> StatedSubstrate(IReadOnlyList<EditableParameter> ps, SymbolKind kind)
        => [.. SubstrateNames(kind).Select(n => (n, v: Si(Find(ps, n)))).Where(t => t.v is not null)
                                   .Select(t => new ParameterAssignment(t.n, t.v!.Value.ToString("R", CultureInfo.InvariantCulture)))];

    // ── R-as11-4: discontinuities stay what they are ──────────────────────────────────────────────────

    /// <summary>
    /// One note per MBEND, MTEE, MCROSS or MTAPER sharing a net with a line swapped to anything but MLIN:
    /// those have no CPWG/SLIN/TLIN counterpart (D18) and stay as they are. Empty when the extraction fails —
    /// a note is not worth refusing a swap over.
    /// </summary>
    public static IReadOnlyList<string> DiscontinuityNotes(SchematicEditModel model, IEnumerable<string> swappedInstanceNames, SymbolKind toType)
    {
        if (toType == SymbolKind.Mlin) return [];
        TestBench tb;
        try { tb = NetExtractor.Extract(model).TestBench; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException or IOException)
        { return []; }

        var names = new HashSet<string>(swappedInstanceNames, StringComparer.Ordinal);
        var nets = tb.Instances.Where(i => names.Contains(i.InstanceName))
                     .SelectMany(i => i.NetBindings).Where(n => n != "0").ToHashSet(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var inst in tb.Instances)
        {
            string? what = inst.Reference.ToUpperInvariant() switch
            {
                "MBEND" => "bend", "MTEE" => "tee", "MCROSS" => "cross", "MTAPER" => "taper", _ => null,
            };
            if (what is not null && inst.NetBindings.Any(nets.Contains))
                notes.Add($"{inst.Reference.ToUpperInvariant()} {inst.InstanceName} stays a microstrip {what}.");
        }
        return notes;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static LineTypeSwapResult Refused(string why) => new() { Refusal = why };

    private static EditableParameter? Find(IEnumerable<EditableParameter> ps, string name) => ps.FirstOrDefault(p => p.Name == name);

    /// <summary>The source's row of the template's name, verbatim; else one an earlier swap set aside (consumed).</summary>
    private static EditableParameter? Carried(IReadOnlyList<EditableParameter> src, SortedDictionary<string, RememberedParameter> left, DefaultParam t)
    {
        if (Find(src, t.Name) is { } own) { left.Remove(t.Name); return own.Clone(); }
        return left.Remove(t.Name, out var kept) ? Restored(t.Name, kept, t.Dimension) : null;
    }

    private static EditableParameter Restored(string name, RememberedParameter kept, UnitDimension dim)
        => Row(name, kept.Expression, kept.Unit, dim, kept.ShowOnSchematic);

    private static EditableParameter FromTemplate(DefaultParam t, string expression, string unit)
        => Row(t.Name, expression, unit, t.Dimension, t.ShowOnSchematic);

    private static EditableParameter Row(string name, string expr, string unit, UnitDimension dim, bool show)
        => new() { Name = name, Expression = expr, Unit = unit, ShowOnSchematic = show, Dimension = dim };

    private static bool IsLengthName(string n) => n is "H" or "H1" or "H2" or "T" or "Roughness";

    /// <summary>The mean of the measured side gaps, metres — either alone when only one was measured.</summary>
    private static double? MeasuredGap(EditableComponent comp)
    {
        var gaps = new[] { "GapLeft", "GapRight" }
            .Select(k => comp.ArtworkMeasured.TryGetValue(k, out double v) && v > 0 ? v : (double?)null)
            .OfType<double>().ToList();
        return gaps.Count > 0 ? gaps.Average() : null;
    }

    /// <summary>A row's value in base SI, its unit applied; null when it is absent or not a plain number.</summary>
    private static double? Si(EditableParameter? p)
    {
        if (p is null || !NumericText.TryParseDouble(p.Expression.Trim(), out double raw)) return null;
        if (p.Unit.Length == 0) return raw;
        return Units.Scale(UnitNormalizer.ToEngineUnit(p.Unit)) is { } s ? raw * s : null;
    }

    private static double Scale(string unit) => Units.Scale(UnitNormalizer.ToEngineUnit(unit)) ?? 1e-3;

    /// <summary>A derived value: nine significant figures, which is past every model's own accuracy and short
    /// enough to read — and deterministic, which is what lets a swap back recognise its own derivation.</summary>
    private static string Fmt(double v) => v.ToString("G9", CultureInfo.InvariantCulture);

    private static string Ghz(double hz) => (hz / 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " GHz";
}
