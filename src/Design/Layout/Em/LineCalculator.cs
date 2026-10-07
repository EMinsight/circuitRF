// The line calculator (AA-3): a line's width, Z0, ε_eff, loss and guided wavelength on a technology
// layer with NOTHING DRAWN — the answer an agent or a designer wants before the line exists, which
// `impedance --survey` could not give because it needs a line already drawn.
//
// ── TWO ANSWERS, AND NEITHER IS A SECOND COPY ──────────────────────────────────────────────────
//
// The point is to see, before anything is drawn, whether the circuit model and the drawn line will
// agree — an outside agent's 67.8 Ω model line measured 64.0 Ω once drawn, and nothing told it until
// then. So both columns are computed by the code that produces them in the application:
//
//  - THE CIRCUIT MODEL is an MLIN instance ELABORATED: a one-instance test bench whose substrate is
//    MicrostripSubstrateInjection.BuildOverrides for the layer (what a schematic's extraction and a
//    .cnl's CnlTechnologyBinding inject), run through the Elaborator, and asked
//    MicrostripLineModel.LineParameters — the function its own Stamp calls. Not the closed forms
//    called again here: that would be a second evaluation that agrees today and drifts tomorrow.
//  - THE CROSS-SECTION is TraceImpedanceAnalysis.Analyze — what `circuitrf impedance` runs — on a
//    layout built in memory: one straight line of the width on the layer (and, for a coplanar line,
//    ground copper either side at the gap), selected by a pick so the ground strips are copper and
//    never traces under review. The stackup decides the reference exactly as it does on a drawn line.
//
// ── SYNTHESIS ───────────────────────────────────────────────────────────────────────────────────
//
// Each column solves for its own width, because the disagreement IS the answer:
//  - the model by HammerstadJensen.SynthesizeWidth — bisection on the static Compute the model calls,
//    over the model's validity range of W/h, 60 halvings (the parameter editor's MLIN Z0 field calls
//    the same function, so the two never disagree about the width a Z0 needs);
//  - the cross-section by a bracketed root on the cross-section solve itself: bracket from the model's
//    width (or the substrate height), then false position on ln W with the Illinois step, to ONE DBU —
//    the layout's resolution, below which no drawn line can be made. Z0 there is whatever the solve
//    gives at that width, reported, not assumed equal to the target.
// Both target the STATIC impedance (the quasi-static solve has no other); the model's dispersive Z0 at
// the frequency is reported beside it.

using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Em;

/// <summary>What <see cref="LineCalculator.Calculate"/> is asked.</summary>
/// <param name="Layer">A copper drawing layer's name, or a stackup conductor's.</param>
/// <param name="WidthsM">Widths to analyse, metres. One row each.</param>
/// <param name="TargetsZ0">Impedances to synthesise a width for, ohms. One row each.</param>
/// <param name="GapM">A coplanar line: ground copper on the signal's own layer this far from each edge.
/// Null is a microstrip.</param>
/// <param name="FreqHz">Where the dispersive Z0, ε_eff, loss and λg are evaluated; null reports the
/// static values only.</param>
public sealed record LineCalcRequest(
    string Layer,
    IReadOnlyList<double> WidthsM,
    IReadOnlyList<double> TargetsZ0,
    double? GapM = null,
    double? FreqHz = null);

/// <summary>The circuit model's answer at one width: an elaborated MLIN's
/// <see cref="MicrostripLineParameters"/>, with what its validity reporter said.</summary>
public sealed record LineCalcModel(
    double WidthM,
    MicrostripLineParameters Line,
    double? LambdaGM,
    IReadOnlyList<string> Warnings);

/// <summary>The cross-section's answer at one width: the station Z0 and ε_eff of a straight line drawn
/// in memory, through <see cref="TraceImpedanceAnalysis.Analyze"/>.</summary>
public sealed record LineCalcCrossSection(
    long WidthDbu,
    double WidthM,
    double? Z0,
    double? Eeff,
    double? LambdaGM,
    string Configuration,
    string? Refusal,
    IReadOnlyList<string> Notes);

/// <summary>One row: a width analysed, or an impedance synthesised.</summary>
/// <param name="TargetZ0">The impedance asked for, or null for a width asked for.</param>
/// <param name="Model">The model at its width (the synthesised one, for a target). Null when there is
/// no model for this line (coplanar, or no substrate resolves) or the target is out of its range.</param>
/// <param name="ModelRefusal">Why <paramref name="Model"/> is null.</param>
/// <param name="CrossSection">The cross-section at its width (its own synthesised one, for a target).</param>
/// <param name="CrossSectionAtModelWidth">For a target only: the cross-section of the MODEL's width —
/// what a line drawn at the width the schematic will use would measure.</param>
/// <param name="CrossSectionSolves">How many cross-section analyses the row took.</param>
public sealed record LineCalcRow(
    double? TargetZ0,
    LineCalcModel? Model,
    string? ModelRefusal,
    LineCalcCrossSection? CrossSection,
    LineCalcCrossSection? CrossSectionAtModelWidth,
    int CrossSectionSolves);

/// <summary>What <see cref="LineCalculator.Calculate"/> found.</summary>
public sealed record LineCalcResult
{
    public string? Refusal { get; init; }
    public bool Ok => Refusal is null;

    /// <summary>The drawing layer the line is on, and the stackup conductor it is bound to.</summary>
    public string LayerName { get; init; } = "";
    public string ConductorName { get; init; } = "";

    /// <summary>The substrate the MODEL is given — null when none resolves (the model then has no
    /// answer, and <see cref="SubstrateRefusal"/> says why).</summary>
    public ResolvedSubstrate? Substrate { get; init; }
    public string? SubstrateRefusal { get; init; }

    public double? GapM { get; init; }
    public double? FreqHz { get; init; }
    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;

    public IReadOnlyList<LineCalcRow> Rows { get; init; } = [];

    /// <summary>Things true of every row: the substrate resolver's warnings.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static LineCalcResult Refused(string why) => new() { Refusal = why };
}

public static class LineCalculator
{
    /// <summary>The line drawn for the cross-section is this many of its widths long — well past
    /// <see cref="TraceImpedanceAnalysis.MinAspect"/>, so it is a trace and not a pad.</summary>
    public const double LengthInWidths = 20;

    /// <summary>A coplanar ground strip is this many of (W + G) wide: past the reach of the cut once
    /// coplanar ground is found (TraceCrossSection.Cut bounds it at 6·(G + W) + W/2), so the strip's
    /// far edge is never in the solve and the ground reads as unbounded.</summary>
    public const double GroundStripInWidths = 10;

    /// <summary>The cross-section synthesis gives up after this many solves.</summary>
    public const int MaxSynthesisSolves = 60;

    /// <summary>The copper drawing layers of <paramref name="tech"/> — those bound to a stackup
    /// conductor — by name: what <see cref="ResolveLayer"/> accepts, and what a refusal lists.</summary>
    public static IReadOnlyList<string> CopperLayerNames(Technology tech)
    {
        var bound = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).SelectMany(l => l.DrawingLayers).ToHashSet();
        return [.. tech.Layers.Where(l => bound.Contains(l.Key)).Select(l => l.Name)];
    }

    /// <summary>The drawing layer and stackup conductor <paramref name="name"/> means: a copper drawing
    /// layer's name first, then a stackup conductor's (which draws on its first drawing layer). Null
    /// when it is neither, ignoring case.</summary>
    public static (LayerKey Key, string LayerName, StackupLayer Conductor)? ResolveLayer(Technology tech, string name)
    {
        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        if (tech.Layers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) is { } def
            && conductors.FirstOrDefault(c => c.DrawingLayers.Contains(def.Key)) is { } bound)
            return (def.Key, def.Name, bound);
        if (conductors.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { DrawingLayers.Count: > 0 } cond)
        {
            var key = cond.DrawingLayers[0];
            return (key, tech.Layers.FirstOrDefault(l => l.Key == key)?.Name is { Length: > 0 } n ? n : cond.Name, cond);
        }
        return null;
    }

    /// <summary>Every row of <paramref name="request"/>, both answers each. Never throws for a bad
    /// request: an unknown layer or an unusable stackup is a <see cref="LineCalcResult.Refusal"/>, and a
    /// column that has no answer for one row says why in that row.</summary>
    public static LineCalcResult Calculate(Technology tech, LineCalcRequest request, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(tech);
        ArgumentNullException.ThrowIfNull(request);
        var ct = control?.Token ?? CancellationToken.None;

        if (ResolveLayer(tech, request.Layer) is not { } layer)
            return LineCalcResult.Refused(
                $"'{request.Layer}' is not a copper layer of technology '{tech.Name}'. Its copper layers are: " +
                string.Join(", ", CopperLayerNames(tech)) + ".");
        var (_, _, _, stackRefusal) = TraceStack.StackOf(tech);
        if (stackRefusal is not null) return LineCalcResult.Refused(stackRefusal);
        if (request.GapM is { } g && !(g > 0)) return LineCalcResult.Refused("The coplanar gap must be a positive length.");
        if (request.FreqHz is { } f && !(f > 0 && double.IsFinite(f))) return LineCalcResult.Refused("The frequency must be a positive number of hertz.");

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        double metresPerDbu = 1e-6 / dbuPerMicron;

        // The substrate the model gets is the one the schematic's extraction injects for this conductor.
        var overrides = MicrostripSubstrateInjection.BuildOverrides(tech, out string? substrateWarning, layer.Conductor.Name);
        var (substrate, failure, _) = SubstrateResolver.ResolveElectrical(tech, new PCellLayerSelection(layer.Conductor.Name, null));
        var warnings = new List<string>();
        if (substrateWarning is not null && overrides.Count > 0) warnings.Add(substrateWarning);

        string? noModel = request.GapM is not null
            ? "no circuit component models a coplanar line, so the cross-section is the only answer"
            : overrides.Count == 0
                ? $"the circuit model has no substrate here: {failure?.Reason ?? substrateWarning ?? "the technology resolves none"}"
                : null;

        long? gapDbu = request.GapM is { } gm ? Math.Max(1, (long)Math.Round(gm / metresPerDbu)) : null;
        double hGuess = substrate?.HeightMeters ?? 100e-6;

        var rows = new List<LineCalcRow>();
        foreach (double w in request.WidthsM)
        {
            ct.ThrowIfCancellationRequested();
            long wDbu = Math.Max(1, (long)Math.Round(w / metresPerDbu));
            LineCalcModel? model = null;
            string? modelRefusal = noModel;
            if (noModel is null)
                (model, modelRefusal) = ModelAt(wDbu * metresPerDbu, overrides, request.FreqHz);
            var xs = CrossSectionAt(tech, layer, wDbu, gapDbu, request.FreqHz, dbuPerMicron, control);
            rows.Add(new LineCalcRow(null, model, modelRefusal, xs, null, 1));
        }

        foreach (double z in request.TargetsZ0)
        {
            ct.ThrowIfCancellationRequested();
            LineCalcModel? model = null;
            string? modelRefusal = noModel;
            if (noModel is null && substrate is not null)
            {
                var reporter = new MicrostripValidityReporter("(line calculator)");
                // The parameter editor's own synthesis, on the substrate as the model will read it
                // (the overrides are "R"-formatted, so these are the same doubles).
                double wModel = HammerstadJensen.SynthesizeWidth(z,
                    Parse(overrides, "H"), Parse(overrides, "T"), Parse(overrides, "Er"), reporter);
                var refusal = reporter.Drain();
                if (refusal.Count > 0) modelRefusal = string.Join(" ", refusal.Select(m => m.Message));
                else (model, modelRefusal) = ModelAt(wModel, overrides, request.FreqHz);
            }

            var (xs, solves) = SynthesizeCrossSection(tech, layer, z, model?.WidthM ?? hGuess, gapDbu,
                                                       request.FreqHz, dbuPerMicron, metresPerDbu, control);
            LineCalcCrossSection? atModel = null;
            if (model is not null)
            {
                long wm = Math.Max(1, (long)Math.Round(model.WidthM / metresPerDbu));
                atModel = xs is not null && xs.WidthDbu == wm
                    ? xs
                    : CrossSectionAt(tech, layer, wm, gapDbu, request.FreqHz, dbuPerMicron, control);
                if (!ReferenceEquals(atModel, xs)) solves++;
            }
            rows.Add(new LineCalcRow(z, model, modelRefusal, xs, atModel, solves));
        }

        return new LineCalcResult
        {
            LayerName = layer.LayerName,
            ConductorName = layer.Conductor.Name,
            Substrate = overrides.Count > 0 ? substrate : null,
            SubstrateRefusal = overrides.Count > 0 ? null : failure?.Reason ?? substrateWarning,
            GapM = gapDbu is { } gd ? gd * metresPerDbu : null,
            FreqHz = request.FreqHz,
            DbuPerMicron = dbuPerMicron,
            Rows = rows,
            Warnings = warnings,
        };
    }

    private static double Parse(IReadOnlyList<ParameterAssignment> overrides, string name) =>
        double.Parse(overrides.Single(o => o.Name == name).Expression, CultureInfo.InvariantCulture);

    /// <summary>The MLIN of width <paramref name="wM"/> on the injected substrate, ELABORATED — the
    /// model a run stamps, asked for its line parameters.</summary>
    private static (LineCalcModel? Model, string? Refusal) ModelAt(
        double wM, IReadOnlyList<ParameterAssignment> substrate, double? freqHz)
    {
        var tb = new TestBench("line");
        var parameters = new List<ParameterAssignment>
        {
            new("W", wM.ToString("R", CultureInfo.InvariantCulture)),
            new("L", "1e-3"),
        };
        parameters.AddRange(substrate);
        tb.Instances.Add(new Instance("TL1", "MLIN", ["a", "b"], parameters));
        try
        {
            using var netlist = new Elaborator(new Library("line")).Elaborate(tb);
            if (netlist.Components.Single().Model is not MicrostripLineModel mlin)
                return (null, "MLIN did not elaborate to a microstrip line model");
            var line = mlin.LineParameters(freqHz ?? 0.0);
            var warnings = mlin.DrainWarnings().Select(w => w.Message).ToList();
            double? lambda = freqHz is { } f ? MicrostripLoss.SpeedOfLight / (f * Math.Sqrt(line.Eeff)) : null;
            return (new LineCalcModel(wM, line, lambda, warnings), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>The cross-section of a straight line <paramref name="wDbu"/> wide drawn on the layer —
    /// <see cref="TraceImpedanceAnalysis.Analyze"/> on a layout built in memory.</summary>
    internal static LineCalcCrossSection CrossSectionAt(
        Technology tech, (LayerKey Key, string LayerName, StackupLayer Conductor) layer,
        long wDbu, long? gapDbu, double? freqHz, int dbuPerMicron, RunControl? control)
    {
        double metresPerDbu = 1e-6 / dbuPerMicron;
        long half = (long)Math.Round(0.5 * LengthInWidths * Math.Max(wDbu, (gapDbu ?? 0) + wDbu));
        long y0 = -(wDbu / 2), y1 = y0 + wDbu;
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = layer.Key, X1 = -half, Y1 = y0, X2 = half, Y2 = y1 },
        };
        if (gapDbu is { } g)
        {
            long strip = (long)Math.Round(GroundStripInWidths * (wDbu + g));
            shapes.Add(new RectShape { Layer = layer.Key, X1 = -half, Y1 = y1 + g, X2 = half, Y2 = y1 + g + strip });
            shapes.Add(new RectShape { Layer = layer.Key, X1 = -half, Y1 = y0 - g - strip, X2 = half, Y2 = y0 - g });
        }

        var scope = new TraceImpedanceScope();
        scope.Picks.Add(new TracePick(layer.LayerName, 0, (y0 + y1) / 2, TracePickExtent.Trace));
        var options = new TraceImpedanceOptions
        {
            Layers = [layer.Key],
            // Wide enough that the line is always a trace; it changes which copper is READ as a trace,
            // never a cut's answer.
            MaxWidthMicrons = 2.0 * wDbu / dbuPerMicron + 1,
            ViaTransitionMicrons = 0,
            Scope = scope,
        };

        var report = TraceImpedanceAnalysis.Analyze(shapes, tech, dbuPerMicron, options,
                                                     control is null ? null : new RunControl { Token = control.Token });
        double wM = wDbu * metresPerDbu;
        if (report.Refusal is { } why) return new LineCalcCrossSection(wDbu, wM, null, null, null, "", why, []);
        var trace = report.Layers.SelectMany(l => l.Traces).FirstOrDefault();
        if (trace is null)
            return new LineCalcCrossSection(wDbu, wM, null, null, null, "", "the line was not found as a trace", [.. report.Notes]);

        // Every cut along a uniform straight line is the same cut; the middle one is taken.
        var stations = trace.Stations.Where(s => s.Z0 is not null).ToList();
        if (stations.Count == 0)
            return new LineCalcCrossSection(wDbu, wM, null, null, null, trace.Configuration,
                trace.Stations.Select(s => s.Refusal).FirstOrDefault(r => r is not null) ?? "the cross-section has no answer",
                [.. trace.Notes]);
        var mid = stations.OrderBy(s => Math.Abs(s.X)).First();
        double? lambda = freqHz is { } f && mid.Eeff is { } e ? MicrostripLoss.SpeedOfLight / (f * Math.Sqrt(e)) : null;
        return new LineCalcCrossSection(wDbu, wM, mid.Z0, mid.Eeff, lambda, mid.Configuration ?? trace.Configuration, null, [.. trace.Notes]);
    }

    /// <summary>The width, to one DBU, whose cross-section Z0 is <paramref name="target"/>: a bracket
    /// grown from <paramref name="startM"/>, then false position on ln W (Illinois). Null with a
    /// refusal-carrying section when no width brackets it.</summary>
    private static (LineCalcCrossSection? Section, int Solves) SynthesizeCrossSection(
        Technology tech, (LayerKey Key, string LayerName, StackupLayer Conductor) layer, double target, double startM,
        long? gapDbu, double? freqHz, int dbuPerMicron, double metresPerDbu, RunControl? control)
    {
        int solves = 0;
        var seen = new Dictionary<long, LineCalcCrossSection>();
        LineCalcCrossSection At(long w)
        {
            if (seen.TryGetValue(w, out var hit)) return hit;
            solves++;
            return seen[w] = CrossSectionAt(tech, layer, w, gapDbu, freqHz, dbuPerMicron, control);
        }

        // Z0 falls as the line widens. Grow a bracket [lo, hi] with Z0(lo) > target > Z0(hi).
        long w0 = Math.Max(1, (long)Math.Round(startM / metresPerDbu));
        var first = At(w0);
        if (first.Z0 is not { } z0First) return (first, solves);
        long lo, hi;
        if (z0First > target)
        {
            lo = w0; hi = w0;
            do
            {
                lo = hi;
                hi = Math.Max(hi + 1, (long)Math.Round(hi * 1.6));
                if (solves >= MaxSynthesisSolves || At(hi).Z0 is not { } zh)
                    return (Unbracketed(At(hi), target, w0, hi), solves);
                if (zh <= target) break;
            } while (true);
        }
        else
        {
            hi = w0; lo = w0;
            do
            {
                hi = lo;
                lo = Math.Max(1, (long)Math.Round(lo / 1.6));
                if (lo == hi || solves >= MaxSynthesisSolves || At(lo).Z0 is not { } zl)
                    return (Unbracketed(At(lo), target, lo, w0), solves);
                if (zl >= target) break;
            } while (true);
        }

        // False position on ln W, Illinois-modified so neither end stalls, to one DBU.
        double fLo = At(lo).Z0!.Value - target, fHi = At(hi).Z0!.Value - target;
        int side = 0;
        while (hi - lo > 1 && solves < MaxSynthesisSolves)
        {
            double xl = Math.Log(lo), xh = Math.Log(hi);
            double x = fLo == fHi ? 0.5 * (xl + xh) : xh - fHi * (xh - xl) / (fHi - fLo);
            long w = Math.Clamp((long)Math.Round(Math.Exp(x)), lo + 1, hi - 1);
            if (At(w).Z0 is not { } zw) return (At(w), solves);
            double fw = zw - target;
            if (fw > 0) { lo = w; fLo = fw; if (side == -1) fHi /= 2; side = -1; }
            else        { hi = w; fHi = fw; if (side == +1) fLo /= 2; side = +1; }
        }

        var a = At(lo); var b = At(hi);
        return (Math.Abs(a.Z0!.Value - target) <= Math.Abs(b.Z0!.Value - target) ? a : b, solves);
    }

    private static LineCalcCrossSection Unbracketed(LineCalcCrossSection last, double target, long lo, long hi)
    {
        if (last.Refusal is not null) return last;
        return last with
        {
            Z0 = null, Eeff = null, LambdaGM = null,
            Refusal = $"no width from {lo} to {hi} DBU gives {target.ToString("0.##", CultureInfo.InvariantCulture)} Ω " +
                      $"(the last solved, {last.WidthDbu} DBU, gives {last.Z0?.ToString("0.##", CultureInfo.InvariantCulture)} Ω)",
        };
    }
}
