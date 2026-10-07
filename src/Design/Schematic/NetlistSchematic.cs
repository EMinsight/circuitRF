using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Schematic;

/// <summary>What <see cref="NetlistSchematic.Build"/> made of a netlist.</summary>
/// <param name="Schematic">The drawing, or null when anything was refused — a drawing that leaves
/// out one instance is a different circuit, so it is never handed back half made.</param>
/// <param name="Refusals">Why the netlist cannot be drawn, one sentence per cause. Empty on success.</param>
/// <param name="Notes">What the drawing does not carry, or carries differently, that a reader should
/// hear. Never a reason to refuse.</param>
public sealed record NetlistSchematicResult(
    SchematicEditModel?   Schematic,
    IReadOnlyList<string> Refusals,
    IReadOnlyList<string> Notes);

/// <summary>
/// A drawn schematic from a netlist (brief-agent-authoring-overview.md AA-6): every instance placed
/// as its built-in symbol, the signal path left to right with the ports at its ends, shunt elements
/// dropped below the net they hang from, a ground symbol on every terminal on net <c>0</c>, every
/// net wired orthogonally, and every net LABELLED with the name the netlist gave it.
///
/// <para><b>The round trip is the contract.</b> The schematic extracts — through
/// <see cref="SchematicCircuit"/>, the extraction Simulate performs — to the same instances, nets
/// and values the netlist states. That is why every net carries a label rather than only the ones
/// a person would label: a net's NAME is part of the circuit (a measurement reads <c>V(out)</c>),
/// and an unlabelled wire extracts under an invented one. The only parameter a drawing adds is the
/// port count a variadic symbol reads its pins from (<c>NumPorts</c> on an SDD or a Z-port,
/// <c>Pins</c> on a Verilog-A device), and only when the line left it to be inferred from its nets.</para>
///
/// <para><b>Geometry is connectivity here</b>, so the wiring is <see cref="SchematicAutoRouter"/>'s,
/// the router the SPICE subcircuit import already draws with: it knows which crossings join two nets
/// and which do not. A terminal it cannot reach is connected by a net label, which is a real
/// connection — the drawing suffers, the circuit does not, and the result says so.</para>
///
/// <para><b>What it refuses</b> rather than approximates: a netlist that defines cells (a drawing of
/// a hierarchy needs a cell folder per definition, which this does not write), an instance whose
/// type has no schematic symbol, an instance whose net count its symbol cannot carry, and a wirebond,
/// whose pins come from the design file it references rather than from a built-in symbol.</para>
///
/// <para>Nothing here touches the disk. The caller decides where the drawing goes, which is also what
/// decides how a relative file reference in it resolves — see the CLI's own note about that.</para>
/// </summary>
public static class NetlistSchematic
{
    private const double P = SchematicAutoRouter.P;

    /// <summary>Free wire between a net region's edge and the next pin along the main line.</summary>
    private const double Lead = 400;

    /// <summary>Centre-to-centre spacing of the elements hanging from one net.</summary>
    private const double HangerPitch = 800;

    /// <summary>How far below the main line a hanging element's upper pin sits, so its wire Ts onto
    /// the line with a vertex rather than lying along it.</summary>
    private const double HangerDrop = 300;

    /// <summary>How far above the main line an element bridging two path nets sits.</summary>
    private const double BridgeRise = 900;

    /// <summary>Spacing of successive rows of bridging elements above the line.</summary>
    private const double BridgeRowPitch = 700;

    /// <summary>Clearance kept between the hanging elements and the rows of everything else.</summary>
    private const double RowGap = 400;

    /// <summary>Slot width of the rows "everything else" is placed in.</summary>
    private const double Slot = 400;

    private const string Ground = "0";

    /// <summary>The kinds a netlist line never draws as: sentinels, and a placeholder that would
    /// assert nothing about the part.</summary>
    private static readonly HashSet<SymbolKind> NotDrawable =
    [
        SymbolKind.Ground, SymbolKind.Pin, SymbolKind.Var, SymbolKind.Meas,
        SymbolKind.Generic, SymbolKind.SpiceModel,
    ];

    /// <summary>Several kinds share one engine component; the drawing takes the first of each
    /// group whose pins can carry the line's nets.</summary>
    private static readonly SymbolKind[] Preferred =
    [
        SymbolKind.Term, SymbolKind.TermG,
        SymbolKind.Tuner, SymbolKind.LoadTuner, SymbolKind.SourceTuner,
        SymbolKind.Mixer, SymbolKind.MixerD,
        SymbolKind.Switch, SymbolKind.SwitchD,
        SymbolKind.Coupler, SymbolKind.Hybrid90, SymbolKind.Hybrid180,
    ];

    /// <summary>The kinds a path may end on when the netlist has fewer than two ports: the things
    /// that drive or terminate a circuit.</summary>
    private static readonly HashSet<SymbolKind> Fixtures =
    [
        SymbolKind.Term, SymbolKind.TermG, SymbolKind.Tuner, SymbolKind.LoadTuner,
        SymbolKind.SourceTuner, SymbolKind.P1Tone, SymbolKind.PnTone, SymbolKind.ToneSource,
        SymbolKind.CurrentToneSource, SymbolKind.Vdc, SymbolKind.ZPort,
    ];

    // ─────────────────────────────────────────────────────────────────────────
    //  One placed thing
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class Item
    {
        public required EditableComponent    Comp  { get; init; }

        /// <summary>Per symbol pin, the net — <c>"0"</c> for ground, rewritten to a private name
        /// once that pin has a ground symbol of its own.</summary>
        public required string[]             Nets  { get; init; }

        public required (float X, float Y)[] Local { get; init; }

        public bool Placed { get; set; }

        public IEnumerable<string> SignalNets
            => Nets.Where(n => n != Ground).Distinct(StringComparer.Ordinal);

        public (double X, double Y) Pin(int k)
            => SchematicGeometry.LocalToWorld(Local[k].X, Local[k].Y,
                   Comp.X, Comp.Y, Comp.Rotation, Comp.MirrorX);

        /// <summary>Pin offset from the component origin under the current rotation.</summary>
        public (double X, double Y) Offset(int k)
            => SchematicGeometry.LocalToWorld(Local[k].X, Local[k].Y, 0, 0, Comp.Rotation, Comp.MirrorX);

        /// <summary>The glyph's box relative to the origin, under the current rotation — measured
        /// off the renderer itself, so a keep-out is the symbol that is DRAWN.</summary>
        public (double MinX, double MinY, double MaxX, double MaxY) Box { get; set; }

        /// <summary>The drawn glyph's lowest point below the origin — what the renderer hangs the
        /// label block from.</summary>
        public double GlyphBottom { get; set; }

        /// <summary><see cref="Box"/> with the label block in it — what spacing is decided on, and
        /// what a wire is kept out of, so no wire is drawn through a value.</summary>
        public (double MinX, double MinY, double MaxX, double MaxY) Full { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Entry point
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws <paramref name="tb"/>. <paramref name="lib"/> is consulted only to refuse a hierarchy.
    /// </summary>
    /// <param name="schematicDirectory">Where the drawing will be written, when known. The netlist
    /// reader resolves a Touchstone <c>File</c> to an absolute path; a schematic stores it relative to
    /// its own folder, so it is rewritten relative to this one. Null leaves it absolute.</param>
    public static NetlistSchematicResult Build(Library lib, TestBench tb, string? schematicDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(lib);
        ArgumentNullException.ThrowIfNull(tb);

        var refusals = new List<string>();
        var notes    = new List<string>();

        if (lib.Cells.Count > 0)
            refusals.Add(
                $"The netlist defines {lib.Cells.Count} cell(s) ({string.Join(", ", lib.Cells.Select(c => c.Name))}). "
              + "A drawing of a hierarchy needs a cell folder for each definition, which this does not "
              + "write; draw each definition as a netlist of its own, or flatten it first.");

        var items = new List<Item>();
        foreach (var inst in tb.Instances)
        {
            if (lib.Find(inst.Reference) is not null) continue;   // already refused above
            if (MapInstance(inst, schematicDirectory, out string? why) is { } item) items.Add(item);
            else refusals.Add(why!);
        }

        if (refusals.Count > 0) return new NetlistSchematicResult(null, refusals, notes);

        var model = new SchematicEditModel();
        foreach (var it in items) model.Components.Add(it.Comp);

        var placed = Place(items, notes);
        var grounds = PlaceGrounds(model, placed);
        Wire(model, placed, grounds, notes);
        PlaceDirectives(model, tb);

        foreach (var a in tb.Analyses) model.Analyses.Add(a);
        if (tb.Tuning is { IsEmpty: false } tuning) model.Tuning = tuning.Clone();

        if (tb.Functions.Count > 0)
            notes.Add(
                $"{tb.Functions.Count} user function(s) ({string.Join(", ", tb.Functions.Select(f => f.Name))}) "
              + "are not carried: a schematic has nowhere to declare one.");
        if (tb.RawDirectives.Count > 0)
            notes.Add(
                $"{tb.RawDirectives.Count} directive(s) the netlist reader stores verbatim are not carried: "
              + string.Join("; ", tb.RawDirectives.Select(r => $"{r.Kind} {r.RawLine}".Trim())) + ".");

        return new NetlistSchematicResult(model, refusals, notes);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Netlist line → component
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The component a netlist line draws as, with its parameters and the net on each pin — or null
    /// with the reason. The kind is the one whose <see cref="ComponentTypeRegistry.EngineReference"/>
    /// IS the line's type, so this is the inverse of the extractor's own mapping and not a second table.
    /// </summary>
    private static Item? MapInstance(Instance inst, string? schematicDirectory, out string? why)
    {
        why = null;
        string reference = inst.Reference;

        if (reference.Equals("WBond", StringComparison.OrdinalIgnoreCase))
        {
            why = $"{inst.InstanceName}: a wirebond's pins come from the design file it references, not "
                + "from a built-in symbol, so it cannot be drawn from its netlist line.";
            return null;
        }

        var candidates = Enum.GetValues<SymbolKind>()
            .Where(k => !NotDrawable.Contains(k)
                        && string.Equals(ComponentTypeRegistry.EngineReference(k), reference,
                                         StringComparison.OrdinalIgnoreCase))
            .ToList();

        // The multi-tone spellings are one tile each; the extractor picks them off NumFreqs.
        if (reference.Equals("V_nTone", StringComparison.OrdinalIgnoreCase)) candidates = [SymbolKind.ToneSource];
        if (reference.Equals("I_nTone", StringComparison.OrdinalIgnoreCase)) candidates = [SymbolKind.CurrentToneSource];

        // A short code the netlist also accepts (`Term` for a port).
        if (candidates.Count == 0 && ComponentTypeRegistry.TryParseCode(reference, out var coded, out _)
            && !NotDrawable.Contains(coded))
            candidates = [coded];

        if (candidates.Count == 0)
        {
            why = $"{inst.InstanceName}: '{reference}' has no schematic symbol, so it cannot be drawn.";
            return null;
        }

        candidates = [.. candidates.OrderBy(k => Array.IndexOf(Preferred, k) is var i and >= 0 ? i : int.MaxValue)];

        string? lastWhy = null;
        foreach (var kind in candidates)
        {
            var comp = NewComponent(inst, kind, schematicDirectory);
            if (PinNets(comp, inst, out lastWhy) is { } nets)
                return new Item { Comp = comp, Nets = nets, Local = LocalPins(comp) };
        }

        why = $"{inst.InstanceName}: {lastWhy}";
        return null;
    }

    /// <summary>A value longer than this is left off the drawing — it is still the component's
    /// parameter, it simply does not fit beside the symbol.</summary>
    private const int LongestShownValue = 22;

    private static EditableComponent NewComponent(Instance inst, SymbolKind kind, string? schematicDirectory)
    {
        var info = ComponentTypeRegistry.Get(kind);
        var comp = new EditableComponent
        {
            InstanceName     = inst.InstanceName,
            Symbol           = kind,
            ShowTypeLabel    = info.DefaultShowTypeLabel,
            ShowInstanceName = info.DefaultShowInstanceName,
        };

        // Parameters FIRST: a variadic symbol reads its own pin count off one of them.
        var template = ComponentTypeRegistry.DefaultParameters(kind, 0);
        foreach (var o in inst.Overrides)
        {
            var t = template.FirstOrDefault(d => d.Name == o.Name);
            string expr = o.Expression;
            if (kind == SymbolKind.Snp && o.Name.Equals("File", StringComparison.OrdinalIgnoreCase))
                expr = RelativeFile(expr, schematicDirectory);
            bool show = t.Name is not null ? t.ShowOnSchematic : inst.Overrides.Count <= 2;
            comp.Parameters.Add(new EditableParameter
            {
                Name            = o.Name,
                Expression      = expr,
                Unit            = o.Unit ?? "",
                // The palette's own choice of what a placed part shows; a part the palette says
                // nothing about shows its values only when there are few enough to read.
                ShowOnSchematic = show && o.Name.Length + expr.Length + (o.Unit?.Length ?? 0) <= LongestShownValue,
                Dimension       = t.Name is not null ? t.Dimension : UnitDimension.None,
            });
        }

        if (inst.RefNetBinding is not null && kind == SymbolKind.Snp
            && !comp.Parameters.Any(p => p.Name == "RefNode"))
            comp.Parameters.Add(new EditableParameter { Name = "RefNode", Expression = "true", ShowOnSchematic = false });

        return comp;
    }

    /// <summary>A quoted absolute path, relative to <paramref name="dir"/> — what a schematic stores,
    /// and what the extraction's own reader resolves back to the same absolute path.</summary>
    private static string RelativeFile(string expr, string? dir)
    {
        if (dir is null || expr.Length < 2 || expr[0] != '"' || expr[^1] != '"') return expr;
        string path = expr[1..^1];
        if (!System.IO.Path.IsPathRooted(path)) return expr;
        return "\"" + System.IO.Path.GetRelativePath(dir, path).Replace('\\', '/') + "\"";
    }

    private static (float X, float Y)[] LocalPins(EditableComponent comp)
        => [.. (comp.Symbol == SymbolKind.Snp ? comp.GetEffectiveSnpPortDefs() : SymbolPortDefs.For(comp.Symbol, comp.PortCount))
               .Select(p => (p.LocalX, p.LocalY))];

    /// <summary>
    /// The net on each of <paramref name="comp"/>'s pins, or null when its symbol cannot carry the
    /// line's nets — the exact inverse of each special case in <c>NetExtractor.EmitInstance</c>.
    /// </summary>
    private static string[]? PinNets(EditableComponent comp, Instance inst, out string? why)
    {
        why = null;
        var nets = inst.NetBindings;
        var kind = comp.Symbol;

        // A variadic box whose line left its width to be inferred from the nets: state it, because
        // the symbol has no other way to know how many pins to draw.
        if (ComponentTypeRegistry.PortCountParameter(kind) is { } countParam
            && kind is not (SymbolKind.Switch or SymbolKind.SwitchD or SymbolKind.WBond)
            && !comp.Parameters.Any(p => p.Name == countParam))
        {
            int n = kind is SymbolKind.Sdd or SymbolKind.ZPort ? nets.Count / 2 : nets.Count;
            if (n >= 1)
                comp.Parameters.Add(new EditableParameter
                {
                    Name = countParam, Expression = n.ToString(CultureInfo.InvariantCulture), ShowOnSchematic = false,
                });
        }

        int pins = LocalPins(comp).Length;

        // One pin on the symbol, a second net hard-wired to ground by the extractor.
        if (kind is SymbolKind.TermG or SymbolKind.ViaGnd
                 or SymbolKind.Tuner or SymbolKind.LoadTuner or SymbolKind.SourceTuner)
        {
            if (nets.Count == 2 && nets[1] == Ground) return [nets[0]];
            why = $"'{inst.Reference}' draws one pin with its reference on ground; this line binds "
                + $"{string.Join(" ", nets)}.";
            return null;
        }

        // N pins for N ports, every port's minus on ground.
        if (NetExtractor.GroundReferencedPortBlocks.Contains(kind))
        {
            if (nets.Count == 2 * pins && Enumerable.Range(0, pins).All(i => nets[2 * i + 1] == Ground))
                return [.. Enumerable.Range(0, pins).Select(i => nets[2 * i])];
            why = $"'{inst.Reference}' as drawn has {pins} pin(s), each port's minus on ground; this "
                + $"line binds {nets.Count} net(s).";
            return null;
        }

        if (kind == SymbolKind.Snp && inst.RefNetBinding is { } refNet)
        {
            if (nets.Count + 1 == pins) return [.. nets, refNet];
        }
        else if (nets.Count == pins) return [.. nets];

        why = $"'{inst.Reference}' binds {nets.Count} net(s) and its symbol has {pins} pin(s).";
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Placement
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lays every item down, and returns them in the order the router should see them — path first,
    /// so each main-line net is seeded by a path pin and drawn as one straight run that everything
    /// else then Ts onto.
    /// </summary>
    private static List<Item> Place(List<Item> items, List<string> notes)
    {
        var order = new List<Item>();
        if (items.Count == 0) return order;

        var withPins = items.Where(i => i.Local.Length > 0).ToList();

        // ── The signal path ──────────────────────────────────────────────────
        var (path, start, end) = FindPath(withPins);

        var pathNets = new List<string>();
        var junction = new Dictionary<string, double>(StringComparer.Ordinal);   // net → main-line x

        var hangers = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        bool IsHanger(Item it)
            => !it.Placed && it.Local.Length is 1 or 2 && it.SignalNets.Count() == 1;

        if (path.Count > 0 || start is not null)
        {
            string firstNet = path.Count > 0 ? path[0].In : start!.SignalNets.First();
            pathNets.Add(firstNet);
            foreach (var step in path) pathNets.Add(step.Out);
            foreach (var step in path) step.Item.Placed = true;

            foreach (var n in pathNets) hangers[n] = [];
            foreach (var it in withPins)
                if (IsHanger(it) && hangers.TryGetValue(it.SignalNets.First(), out var list)) list.Add(it);

            // The end fixtures sit at the very ends of their nets' rows, so the main line runs
            // from one port to the other.
            void MoveTo(Item? it, bool first)
            {
                if (it is null) return;
                foreach (var l in hangers.Values) l.Remove(it);
                string n = it.SignalNets.First();
                if (!hangers.TryGetValue(n, out var list)) return;
                if (first) list.Insert(0, it); else list.Add(it);
            }
            MoveTo(start, first: true);
            if (end is not null && !ReferenceEquals(end, start)) MoveTo(end, first: false);

            // `cursor` is where the main line has got to: the last element's exit, or 0.
            double cursor = 0;
            for (int k = 0; k < pathNets.Count; k++)
            {
                string net = pathNets[k];
                var row = hangers[net];
                double first = k == 0 ? 0 : cursor + Lead;
                double x = first, last = first;
                for (int h = 0; h < row.Count; h++)
                {
                    var it = row[h];
                    PlaceHanger(it, net, x, HangerDrop);
                    if (h == 0 && k > 0)
                    {
                        // Clear of the previous element's labels as well as its glyph.
                        double shift = Snap(Math.Max(0, cursor - P - (it.Comp.X + it.Full.MinX)));
                        if (shift > 0) { PlaceHanger(it, net, x + shift, HangerDrop); first += shift; }
                    }
                    last = it.Pin(Array.IndexOf(it.Nets, net)).X;
                    order.Add(it);
                    if (h + 1 < row.Count)
                    {
                        var nx = row[h + 1];
                        PlaceHanger(nx, net, 0, HangerDrop);   // orient, to measure it
                        // With its pin at x = 0, nx's left edge is at nx.Comp.X + nx.Full.MinX.
                        double clear = it.Comp.X + it.Full.MaxX + 2 * P - (nx.Comp.X + nx.Full.MinX);
                        x = Math.Max(last + HangerPitch, Snap(clear));
                    }
                }

                double entry = row.Count > 0 ? last + Lead : k == 0 ? 0 : cursor + 2 * Lead;
                junction[net] = row.Count > 0 ? (first + last) / 2 : Snap((cursor + entry) / 2);

                if (k < path.Count) cursor = PlaceOnPath(path[k], entry);
            }
            // Path elements go to the router FIRST, then the two end fixtures, so each end net is
            // drawn as the line running on to its port and the other hangers T onto that.
            order.RemoveAll(i => ReferenceEquals(i, start) || ReferenceEquals(i, end));
            var head = path.Select(s => s.Item).ToList();
            if (start is not null) head.Add(start);
            if (end is not null && !ReferenceEquals(end, start)) head.Add(end);
            order.InsertRange(0, head);
        }

        // ── Elements bridging two path nets: above the line ────────────────
        var aboveRows = new List<List<(double L, double R)>>();
        foreach (var it in withPins)
        {
            if (it.Placed || it.Local.Length != 2) continue;
            var sn = it.SignalNets.ToList();
            if (sn.Count != 2 || !junction.ContainsKey(sn[0]) || !junction.ContainsKey(sn[1])) continue;

            string left  = junction[sn[0]] <= junction[sn[1]] ? sn[0] : sn[1];
            int leftPin  = Array.IndexOf(it.Nets, left);
            Orient(it, leftPin, 1 - leftPin, horizontal: true);
            double cx = Snap((junction[sn[0]] + junction[sn[1]]) / 2
                             - (it.Offset(leftPin).X + it.Offset(1 - leftPin).X) / 2);
            double l = cx + it.Full.MinX - P, rr = cx + it.Full.MaxX + P;

            int r = 0;
            while (true)
            {
                if (r == aboveRows.Count) aboveRows.Add([]);
                if (!aboveRows[r].Any(s => s.L < rr && l < s.R)) break;
                r++;
            }
            aboveRows[r].Add((l, rr));
            it.Comp.X = cx;
            it.Comp.Y = Snap(-BridgeRise - r * BridgeRowPitch - it.Offset(leftPin).Y);
            it.Placed = true;
            order.Add(it);
        }

        // ── Everything else: rows below, near what it connects to ──────────
        double rowsTop = Snap(order.Count == 0 ? 0
            : order.Max(i => i.Comp.Y + i.Full.MaxY + (i.Nets.Contains(Ground) ? 300 : 0)) + RowGap);

        var anchor = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var anchorTop = new Dictionary<string, double>(StringComparer.Ordinal);   // net → its highest terminal
        void Anchor(Item it)
        {
            for (int k = 0; k < it.Nets.Length; k++)
            {
                if (it.Nets[k] == Ground) continue;
                if (!anchor.TryGetValue(it.Nets[k], out var l)) anchor[it.Nets[k]] = l = [];
                var (px, py) = it.Pin(k);
                l.Add(px);
                anchorTop[it.Nets[k]] = anchorTop.TryGetValue(it.Nets[k], out double t) ? Math.Min(t, py) : py;
            }
        }
        foreach (var it in order) Anchor(it);

        var occupied = new List<(int Row, double L, double R)>();
        double rightmost = order.Count == 0 ? 0 : order.Max(i => i.Comp.X + i.Full.MaxX);

        var rest = withPins.Where(i => !i.Placed).ToList();
        while (rest.Count > 0)
        {
            // Take the next one touching something already placed, so a chain grows outwards from
            // the path rather than being scattered in file order.
            var next = rest.FirstOrDefault(i => i.SignalNets.Any(anchor.ContainsKey)) ?? rest[0];
            rest.Remove(next);

            OrientFree(next, anchor);
            var xs = next.SignalNets.Where(anchor.ContainsKey).Select(n => anchor[n].Average()).ToList();
            double want = xs.Count > 0 ? xs.Average() : rightmost + 2 * Slot;

            double left = next.Full.MinX - 2 * P, right = next.Full.MaxX + 2 * P;
            double height = next.Full.MaxY - next.Full.MinY + (next.Nets.Contains(Ground) ? 300 : 0);
            int rowsNeeded = Math.Max(1, (int)Math.Ceiling((height + 2 * P) / RowPitchOf()));

            // A chain grows DOWNWARDS: a part hangs below the nets it connects to, so the wire to
            // each comes from above rather than looping round from the far side.
            double below = next.SignalNets.Where(anchorTop.ContainsKey)
                               .Select(n => anchorTop[n] + 2 * P).DefaultIfEmpty(rowsTop).Max();
            int firstRow = Math.Max(0, (int)Math.Ceiling((below - rowsTop) / RowPitchOf()));

            double bestCost = double.MaxValue, bestX = want; int bestRow = firstRow;
            for (int row = firstRow; row < firstRow + 40; row++)
            {
                for (int s = 0; s < 80; s++)
                {
                    foreach (int sign in s == 0 ? new[] { 1 } : new[] { 1, -1 })
                    {
                        double x = Snap(want + sign * s * Slot);
                        bool clash = occupied.Any(o => o.Row >= row && o.Row < row + rowsNeeded
                                                       && o.L < x + right && x + left < o.R);
                        if (clash) continue;
                        double cost = Math.Abs(x - want) / Slot + (row - firstRow) * 3;
                        if (cost < bestCost) { bestCost = cost; bestX = x; bestRow = row; }
                    }
                }
                if (bestCost <= (row - firstRow + 1) * 3) break;   // no later row can beat it
            }

            next.Comp.X = bestX;
            next.Comp.Y = Snap(rowsTop + bestRow * RowPitchOf() - next.Full.MinY);
            for (int r = bestRow; r < bestRow + rowsNeeded; r++) occupied.Add((r, bestX + left, bestX + right));
            next.Placed = true;
            order.Add(next);
            Anchor(next);
            rightmost = Math.Max(rightmost, next.Comp.X + next.Full.MaxX);
        }

        // Components with no pins (a mutual coupling names its inductors, not nets): along the top.
        double tx = 0;
        foreach (var it in items.Where(i => i.Local.Length == 0))
        {
            BoxOf(it);
            it.Comp.X = Snap(tx - it.Box.MinX);
            it.Comp.Y = Snap(-BridgeRise - aboveRows.Count * BridgeRowPitch - BridgeRowPitch - it.Full.MaxY);
            tx = it.Comp.X + it.Box.MaxX + 2 * Slot;
            it.Placed = true;
        }

        return order;

        static double RowPitchOf() => 600;
    }

    private sealed record Step(Item Item, int InPin, int OutPin, string In, string Out);

    /// <summary>
    /// The main line: the shortest chain of elements from the first port to the second, or — with
    /// fewer than two ports — between the two drive/termination fixtures furthest apart.
    /// </summary>
    private static (List<Step> Path, Item? Start, Item? End) FindPath(List<Item> items)
    {
        var bridges = items.Where(i => i.SignalNets.Count() >= 2).ToList();

        var ports = items.Where(i => i.Comp.Symbol is SymbolKind.Term or SymbolKind.TermG && i.SignalNets.Count() == 1)
                         .OrderBy(i => NumOf(i.Comp))
                         .ToList();

        List<Item> ends;
        if (ports.Count >= 2) ends = ports;
        else
        {
            ends = [.. items.Where(i => i.SignalNets.Count() == 1 && Fixtures.Contains(i.Comp.Symbol))];
            if (ends.Count < 2) ends = [.. items.Where(i => i.SignalNets.Count() == 1)];
        }

        (Item? A, Item? B, List<Step>? Path) best = (null, null, null);
        if (ports.Count >= 2)
        {
            var p = Bfs(bridges, ports[0].SignalNets.First(), ports[1].SignalNets.First());
            best = (ports[0], ports[1], p);
        }
        else
        {
            for (int a = 0; a < ends.Count; a++)
                for (int b = a + 1; b < ends.Count; b++)
                {
                    var p = Bfs(bridges, ends[a].SignalNets.First(), ends[b].SignalNets.First());
                    if (p is not null && (best.Path is null || p.Count > best.Path.Count))
                        best = (ends[a], ends[b], p);
                }
        }

        if (best.Path is not null) return (best.Path, best.A, best.B);

        // No two ends connect: the longest chain out of whatever there is, so there is still a line.
        if (ends.Count > 0) return ([], ends[0], null);
        if (bridges.Count == 0) return ([], null, null);
        string s = bridges[0].SignalNets.First();
        var far = Farthest(bridges, s);
        return (Bfs(bridges, s, far) ?? [], null, null);
    }

    private static int NumOf(EditableComponent c)
        => int.TryParse(c.Parameters.FirstOrDefault(p => p.Name == "Num")?.Expression, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;

    private static List<Step>? Bfs(List<Item> bridges, string from, string to)
    {
        if (from == to) return [];
        var prev = new Dictionary<string, Step>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            string n = queue.Dequeue();
            foreach (var it in bridges)
            {
                int inPin = Array.IndexOf(it.Nets, n);
                if (inPin < 0) continue;
                for (int k = 0; k < it.Nets.Length; k++)
                {
                    string m = it.Nets[k];
                    if (m == Ground || m == n || !seen.Add(m)) continue;
                    prev[m] = new Step(it, inPin, k, n, m);
                    if (m == to)
                    {
                        var path = new List<Step>();
                        for (string c = to; c != from; c = prev[c].In) path.Add(prev[c]);
                        path.Reverse();
                        // An element may not appear twice on one line.
                        return path.Select(p => p.Item).Distinct().Count() == path.Count ? path : null;
                    }
                    queue.Enqueue(m);
                }
            }
        }
        return null;
    }

    private static string Farthest(List<Item> bridges, string from)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        string last = from;
        while (queue.Count > 0)
        {
            last = queue.Dequeue();
            foreach (var it in bridges.Where(i => i.Nets.Contains(last)))
                foreach (var m in it.Nets)
                    if (m != Ground && seen.Add(m)) queue.Enqueue(m);
        }
        return last;
    }

    /// <summary>Places a path element with its entry pin at <paramref name="entryX"/> on the main
    /// line; returns the x the line continues from.</summary>
    private static double PlaceOnPath(Step step, double entryX)
    {
        var it = step.Item;
        Orient(it, step.InPin, step.OutPin, horizontal: true);
        var (ix, iy) = it.Offset(step.InPin);
        it.Comp.X = Snap(entryX - ix);
        it.Comp.Y = Snap(-iy);
        double rightmost = it.Comp.X + Math.Max(it.Box.MaxX, Enumerable.Range(0, it.Local.Length).Max(k => it.Offset(k).X));
        return Math.Max(it.Pin(step.OutPin).X, rightmost - P);
    }

    /// <summary>A hanging element: its signal pin uppermost at (x, <paramref name="drop"/>).</summary>
    private static void PlaceHanger(Item it, string net, double x, double drop)
    {
        int top = Array.IndexOf(it.Nets, net);
        int other = it.Local.Length == 2 ? 1 - top : -1;
        Orient(it, top, other, horizontal: false);
        var (ox, oy) = it.Offset(top);
        it.Comp.X = Snap(x - ox);
        it.Comp.Y = Snap(drop - oy);
        it.Placed = true;
    }

    /// <summary>
    /// Picks the rotation that puts pin <paramref name="a"/> furthest left of pin <paramref name="b"/>
    /// (horizontal) or furthest above it (vertical) — R0 on a tie, so a symbol is drawn as the palette
    /// draws it whenever that already reads correctly.
    /// </summary>
    private static void Orient(Item it, int a, int b, bool horizontal)
    {
        SymbolRotation best = SymbolRotation.R0;
        double bestScore = double.MaxValue;
        foreach (var rot in new[] { SymbolRotation.R0, SymbolRotation.R90, SymbolRotation.R270, SymbolRotation.R180 })
        {
            it.Comp.Rotation = rot;
            var pa = it.Offset(a);
            double score;
            if (b >= 0)
            {
                var pb = it.Offset(b);
                score = horizontal ? (pa.X - pb.X) * 10 + Math.Abs(pa.Y - pb.Y)
                                   : (pa.Y - pb.Y) * 10 + Math.Abs(pa.X - pb.X);
            }
            else score = horizontal ? pa.X : pa.Y;   // a single pin: furthest left / furthest up
            if (score < bestScore - 1e-9) { bestScore = score; best = rot; }
        }
        it.Comp.Rotation = best;
        BoxOf(it);
    }

    /// <summary>An element off the line: two-terminal parts stand upright with an already-placed net
    /// on top, so the wire to it rises; anything else is drawn as the palette draws it.</summary>
    private static void OrientFree(Item it, Dictionary<string, List<double>> anchor)
    {
        if (it.Local.Length == 2)
        {
            int top = anchor.ContainsKey(it.Nets[0]) || it.Nets[1] == Ground ? 0
                    : anchor.ContainsKey(it.Nets[1]) || it.Nets[0] == Ground ? 1 : 0;
            Orient(it, top, 1 - top, horizontal: false);
            return;
        }
        it.Comp.Rotation = SymbolRotation.R0;
        BoxOf(it);
    }

    /// <summary>The drawn glyph's box relative to the origin, with every pin inside it.</summary>
    /// <summary>
    /// Measures the item under its current rotation — glyph, pins and labels — and, for an upright
    /// part, moves its labels beside it first.
    /// </summary>
    private static (double, double, double, double) BoxOf(Item it)
    {
        var (box, bottom, full) = Measure(it);
        it.Box = box;
        it.GlyphBottom = bottom;
        it.Comp.LabelOffsets.Clear();
        if (box.Item4 - box.Item2 > box.Item3 - box.Item1 && it.Comp.Symbol != SymbolKind.Ground)
        {
            LabelsBeside(it);
            full = Measure(it).Full;
        }
        it.Full = (Math.Min(full.Item1, box.Item1), Math.Min(full.Item2, box.Item2),
                   Math.Max(full.Item3, box.Item3), Math.Max(full.Item4, box.Item4));
        return box;
    }

    private static ((double, double, double, double) Box, double GlyphBottom, (double, double, double, double) Full)
        Measure(Item it)
    {
        var probe = new SchematicEditModel();
        var c = it.Comp;
        double x = c.X, y = c.Y;
        c.X = 0; c.Y = 0;
        probe.Components.Add(c);
        var rc = probe.BuildRenderModel().Model.Components[0];
        c.X = x; c.Y = y;

        double minX = rc.GlyphBbMinX, minY = rc.GlyphBbMinY, maxX = rc.GlyphBbMaxX, maxY = rc.GlyphBbMaxY;
        for (int k = 0; k < it.Local.Length; k++)
        {
            var (px, py) = it.Offset(k);
            minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
        }
        // The label block, from the renderer's own row geometry but with a width nearer the text
        // than its culling estimate, which is generous on purpose and would space parts a third
        // further apart than the text needs.
        double lMinX = minX, lMinY = minY, lMaxX = maxX, lMaxY = maxY;
        double baseX = SchematicComponent.LabelBaseXFor(c.Symbol);
        double baseY = SchematicComponent.LabelBaseYFor(c.Symbol, c.PortCount, rc.GlyphBbMaxY);
        for (int i = 0; i < rc.Labels.Count; i++)
        {
            if (string.IsNullOrEmpty(rc.Labels[i])) continue;
            var (dx, dy) = SchematicComponent.LabelOffsetAt(c.LabelOffsets, i);
            double bx = baseX + dx, by = baseY + dy + i * SchematicComponent.LabelWorldStep;
            lMinX = Math.Min(lMinX, bx);
            lMaxX = Math.Max(lMaxX, bx + rc.Labels[i].Length * LabelCharWidth);
            lMinY = Math.Min(lMinY, by - SchematicComponent.LabelWorldHeight);
            lMaxY = Math.Max(lMaxY, by + 20);
        }
        return ((minX, minY, maxX, maxY), rc.GlyphBbMaxY, (lMinX, lMinY, lMaxX, lMaxY));
    }

    /// <summary>Average world width of one label character at the renderer's label size.</summary>
    private const double LabelCharWidth = 40;

    /// <summary>
    /// Moves an upright part's label block beside it, vertically centred on the glyph. The default
    /// place is UNDER the glyph, which on a part standing on its own ground symbol is on top of
    /// that symbol.
    /// </summary>
    private static void LabelsBeside(Item it)
    {
        var c = it.Comp;
        int rows = 2 + c.Parameters.Count(p => p.ShowOnSchematic);
        double baseX = SchematicComponent.LabelBaseXFor(c.Symbol);
        double baseY = SchematicComponent.LabelBaseYFor(c.Symbol, c.PortCount, it.GlyphBottom);
        double centre = (it.Box.MinY + it.Box.MaxY) / 2;
        double firstBaseline = centre - rows * SchematicComponent.LabelWorldStep / 2 + SchematicComponent.LabelWorldHeight;
        c.LabelOffsets.Clear();
        c.LabelOffsets.Add((it.Box.MaxX + 60 - baseX, firstBaseline - baseY));
    }

    private static double Snap(double v) => Math.Round(v / P) * P;

    // ─────────────────────────────────────────────────────────────────────────
    //  Grounds
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One ground symbol ON every terminal sitting on net <c>0</c>, pointing the way that terminal
    /// does. Each pair gets a private net name so the router never joins two grounds across the
    /// sheet; extraction gives them all net <c>0</c> regardless, because a ground NAMES its net.
    /// </summary>
    private static List<Item> PlaceGrounds(SchematicEditModel model, List<Item> placed)
    {
        var made = new List<Item>();
        int n = 0;
        foreach (var owner in placed)
            for (int k = 0; k < owner.Nets.Length; k++)
            {
                if (owner.Nets[k] != Ground) continue;
                string net = $"{Ground}#{n++}";
                owner.Nets[k] = net;

                var (px, py) = owner.Pin(k);
                var (ux, uy) = Outward(owner, k);
                var comp = new EditableComponent
                {
                    Symbol           = SymbolKind.Ground,
                    ShowTypeLabel    = ComponentTypeRegistry.Get(SymbolKind.Ground).DefaultShowTypeLabel,
                    ShowInstanceName = ComponentTypeRegistry.Get(SymbolKind.Ground).DefaultShowInstanceName,
                    X        = px,
                    Y        = py,
                    // A ground's stem is drawn along local +Y, so it is turned to point AWAY from
                    // the terminal it sits on.
                    Rotation = (ux, uy) switch
                    {
                        (0, > 0) => SymbolRotation.R0,
                        (< 0, 0) => SymbolRotation.R90,
                        (0, < 0) => SymbolRotation.R180,
                        _        => SymbolRotation.R270,
                    },
                };
                comp.InstanceName = $"{ComponentTypeRegistry.InstancePrefix(SymbolKind.Ground)}{n}";
                model.Components.Add(comp);
                var g = new Item { Comp = comp, Nets = [net], Local = [(0f, 0f)], Placed = true };
                BoxOf(g);
                made.Add(g);
            }
        return made;
    }

    /// <summary>The way a lead points: from the glyph's centre towards the pin, on the dominant axis.</summary>
    private static (int X, int Y) Outward(Item it, int k)
    {
        var (px, py) = it.Offset(k);
        double cx = (it.Box.MinX + it.Box.MaxX) / 2, cy = (it.Box.MinY + it.Box.MaxY) / 2;
        double dx = px - cx, dy = py - cy;
        if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6) return (0, -1);
        return Math.Abs(dx) > Math.Abs(dy) ? (Math.Sign(dx), 0) : (0, Math.Sign(dy));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Wires and labels
    // ─────────────────────────────────────────────────────────────────────────

    private static void Wire(SchematicEditModel model, List<Item> placed, List<Item> grounds, List<string> notes)
    {
        var all = placed.Concat(grounds).ToList();

        var blocks = all.Select(it => new SchematicAutoRouter.Block(
            [.. Enumerable.Range(0, it.Local.Length).Select(k => { var (x, y) = it.Pin(k); return (x, y, (string?)it.Nets[k]); })],
            KeepOut(it, isGround: it.Comp.Symbol == SymbolKind.Ground))).ToList();

        var routed = SchematicAutoRouter.Route(blocks);

        // The net each terminal cell belongs to, so each wire can be attributed to its net — the
        // route's last vertex is always the terminal it was routed to.
        var netAt = new Dictionary<(long, long), string>();
        foreach (var it in all)
            for (int k = 0; k < it.Local.Length; k++)
                netAt[Key(it.Pin(k))] = it.Nets[k];

        var wiresOf = new Dictionary<string, List<EditableWire>>(StringComparer.Ordinal);
        foreach (var path in routed.Wires)
        {
            var wire = new EditableWire();
            foreach (var pt in path) wire.Points.Add(pt);
            model.Wires.Add(wire);
            if (netAt.TryGetValue(Key(path[^1]), out var net) || netAt.TryGetValue(Key(path[0]), out net))
            {
                if (!wiresOf.TryGetValue(net, out var l)) wiresOf[net] = l = [];
                l.Add(wire);
            }
        }

        // A terminal the router could not reach is connected by name instead — a real connection.
        var unroutedNets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (x, y, net) in routed.Unrouted)
        {
            if (net.StartsWith(Ground + "#", StringComparison.Ordinal)) continue;   // a ground sits ON its pin
            model.NetLabels.Add(new EditableNetLabel { X = x, Y = y, Name = net });
            unroutedNets.Add(net);
        }
        if (unroutedNets.Count > 0)
            notes.Add(
                $"{unroutedNets.Count} net(s) could not be wired completely without crossing something, and "
              + $"are joined by net label instead: {string.Join(", ", unroutedNets.Order(StringComparer.Ordinal))}. "
              + "The circuit is the one the netlist states; the drawing is what suffered.");

        // Every net keeps the name the netlist gave it: one label, on a stretch of its own wire no
        // other net's wire passes through, or on one of its terminals when it has no wire at all.
        var segs = new List<(string Net, double Ax, double Ay, double Bx, double By)>();
        foreach (var (net, ws) in wiresOf)
            foreach (var w in ws)
                for (int i = 0; i + 1 < w.Points.Count; i++)
                    segs.Add((net, w.Points[i].X, w.Points[i].Y, w.Points[i + 1].X, w.Points[i + 1].Y));

        var keepCells = new HashSet<(long, long)>();
        foreach (var b in blocks) foreach (var c in b.KeepOut) keepCells.Add(Key(c));

        var signalNets = placed.SelectMany(i => i.Nets).Where(n => !n.StartsWith(Ground + "#", StringComparison.Ordinal) && n != Ground)
                               .Distinct(StringComparer.Ordinal).ToList();
        foreach (var net in signalNets)
        {
            if (unroutedNets.Contains(net)) continue;   // already labelled at its terminals
            if (LabelPoint(net, wiresOf, segs, keepCells) is { } at)
            {
                var label = new EditableNetLabel { Name = net };
                label.AnchorToWire(at.Wire, at.X, at.Y);
                model.NetLabels.Add(label);
                continue;
            }
            var term = placed.SelectMany(i => Enumerable.Range(0, i.Nets.Length).Where(k => i.Nets[k] == net).Select(k => i.Pin(k))).First();
            model.NetLabels.Add(new EditableNetLabel { X = term.X, Y = term.Y, Name = net });
        }
    }

    private static (EditableWire Wire, double X, double Y)? LabelPoint(
        string net, Dictionary<string, List<EditableWire>> wiresOf,
        List<(string Net, double Ax, double Ay, double Bx, double By)> segs, HashSet<(long, long)> keep)
    {
        if (!wiresOf.TryGetValue(net, out var ws)) return null;

        var candidates = new List<(EditableWire W, double X, double Y, double Score)>();
        foreach (var w in ws)
            for (int i = 0; i + 1 < w.Points.Count; i++)
            {
                var (ax, ay) = w.Points[i];
                var (bx, by) = w.Points[i + 1];
                bool horizontal = ay == by;
                double len = Math.Abs(bx - ax) + Math.Abs(by - ay);
                int cells = (int)Math.Round(len / P);
                for (int c = 1; c < cells; c++)
                {
                    double x = ax + Math.Sign(bx - ax) * c * P, y = ay + Math.Sign(by - ay) * c * P;
                    if (keep.Contains(Key((x, y)))) continue;
                    if (segs.Any(s => s.Net != net && SchematicGeometry.PointOnSegment(x, y, s.Ax, s.Ay, s.Bx, s.By, P / 2)))
                        continue;
                    // Long horizontal runs first, and the middle of a run rather than its end.
                    double score = (horizontal ? 0 : 1000) - len + Math.Abs(c - cells / 2.0) * 10;
                    candidates.Add((w, x, y, score));
                }
            }
        if (candidates.Count == 0) return null;
        var best = candidates.MinBy(c => c.Score);
        return (best.W, best.X, best.Y);
    }

    /// <summary>
    /// The world cells no wire may enter: the drawn glyph, one grid square proud — except each pin's
    /// own cell and the cell immediately outside it, which is what makes a terminal reachable from
    /// exactly the direction its lead points. A ground keeps out only its own glyph, because it sits
    /// ON another part's pin and a margin would wall that pin in.
    /// </summary>
    private static IReadOnlyList<(double X, double Y)> KeepOut(Item it, bool isGround)
    {
        double margin = isGround ? 0 : P;
        var exempt = new HashSet<(long, long)>();
        for (int k = 0; k < it.Local.Length; k++)
        {
            var (px, py) = it.Pin(k);
            exempt.Add(Key((px, py)));
            if (isGround) continue;
            var (ux, uy) = Outward(it, k);
            exempt.Add(Key((px + ux * P, py + uy * P)));
        }

        var cells = new HashSet<(long, long)>();
        // The glyph's box to the NEAREST grid line, then the margin: a glyph that pokes a few units
        // past its own pin (a polarity mark) must not cost a whole extra cell, or the cell outside
        // the pin is walled in and nothing reaches it.
        double x0 = Snap(it.Comp.X + it.Box.MinX) - margin, x1 = Snap(it.Comp.X + it.Box.MaxX) + margin;
        double y0 = Snap(it.Comp.Y + it.Box.MinY) - margin, y1 = Snap(it.Comp.Y + it.Box.MaxY) + margin;
        for (double x = x0; x <= x1 + 0.5; x += P)
            for (double y = y0; y <= y1 + 0.5; y += P)
                cells.Add(Key((x, y)));

        // The label rows too, with no margin: a wire through a value is unreadable, a wire
        // alongside one is not. Only the cells the text actually covers — the box around glyph
        // AND text would wall in a pin that sits between the two.
        if (!isGround)
            foreach (var (l, t, r, b) in LabelRows(it))
                for (double x = Math.Ceiling((it.Comp.X + l) / P) * P; x <= it.Comp.X + r; x += P)
                    for (double y = Math.Ceiling((it.Comp.Y + t) / P) * P; y <= it.Comp.Y + b; y += P)
                        cells.Add(Key((x, y)));

        cells.ExceptWith(exempt);
        return [.. cells.Select(c => (c.Item1 * P, c.Item2 * P))];
    }

    /// <summary>Each non-empty label row's box, relative to the component origin.</summary>
    private static IEnumerable<(double L, double T, double R, double B)> LabelRows(Item it)
    {
        var c = it.Comp;
        var labels = c.ToRenderComponent().Labels;
        double baseX = SchematicComponent.LabelBaseXFor(c.Symbol);
        double baseY = SchematicComponent.LabelBaseYFor(c.Symbol, c.PortCount, it.GlyphBottom);
        for (int i = 0; i < labels.Count; i++)
        {
            if (string.IsNullOrEmpty(labels[i])) continue;
            var (dx, dy) = SchematicComponent.LabelOffsetAt(c.LabelOffsets, i);
            double bx = baseX + dx, by = baseY + dy + i * SchematicComponent.LabelWorldStep;
            yield return (bx, by - SchematicComponent.LabelWorldHeight, bx + labels[i].Length * LabelCharWidth, by + 20);
        }
    }

    private static (long, long) Key((double X, double Y) p)
        => ((long)Math.Round(p.X / P), (long)Math.Round(p.Y / P));

    // ─────────────────────────────────────────────────────────────────────────
    //  Variables and measurements
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The netlist's globals in one VAR block and its measurements in one MEAS block, side
    /// by side ABOVE everything else, each ending just over the circuit. Above rather than below
    /// because a block's rows grow downwards from its glyph, and a block at the bottom of the sheet
    /// runs off the end of a picture framed on the glyphs.</summary>
    private static void PlaceDirectives(SchematicEditModel model, TestBench tb)
    {
        if (tb.GlobalVariables.Count == 0 && tb.Measurements.Count == 0) return;

        var bounds = model.BuildRenderModel().Model;
        double top = 0, x = 0;
        if (model.Components.Count > 0)
        {
            top = bounds.Components.Min(c => c.FullBbMinY);
            x   = bounds.Components.Min(c => c.FullBbMinX);
        }
        foreach (var w in model.Wires) foreach (var (_, wy) in w.Points) top = Math.Min(top, wy);
        x = Snap(x);

        void Block(SymbolKind kind, string name, IEnumerable<EditableParameter> rows)
        {
            var c = new EditableComponent { Symbol = kind, InstanceName = name };
            foreach (var r in rows) c.Parameters.Add(r);
            var probe = new SchematicEditModel();
            probe.Components.Add(c);
            var rc = probe.BuildRenderModel().Model.Components[0];
            c.X = Snap(x - rc.FullBbMinX);
            c.Y = Snap(top - 400 - rc.FullBbMaxY);
            model.Components.Add(c);
            x = Snap(c.X + rc.FullBbMaxX + 600);
        }

        if (tb.GlobalVariables.Count > 0)
            Block(SymbolKind.Var, "VAR1", tb.GlobalVariables.Select(v => new EditableParameter
            {
                Name = v.Name, Expression = v.Expression ?? "", Unit = v.Unit ?? "",
            }));
        if (tb.Measurements.Count > 0)
            Block(SymbolKind.Meas, "MEAS1", tb.Measurements.Select(m => new EditableParameter
            {
                Name = m.Name, Expression = m.Expression, Unit = m.Unit ?? "",
            }));
    }
}
