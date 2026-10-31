using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using CircuitRF.Design.Layout.PCells.Wire;

using CircuitRF.Design.Symbol;
namespace CircuitRF.Design.Schematic;

// See KitTemplateSymbol.cs for why this alias has to sit inside the namespace.
using Symbol = global::CircuitRF.Design.Symbol.Symbol;

/// <summary>One port of the cell a glyph draws: the kit's coordinates (y up) and outward orientation in degrees.</summary>
public sealed record KitGlyphPort(string Name, double X, double Y, double OrientationDeg);

/// <summary>What <see cref="KitGlyphSymbol.TryBuild"/> made of one glyph, and what it could not carry.</summary>
/// <param name="Dropped">SVG element name → how many were not drawn.</param>
/// <param name="PinsOffLead">Pins placed on the grid where the glyph has no lead end for them.</param>
/// <param name="Notes">The report's sentences for this glyph, empty when everything converted.</param>
public sealed record KitGlyphResult(
    Symbol Symbol,
    IReadOnlyDictionary<string, int> Dropped,
    IReadOnlyList<string> PinsOffLead,
    IReadOnlyList<KitGlyphNote> Notes);

/// <summary>One sentence of a glyph's report. A warning when a pin is not where the drawing shows a terminal.</summary>
public sealed record KitGlyphNote(string Text, bool Warning);

/// <summary>
/// A <c>.csym</c> built from a kit's OWN drawing of a cell (brief-gdf-4).
///
/// <para><b>This is not circuitRF drawing on the kit's behalf</b>, which <see cref="PCellKitSchematicParts"/>
/// refuses to do. The lines, shapes and text are the kit's. What circuitRF adds is only where the pins go, and
/// that comes from the cell's ports: a glyph carries no pin markup, just lead lines running to its box edge.</para>
///
/// <list type="bullet">
/// <item><b>The box is the glyph's own</b> — its <c>viewBox</c>, else its width and height, else what it
/// draws — scaled uniformly onto the connection grid and centred on the origin.</item>
/// <item><b>A port's outward orientation picks the edge</b>: 180° left, 0° right, 90° top, 270° bottom. Pins
/// on an edge keep the ports' order across it and are spread evenly on the grid; where a lead line of the
/// glyph ends on that edge, the pin goes on the lead end.</item>
/// <item><b>A pin is never dropped.</b> More ports on an edge than lead ends there puts the rest on the grid
/// and says so.</item>
/// <item><b>Colours are not carried</b>, and the kit's dark-mode CSS is not read: the symbol takes the
/// schematic theme's symbol colour as every built-in does. The glyph's plain class and element rules ARE
/// read, for what is filled and how text is set. A shape is filled at the kit's own opacity
/// (<see cref="SymbolFill"/>) when it has a fill that is not none and not near-white, or has neither fill
/// nor stroke; a near-white shape with no stroke is the glyph's background and is not drawn.</item>
/// <item><b>What cannot be drawn is dropped and counted</b> — a filter, an image, a <c>use</c>, a gradient
/// definition — never silently.</item>
/// <item><b>No parameters</b>: those come from the generator (<see cref="PCellKitSchematicParts"/>).</item>
/// </list>
///
/// <para>Framework-free, like everything else in <c>src/Design</c>.</para>
/// </summary>
public static class KitGlyphSymbol
{
    private const double P = 100.0;   // the connection grid, local units

    private enum Edge { Left, Right, Top, Bottom }

    /// <summary>Builds the symbol for one glyph, or null with <paramref name="problem"/> saying why when the text is
    /// not SVG.</summary>
    public static KitGlyphResult? TryBuild(string svgText, IReadOnlyList<KitGlyphPort> ports, out string? problem)
    {
        problem = null;
        XDocument doc;
        try
        {
            // No DTD and no resolver: a glyph is somebody else's file and must not fetch anything.
            using var xml = XmlReader.Create(new StringReader(svgText), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver   = null,
            });
            doc = XDocument.Load(xml);
        }
        catch (XmlException ex)
        {
            problem = $"it is not readable SVG: {ex.Message}";
            return null;
        }
        var root = doc.Root!;
        if (root.Name.LocalName != "svg")
        {
            problem = $"its root element is <{root.Name.LocalName}>, not <svg>.";
            return null;
        }

        var reader = new SvgReader(Css.Read(root));
        reader.Walk(root, Affine.Identity, Style.Root);

        var box = ViewBoxOf(root) ?? SizeBoxOf(root) ?? reader.Bounds()
                  ?? new Box(0, 0, 100, 100);
        if (box.W <= 0 || box.H <= 0) box = new Box(box.X, box.Y, Math.Max(box.W, 1), Math.Max(box.H, 1));

        // Which ports go on which edge, ordered along it as the cell orders them across it.
        var byEdge = new Dictionary<Edge, List<int>>();
        for (int i = 0; i < ports.Count; i++)
        {
            var edge = EdgeOf(ports[i].OrientationDeg);
            if (!byEdge.TryGetValue(edge, out var list)) byEdge[edge] = list = [];
            list.Add(i);
        }
        foreach (var (edge, list) in byEdge)
            list.Sort((a, b) => edge is Edge.Left or Edge.Right
                ? ports[b].Y.CompareTo(ports[a].Y)      // kit y is up; the symbol's runs down, so top first
                : ports[a].X.CompareTo(ports[b].X));

        // Lead ends: a straight segment with one end on the box edge and the other inside it.
        double tol = 0.02 * Math.Max(box.W, box.H);
        var leads = new Dictionary<Edge, List<double>>();   // along-edge positions, glyph units
        foreach (var (a, b) in reader.Segments)
        {
            TryLead(a, b); TryLead(b, a);
        }
        void TryLead((double X, double Y) end, (double X, double Y) other)
        {
            foreach (Edge e in Enum.GetValues<Edge>())
            {
                double d = DistanceToEdge(end, e, box), dOther = DistanceToEdge(other, e, box);
                if (Math.Abs(d) > tol || dOther <= tol) continue;
                double along = e is Edge.Left or Edge.Right ? end.Y : end.X;
                if (!leads.TryGetValue(e, out var l)) leads[e] = l = [];
                if (!l.Any(v => Math.Abs(v - along) <= tol)) l.Add(along);
            }
        }
        foreach (var l in leads.Values) l.Sort();

        double size = ChooseSize(box, byEdge, leads);
        double s = size / Math.Max(box.W, box.H);
        double cx = box.X + box.W / 2, cy = box.Y + box.H / 2;
        (double X, double Y) Map(double x, double y) => ((x - cx) * s, (y - cy) * s);

        var primitives = reader.Emit(Map, s);

        // Pins, edge by edge.
        var pins = new SymbolPin[ports.Count];
        var offLead = new List<string>();
        var notes = new List<KitGlyphNote>();
        foreach (Edge e in Enum.GetValues<Edge>())
        {
            if (!byEdge.TryGetValue(e, out var order)) continue;
            double edgeLocal = EdgeLocal(e, box, s);
            double pinEdge = SnapOutward(edgeLocal, e);
            double halfAlong = (e is Edge.Left or Edge.Right ? box.H : box.W) * s / 2;
            var grid = GridInside(-halfAlong, halfAlong);

            var leadLocal = (leads.GetValueOrDefault(e) ?? [])
                .Select(a => (e is Edge.Left or Edge.Right ? (a - cy) : (a - cx)) * s)
                .ToList();
            var (positions, onLead) = PlaceAlong(order.Count, grid, leadLocal);

            for (int k = 0; k < order.Count; k++)
            {
                int i = order[k];
                double along = positions[k];
                var (x, y) = e is Edge.Left or Edge.Right ? (pinEdge, along) : (along, pinEdge);
                pins[i] = new SymbolPin(x, y, i, ports[i].Name) { NameAlign = AlignOf(e) };

                // Carry the lead out to the grid when the glyph's edge, or its lead end, is not on it.
                double from = onLead[k] is { } leadAt ? leadAt : along;
                var start = e is Edge.Left or Edge.Right ? (edgeLocal, from) : (from, edgeLocal);
                if (Math.Abs(start.Item1 - x) > 0.5 || Math.Abs(start.Item2 - y) > 0.5)
                    primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                                     start.Item1, start.Item2, x, y));
                if (onLead[k] is null) offLead.Add(ports[i].Name);
            }

            int missed = onLead.Count(v => v is null);
            if (missed > 0)
                notes.Add(new($"{missed} of {order.Count} pin(s) on the {e.ToString().ToLowerInvariant()} edge " +
                              $"{(missed == 1 ? "is" : "are")} not on a lead of the glyph and placed on the grid: " +
                              string.Join(", ", order.Where((_, k) => onLead[k] is null).Select(i => ports[i].Name)) + ".",
                              Warning: true));
        }

        if (ports.Count == 0) notes.Add(new("The cell has no ports, so its symbol has no pins.", Warning: true));
        if (reader.Dropped.Count > 0)
            notes.Add(new("Not drawn from the glyph: " +
                          string.Join(", ", reader.Dropped.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                                  .Select(kv => $"{kv.Key} ×{kv.Value}")) + ".", Warning: false));

        var symbol = new Symbol(primitives, pins, ports.Count);
        return new KitGlyphResult(symbol, reader.Dropped, offLead, notes);
    }

    // ── The install ───────────────────────────────────────────────────────────

    /// <summary>What <see cref="Install"/> wrote, kept and could not do, for the import report.</summary>
    public sealed class InstallResult
    {
        /// <summary>Cell → the <c>.csym</c> built from its glyph.</summary>
        public Dictionary<string, string> Built { get; } = new(StringComparer.Ordinal);
        /// <summary>Cell → the <c>.csym</c> copied from the kit's own.</summary>
        public Dictionary<string, string> Copied { get; } = new(StringComparer.Ordinal);
        /// <summary>Cells whose <c>.csym</c> already existed in the workspace and was left as it is.</summary>
        public List<string> Kept { get; } = [];
        /// <summary>Per cell, what the report should say: dropped constructs, pins off a lead, failures.</summary>
        public List<(string Cell, string Text, bool Warning)> Lines { get; } = [];

        public int Count => Built.Count + Copied.Count + Kept.Count;

        public string Tally()
            => $"{Built.Count} schematic symbol(s) built from the kit's own glyphs, {Copied.Count} copied from its own " +
               $".csym files, {Kept.Count} already in the workspace and kept.";
    }

    /// <summary>The sentence every surface puts beside these symbols: placeable is not simulatable.</summary>
    public const string NoModelNote =
        "A symbol lets the cell be placed on a schematic and seen by Update Layout from Schematic and LVS. It gives " +
        "the cell no circuit model: that comes from an EM run's S-parameters.";

    /// <summary>
    /// Writes a <c>symbols/&lt;cell&gt;.csym</c> beside the kit's declaration for every offered cell the report found
    /// a drawing for — copied from the kit's own <c>.csym</c>, else built from its glyph — which is exactly where
    /// <see cref="PCellKitSchematicParts.FindSymbolFile"/> looks, so the next palette rebuild mounts them.
    ///
    /// <para><b>An existing file is never overwritten.</b> It may be one somebody edited in the symbol editor, and
    /// nothing tells that apart from one this wrote; the report says it was kept, and deleting it rebuilds it.</para>
    /// </summary>
    public static InstallResult Install(string kitFolder, GdsfactoryImportReport report)
    {
        var result = new InstallResult();
        string dir = Path.Combine(kitFolder, PCellKitSchematicParts.SymbolsFolderName);
        var offered = new HashSet<string>(report.Offered, StringComparer.Ordinal);

        foreach (string cell in report.Symbols.Keys.Concat(report.Glyphs.Keys).Distinct(StringComparer.Ordinal)
                                      .Where(offered.Contains).Order(StringComparer.Ordinal))
        {
            string target = Path.Combine(dir, cell + PCellKitSchematicParts.SymbolExtension);
            if (PCellKitSchematicParts.FindSymbolFile(kitFolder, cell) is not null)
            {
                result.Kept.Add(cell);
                continue;
            }

            try
            {
                Directory.CreateDirectory(dir);
                if (report.Symbols.TryGetValue(cell, out string? kitSymbol))
                {
                    // Read first, so a kit file circuitRF cannot read is reported here and not on every open.
                    SymbolPersistence.LoadFromFile(kitSymbol);
                    File.Copy(kitSymbol, target);
                    result.Copied[cell] = target;
                    continue;
                }

                string glyph = report.Glyphs[cell];
                var ports = (report.Ports.GetValueOrDefault(cell) ?? [])
                    .Select(p => new KitGlyphPort(p.Name, p.X, p.Y, p.Orientation)).ToList();
                if (TryBuild(File.ReadAllText(glyph), ports, out string? problem) is not { } built)
                {
                    result.Lines.Add((cell, $"'{glyph}' could not be made into a schematic symbol: {problem}", true));
                    continue;
                }
                SymbolPersistence.SaveToFile(target, built.Symbol);
                result.Built[cell] = target;
                foreach (var note in built.Notes) result.Lines.Add((cell, note.Text, note.Warning));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                          or System.Text.Json.JsonException)
            {
                string source = report.Symbols.GetValueOrDefault(cell) ?? report.Glyphs[cell];
                result.Lines.Add((cell, $"'{source}' could not be made into a schematic symbol: {ex.Message}", true));
            }
        }
        return result;
    }

    // ── Pin placement ─────────────────────────────────────────────────────────

    private static Edge EdgeOf(double deg)
    {
        double d = ((deg % 360) + 360) % 360;
        return ((int)Math.Round(d / 90) % 4) switch
        {
            0 => Edge.Right,
            1 => Edge.Top,
            2 => Edge.Left,
            _ => Edge.Bottom,
        };
    }

    private static SymbolPinNameAlign AlignOf(Edge e) => e switch
    {
        Edge.Left  => SymbolPinNameAlign.Left,
        Edge.Right => SymbolPinNameAlign.Right,
        Edge.Top   => SymbolPinNameAlign.Top,
        _          => SymbolPinNameAlign.Bottom,
    };

    /// <summary>Signed distance from <paramref name="p"/> inward to edge <paramref name="e"/>, glyph units.</summary>
    private static double DistanceToEdge((double X, double Y) p, Edge e, Box b) => e switch
    {
        Edge.Left   => p.X - b.X,
        Edge.Right  => b.X + b.W - p.X,
        Edge.Top    => p.Y - b.Y,
        _           => b.Y + b.H - p.Y,
    };

    private static double EdgeLocal(Edge e, Box b, double s) => e switch
    {
        Edge.Left   => -b.W * s / 2,
        Edge.Right  =>  b.W * s / 2,
        Edge.Top    => -b.H * s / 2,
        _           =>  b.H * s / 2,
    };

    private static double SnapOutward(double v, Edge e)
        => e is Edge.Left or Edge.Top ? Math.Floor(v / P + 1e-9) * P : Math.Ceiling(v / P - 1e-9) * P;

    /// <summary>Grid positions strictly inside (<paramref name="lo"/>, <paramref name="hi"/>) — never a corner.</summary>
    private static List<double> GridInside(double lo, double hi)
    {
        var grid = new List<double>();
        for (double v = Math.Floor(lo / P + 1e-9) * P + P; v < hi - 1e-6; v += P)
            if (v > lo + 1e-6) grid.Add(v);
        return grid;
    }

    /// <summary>
    /// The symbol's size on the grid: the smallest even number of grid squares that gives every edge room for its
    /// pins with a free square at each end, then — among a few larger sizes — the one that puts the glyph's edges
    /// and lead ends nearest the grid, so a pin needs the shortest carried-out lead. A tie keeps the smaller.
    /// </summary>
    private static double ChooseSize(Box box, Dictionary<Edge, List<int>> byEdge, Dictionary<Edge, List<double>> leads)
    {
        double longest = Math.Max(box.W, box.H);
        double min = 4 * P;
        foreach (var (e, list) in byEdge)
        {
            double along = e is Edge.Left or Edge.Right ? box.H : box.W;
            min = Math.Max(min, (list.Count + 1) * P * longest / along);
        }
        min = Math.Ceiling(min / (2 * P) - 1e-9) * 2 * P;

        double best = min, bestScore = double.MaxValue;
        for (double size = min; size <= Math.Max(2 * min, min + 8 * P) + 1e-6; size += 2 * P)
        {
            double s = size / longest, score = 0;
            foreach (var e in byEdge.Keys)
            {
                double edge = EdgeLocal(e, box, s);
                score += Math.Abs(SnapOutward(edge, e) - edge);
                double c = e is Edge.Left or Edge.Right ? box.Y + box.H / 2 : box.X + box.W / 2;
                foreach (double a in leads.GetValueOrDefault(e) ?? [])
                {
                    double v = (a - c) * s;
                    score += Math.Abs(v - Math.Round(v / P) * P);
                }
            }
            if (score < bestScore - 0.5) { best = size; bestScore = score; }
        }
        return best;
    }

    /// <summary>
    /// Where <paramref name="n"/> pins go along one edge, in order, and for each the lead end it sits on (null
    /// when none).
    ///
    /// <para>The pins are first spread evenly on the grid. Then as many as possible sit on lead ends, keeping
    /// their order: every order-keeping choice of which pins take which lead ends is tried, a pin not on a lead
    /// keeps its even slot when that still lies between its neighbours and is otherwise spread on the grid
    /// between them, and the choice that moves the pins least from the even spread wins — a tie going to the
    /// most centred, so a symmetric glyph gets a symmetric symbol. When no choice fits on the grid the edge
    /// keeps the even spread and no pin on it claims a lead: said, never forced.</para>
    /// </summary>
    private static (List<double> Positions, List<double?> OnLead) PlaceAlong(int n, List<double> grid, List<double> leadsAlong)
    {
        var even = Spread(grid, n);
        var none = Enumerable.Repeat<double?>(null, n).ToList();
        if (leadsAlong.Count == 0 || n == 0) return (even, none);

        // Each lead end snapped to the grid point it must sit on; two ends on one grid point are one lead.
        var leadGrid = leadsAlong
            .Select(a => (Lead: a, At: grid.MinBy(g => Math.Abs(g - a))))
            .GroupBy(t => t.At).Select(g => g.First()).OrderBy(t => t.At).ToList();
        int pairs = Math.Min(n, leadGrid.Count);

        (List<double> Positions, List<double?> OnLead)? best = null;
        double bestCost = double.MaxValue, bestCentre = double.MaxValue;
        int tried = 0;
        foreach (var pinsChosen in Choose(n, pairs))
            foreach (var leadsChosen in Choose(leadGrid.Count, pairs))
            {
                if (++tried > MaxPlacementsTried) break;
                var anchor = new int?[n];
                for (int k = 0; k < pairs; k++) anchor[pinsChosen[k]] = leadsChosen[k];
                if (Fill(anchor) is not { } placed) continue;

                double cost = placed.Positions.Select((p, i) => Math.Abs(p - even[i])).Sum();
                double centre = Math.Abs(placed.Positions.Sum());
                if (cost < bestCost - 1e-6 || (Math.Abs(cost - bestCost) <= 1e-6 && centre < bestCentre - 1e-6))
                {
                    best = placed; bestCost = cost; bestCentre = centre;
                }
            }
        return best ?? (even, none);

        (List<double> Positions, List<double?> OnLead)? Fill(int?[] anchor)
        {
            var positions = new double[n];
            var onLead = new double?[n];
            int prevPin = -1;
            double prevAt = double.NegativeInfinity;
            for (int i = 0; i <= n; i++)
            {
                if (i < n && anchor[i] is null) continue;
                double at = i < n ? leadGrid[anchor[i]!.Value].At : double.PositiveInfinity;
                int count = i - prevPin - 1;
                if (count > 0)
                {
                    var slots = Enumerable.Range(prevPin + 1, count).Select(k => even[k]).ToList();
                    if (slots.All(v => v > prevAt + 1e-6 && v < at - 1e-6))
                        slots.CopyTo(positions, prevPin + 1);
                    else
                    {
                        var free = grid.Where(g => g > prevAt + 1e-6 && g < at - 1e-6).ToList();
                        if (free.Count < count) return null;
                        Spread(free, count).CopyTo(positions, prevPin + 1);
                    }
                }
                if (i < n)
                {
                    positions[i] = at;
                    onLead[i] = leadGrid[anchor[i]!.Value].Lead;
                }
                prevPin = i;
                prevAt = at;
            }
            return ([.. positions], [.. onLead]);
        }
    }

    /// <summary>How many lead assignments one edge may try before keeping the best so far. Every glyph measured has a
    /// handful of pins per edge; this only bounds a pathological one.</summary>
    private const int MaxPlacementsTried = 20_000;

    /// <summary><paramref name="n"/> of <paramref name="slots"/>, evenly spread and in order.</summary>
    private static List<double> Spread(List<double> slots, int n)
    {
        int m = slots.Count;
        var list = new List<double>(n);
        for (int i = 0; i < n; i++)
            list.Add(slots[Math.Clamp((int)Math.Round((i + 1) * (m + 1) / (double)(n + 1), MidpointRounding.AwayFromZero) - 1, 0, m - 1)]);
        return list;
    }

    /// <summary>Every increasing choice of <paramref name="k"/> indices from 0…<paramref name="n"/>−1.</summary>
    private static IEnumerable<int[]> Choose(int n, int k)
    {
        var pick = Enumerable.Range(0, k).ToArray();
        if (k > n) yield break;
        while (true)
        {
            yield return [.. pick];
            int i = k - 1;
            while (i >= 0 && pick[i] == n - k + i) i--;
            if (i < 0) yield break;
            pick[i]++;
            for (int j = i + 1; j < k; j++) pick[j] = pick[j - 1] + 1;
        }
    }

    // ── The SVG ───────────────────────────────────────────────────────────────

    private readonly record struct Box(double X, double Y, double W, double H);

    private static Box? ViewBoxOf(XElement svg)
    {
        var n = Numbers((string?)svg.Attribute("viewBox"));
        return n.Count == 4 && n[2] > 0 && n[3] > 0 ? new Box(n[0], n[1], n[2], n[3]) : null;
    }

    private static Box? SizeBoxOf(XElement svg)
    {
        double? w = Length((string?)svg.Attribute("width")), h = Length((string?)svg.Attribute("height"));
        return w is > 0 && h is > 0 ? new Box(0, 0, w.Value, h.Value) : null;
    }

    /// <summary>A plain or px length; a percentage or a physical unit says nothing about user space.</summary>
    private static double? Length(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.EndsWith("px", StringComparison.Ordinal)) text = text[..^2];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    private static List<double> Numbers(string? text)
    {
        var list = new List<double>();
        if (string.IsNullOrEmpty(text)) return list;
        var t = new PathTokens(text);
        while (t.TryNumber(out double v)) list.Add(v);
        return list;
    }

    /// <summary>A 2-D affine map, SVG's <c>matrix(a b c d e f)</c>.</summary>
    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

        /// <summary>This map after <paramref name="inner"/>: a point goes through inner first.</summary>
        public Affine Then(Affine inner) => new(
            A * inner.A + C * inner.B, B * inner.A + D * inner.B,
            A * inner.C + C * inner.D, B * inner.C + D * inner.D,
            A * inner.E + C * inner.F + E, B * inner.E + D * inner.F + F);

        public bool AxisAligned => Math.Abs(B) < 1e-9 && Math.Abs(C) < 1e-9;
        public double Scale => Math.Sqrt(Math.Abs(A * D - B * C));
    }

    private static Affine ParseTransform(string? text)
    {
        var m = Affine.Identity;
        if (string.IsNullOrWhiteSpace(text)) return m;
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
            int open = text.IndexOf('(', i);
            if (open < 0) break;
            int close = text.IndexOf(')', open);
            if (close < 0) break;
            string name = text[i..open].Trim();
            var a = Numbers(text[(open + 1)..close]);
            double Arg(int k, double d = 0) => k < a.Count ? a[k] : d;
            Affine t = name switch
            {
                "matrix" when a.Count == 6 => new Affine(a[0], a[1], a[2], a[3], a[4], a[5]),
                "translate" => new Affine(1, 0, 0, 1, Arg(0), Arg(1)),
                "scale"     => new Affine(Arg(0, 1), 0, 0, a.Count > 1 ? a[1] : Arg(0, 1), 0, 0),
                "rotate"    => Rotate(Arg(0), Arg(1), Arg(2)),
                "skewX"     => new Affine(1, 0, Math.Tan(Arg(0) * Math.PI / 180), 1, 0, 0),
                "skewY"     => new Affine(1, Math.Tan(Arg(0) * Math.PI / 180), 0, 1, 0, 0),
                _           => Affine.Identity,
            };
            m = m.Then(t);
            i = close + 1;
        }
        return m;

        static Affine Rotate(double deg, double cx, double cy)
        {
            double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
            return new Affine(1, 0, 0, 1, cx, cy).Then(new Affine(c, s, -s, c, 0, 0)).Then(new Affine(1, 0, 0, 1, -cx, -cy));
        }
    }

    /// <summary>
    /// The glyph's own stylesheet, as far as it decides what is FILLED and how text is set: rules whose selector is a
    /// class, an element name, the two together, or <c>*</c>. A rule inside an <c>@media</c> block — a kit's dark-mode
    /// rules — is not read, and no colour is carried from any rule. Read at all because a kit may state
    /// <c>fill:none</c> only through a class, and ignoring that turns every outline it draws into a solid shape.
    /// A selector of any other shape is skipped.
    /// </summary>
    private sealed class Css
    {
        private readonly List<(string? Element, string? Class, Dictionary<string, string> Decl)> _rules = [];

        public static Css Read(XElement root)
        {
            var css = new Css();
            foreach (var style in root.Descendants().Where(d => d.Name.LocalName == "style"))
            {
                string text = StripAtRules(style.Value);
                int i = 0;
                while (i < text.Length)
                {
                    int open = text.IndexOf('{', i), close = open < 0 ? -1 : text.IndexOf('}', open);
                    if (open < 0 || close < 0) break;
                    var decl = Declarations(text[(open + 1)..close]);
                    foreach (string sel in text[i..open].Split(',').Select(x => x.Trim()))
                        if (Simple(sel) is { } parsed) css._rules.Add((parsed.Element, parsed.Class, decl));
                    i = close + 1;
                }
            }
            return css;
        }

        /// <summary>Every declaration a rule gives <paramref name="e"/>, element rules before class rules, each in
        /// document order — a class outranks an element name.</summary>
        public IEnumerable<KeyValuePair<string, string>> For(XElement e)
        {
            var classes = ((string?)e.Attribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string name = e.Name.LocalName;
            foreach (var r in _rules.Where(r => r.Class is null && (r.Element is null || r.Element == name)))
                foreach (var d in r.Decl) yield return d;
            foreach (var r in _rules.Where(r => r.Class is not null && classes.Contains(r.Class) && (r.Element is null || r.Element == name)))
                foreach (var d in r.Decl) yield return d;
        }

        private static (string? Element, string? Class)? Simple(string sel)
        {
            if (sel == "*") return (null, null);
            if (sel.Length == 0 || sel.Any(c => c is ' ' or '>' or '+' or '~' or ':' or '[' or '#')) return null;
            int dot = sel.IndexOf('.');
            if (dot < 0) return (sel, null);
            if (sel.IndexOf('.', dot + 1) >= 0) return null;
            return (dot == 0 ? null : sel[..dot], sel[(dot + 1)..]);
        }

        /// <summary>The text with every <c>@…{…}</c> block removed, nested braces included.</summary>
        private static string StripAtRules(string text)
        {
            var sb = new System.Text.StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] != '@') { sb.Append(text[i++]); continue; }
                int open = text.IndexOf('{', i), semi = text.IndexOf(';', i);
                if (open < 0 || (semi >= 0 && semi < open)) { i = semi < 0 ? text.Length : semi + 1; continue; }
                int depth = 0;
                for (i = open; i < text.Length; i++)
                {
                    if (text[i] == '{') depth++;
                    else if (text[i] == '}' && --depth == 0) { i++; break; }
                }
            }
            return sb.ToString();
        }

        public static Dictionary<string, string> Declarations(string text)
        {
            var decl = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in text.Split(';'))
            {
                int colon = part.IndexOf(':');
                if (colon > 0) decl[part[..colon].Trim()] = part[(colon + 1)..].Trim();
            }
            return decl;
        }
    }

    /// <summary>What a shape inherits: whether it is filled, and how its text is set. Colours themselves are not kept.</summary>
    private sealed record Style(string? Fill, string? Stroke, double FontSize, string Anchor, bool Hidden, double FillOpacity)
    {
        public static readonly Style Root = new(null, null, 16, "start", false, 1);

        private static readonly string[] Attributes =
            ["fill", "stroke", "font-size", "text-anchor", "display", "visibility", "fill-opacity", "opacity"];

        /// <summary>SVG's order: presentation attributes, then the stylesheet, then an inline style attribute.</summary>
        public Style Under(XElement e, Css css)
        {
            var decl = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in Attributes)
                if ((string?)e.Attribute(name) is { } v) decl[name] = v.Trim();
            foreach (var (k, v) in css.For(e)) decl[k] = v;
            if ((string?)e.Attribute("style") is { } style)
                foreach (var (k, v) in Css.Declarations(style)) decl[k] = v;

            double? fontSize = decl.TryGetValue("font-size", out var fs) ? Length(fs) : null;
            if (fontSize is null && decl.TryGetValue("font", out var font))
                fontSize = font.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                               .Select(t => t.EndsWith("px", StringComparison.Ordinal) ? Length(t) : null)
                               .FirstOrDefault(v => v is not null);
            double opacity = FillOpacity;
            foreach (var key in new[] { "fill-opacity", "opacity" })
                if (decl.TryGetValue(key, out var o) && double.TryParse(o, NumberStyles.Float, CultureInfo.InvariantCulture, out double ov))
                    opacity *= Math.Clamp(ov, 0, 1);

            return new Style(
                decl.GetValueOrDefault("fill") ?? Fill,
                decl.GetValueOrDefault("stroke") ?? Stroke,
                fontSize ?? FontSize,
                decl.GetValueOrDefault("text-anchor") ?? Anchor,
                Hidden || decl.GetValueOrDefault("display") == "none" || decl.GetValueOrDefault("visibility") == "hidden",
                opacity);
        }

        /// <summary>
        /// How strongly the inside of a closed shape is filled, 0–1 (<see cref="SymbolFill"/>): the kit's own opacity
        /// for a fill that is not none and not near-white, or when neither fill nor stroke is stated (SVG fills that).
        /// A kit shading a region at 25 % is carried as 25 %, so what is drawn over it stays visible. 0 otherwise; a
        /// near-white fill is the glyph's background, not a shade.
        /// </summary>
        public double FillStrength
        {
            get
            {
                if (FillOpacity <= 0) return 0;
                if (Fill is null) return Stroke is null or "none" ? FillOpacity : 0;
                string f = Fill.ToLowerInvariant();
                if (f is "none" or "transparent" || IsLight(f)) return 0;
                return FillOpacity;
            }
        }

        /// <summary>A light fill with no stroke: the glyph's background, which draws nothing a reader sees. Drawn as an
        /// outline it would frame the symbol in a box the kit never drew.</summary>
        public bool Background => Fill is { } f && IsLight(f.ToLowerInvariant()) && Stroke is null or "none";

        private static bool IsLight(string colour)
        {
            if (colour is "white") return true;
            if (colour.StartsWith('#'))
            {
                string hex = colour[1..];
                if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
                if (hex.Length >= 6 && int.TryParse(hex[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                {
                    double r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
                    return (0.299 * r + 0.587 * g + 0.114 * b) / 255 > 0.85;
                }
            }
            return false;
        }
    }

    /// <summary>Reads the SVG into glyph-space drawing items, then emits them as symbol primitives once the scale is known.</summary>
    private sealed class SvgReader
    {
        private readonly List<Func<Func<double, double, (double X, double Y)>, double, SymbolPrimitive>> _items = [];
        private readonly List<(double X, double Y)> _extent = [];

        public Dictionary<string, int> Dropped { get; } = new(StringComparer.Ordinal);
        public List<((double X, double Y) A, (double X, double Y) B)> Segments { get; } = [];

        /// <summary>Elements that only describe the document, and the stylesheet, which <see cref="Css"/> has read.</summary>
        private static readonly HashSet<string> Silent = ["title", "desc", "metadata", "style"];

        private readonly Css _css;

        public SvgReader(Css css) => _css = css;

        public void Walk(XElement e, Affine m, Style style)
        {
            foreach (var child in e.Elements())
            {
                string name = child.Name.LocalName;
                if (child.Name.Namespace != XNamespace.None && child.Name.NamespaceName != "http://www.w3.org/2000/svg")
                    continue;   // editor metadata in its own namespace is not drawing
                if (Silent.Contains(name)) continue;

                var st = style.Under(child, _css);
                var cm = m.Then(ParseTransform((string?)child.Attribute("transform")));
                if (st.Hidden || (st.Background && name is "rect" or "circle" or "ellipse" or "polygon" or "path")) continue;

                switch (name)
                {
                    case "g" or "a" or "switch" or "svg": Walk(child, cm, st); break;
                    case "defs":
                        // Definitions are drawn only when something refers to them, which nothing here follows.
                        foreach (var def in child.Elements()) Drop(def.Name.LocalName);
                        break;
                    case "path":     PathOf(child, cm, st); break;
                    case "line":     LineOf(child, cm); break;
                    case "polyline": PolyOf(child, cm, st, closed: false); break;
                    case "polygon":  PolyOf(child, cm, st, closed: true); break;
                    case "rect":     RectOf(child, cm, st); break;
                    case "circle":   EllipseOf(child, cm, st, circle: true); break;
                    case "ellipse":  EllipseOf(child, cm, st, circle: false); break;
                    case "text":     TextOf(child, cm, st); break;
                    default:         Drop(name); break;
                }
            }
        }

        private void Drop(string name) => Dropped[name] = Dropped.GetValueOrDefault(name) + 1;

        private static double A(XElement e, string name) => Length((string?)e.Attribute(name)) ?? 0;

        public Box? Bounds()
        {
            if (_extent.Count == 0) return null;
            double x0 = _extent.Min(p => p.X), x1 = _extent.Max(p => p.X);
            double y0 = _extent.Min(p => p.Y), y1 = _extent.Max(p => p.Y);
            return new Box(x0, y0, x1 - x0, y1 - y0);
        }

        public List<SymbolPrimitive> Emit(Func<double, double, (double X, double Y)> map, double scale)
            => [.. _items.Select(f => f(map, scale))];

        /// <summary><paramref name="prim"/> with its fill strength set through the one rule (<see cref="SymbolFill.Set"/>).</summary>
        private static SymbolPrimitive Filled(SymbolPrimitive prim, double fill)
        {
            SymbolFill.Set(prim, fill);
            return prim;
        }

        private static List<double[]> Mapped(IEnumerable<(double X, double Y)> pts, Func<double, double, (double X, double Y)> map)
            => [.. pts.Select(p => { var q = map(p.X, p.Y); return new[] { q.X, q.Y }; })];

        private void AddRun(List<(double X, double Y)> pts, bool closed, double fill, bool leads)
        {
            if (pts.Count < 2) return;
            var copy = pts.ToList();
            _extent.AddRange(copy);
            if (leads)
                for (int i = 0; i + 1 < copy.Count; i++) Segments.Add((copy[i], copy[i + 1]));
            if (closed && fill > 0)
                _items.Add((map, _) => Filled(new PolygonPrimitive { ColorRole = SymbolColorRole.SymbolLine, Points = Mapped(copy, map) }, fill));
            else
            {
                if (closed && copy[0] != copy[^1]) copy.Add(copy[0]);
                _items.Add((map, _) => new PolylinePrimitive { ColorRole = SymbolColorRole.SymbolLine, Points = Mapped(copy, map) });
            }
        }

        private void LineOf(XElement e, Affine m)
        {
            var a = m.Apply(A(e, "x1"), A(e, "y1"));
            var b = m.Apply(A(e, "x2"), A(e, "y2"));
            _extent.Add(a); _extent.Add(b);
            Segments.Add((a, b));
            _items.Add((map, _) =>
            {
                var p = map(a.X, a.Y); var q = map(b.X, b.Y);
                return new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, p.X, p.Y, q.X, q.Y);
            });
        }

        private void PolyOf(XElement e, Affine m, Style st, bool closed)
        {
            var n = Numbers((string?)e.Attribute("points"));
            var pts = new List<(double X, double Y)>();
            for (int i = 0; i + 1 < n.Count; i += 2) pts.Add(m.Apply(n[i], n[i + 1]));
            AddRun(pts, closed, closed ? st.FillStrength : 0, leads: !closed);
        }

        private void RectOf(XElement e, Affine m, Style st)
        {
            double x = A(e, "x"), y = A(e, "y"), w = A(e, "width"), h = A(e, "height");
            if (w <= 0 || h <= 0) return;
            double rx = A(e, "rx"), ry = A(e, "ry");
            if (rx <= 0) rx = ry;
            double fill = st.FillStrength;
            if (!m.AxisAligned)
            {
                AddRun([m.Apply(x, y), m.Apply(x + w, y), m.Apply(x + w, y + h), m.Apply(x, y + h)], true, fill, leads: false);
                return;
            }
            var p0 = m.Apply(x, y); var p1 = m.Apply(x + w, y + h);
            _extent.Add(p0); _extent.Add(p1);
            double r = rx * Math.Abs(m.A);
            _items.Add((map, s) =>
            {
                var a = map(p0.X, p0.Y); var b = map(p1.X, p1.Y);
                double cx = (a.X + b.X) / 2, cy = (a.Y + b.Y) / 2, W = Math.Abs(b.X - a.X), H = Math.Abs(b.Y - a.Y);
                return r > 0
                    ? Filled(new RoundedRectPrimitive { ColorRole = SymbolColorRole.SymbolLine, Cx = cx, Cy = cy, W = W, H = H, Radius = r * s }, fill)
                    : Filled(new RectPrimitive { ColorRole = SymbolColorRole.SymbolLine, Cx = cx, Cy = cy, W = W, H = H }, fill);
            });
        }

        private void EllipseOf(XElement e, Affine m, Style st, bool circle)
        {
            double cx = A(e, "cx"), cy = A(e, "cy");
            double rx = circle ? A(e, "r") : A(e, "rx"), ry = circle ? rx : A(e, "ry");
            if (rx <= 0 || ry <= 0) return;
            double fill = st.FillStrength;
            if (!m.AxisAligned)
            {
                var pts = new List<(double X, double Y)>();
                for (int k = 0; k < 64; k++)
                {
                    double t = 2 * Math.PI * k / 64;
                    pts.Add(m.Apply(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
                }
                AddRun(pts, true, fill, leads: false);
                return;
            }
            var c = m.Apply(cx, cy);
            double RX = rx * Math.Abs(m.A), RY = ry * Math.Abs(m.D);
            _extent.Add((c.X - RX, c.Y - RY)); _extent.Add((c.X + RX, c.Y + RY));
            _items.Add((map, s) =>
            {
                var q = map(c.X, c.Y);
                return Math.Abs(RX - RY) < 1e-9
                    ? Filled(new CirclePrimitive { ColorRole = SymbolColorRole.SymbolLine, Cx = q.X, Cy = q.Y, R = RX * s }, fill)
                    : Filled(new EllipsePrimitive { ColorRole = SymbolColorRole.SymbolLine, Cx = q.X, Cy = q.Y, Rx = RX * s, Ry = RY * s }, fill);
            });
        }

        private void TextOf(XElement e, Affine m, Style st)
        {
            string content = string.Join(" ", e.DescendantNodes().OfType<XText>().Select(t => t.Value.Trim())
                                                .Where(t => t.Length > 0));
            if (content.Length == 0) return;
            // The first tspan's position stands for the text's when the text element has none of its own.
            var xs = Numbers((string?)e.Attribute("x"));
            var ys = Numbers((string?)e.Attribute("y"));
            var span = e.Elements().FirstOrDefault(c => c.Name.LocalName == "tspan");
            if (xs.Count == 0 && span is not null) xs = Numbers((string?)span.Attribute("x"));
            if (ys.Count == 0 && span is not null) ys = Numbers((string?)span.Attribute("y"));
            var at = m.Apply(xs.FirstOrDefault(), ys.FirstOrDefault());
            double size = st.FontSize * m.Scale;
            _extent.Add(at);
            var align = st.Anchor switch { "middle" => SymbolTextAlign.Center, "end" => SymbolTextAlign.Right, _ => SymbolTextAlign.Left };
            _items.Add((map, s) =>
            {
                var q = map(at.X, at.Y);
                return new TextPrimitive
                {
                    Content = content, AnchorX = q.X, AnchorY = q.Y, FontSize = size * s,
                    Align = align, VAlign = SymbolTextVAlign.Baseline, ColorRole = SymbolColorRole.SymbolText,
                };
            });
        }

        // ── path data ──

        private void PathOf(XElement e, Affine m, Style st)
        {
            string d = (string?)e.Attribute("d") ?? "";
            double fill = st.FillStrength;
            var t = new PathTokens(d);
            var run = new List<(double X, double Y)>();
            (double X, double Y) cur = (0, 0), start = (0, 0), lastCtrl = (0, 0);
            char lastCmd = ' ';
            char cmd = ' ';

            void Flush(bool closed)
            {
                AddRun(run, closed, fill, leads: true);
                run.Clear();
            }
            void LineTo((double X, double Y) p)
            {
                if (run.Count == 0) run.Add(m.Apply(cur.X, cur.Y));
                run.Add(m.Apply(p.X, p.Y));
                cur = p;
            }
            void Curve(params (double X, double Y)[] pts)   // control points then end, glyph user space
            {
                var all = new[] { cur }.Concat(pts).Select(p => m.Apply(p.X, p.Y)).ToArray();
                if (fill > 0)
                {
                    // A filled or tinted outline must stay one closed polygon, so the curve is flattened into it.
                    if (run.Count == 0) run.Add(all[0]);
                    for (int k = 1; k <= 16; k++) run.Add(Bezier(all, k / 16.0));
                }
                else
                {
                    Flush(false);
                    _extent.AddRange(all);
                    var c = all;
                    _items.Add(c.Length == 3
                        ? (map, _) =>
                        {
                            var p0 = map(c[0].X, c[0].Y); var p1 = map(c[1].X, c[1].Y); var p2 = map(c[2].X, c[2].Y);
                            return new QuadCurvePrimitive { ColorRole = SymbolColorRole.SymbolLine, P0X = p0.X, P0Y = p0.Y, CtrlX = p1.X, CtrlY = p1.Y, P2X = p2.X, P2Y = p2.Y };
                        }
                        : (map, _) =>
                        {
                            var p0 = map(c[0].X, c[0].Y); var p1 = map(c[1].X, c[1].Y); var p2 = map(c[2].X, c[2].Y); var p3 = map(c[3].X, c[3].Y);
                            return new CubicCurvePrimitive { ColorRole = SymbolColorRole.SymbolLine, P0X = p0.X, P0Y = p0.Y, C1X = p1.X, C1Y = p1.Y, C2X = p2.X, C2Y = p2.Y, P3X = p3.X, P3Y = p3.Y };
                        });
                }
                cur = pts[^1];
            }

            while (true)
            {
                if (t.TryCommand(out char c)) cmd = c;
                else if (cmd == ' ' || !t.HasMore) break;
                else if (cmd is 'M') cmd = 'L';           // numbers after a moveto are linetos
                else if (cmd is 'm') cmd = 'l';

                bool rel = char.IsLower(cmd);
                (double X, double Y) Pt()
                {
                    double x = t.Number(), y = t.Number();
                    return rel ? (cur.X + x, cur.Y + y) : (x, y);
                }

                try
                {
                    switch (char.ToUpperInvariant(cmd))
                    {
                        case 'M':
                            Flush(false);
                            cur = start = Pt();
                            break;
                        case 'L': LineTo(Pt()); break;
                        case 'H': { double x = t.Number(); LineTo((rel ? cur.X + x : x, cur.Y)); break; }
                        case 'V': { double y = t.Number(); LineTo((cur.X, rel ? cur.Y + y : y)); break; }
                        case 'Z':
                            if (run.Count > 0) { if (cur != start) LineTo(start); Flush(true); }
                            cur = start;
                            break;
                        case 'C':
                        {
                            var c1 = Pt(); var c2 = Pt(); var p = Pt();
                            Curve(c1, c2, p); lastCtrl = c2;
                            break;
                        }
                        case 'S':
                        {
                            var c1 = lastCmd is 'C' or 'S' ? (2 * cur.X - lastCtrl.X, 2 * cur.Y - lastCtrl.Y) : cur;
                            var c2 = Pt(); var p = Pt();
                            Curve(c1, c2, p); lastCtrl = c2;
                            break;
                        }
                        case 'Q':
                        {
                            var c1 = Pt(); var p = Pt();
                            Curve(c1, p); lastCtrl = c1;
                            break;
                        }
                        case 'T':
                        {
                            var c1 = lastCmd is 'Q' or 'T' ? (2 * cur.X - lastCtrl.X, 2 * cur.Y - lastCtrl.Y) : cur;
                            var p = Pt();
                            Curve(c1, p); lastCtrl = c1;
                            break;
                        }
                        case 'A':
                        {
                            double rx = t.Number(), ry = t.Number(), rot = t.Number();
                            bool large = t.Flag(), sweep = t.Flag();
                            var p = Pt();
                            foreach (var q in ArcPoints(cur, p, rx, ry, rot, large, sweep)) LineTo(q);
                            break;
                        }
                        default: return;   // not path data: stop here, keeping what was read
                    }
                }
                catch (FormatException) { break; }
                lastCmd = char.ToUpperInvariant(cmd);
            }
            Flush(false);
        }

        private static (double X, double Y) Bezier((double X, double Y)[] p, double t)
        {
            var pts = p.ToArray();
            for (int n = pts.Length - 1; n > 0; n--)
                for (int i = 0; i < n; i++)
                    pts[i] = (pts[i].X + (pts[i + 1].X - pts[i].X) * t, pts[i].Y + (pts[i + 1].Y - pts[i].Y) * t);
            return pts[0];
        }

        /// <summary>An elliptical arc as points along it, endpoint parameterisation to centre (the SVG implementation notes).</summary>
        private static IEnumerable<(double X, double Y)> ArcPoints(
            (double X, double Y) p0, (double X, double Y) p1, double rx, double ry, double rotDeg, bool large, bool sweep)
        {
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            if (rx < 1e-12 || ry < 1e-12 || p0 == p1) { yield return p1; yield break; }
            double phi = rotDeg * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
            double dx = (p0.X - p1.X) / 2, dy = (p0.Y - p1.Y) / 2;
            double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
            double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
            if (lambda > 1) { rx *= Math.Sqrt(lambda); ry *= Math.Sqrt(lambda); }
            double num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
            double den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
            double co = Math.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
            double cxp = co * rx * y1 / ry, cyp = -co * ry * x1 / rx;
            double cx = cos * cxp - sin * cyp + (p0.X + p1.X) / 2, cy = sin * cxp + cos * cyp + (p0.Y + p1.Y) / 2;
            double th1 = Math.Atan2((y1 - cyp) / ry, (x1 - cxp) / rx);
            double th2 = Math.Atan2((-y1 - cyp) / ry, (-x1 - cxp) / rx);
            double dth = th2 - th1;
            if (sweep && dth < 0) dth += 2 * Math.PI;
            if (!sweep && dth > 0) dth -= 2 * Math.PI;
            int n = Math.Max(4, (int)Math.Ceiling(Math.Abs(dth) / (Math.PI / 16)));
            for (int k = 1; k <= n; k++)
            {
                double th = th1 + dth * k / n;
                double ex = rx * Math.Cos(th), ey = ry * Math.Sin(th);
                yield return k == n ? p1 : (cos * ex - sin * ey + cx, sin * ex + cos * ey + cy);
            }
        }
    }

    /// <summary>SVG path-data and number-list tokens: commands, numbers (<c>-.5.5</c> is two), and arc flags.</summary>
    private sealed class PathTokens(string text)
    {
        private int _i;

        public bool HasMore { get { Skip(); return _i < text.Length; } }

        private void Skip()
        {
            while (_i < text.Length && (char.IsWhiteSpace(text[_i]) || text[_i] == ',')) _i++;
        }

        public bool TryCommand(out char c)
        {
            Skip();
            c = _i < text.Length ? text[_i] : ' ';
            if (_i < text.Length && char.IsLetter(c) && c is not ('e' or 'E')) { _i++; return true; }
            return false;
        }

        public bool TryNumber(out double v)
        {
            Skip();
            int s = _i;
            if (_i < text.Length && text[_i] is '+' or '-') _i++;
            bool digits = false, dot = false;
            while (_i < text.Length)
            {
                char ch = text[_i];
                if (char.IsDigit(ch)) { digits = true; _i++; }
                else if (ch == '.' && !dot) { dot = true; _i++; }
                else break;
            }
            if (digits && _i < text.Length && text[_i] is 'e' or 'E')
            {
                int e = _i++;
                if (_i < text.Length && text[_i] is '+' or '-') _i++;
                if (_i < text.Length && char.IsDigit(text[_i])) while (_i < text.Length && char.IsDigit(text[_i])) _i++;
                else _i = e;
            }
            if (!digits) { _i = s; v = 0; return false; }
            return double.TryParse(text.AsSpan(s, _i - s), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        // Caught by the path reader, which keeps what it read before the malformed token; never shown.
        public double Number() => TryNumber(out double v) ? v : throw new FormatException();

        public bool Flag()
        {
            Skip();
            if (_i < text.Length && text[_i] is '0' or '1') return text[_i++] == '1';
            throw new FormatException();
        }
    }
}
