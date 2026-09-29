// brief-em3d-78 R-em3d78-2/-3 — RF harmonic currents in a thermal run: which wire array carries each port's harmonics, how the
// array's wires share them, and each wire's peak current per harmonic at a point.
//
// ARRAYS (R-em3d78-2). A wire array is the set of wires whose two ends land on the same two conductors. A .wBond's wires come
// grouped already — its arrays ARE its A matrix — and keep that grouping, named as their solids are (<instance>/wire/<array>);
// a .c3d's drawn wires are grouped here by their resolved end pads, and named by the drawn wires they came from (w1, or w1+w2).
// A drawn wire running the other way from the rest of its group is reversed first: the share assumes one direction, and a
// reversed wire's mutual inductance would enter with the wrong sign.
//
// THE SHARE (R-em3d78-3, D7) is wBond's, never re-derived: ArrayShare — a .wBond's own reduction, and for drawn wires the same
// reduction of their resolved AXES (before a foot is added: the centreline wBond reads). A .wBond keeps its ground plane; drawn
// wires are shared in free space, because a .c3d states no ground reference for the image to reflect in. Per unit current into
// array k every wire carries a column of X·L_arr — including the circulating current of the design's OTHER arrays, which sums to
// zero over each. Wires of different sources (two placed layouts, or a layout and the drawn wires) do not couple in the share.
//
// A PORT'S HARMONICS go to the ONE array with an end on its positive conductor; several is a refusal listing them (state the
// current per array instead). Two entries reaching one array — a two-port driven from both ends — use the larger magnitude per
// harmonic, and the notes give both. Two arrays carrying one harmonic add in MAGNITUDE on a wire they both reach: their phases
// are not taken, so the sum is the conservative bound.

using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Thermal.Electrothermal;
using CircuitRF.WBond;

namespace CircuitRF.Design.Thermal;

/// <summary>One wire array of a thermal run: its name, its wires (indices into the lowering's wires), its two conductors.</summary>
public sealed record ThermalWireArray(string Name, IReadOnlyList<int> Wires, string PadA, string PadB, bool FromWBond);

/// <summary>R-em3d78-2/-3 — a run's arrays, their shares, and the harmonic entries each array carries.</summary>
public sealed class ThermalRfPlan
{
    private ThermalRfPlan(IReadOnlyList<ThermalWireArray> arrays, double[][] share, IReadOnlyList<(CemThermalCurrent Entry, int Array)> entries,
                          string[][] wireLabels, bool byN)
    {
        Arrays = arrays;
        Share = share;
        Entries = entries;
        WireLabels = wireLabels;
        _byN = byN;
    }

    private readonly bool _byN;

    public IReadOnlyList<ThermalWireArray> Arrays { get; }

    /// <summary>[array][wire]: the current in each of the lowering's wires per ampere into the array.</summary>
    public double[][] Share { get; }

    /// <summary>Each current entry with harmonics, and the array it drives.</summary>
    public IReadOnlyList<(CemThermalCurrent Entry, int Array)> Entries { get; }

    /// <summary>Per wire, the harmonics it carries, by label (fixed for the run, so a sweep's cubes line up).</summary>
    public string[][] WireLabels { get; }

    /// <summary>The run's notes about its arrays and shares.</summary>
    public List<string> Notes { get; } = [];

    public bool Any => Entries.Count > 0;

    /// <summary>The label of harmonic <paramref name="n"/> of <paramref name="c"/>: <c>h2</c> when every entry states one F0, else
    /// prefixed with the entry's subject.</summary>
    public string Label(CemThermalCurrent c, int n) => LabelOf(_byN, c, n);

    private static string LabelOf(bool byN, CemThermalCurrent c, int n) => byN ? $"h{n}" : $"{(c.Array ?? $"port{c.Port}")}:h{n}";

    /// <summary>The wire arrays of <paramref name="plans"/>, as R-em3d78-2 groups them.</summary>
    public static List<ThermalWireArray> Group(C3dElaboration e, IReadOnlyList<ThermalWirePlan> plans) => Group(e, [.. plans.Select(p => p.Name)]);

    /// <summary>The wire arrays of the wires named <paramref name="names"/> (each one of <paramref name="e"/>'s wires), indices into
    /// that list.</summary>
    public static List<ThermalWireArray> Group(C3dElaboration e, IReadOnlyList<string> names)
    {
        var reports = e.Wires.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var arrays = new List<ThermalWireArray>();
        var wbond = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var drawn = new Dictionary<(string, string), List<int>>();
        for (int i = 0; i < names.Count; i++)
        {
            var r = reports[names[i]];
            if (r.Array.Length > 0)
            {
                string name = r.Name[..r.Name.LastIndexOf('/')];
                (wbond.TryGetValue(name, out var l) ? l : wbond[name] = []).Add(i);
            }
            else
            {
                var key = string.CompareOrdinal(r.Start.Pad, r.End.Pad) <= 0 ? (r.Start.Pad, r.End.Pad) : (r.End.Pad, r.Start.Pad);
                (drawn.TryGetValue(key, out var l) ? l : drawn[key] = []).Add(i);
            }
        }
        foreach (var (name, wires) in wbond)
        {
            var r = reports[names[wires[0]]];
            arrays.Add(new ThermalWireArray(name, wires, r.Start.Pad, r.End.Pad, true));
        }
        foreach (var ((a, b), wires) in drawn)
        {
            var bases = wires.Select(w => BaseName(names[w])).Distinct(StringComparer.Ordinal).ToList();
            arrays.Add(new ThermalWireArray(string.Join("+", bases), wires, a, b, false));
        }
        return arrays;
    }

    /// <summary>A drawn wire's name without its array index: <c>w1[3]</c> → <c>w1</c>.</summary>
    private static string BaseName(string name) => name.EndsWith(']') && name.LastIndexOf('[') is > 0 and var k ? name[..k] : name;

    /// <summary>
    /// The plan of <paramref name="t"/>'s harmonic currents over <paramref name="lowering"/>'s wires, or null with the refusal. A
    /// setup with no harmonics gives a plan with no entries (and no share is computed).
    /// </summary>
    /// <remarks><paramref name="fromCircuit"/> — the currents are a harmonic-balance run's (brief-em3d-79), so no Currents entry is
    /// the user's to change: a refusal names what can be changed there, the model's wires and ports.</remarks>
    public static ThermalRfPlan? Build(C3dElaboration e, ThermalLowering lowering, CemThermal t, out string? refusal, bool fromCircuit = false)
    {
        refusal = null;
        var plans = lowering.Wires;
        var withHarmonics = (t.Currents ?? []).Where(c => c.Harmonics is { Count: > 0 }).ToList();
        var arrays = Group(e, plans);
        if (withHarmonics.Count == 0) return new ThermalRfPlan(arrays, [], [], [.. plans.Select(_ => Array.Empty<string>())], true);

        string Listing() => arrays.Count == 0 ? "this model has none" : string.Join(", ", arrays.Select(a => $"'{a.Name}' ({a.PadA} – {a.PadB})"));
        var entries = new List<(CemThermalCurrent, int)>();
        foreach (var c in withHarmonics)
        {
            if (c.Array is { } name)
            {
                int k = arrays.FindIndex(a => a.Name == name);
                if (k < 0) { refusal = $"The thermal setup gives array '{name}' harmonic currents, and there is no such wire array: {Listing()}."; return null; }
                entries.Add((c, k));
                continue;
            }
            var port = lowering.RfPorts.First(p => p.Port == c.Port);
            var on = arrays.Select((a, k) => (a, k)).Where(x => x.a.PadA == port.PositiveSolid || x.a.PadB == port.PositiveSolid).ToList();
            if (on.Count == 0)
            {
                refusal = $"Port {c.Port} carries harmonic currents{(fromCircuit ? " (from the circuit's harmonic balance)" : "")}, and no wire array " +
                          $"has an end on its positive conductor '{port.PositiveSolid}': at RF a port's current is carried by the wires. Wire arrays: {Listing()}." +
                          (fromCircuit ? " Bond a wire to that conductor, or put the port on a conductor a wire array lands on." : "");
                return null;
            }
            if (on.Count > 1)
            {
                refusal = $"Port {c.Port} carries harmonic currents, and {on.Count} wire arrays have an end on its positive conductor " +
                          $"'{port.PositiveSolid}': {string.Join(", ", on.Select(x => $"'{x.a.Name}' ({x.a.PadA} – {x.a.PadB})"))}. Which carries how much " +
                          (fromCircuit
                              ? "is not decided here, and a circuit-driven run's currents are the circuit's, per port: give each array a port on a conductor " +
                                "only it lands on (its other end), so the circuit states each array's current."
                              : "is not decided here: state the current per array instead — a Currents entry with \"Array\": \"<name>\" in place of \"Port\".");
                return null;
            }
            entries.Add((c, on[0].k));
        }

        var share = Shares(e, plans, arrays, out var shareNotes);
        bool byN = withHarmonics.Select(c => (c.F0 ?? "").Trim()).Distinct(StringComparer.Ordinal).Count() == 1;
        var labels = new SortedSet<string>[plans.Count];
        for (int j = 0; j < plans.Count; j++) labels[j] = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (c, k) in entries)
            foreach (var h in c.Harmonics!)
                for (int j = 0; j < plans.Count; j++)
                    if (share[k][j] != 0) labels[j].Add(LabelOf(byN, c, h.N));
        var plan = new ThermalRfPlan(arrays, share, entries, [.. labels.Select(l => l.OrderBy(Order).ToArray())], byN);
        plan.Notes.AddRange(shareNotes);
        plan.Notes.Add("At RF each array's current divides among its wires by their inductance (wBond's share), the same at every harmonic " +
                       "and independent of σ; the DC current's share is the conduction solve's, which follows σ(T).");
        plan.Notes.Add("RF heat is applied per unit length along each wire's span, uniform around its section: across a wire tens of microns wide " +
                       "the temperature difference is negligible, so where the skin current flows does not matter to it. A foot lying on its pad " +
                       "takes DC heat only.");
        foreach (var multi in entries.GroupBy(x => x.Item2).Where(g => g.Count() > 1))
            plan.Notes.Add($"Array '{arrays[multi.Key].Name}' is given harmonic currents by {string.Join(" and ", multi.Select(x => x.Item1.Subject))}: " +
                           "per harmonic the larger magnitude is used (they differ by the shunt current between its ends).");
        if (entries.Select(x => x.Item2).Distinct().Count() > 1 && Enumerable.Range(0, plans.Count).Any(j => entries.Select(x => x.Item2).Distinct().Count(k => share[k][j] != 0) > 1))
            plan.Notes.Add("Two or more driven arrays reach the same wire (one layout's arrays share their wires' inductance): their currents at one " +
                           "harmonic add in magnitude there, the conservative bound, since their phases are not taken.");
        return plan;
    }

    /// <summary>Orders labels by harmonic number, not as text (h2 before h10).</summary>
    private static string Order(string label)
    {
        int at = label.LastIndexOf('h');
        return label[..at] + (int.TryParse(label[(at + 1)..], out int n) ? n.ToString("D6", CultureInfo.InvariantCulture) : label[(at + 1)..]);
    }

    /// <summary>[array][wire] shares: a .wBond's from its design, drawn wires' from their axes; zero between sources.</summary>
    private static double[][] Shares(C3dElaboration e, IReadOnlyList<ThermalWirePlan> plans, List<ThermalWireArray> arrays, out List<string> notes)
    {
        notes = [];
        var share = arrays.Select(_ => new double[plans.Count]).ToArray();
        var reports = e.Wires.ToDictionary(r => r.Name, StringComparer.Ordinal);

        // .wBond arrays: per placed layout, its design's own reduction
        var byInstance = arrays.Select((a, k) => (a, k)).Where(x => x.a.FromWBond)
                               .GroupBy(x => Prefix(reports[plans[x.a.Wires[0]].Name]), StringComparer.Ordinal);
        var fallback = new List<(ThermalWireArray Array, int K)>();
        foreach (var inst in byInstance)
        {
            var frame = e.Instances.FirstOrDefault(f => f.Layout && f.Path + "/" == inst.Key);
            var source = frame is null ? null : Em3dWireSource.ForLayout(frame.DocumentPath, out _, out _);
            if (source is null) { fallback.AddRange(inst.Select(x => (x.a, x.k))); continue; }
            var design = source.Design;
            var s = ArrayShare.For(design);
            var first = new int[design.Arrays.Count];
            for (int a = 1; a < first.Length; a++) first[a] = first[a - 1] + design.Arrays[a - 1].Wires.Count;
            // every wire of this layout that the run solves, by its place in the design
            var members = new List<(int Plan, int Design)>();
            for (int j = 0; j < plans.Count; j++)
            {
                var r = reports[plans[j].Name];
                if (r.Array.Length == 0 || Prefix(r) != inst.Key) continue;
                int a = design.Arrays.FindIndex(x => x.Name == r.Array);
                if (a >= 0 && r.Member >= 1 && r.Member <= design.Arrays[a].Wires.Count) members.Add((j, first[a] + r.Member - 1));
            }
            foreach (var (arr, k) in inst)
            {
                var r = reports[plans[arr.Wires[0]].Name];
                int a = design.Arrays.FindIndex(x => x.Name == r.Array);
                var per = s.PerUnitCurrent(a);
                foreach (var (j, d) in members) share[k][j] = per[d];
            }
            notes.Add($"'{Path.GetFileName(source.Path)}' ({inst.Key.TrimEnd('/')}): the RF share is the design's own inductance reduction, " +
                      $"with its ground plane {(design.GroundPlane.Enabled ? "on" : "off")}.");
        }

        // drawn wires (and a .wBond whose design could not be re-read): the same reduction of their axes, in free space
        var centreline = arrays.Select((a, k) => (a, k)).Where(x => !x.a.FromWBond).Select(x => (x.a, x.k)).Concat(fallback).ToList();
        if (centreline.Count > 0)
        {
            var wires = new List<ShareCentreline>();
            var planOf = new List<int>();
            foreach (var (arr, _) in centreline)
            {
                // every wire runs the way the array's first one does (as drawn: a reversal would change the arithmetic's order)
                string from = reports[plans[arr.Wires[0]].Name].Start.Pad;
                foreach (int j in arr.Wires)
                {
                    var r = reports[plans[j].Name];
                    var axis = e.DrawnWires.TryGetValue(plans[j].Name, out var dw) && dw.Axis.Count >= 2 ? dw.Axis : plans[j].Centreline;
                    var pts = axis.Select(p => (p.X, p.Y, p.Z)).ToList();
                    if (r.Start.Pad != from) pts.Reverse();
                    wires.Add(new ShareCentreline(pts, plans[j].DiameterM, arr.Name));
                    planOf.Add(j);
                }
            }
            var (s, index) = ArrayShare.FromCentrelines(wires, groundPlane: false);
            foreach (var (arr, k) in centreline)
            {
                int a = Array.IndexOf(s.ArrayNames, arr.Name);
                var per = s.PerUnitCurrent(a);
                for (int i = 0; i < planOf.Count; i++) share[k][planOf[i]] = per[index[i]];
            }
            notes.Add($"{centreline.Sum(x => x.Item1.Wires.Count)} drawn wire(s) in {centreline.Count} array(s): the RF share is wBond's inductance " +
                      "reduction of their axes, in free space (a 3D view states no ground plane for the image method).");
            if (fallback.Count > 0)
                notes.Add($"{fallback.Count} .wBond array(s) could not be re-read from their layout, so their share was taken from the wires' " +
                          "resolved centrelines instead.");
        }
        return share;
    }

    /// <summary>A .wBond wire's instance prefix: its name less <c>wire/&lt;array&gt;/&lt;member&gt;</c>.</summary>
    private static string Prefix(Em3dWireReport r)
    {
        string tail = $"wire/{r.Array}/{r.Member}";
        return r.Name.EndsWith(tail, StringComparison.Ordinal) ? r.Name[..^tail.Length] : "";
    }

    /// <summary>One harmonic as evaluated at a point: its frequency and its peak into its array.</summary>
    private readonly record struct Drive(double FrequencyHz, double PeakA);

    /// <summary>
    /// R-em3d78-1 — every wire's harmonics at a point: <paramref name="value"/> evaluates an expression (NaN with the refusal
    /// recorded by the caller), every current is scaled by <paramref name="scale"/>, RMS becomes peak × √2. <paramref name="notes"/>,
    /// when given, receives the both-ends comparison.
    /// </summary>
    public List<WireHarmonic>[] At(Func<string?, string, ThermalQuantity, double> value, double scale, List<string>? notes = null)
    {
        int nw = WireLabels.Length;
        // one drive per array per FREQUENCY: two entries on one array at one frequency are its two ends, and the larger is used —
        // whether or not their F0 texts are spelled alike ("Ffund" and "2 GHz" are one frequency, not two to add)
        var perArray = new Dictionary<(int Array, double FrequencyHz), (string Label, Drive Drive)>();
        var from = new Dictionary<(int, double), List<(string Subject, double Peak)>>();
        foreach (var (c, k) in Entries)
        {
            double f0 = value(c.F0, $"{Cap(c.Subject)}'s F0", ThermalQuantity.Frequency);
            foreach (var h in c.Harmonics!)
            {
                double amp = value(h.Amp, $"{Cap(c.Subject)}'s harmonic {h.N}", ThermalQuantity.Current);
                double peak = scale * Math.Abs(h.As == ThermalAmplitude.Rms ? amp * Math.Sqrt(2) : amp);
                double f = h.N * f0;
                var key = (k, f);
                (from.TryGetValue(key, out var l) ? l : from[key] = []).Add((c.Subject, peak));
                if (!perArray.TryGetValue(key, out var d) || peak > d.Drive.PeakA) perArray[key] = (Label(c, h.N), new Drive(f, peak));
            }
        }
        if (notes is not null)
            foreach (var ((k, fHz), list) in from.Where(x => x.Value.Count > 1))
            {
                double hi = list.Max(x => x.Peak), lo = list.Min(x => x.Peak);
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Array '{Arrays[k].Name}' at {fHz:G6} Hz: {string.Join(", ", list.Select(x => $"{x.Subject} {x.Peak:G4} A"))} (peak); the larger, {hi:G4} A, " +
                    $"is used — {hi - lo:G4} A apart."));
            }
        var wires = new List<WireHarmonic>[nw];
        for (int j = 0; j < nw; j++)
        {
            var list = wires[j] = [];
            foreach (string label in WireLabels[j])
            {
                double peak = 0, f = double.NaN;
                foreach (var ((k, _), (l, d)) in perArray)
                {
                    if (l != label || Share[k][j] == 0) continue;
                    peak += Math.Abs(Share[k][j]) * d.PeakA;
                    f = d.FrequencyHz;
                }
                list.Add(new WireHarmonic(label, f, peak));
            }
            // Two labels at one frequency (entries whose F0 texts differ — "Ffund" and "2 GHz" — so their labels carry their
            // subjects) are one harmonic on this wire: their magnitudes add, the header's conservative bound, into the first
            // label; the others read 0 here. Kept apart, the heat was ½(a² + b²)R′ instead of ½(a + b)²R′.
            for (int a = 0; a < list.Count; a++)
                for (int b = a + 1; b < list.Count; b++)
                {
                    if (!(list[a].FrequencyHz > 0) || list[b].PeakA == 0 ||
                        Math.Abs(list[a].FrequencyHz - list[b].FrequencyHz) > 1e-9 * list[a].FrequencyHz) continue;
                    string said = $"'{list[a].Label}' and '{list[b].Label}' are one frequency: on a wire both reach, their magnitudes add, " +
                                  $"carried under '{list[a].Label}'.";
                    if (notes is not null && !notes.Contains(said)) notes.Add(said);
                    list[a] = list[a] with { PeakA = list[a].PeakA + list[b].PeakA };
                    list[b] = list[b] with { PeakA = 0 };
                }
        }
        return wires;
    }

    /// <summary>R-em3d78-1 — the text of a current's harmonics as the Setups dialog shows them: <c>1: I1 Peak; 2: 0.12 Rms</c>.</summary>
    public static string HarmonicsText(IReadOnlyList<CemThermalHarmonic>? harmonics)
        => harmonics is null ? "" : string.Join("; ", harmonics.Select(h => $"{h.N}: {h.Amp}{(h.As is { } a ? " " + a : "")}"));

    /// <summary>The harmonics <paramref name="text"/> spells (<see cref="HarmonicsText"/>'s form), or null with the reason. Every one
    /// states Peak or Rms: D8, an amplitude is never inferred.</summary>
    public static List<CemThermalHarmonic>? ParseHarmonics(string text, out string? error)
    {
        error = null;
        var list = new List<CemThermalHarmonic>();
        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0 || !int.TryParse(part[..colon].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
            {
                error = $"'{part}' is not 'N: amplitude Peak|Rms' with N a harmonic number, at least 1.";
                return null;
            }
            var words = part[(colon + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            ThermalAmplitude? kind = words.Length >= 2 && Enum.TryParse<ThermalAmplitude>(words[^1], ignoreCase: true, out var k) ? k : null;
            if (kind is null)
            {
                error = $"Harmonic {n} states no Peak or Rms: say which its amplitude is — it is never assumed.";
                return null;
            }
            list.Add(new CemThermalHarmonic { N = n, Amp = string.Join(' ', words[..^1]), As = kind });
        }
        return list;
    }

    /// <summary>
    /// R-em3d78-1 — the DC-equivalent RMS of a current, √(I_dc² + Σ|Iₙ|²/2) with Iₙ the peaks: a READOUT, for comparison only — RF
    /// heats more than this DC current would, because of skin effect. Null when something in it does not evaluate.
    /// </summary>
    public static double? DcEquivalentRms(C3dResolution resolution, CemThermalCurrent c)
    {
        double sum = 0;
        if (!string.IsNullOrWhiteSpace(c.Dc))
        {
            if (C3dThermal.Evaluate(resolution, c.Dc, out _, ThermalQuantity.Current) is not { } dc) return null;
            sum += dc * dc;
        }
        foreach (var h in c.Harmonics ?? [])
        {
            if (h.As is not { } kind || string.IsNullOrWhiteSpace(h.Amp) || C3dThermal.Evaluate(resolution, h.Amp, out _, ThermalQuantity.Current) is not { } amp) return null;
            double peak = kind == ThermalAmplitude.Rms ? amp * Math.Sqrt(2) : amp;
            sum += peak * peak / 2;
        }
        return Math.Sqrt(sum);
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>R-em3d78-4a — a wire's AC resistance per unit length from wBond's exact Bessel solution, at the σ it is handed.</summary>
    public static AcResistance Bessel(double diameterM)
    {
        double radius = diameterM / 2;
        return (f, sigma) => InternalImpedance.ResistanceWithSigmaSlope(f, radius, sigma);
    }

    /// <summary>
    /// R-em3d78-4d — the wires longer than a tenth of the wavelength at their highest harmonic, in the densest dielectric their span
    /// runs through (<paramref name="epsr"/>, 1 in air): the current along them is taken as uniform, and a standing wave would move
    /// the hot spot, so each is named. Not a refusal.
    /// </summary>
    public static IEnumerable<string> ElectricallyLong(IReadOnlyList<ThermalWirePlan> plans, IReadOnlyList<IReadOnlyList<WireHarmonic>> harmonics,
                                                       IReadOnlyList<double> epsr)
    {
        const double c0 = 299_792_458;
        for (int j = 0; j < plans.Count; j++)
        {
            var top = harmonics[j].Where(h => h.PeakA > 0 && double.IsFinite(h.FrequencyHz)).MaxBy(h => h.FrequencyHz);
            if (top is null) continue;
            double lambda = c0 / (top.FrequencyHz * Math.Sqrt(Math.Max(1, epsr[j])));
            double len = plans[j].ChainLengthM;
            if (len > lambda / 10)
            {
                var inv = CultureInfo.InvariantCulture;
                double er = Math.Max(1, epsr[j]);
                yield return $"Wire '{plans[j].Name}' is {(len * 1e6).ToString("F0", inv)} µm long, more than a tenth of the wavelength at its " +
                             $"{top.Label} ({(top.FrequencyHz / 1e9).ToString("G4", inv)} GHz: λ/10 = {(lambda / 10 * 1e6).ToString("F0", inv)} µm with " +
                             $"εr {er.ToString("G3", inv)}). The RF current along it is taken as uniform; a standing wave would move its hot spot.";
            }
        }
    }
}
