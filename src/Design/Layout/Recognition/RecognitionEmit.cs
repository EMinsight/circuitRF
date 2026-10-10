// The circuit — brief-artsch-6-emit-and-target-cell.md R-as6-2; overview D1, D10, D15, D20;
// docs/design/artwork-to-schematic.md §7.
//
// AS-3's board, AS-4's parts and AS-5's lines become ONE TestBench — the type CnlReader produces, so the
// drawing (NetlistSchematic.Build) and the .cnl text (the CLI's -o x.cnl) are two spellings of the same
// circuit and neither is a second writer:
//
//   instances  parts by designator; lines TL…, bends B…, tees TEE…, crosses X…, tapers TP…, CPWG CP…, SLIN SL…,
//              TLIN fallbacks TF…, vias V… / VG…, ports as AS-3 named them — numbered along the signal path
//              from port 1, so a name reads the board in order;
//   nets       n<k> along the same walk; a port's net its name in lower case; ground 0;
//   globals    every unknown value a variable with a transparent start, and a tune entry for it;
//   analysis   SP1, the .cem's sweep, else 100 MHz – 6 GHz in 201 points (D15);
//   technology the artwork's .ctech (D20) — no line carries a substrate value; the binding injects it.

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>What the emitted circuit may be told beyond the recognition itself.</summary>
public sealed record RecognitionEmitOptions
{
    /// <summary>The S-parameter sweep (<c>--start/--stop/--npts</c>, the dialog's fields); null: the layout's
    /// EM setup's sweep, else <see cref="DefaultSweep"/> (D15).</summary>
    public FrequencySpec? Sweep { get; init; }

    /// <summary>100 MHz – 6 GHz, 201 points.</summary>
    public static FrequencySpec DefaultSweep => new("100", "6", 201, SweepKind.Linear, "MHz", "GHz");

    /// <summary>The significant figures every number the circuit carries is written with — a line's widths and
    /// lengths, a via's sizes, a port's Z, a part's value, a variable's start and its tuning range
    /// (<c>--digits</c>, the dialog's digits menu).</summary>
    public int Digits { get; init; } = DefaultDigits;

    /// <summary>What a run that does not say gets.</summary>
    public const int DefaultDigits = 6;

    /// <summary>Whether a shunt part is drawn on the side of its line that its copper is on (above or below) — the
    /// user's "Link symbol and footprint orientation" setting, <c>--free-orientation</c> off it. False draws every one
    /// below its line, as an unhinted drawing does (designer report, round 15).</summary>
    public bool ArtworkSides { get; init; } = true;

    /// <summary>Every figure a double carries that survives a round trip as text.</summary>
    public const int AllDigits = 15;

    /// <summary><paramref name="v"/> to <paramref name="digits"/> significant figures, in plain notation —
    /// <c>12.3457</c>, never <c>1.23457E+01</c>.</summary>
    public static string Spell(double v, int digits)
    {
        if (v == 0 || !double.IsFinite(v)) return v == 0 ? "0" : v.ToString(CultureInfo.InvariantCulture);
        double r = double.Parse(v.ToString("G" + Math.Clamp(digits, 1, AllDigits), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return r.ToString("0.###############", CultureInfo.InvariantCulture);
    }
}

/// <summary>The recognised circuit and what the drawing needs beside it.</summary>
public sealed record RecognitionCircuit(TestBench TestBench)
{
    /// <summary>Each created instance's artwork anchor, DBU (D12).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(long X, long Y)>> Anchors { get; init; } =
        new Dictionary<string, IReadOnlyList<(long X, long Y)>>();

    /// <summary>What was measured on each line and is not one of its parameters (R-as6-4).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Measured { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, double>>();

    /// <summary>Each instance's artwork point — the drawing's placement hints (R-as6-3).</summary>
    public IReadOnlyDictionary<string, (long X, long Y)> Hints =>
        Anchors.Where(a => a.Value.Count > 0).ToDictionary(a => a.Key, a => ArtworkAnchors_Centre(a.Value));

    /// <summary>What the emit could not write, one sentence each.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Lines and vias left out because the copper they model reaches no port and no part — each one's type
    /// and artwork anchor.</summary>
    public IReadOnlyList<(string Type, IReadOnlyList<(long X, long Y)> Anchor)> StrayLines { get; init; } = [];

    /// <summary>
    /// The <c>.cnl</c> text with every file it names — the technology and each Touchstone model — written
    /// relative to <paramref name="cnlDirectory"/>, where the file is going: what the reader resolves them
    /// against.
    /// </summary>
    public string CnlText(string cnlDirectory, string? header = "recognised from artwork")
    {
        var tb = TestBench;
        var copy = new TestBench(tb.Name) { Technology = Relative(tb.Technology, cnlDirectory), Tuning = tb.Tuning };
        copy.GlobalVariables.AddRange(tb.GlobalVariables);
        copy.Analyses.AddRange(tb.Analyses);
        foreach (var inst in tb.Instances)
            copy.Instances.Add(inst.Reference != "SnP" ? inst : new Instance(inst.InstanceName, inst.Reference, inst.NetBindings,
                inst.Overrides.Select(o => o.Name == "File" ? new ParameterAssignment("File", $"\"{Relative(o.Expression.Trim('"'), cnlDirectory)}\"") : o)));
        return CnlWriter.Write(copy, header);
    }

    private static string? Relative(string? path, string dir) =>
        path is null || !Path.IsPathRooted(path) ? path : Schematic.SchematicTechnology.StoredRef(path, dir);

    private static (long X, long Y) ArtworkAnchors_Centre(IReadOnlyList<(long X, long Y)> a) =>
        Schematic.ArtworkAnchors.Centre(a)!.Value;
}

/// <summary>R-as6-2, written once.</summary>
public static class RecognitionEmit
{
    /// <summary>The analysis the circuit carries.</summary>
    public const string AnalysisName = "SP1";

    /// <summary>The measured key of a part's direction on the board: pad 1 toward pad 2, degrees, the layout's axes.</summary>
    public const string PadAxisKey = "PadAxisDeg";

    private sealed class Proto
    {
        public required string Type { get; init; }          // the .cnl type token
        public required string Prefix { get; init; }        // the numbering series, or "" for a fixed name
        public string? FixedName { get; init; }             // a designator, a port's name
        public required List<string> Nodes { get; init; }   // node names before the union
        public List<ParameterAssignment> Parameters { get; } = [];
        public IReadOnlyList<(long X, long Y)> Anchor { get; init; } = [];
        public Dictionary<string, double> Measured { get; } = new(StringComparer.Ordinal);
        public int PortNumber { get; init; }
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// The circuit of <paramref name="result"/>, read from <paramref name="input"/>. Refuses (throws) only on a
    /// recognition that was itself refused.
    /// </summary>
    public static RecognitionCircuit Build(RecognitionResult result, RecognitionInput input, RecognitionEmitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(input);
        if (!result.Ok) throw new InvalidOperationException(result.Refusal ?? "The recognition produced no board.");
        options ??= new RecognitionEmitOptions();
        var board = result.Board!;
        var lines = result.Lines;
        var tech = input.Technology;
        var notes = new List<string>();
        var protos = new List<Proto>();
        int digits = options.Digits;
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);

        string Find(string x)
        {
            while (parent.TryGetValue(x, out var p) && p != x) x = parent[x] = parent.GetValueOrDefault(p, p);
            return x;
        }
        void Union(string a, string b)
        {
            string ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (rb == Ground) parent[ra] = rb; else parent[rb] = ra;   // ground is always the root
        }
        string NodeOf(string name) => lines.NodeOf(name);

        // The island every node is on, so a net the artwork NAMES (a shape's Net) names the circuit's net too
        // (designer report, round 16: a trace labelled in the layout came back as n7).
        var islandOfNode = new Dictionary<string, int>(StringComparer.Ordinal);
        void OnIsland(string node, int island) { if (island >= 0) islandOfNode.TryAdd(node, island); }
        foreach (var node in lines.Nodes) OnIsland(node.Name, node.Island);

        // ── ports ────────────────────────────────────────────────────────────────────────────────────
        foreach (var port in board.Ports.OrderBy(p => p.Number))
        {
            OnIsland(NodeOf($"P{port.Number}"), port.Island);
            var p = new Proto
            {
                Type = "Port", Prefix = "", FixedName = Identifier(port.Name, "P"), Nodes = [NodeOf($"P{port.Number}"), Ground],
                Anchor = [(port.X, port.Y)], PortNumber = port.Number,
            };
            p.Parameters.Add(new ParameterAssignment("Num", port.Number.ToString(CultureInfo.InvariantCulture)));
            p.Parameters.Add(new ParameterAssignment("Z", Num(port.Z0.Real, digits), "Ohm"));
            protos.Add(p);
        }

        // ── parts ────────────────────────────────────────────────────────────────────────────────────
        var variables = new List<(string Name, double Value, PartKind Kind, bool Shunt)>();
        foreach (var row in result.Parts.Rows.Where(r => r.IsModelled))
        {
            var ends = new List<string>();
            for (int t = 0; t < row.Terminals.Count && t < 2; t++)
            {
                var term = row.Terminals[t];
                ends.Add(term.Island >= 0 ? NodeOf($"{row.Refdes}.{t + 1}") : Ground);
                if (term.Island >= 0) OnIsland(ends[^1], term.Island);
            }
            if (ends.Count < 2) { notes.Add($"{row.Refdes}: fewer than two terminals were measured, so it is not in the circuit."); continue; }

            if (row.Kind == PartKind.Short) { Union(ends[0], ends[1]); continue; }
            if (row.Kind is PartKind.Open or PartKind.Ignore) continue;

            string name = Identifier(row.Refdes, row.ParameterName);
            // Which way the part lies on the board — pad 1 toward pad 2, degrees — so a layout made back from the
            // schematic can lay it as the board does (designer report, round 16).
            var (t1, t2) = (row.Terminals[0], row.Terminals[1]);
            double padAxis = Math.Round(Math.Atan2(t2.Y - t1.Y, t2.X - t1.X) * 180.0 / Math.PI, 6);
            if (row.Model == PartModelKind.SnP && result.Parts.ResolveModelFile(row) is { } file)
            {
                // A two-port: port 1 the signal end, port 2 the other end — ground for a shunt part (R-as6-2).
                var snp = new Proto { Type = "SnP", Prefix = "", FixedName = name, Nodes = ends, Anchor = [(row.X, row.Y)] };
                snp.Parameters.Add(new ParameterAssignment("NumPorts", "2"));
                snp.Parameters.Add(new ParameterAssignment("File", $"\"{file}\""));
                AddFootprint(snp, row);
                snp.Measured[PadAxisKey] = padAxis;
                protos.Add(snp);
                continue;
            }

            var part = new Proto { Type = row.ParameterName, Prefix = "", FixedName = name, Nodes = ends, Anchor = [(row.X, row.Y)] };
            if (row.Value is { } value)
            {
                var (number, unit) = Split(PartsTable.ValueText(value, row.GeneratedKind, digits));
                part.Parameters.Add(new ParameterAssignment(row.ParameterName, number, unit));
            }
            else
            {
                string variable = Identifier(row.Variable ?? row.DefaultVariable, "V");
                part.Parameters.Add(new ParameterAssignment(row.ParameterName, variable));
                if (!variables.Any(v => v.Name == variable))
                    variables.Add((variable, row.TransparentValue, row.GeneratedKind, row.Connection == PartConnection.Shunt));
            }
            AddFootprint(part, row);
            part.Measured[PadAxisKey] = padAxis;
            protos.Add(part);
        }

        // ── vias ─────────────────────────────────────────────────────────────────────────────────────
        for (int v = 0; v < board.Vias.Count; v++)
        {
            var via = board.Vias[v];
            for (int i = 0; i < via.Islands.Count; i++) OnIsland(NodeOf($"V{v + 1}.{i + 1}"), via.Islands[i]);
            switch (via.Element)
            {
                case ViaElement.Gnd:
                    foreach (int i in Enumerable.Range(0, via.Islands.Count)) Union(NodeOf($"V{v + 1}.{i + 1}"), Ground);
                    continue;
                case ViaElement.Via when via.Islands.Count >= 2:
                {
                    var p = new Proto { Type = "VIA", Prefix = "V", Nodes = [NodeOf($"V{v + 1}.1"), NodeOf($"V{v + 1}.2")], Anchor = [(via.X, via.Y)] };
                    AddLayer(p, "FromLayer", ConductorOf(tech, board, via, 0));
                    AddLayer(p, "ToLayer", ConductorOf(tech, board, via, 1));
                    AddViaSize(p, via, board.DbuPerMicron, digits);
                    protos.Add(p);
                    continue;
                }
                case ViaElement.ViaGnd or ViaElement.Via when via.Islands.Count >= 1:
                {
                    var p = new Proto { Type = "VIAGND", Prefix = "VG", Nodes = [NodeOf($"V{v + 1}.1"), Ground], Anchor = [(via.X, via.Y)] };
                    AddLayer(p, "FromLayer", ConductorOf(tech, board, via, 0));
                    AddViaSize(p, via, board.DbuPerMicron, digits);
                    protos.Add(p);
                    continue;
                }
                case ViaElement.None:
                    continue;
                default:
                    notes.Add($"A via at ({via.X}, {via.Y}) DBU joins no signal copper the circuit has, and is not in it.");
                    continue;
            }
        }

        // ── lines ────────────────────────────────────────────────────────────────────────────────────
        foreach (var e in lines.Elements)
        {
            var p = new Proto
            {
                Type = e.Type.ToString(), Prefix = LinePrefix(e.Type), Nodes = [.. e.Nodes.Select(NodeOf)], Anchor = e.Anchor,
            };
            foreach (var (k, value) in e.Parameters.OrderBy(kv => ParameterOrder(kv.Key)))
                p.Parameters.Add(LineParameter(k, value, digits));
            if (e.Type is not LineElementType.TLIN)
            {
                AddLayer(p, "SignalLayer", e.SignalLayer);
                if (e.Type is not LineElementType.SLIN) AddLayer(p, "GroundReference", e.GroundReference);
            }
            if (e.Z0 is { } z0) p.Measured["Z0"] = z0;
            if (e.Eeff is { } eeff) p.Measured["Eeff"] = eeff;
            if (e.GapLeft is { } gl) p.Measured["GapLeft"] = gl;
            if (e.GapRight is { } gr) p.Measured["GapRight"] = gr;
            if (e.Type == LineElementType.TLIN && e.Width is { } w) p.Measured["W"] = w;   // held for a swap (D19)
            protos.Add(p);
        }

        // ── one walk from port 1 orders the names and the nets ──────────────────────────────────────
        foreach (var p in protos) for (int i = 0; i < p.Nodes.Count; i++) p.Nodes[i] = Find(p.Nodes[i]);
        var stray = DropStrayLines(protos);
        var (elementOrder, nodeOrder) = Walk(protos);

        var taken = new HashSet<string>(protos.Where(p => p.FixedName is not null).Select(p => p.FixedName!), StringComparer.OrdinalIgnoreCase);
        // Two fixed names alike (two ports both named after a part's pad, say) — the later takes a suffix.
        var fixedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in elementOrder.Where(p => p.FixedName is not null))
        {
            string n = p.FixedName!;
            if (!fixedSeen.Add(n)) { int k = 2; while (taken.Contains($"{n}_{k}")) k++; n = $"{n}_{k}"; taken.Add(n); fixedSeen.Add(n); }
            p.Name = n;
        }
        var counter = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in elementOrder.Where(p => p.FixedName is null))
        {
            int k = counter.GetValueOrDefault(p.Prefix);
            string n;
            do n = $"{p.Prefix}{++k}"; while (taken.Contains(n));
            counter[p.Prefix] = k;
            taken.Add(n);
            p.Name = n;
        }

        // A net the artwork names takes that name: the copper's island names every node on it, so a labelled trace
        // a line element splits keeps its name on each piece — the first in walk order bare, the rest numbered
        // (RFin, RFin_2, …) — which is what makes a section findable by the name it was given in the layout.
        var islandName = board.Islands.Where(i => i.NetName is { Length: > 0 })
                                      .ToDictionary(i => i.Id, i => Identifier(i.NetName!, "N"));
        var statedName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (node, island) in islandOfNode.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (islandName.TryGetValue(island, out var stated) && Find(node) is var root && root != Ground
                && (!statedName.TryGetValue(root, out var had) || string.CompareOrdinal(stated, had) < 0))
                statedName[root] = stated;

        var netName = new Dictionary<string, string>(StringComparer.Ordinal) { [Ground] = Ground };
        var netsTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Ground };
        string TakeStated(string bare)
        {
            string n = bare;
            for (int k = 2; !netsTaken.Add(n); k++) n = $"{bare}_{k}";
            return n;
        }
        foreach (var p in protos.Where(p => p.Type == "Port").OrderBy(p => p.PortNumber))
            if (!netName.ContainsKey(p.Nodes[0]))
            {
                if (statedName.TryGetValue(p.Nodes[0], out var stated)) { netName[p.Nodes[0]] = TakeStated(stated); continue; }
                string n = p.Name.ToLowerInvariant();
                while (!netsTaken.Add(n)) n += "_";
                netName[p.Nodes[0]] = n;
            }
        int nk = 0;
        foreach (var node in nodeOrder)
            if (!netName.ContainsKey(node))
            {
                if (statedName.TryGetValue(node, out var stated)) { netName[node] = TakeStated(stated); continue; }
                string n;
                do n = $"n{++nk}"; while (!netsTaken.Add(n));
                netName[node] = n;
            }

        // ── the TestBench ─────────────────────────────────────────────────────────────────────────────
        var tb = new TestBench("tb") { Technology = input.TechnologyPath is { } tp ? Path.GetFullPath(tp) : null };
        foreach (var (name, value, kind, _) in variables)
        {
            var (number, unit) = Split(PartsTable.ValueText(value, kind, digits));
            tb.GlobalVariables.Add(new Variable(name, number, unit));
        }
        var anchors = new Dictionary<string, IReadOnlyList<(long X, long Y)>>(StringComparer.Ordinal);
        var measured = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal);
        foreach (var p in elementOrder)
        {
            tb.Instances.Add(new Instance(p.Name, p.Type, p.Nodes.Select(n => netName[n]), p.Parameters));
            if (p.Anchor.Count > 0) anchors[p.Name] = p.Anchor;
            if (p.Measured.Count > 0) measured[p.Name] = p.Measured;
        }

        if (variables.Count > 0)
        {
            tb.Tuning = new TuningSetup();
            foreach (var (name, value, kind, _) in variables)
            {
                // ×0.1 … ×10 of the start; a transparent start of zero-ish (series R 0 Ω, series L 0.1 nH) is
                // 0 … 10 Ω / 0 … 1 nH instead, so the range still holds a value that does something.
                var (lo, hi) = (kind, value) switch
                {
                    (PartKind.R, <= 0) => (0.0, 10.0),
                    (PartKind.L, <= 0.1e-9 * 1.000001) => (0.0, 1e-9),
                    _ => (0.1 * value, 10 * value),
                };
                tb.Tuning.Variables.Add(new TunableEntry
                {
                    Key = name, Tune = true, Opt = false,
                    Min = PartsTable.ValueText(lo, kind, digits), Max = PartsTable.ValueText(hi, kind, digits),
                });
            }
        }

        var sweep = options.Sweep ?? input.EmSetup?.Frequency ?? RecognitionEmitOptions.DefaultSweep;
        tb.Analyses.Add(new SParameterAnalysis(AnalysisName, sweep));

        return new RecognitionCircuit(tb) { Anchors = anchors, Measured = measured, Notes = notes, StrayLines = stray };
    }

    private const string Ground = "0";

    /// <summary>
    /// Elements in the order a walk from port 1 meets them, and nodes likewise: breadth first over the nets,
    /// each net's elements in the order they were made. Whatever the walk does not reach follows in order.
    /// </summary>
    /// <summary>
    /// Removes every group of lines and vias, joined through their non-ground nodes, that holds no port and no part — the
    /// traces running to a pin of a part the circuit does not model (an IC), or copper between two such pins. They
    /// change no S-parameter and drew as loose elements nobody could place (designer report, round 15: five of them,
    /// both ends unconnected). Returns what was removed.
    /// </summary>
    private static List<(string Type, IReadOnlyList<(long X, long Y)> Anchor)> DropStrayLines(List<Proto> protos)
    {
        var byNode = new Dictionary<string, List<Proto>>(StringComparer.Ordinal);
        foreach (var p in protos)
            foreach (var n in p.Nodes.Distinct())
                if (n != Ground) (byNode.TryGetValue(n, out var l) ? l : byNode[n] = []).Add(p);

        var seen = new HashSet<Proto>(ReferenceEqualityComparer.Instance);
        var drop = new HashSet<Proto>(ReferenceEqualityComparer.Instance);
        foreach (var start in protos)
        {
            if (!seen.Add(start)) continue;
            var group = new List<Proto> { start };
            for (int i = 0; i < group.Count; i++)
                foreach (var n in group[i].Nodes)
                    if (n != Ground)
                        foreach (var q in byNode[n])
                            if (seen.Add(q)) group.Add(q);
            // A port or a part has a fixed name; a line or a via is numbered.
            if (group.All(p => p.FixedName is null)) drop.UnionWith(group);
        }
        var removed = protos.Where(drop.Contains).Select(p => (p.Type, p.Anchor)).ToList();
        protos.RemoveAll(drop.Contains);
        return removed;
    }

    private static (List<Proto> Elements, List<string> Nodes) Walk(List<Proto> protos)
    {
        var byNode = new Dictionary<string, List<Proto>>(StringComparer.Ordinal);
        foreach (var p in protos)
            foreach (var n in p.Nodes.Distinct())
                if (n != Ground) (byNode.TryGetValue(n, out var l) ? l : byNode[n] = []).Add(p);

        var elements = new List<Proto>();
        var nodes = new List<string>();
        var seenE = new HashSet<Proto>(ReferenceEqualityComparer.Instance);
        var seenN = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        void Visit(Proto p)
        {
            if (!seenE.Add(p)) return;
            elements.Add(p);
            foreach (var n in p.Nodes)
                if (n != Ground && seenN.Add(n)) { nodes.Add(n); queue.Enqueue(n); }
        }

        var starts = protos.Where(p => p.Type == "Port").OrderBy(p => p.PortNumber).Concat(protos).ToList();
        foreach (var start in starts)
        {
            if (seenE.Contains(start)) continue;
            Visit(start);
            while (queue.Count > 0)
                foreach (var p in byNode[queue.Dequeue()]) Visit(p);
        }
        return (elements, nodes);
    }

    /// <summary>
    /// The part's land pattern as a footprint reference, where its case is known — designer report, round 15: the case
    /// was read (from the placed part, its land pattern, the placement file or the bill of materials) and shown in the
    /// parts table, and then never reached the schematic, so the parts had no footprint and Update Layout from
    /// Schematic had nothing to place.
    /// </summary>
    private static void AddFootprint(Proto part, PartRow row)
    {
        if (row.Case is { } smtCase)
            part.Parameters.Add(new ParameterAssignment(ArtworkParameters.FootprintName,
                new Footprints.FootprintRef(smtCase, Footprints.FootprintRef.DefaultDensity).ToString()));
    }

    private static string LinePrefix(LineElementType t) => t switch
    {
        LineElementType.MLIN => "TL",
        LineElementType.MBEND => "B",
        LineElementType.MTEE => "TEE",
        LineElementType.MCROSS => "X",
        LineElementType.MTAPER => "TP",
        LineElementType.CPWG => "CP",
        LineElementType.SLIN => "SL",
        _ => "TF",
    };

    private static readonly string[] Order = ["Z", "W", "W1", "W2", "W3", "W4", "G", "L", "Angle", "Miter", "Eeff", "F", "Ac", "Ad"];

    private static int ParameterOrder(string name) => Array.IndexOf(Order, name) is var i and >= 0 ? i : Order.Length;

    /// <summary>One line parameter in the unit a person reads it in: lengths in mm, an angle in degrees, F in GHz,
    /// Z in Ω; Eeff, Miter and the dB/m losses bare.</summary>
    private static ParameterAssignment LineParameter(string name, double value, int digits) => name switch
    {
        "W" or "W1" or "W2" or "W3" or "W4" or "G" or "L" => new(name, Num(value * 1e3, digits), "mm"),
        "Angle" => new(name, Num(value, digits), "deg"),
        "F" => new(name, Num(value / 1e9, digits), "GHz"),
        "Z" => new(name, Num(value, digits), "Ohm"),
        _ => new(name, Num(value, digits)),
    };

    private static string Num(double v, int digits) => RecognitionEmitOptions.Spell(v, digits);

    private static void AddLayer(Proto p, string name, string? layer)
    {
        // Bare where it can be — a schematic stores a layer name unquoted, and the drawing carries the text
        // through as it is; quoted only where a space or a quote would split the netlist line.
        if (layer is { Length: > 0 })
            p.Parameters.Add(new ParameterAssignment(name, layer.Any(c => char.IsWhiteSpace(c) || c is '"' or ';') ? $"\"{layer}\"" : layer));
    }

    private static void AddViaSize(Proto p, RecognizedVia via, int dbuPerMicron, int digits)
    {
        if (via.DrillDbu > 0) p.Parameters.Add(new ParameterAssignment("Drill", Num(via.DrillDbu / (double)dbuPerMicron / 1e3, digits), "mm"));
        if (via.PadDbu > 0) p.Parameters.Add(new ParameterAssignment("Pad", Num(via.PadDbu / (double)dbuPerMicron / 1e3, digits), "mm"));
    }

    /// <summary>The stackup conductor a via's <paramref name="end"/>-th island is on: the island's first drawing
    /// layer the via's span reaches.</summary>
    private static string? ConductorOf(Technology? tech, BoardGraph board, RecognizedVia via, int end)
    {
        if (tech is null || end >= via.Islands.Count) return null;
        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        var order = conductors.Select(c => c.Name).ToList();
        int from = via.SpanFrom is { } f ? order.IndexOf(f) : 0, to = via.SpanTo is { } t ? order.IndexOf(t) : order.Count - 1;
        if (from < 0) from = 0;
        if (to < 0) to = order.Count - 1;
        var (lo, hi) = (Math.Min(from, to), Math.Max(from, to));
        foreach (var layer in board.Islands[via.Islands[end]].Layers)
            for (int c = lo; c <= hi; c++)
                if (conductors[c].DrawingLayers.Contains(layer)) return conductors[c].Name;
        return null;
    }

    /// <summary>A name the netlist can carry: letters, digits and underscores, starting with a letter.</summary>
    internal static string Identifier(string name, string fallbackPrefix)
    {
        string s = Regex.Replace(name ?? "", "[^A-Za-z0-9_]", "_");
        if (s.Length == 0) return fallbackPrefix;
        return char.IsAsciiLetter(s[0]) ? s : fallbackPrefix + s;
    }

    /// <summary><c>100 pF</c> → (<c>100</c>, <c>pF</c>).</summary>
    private static (string Number, string? Unit) Split(string valueText)
    {
        int sp = valueText.LastIndexOf(' ');
        return sp < 0 ? (valueText, null) : (valueText[..sp], valueText[(sp + 1)..]);
    }

}
