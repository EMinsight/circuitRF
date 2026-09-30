namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// The artwork of the two via components (brief-via-component.md R-viac-4): a drill at the origin on
/// the stackup's via layer for the span, a pad on each of the two conductors it joins, and a pin on each
/// pad. Both pins are AT the origin, one per layer, so a line on either layer that ends there lands on
/// its pin and connects.
///
/// <para><b>No pads on the layers between</b> (the brief's D4): only the two conductors the via joins
/// get copper. <b>No plane either</b>: the ground planes a via passes are the stackup's, drawn once
/// for every line and via that returns through them by <see cref="GroundPourPlanner"/>, which cuts
/// this via's <c>Antipad</c> out of each one it passes (the brief's D1, as this round's pour settled
/// it). A plane patch per via would overlap its neighbours' and the lines'.</para>
///
/// <para>Parameters are the component's own: <c>FromLayer</c> and <c>ToLayer</c> (<c>GroundLayer</c>
/// for <see cref="GroundGeneratorId"/>) as conductor NAMES, empty for the stackup default, and the SI
/// lengths <c>Drill</c>/<c>Pad</c>/<c>Antipad</c>, absent for the technology's. Both ids resolve their
/// layers through <see cref="SubstrateResolver.ResolveViaSpan"/>, the same call the schematic's model
/// is resolved by, so the drawn via and the simulated one are the same via.</para>
/// </summary>
public static class ViaPCell
{
    public const string GeneratorId = "VIA";
    public const string GroundGeneratorId = "VIAGND";

    /// <summary>The fallback drill layer when no technology resolves — the key every shipped
    /// technology gives its plated through-hole.</summary>
    private static readonly LayerKey FallbackDrillLayer = new(9, 0);

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
        => Build(parameters, technology, grounded: false);

    public static PCellResult GenerateGround(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
        => Build(parameters, technology, grounded: true);

    /// <summary>What a via instance's parameters resolve to — shared with <see cref="GroundPourPlanner"/>,
    /// which needs the same span and the same antipad to cut its clearance.</summary>
    public sealed record Resolved(ResolvedViaSpan? Span, double Drill, double Pad, double Antipad, IReadOnlyList<string> Notes);

    public static Resolved Resolve(IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, bool grounded)
    {
        string? Name(string key) => parameters.Text(key) is { Length: > 0 } t ? t.Trim() : null;
        var (span, failure, warnings) = SubstrateResolver.ResolveViaSpan(
            technology, Name("FromLayer"), Name(grounded ? "GroundLayer" : "ToLayer"), grounded);
        var notes = new List<string>(warnings);
        if (failure is not null) notes.Add(failure.Reason);

        double drill = parameters.Real("Drill", span?.DrillMeters ?? ViaDefaultsSi.Drill);
        double pad = parameters.Real("Pad", span?.PadMeters ?? ViaDefaultsSi.Pad);
        double antipad = parameters.Real("Antipad", pad + ViaDefaultsSi.AntipadRing);
        if (drill <= 0) drill = ViaDefaultsSi.Drill;
        if (pad < drill) pad = drill;
        return new Resolved(span, drill, pad, antipad, notes);
    }

    private static PCellResult Build(IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, bool grounded)
    {
        var r = Resolve(parameters, technology, grounded);
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long drill = PCellUnits.MetresToDbu(r.Drill, dbuPerMicron);
        long pad = PCellUnits.MetresToDbu(r.Pad, dbuPerMicron);

        var fromLayer = r.Span is { From.DrawingLayers.Count: > 0 } s1 ? s1.From.DrawingLayers[0] : new LayerKey(1, 0);
        var toLayer = r.Span is { To.DrawingLayers.Count: > 0 } s2 ? s2.To.DrawingLayers[0] : fromLayer;
        var drillLayer = r.Span?.DrillLayer
                         ?? technology?.Stackup.Layers.FirstOrDefault(l => l.Kind == StackupKind.Via && l.DrawingLayers.Count > 0)?.DrawingLayers[0]
                         ?? FallbackDrillLayer;

        var shapes = new List<LayoutShape>
        {
            new ViaShape { Layer = drillLayer, X = 0, Y = 0, PadSize = pad, DrillSize = drill },
            new CircleShape { Layer = fromLayer, Cx = 0, Cy = 0, R = pad / 2 },
        };
        if (toLayer != fromLayer) shapes.Add(new CircleShape { Layer = toLayer, Cx = 0, Cy = 0, R = pad / 2 });

        var pins = new List<PCellPin> { new("A", 0, 0, fromLayer, pad, 0.0) };
        // A via to ground has no second terminal: the plane it lands on is the stackup's, not a pin.
        if (!grounded) pins.Add(new PCellPin("B", 0, 0, toLayer, pad, 0.0));

        return new PCellResult(shapes, pins, Diagnostics: r.Notes.Count > 0 ? r.Notes : null);
    }
}

/// <summary>The via defaults in SI — the model's own fallbacks, read from where they are declared so the
/// drawn via and the simulated one cannot default differently.</summary>
internal static class ViaDefaultsSi
{
    public const double Drill = CircuitRF.Core.Devices.ComponentModelFactory.DefaultViaDrillMeters;
    public const double Pad = CircuitRF.Core.Devices.ComponentModelFactory.DefaultViaPadMeters;
    public const double AntipadRing = CircuitRF.Core.Devices.ComponentModelFactory.DefaultViaAntipadRingMeters;
}
