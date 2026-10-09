// Provenance and the component fields — brief-artsch-6-emit-and-target-cell.md R-as6-4, R-as6-5; overview D5, D12.
//
// The drawn schematic is NetlistSchematic.Build's, and Build knows nothing of artwork: what it draws is any
// netlist's drawing. The artwork fields are laid on AFTERWARDS, by instance name — FromArtwork, the anchor
// and what was measured on each component, and the schematic's ArtworkSource block. That is also why a
// .cnl → .csch drawing has none of them: nothing in a hand-written netlist came from artwork.

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>R-as6-4 and R-as6-5, written once.</summary>
public static class RecognitionProvenance
{
    /// <summary>The circuitRF version — the <c>VERSION</c> file, which <c>Directory.Build.props</c> stamps into
    /// every assembly's informational version (<c>AppVersion</c>'s source).</summary>
    public static string Version
    {
        get
        {
            var asm = typeof(RecognitionProvenance).Assembly;
            string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? asm.GetName().Version?.ToString();
            if (string.IsNullOrWhiteSpace(v)) return "unknown";
            int plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    /// <summary>The provenance block of a schematic written into <paramref name="schematicDirectory"/> from
    /// <paramref name="input"/> at <paramref name="utcNow"/>.</summary>
    public static ArtworkProvenance For(RecognitionInput input, string schematicDirectory, DateTime utcNow,
                                        RecognitionEmitOptions? emit = null)
    {
        var o = input.Options;
        var options = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["coplanar"] = o.Coplanar.ToString().ToLowerInvariant(),
            ["coplanarGapFactor"] = Num(o.CoplanarGapFactor),
            ["edgeReachMicrons"] = Num(o.EdgeReachMicrons),
            ["maxGroundViasPerPad"] = o.MaxGroundViasPerPad.ToString(CultureInfo.InvariantCulture),
            ["portMergeMicrons"] = Num(o.PortMergeMicrons),
            ["vias"] = o.Vias.ToString().ToLowerInvariant(),
        };
        if (o.GroundNet is { Length: > 0 } net) options["groundNet"] = net;
        if (o.GroundAt is { } at) options["groundAt"] = $"{at.X},{at.Y}";
        if (o.TopFrequencyHz is { } top) options["topFrequencyHz"] = Num(top);
        if (emit is { Digits: not RecognitionEmitOptions.DefaultDigits } e)
            options["digits"] = e.Digits.ToString(CultureInfo.InvariantCulture);
        if (emit is { ArtworkSides: false }) options["freeOrientation"] = "true";
        if (emit?.Sweep is { } sweep)
            options["sweep"] = $"{sweep.StartExpr} {sweep.StartUnit} to {sweep.StopExpr} {sweep.StopUnit}" +
                               (sweep.NumPoints is { } n ? $", {n} points" : $", step {sweep.StepExpr} {sweep.StepUnit}");

        var scope = input.Scope;
        return new ArtworkProvenance
        {
            Layout = input.ClayPath is { } clay ? SchematicTechnology.StoredRef(Path.GetFullPath(clay), schematicDirectory) : "",
            Scope = scope.IsWhole ? "whole" : IsRectangle(scope) ? "rectangle" : "polygons",
            Rings = scope.IsWhole ? null : [.. scope.Rings.Select(r => (long[])r.Clone())],
            Options = options,
            PartsCsvSha256 = input.PartsCsvText is { } text ? Sha256Text(text)
                             : input.PartsCsvPath is { } csv && File.Exists(csv) ? Sha256(csv) : null,
            Version = Version,
            CreatedUtc = utcNow.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Lays the artwork fields onto <paramref name="drawn"/> by instance name: every component the circuit
    /// created is <c>FromArtwork</c>, with its anchor and what was measured on it. The ground symbols the
    /// drawing adds are not the circuit's and are left alone.
    /// </summary>
    public static void Apply(SchematicEditModel drawn, RecognitionCircuit circuit)
    {
        var created = new HashSet<string>(circuit.TestBench.Instances.Select(i => i.InstanceName), StringComparer.Ordinal);
        foreach (var comp in drawn.Components)
        {
            if (!created.Contains(comp.InstanceName)) continue;
            comp.FromArtwork = true;
            comp.ArtworkAnchor.Clear();
            if (circuit.Anchors.TryGetValue(comp.InstanceName, out var anchor)) comp.ArtworkAnchor.AddRange(anchor);
            comp.ArtworkMeasured.Clear();
            if (circuit.Measured.TryGetValue(comp.InstanceName, out var measured))
                foreach (var (k, v) in measured) comp.ArtworkMeasured[k] = v;
        }
    }

    private static bool IsRectangle(RecognitionScope scope) =>
        scope.Rings is [{ Length: 8 } r] && r[1] == r[3] && r[2] == r[4] && r[5] == r[7] && r[6] == r[0];

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Sha256Text(string text) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
