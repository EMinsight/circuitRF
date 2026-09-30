// brief-em3d-42 R-em3d42-2 — the solids of a layout: its geometry and materials, and nothing else.
//
// SPLIT OUT OF Em3dGenerator, which used to build the whole 3D problem in one pass. Two callers need the
// geometry half now: the generator (a .cem run of a layout), which adds the air box, the ports, the sweep
// and the air above the stack on top of it; and the .c3d elaborator (a layout placed as an INSTANCE in a
// 3D view), which adds none of those because the parent owns them (overview §1i, §1k).
//
// THE SPLIT IS BYTE-IDENTICAL (R-em3d42-2b). Every .cem in the repository and every generator fixture was
// dumped as text BEFORE this file existed (testdata/em3d/generator-dumps/, Em3dGeneratorDumpTests), and the
// generator must still reproduce every dump. That is why the builder below is STAGED rather than one call:
// the generator's notes and materials are ordered lists, and the generator's port and air-box work falls
// BETWEEN the geometry's preparation and its solids — a one-shot "geometry first, then the rest" would
// have reordered both. The stages are:
//
//   Prepare()        everything up to and including the bond wires, and the content's extent;
//   (the generator's ports and air box, which read Bands/Pieces/Ground/FloorZ)
//   BuildSolids()    the solids and sheets in construction order — WITHOUT the air above the stack, whose
//                    slot (order, index, material) is reserved when a caller asks for it, so the generator
//                    adds the air solid itself;
//   FinishNotes()    the temperature notes, which the generator has always written last but one.
//
// `From` is the one-call form an instance uses, with the instance options (§2c): no solve region, no PEC
// floor, and every laterally unbounded slab — a dielectric, a body without an outline, and the ground
// plane that a .cem would have made its floor — bounded by SlabLateralBound's rule: the board outline, else the
// closed hull of the copper above and below the slab, else the drawn geometry's bounding box, with a note
// saying which bound each slab took. A planar setup previewed in 3D (DisplaySlabs) draws its slabs the same way.

using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using WireMaterial = CircuitRF.WBond.WireMaterial;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>What a layout's solids are built for, and at what physics.</summary>
/// <param name="FMaxHz">The top frequency — the sheet rule compares a conductor's thickness with its skin
/// depth there (R-em3d3-5f). Null when there is no sweep (a 3D view elaborated with no setup): a conductor
/// with thickness is then a solid.</param>
/// <param name="TempC">The operating temperature σ(T) is evaluated at.</param>
/// <param name="Instance">True for a layout placed in a 3D view (§2c): no solve region, no floor, bounded slabs.</param>
public sealed record Em3dLayoutSolidsOptions(double? FMaxHz, double TempC, bool Instance)
{
    /// <summary>A <c>.cem</c> run's region: the setup's SolveRegion (the generator's case). Null takes the
    /// whole layout.</summary>
    public EmSetup? RegionSetup { get; init; }

    /// <summary>False when the <c>.cem</c>'s AirBox ZMin states a boundary other than PEC, which steps the
    /// floor rule aside (the generator's case). An instance never has a floor.</summary>
    public bool FloorAllowed { get; init; } = true;

    /// <summary>A planar setup previewed in 3D, for VIEWING only: every laterally unbounded slab is drawn to
    /// <see cref="SlabLateralBound"/>'s shape rather than across the air box. The planar solve still treats
    /// every dielectric as laterally infinite; nothing a solver reads is built this way.</summary>
    public bool DisplaySlabs { get; init; }
}

/// <summary>A layout's geometry as the 3D problem's vocabulary: solids, sheets and materials, and where
/// each came from. No air box, port, sweep, air solid or solve region (R-em3d42-2a).</summary>
public sealed record Em3dLayoutSolidsResult(
    IReadOnlyList<Em3dSolid>    Solids,
    IReadOnlyList<Em3dSheet>    Sheets,
    IReadOnlyList<Em3dMaterial> Materials,
    string?                     Refusal,
    IReadOnlyList<string>       Notes)
{
    public bool Ok => Refusal is null;
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyDictionary<string, Em3dObjectOrigin> Origins { get; init; } = new Dictionary<string, Em3dObjectOrigin>();
    public IReadOnlyDictionary<string, string> MaterialSources { get; init; } = new Dictionary<string, string>();

    /// <summary>Each conductor's net(s), by object name.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> ObjectNets { get; init; } =
        new Dictionary<string, IReadOnlySet<string>>();

    /// <summary>The conductors on ground-reference stackup entries.</summary>
    public IReadOnlyList<string> GroundBandObjects { get; init; } = [];

    public IReadOnlyList<Em3dWireReport> Wires { get; init; } = [];
    public IReadOnlyList<string> NoAlpha { get; init; } = [];
    public IReadOnlyList<string> UnknownTemperature { get; init; } = [];

    /// <summary>The bottom of the stackup, metres — what an instance's z places (overview §1i).</summary>
    public double StackBottomM { get; init; }
}

public static class Em3dLayoutSolids
{
    /// <summary>The solids of <paramref name="source"/>'s layout through <paramref name="tech"/>.</summary>
    /// <param name="wires">Null takes the <c>.wBond</c> stem-paired with the layout (WB40), as a <c>.cem</c> run does.</param>
    public static Em3dLayoutSolidsResult From(EmLayoutSource source, Technology tech, Em3dWireSource? wires,
                                              Em3dLayoutSolidsOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tech);
        ArgumentNullException.ThrowIfNull(options);
        var b = new Builder(source, tech, wires, options);
        if (b.Prepare() is { } refusal) return b.Failed(refusal);
        b.BuildSolids(null, reserveAir: false);
        b.FinishNotes();
        return b.Result();
    }

    /// <summary>
    /// The staged builder (see the file's note). <b>Internal</b>: the generator drives its stages; every
    /// other caller takes <see cref="From"/>.
    /// </summary>
    internal sealed class Builder(EmLayoutSource source, Technology tech, Em3dWireSource? wires, Em3dLayoutSolidsOptions options)
    {
        public readonly List<string> Notes = [];
        public readonly List<string> WarningList = [];
        private readonly List<Em3dMaterial> _materials = [];
        private readonly Dictionary<string, Em3dMaterial> _materialByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _materialSource = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Em3dObjectOrigin> OriginMap = new(StringComparer.Ordinal);
        private double _perDbu;
        // brief-em3d-22 — each conductor's net(s), by object name: a piece's drawn net, a via's own or
        // the net it lands on, a wire's array and its pads' nets. And the pieces on ground-reference
        // entries, which are the ground when the setup names no Ground3D.
        public readonly Dictionary<string, HashSet<string>> ObjectNetMap = new(StringComparer.Ordinal);
        public readonly List<string> GroundBandObjectList = [];

        /// <summary>One merged conductor piece: its polygon, its name and what it became.</summary>
        internal sealed record Piece(PlanarPolygon Poly, string Name, string Material, bool IsSheet,
                                     double ZBottom, double ZTop, double SheetZ, LayerKey Layer,
                                     string? SheetReason, string? Net = null);

        private const double StackPerDbu = 1.0 / (LayoutUnits.DefaultDbuPerMicron * 1e6);
        private const double Mu0 = 4e-7 * Math.PI;

        // ── what Prepare decides, read by the generator's ports and air box ──────────────────────
        public IReadOnlyList<PlanarExtractor.StackBand> Bands { get; private set; } = [];
        public Dictionary<int, List<Piece>> Pieces { get; } = [];
        public PlanarExtractor.StackBand? Ground { get; private set; }
        public bool PecFloor { get; private set; }
        public double FloorZ { get; private set; } = double.NaN;
        public bool FloorStatedAway { get; private set; }
        public Em3dWireBuild? WireBuild { get; private set; }

        /// <summary>The content's extent, metres — what an air box pads.</summary>
        public double Cx0 { get; private set; } = double.PositiveInfinity;
        public double Cy0 { get; private set; } = double.PositiveInfinity;
        public double Cx1 { get; private set; } = double.NegativeInfinity;
        public double Cy1 { get; private set; } = double.NegativeInfinity;
        public double ZLow { get; private set; } = double.PositiveInfinity;
        public double ZHigh { get; private set; } = double.NegativeInfinity;

        private Dictionary<int, PlanarExtractor.StackBand> _bandByIndex = [];
        private List<PlanarExtractor.StackBand> _inProblem = [];
        private readonly Dictionary<int, (double Bottom, double Top)> _dielZ = [];
        private readonly Dictionary<int, List<PlanarPolygon>> _filmPolys = [];
        private readonly List<PlanarPolygon> _outline = [];
        private readonly List<(TechBody Body, double ZBottom, double ZTop, List<PlanarPolygon> Polys)> _bodies = [];
        private readonly List<(LayoutShape Shape, StackupLayer Entry, PlanarExtractor.StackBand Top,
                               PlanarExtractor.StackBand Bottom)> _viaSpans = [];
        private readonly List<string> _unknownTemperature = [];
        private readonly SortedSet<string> _noAlpha = new(StringComparer.Ordinal);
        // An instance's plane: the lowest ground-reference conductor a .cem would have made its floor.
        private PlanarExtractor.StackBand? _instancePlane;

        // ── the solids, once built ───────────────────────────────────────────────────────────────
        public readonly List<Em3dSolid> SolidList = [];
        public readonly List<Em3dSheet> SheetList = [];

        /// <summary>The air solid's reserved slot, when the caller asked for one and the box leaves room.</summary>
        public (int Index, int Order, string Material, double Bottom)? AirSlot { get; private set; }

        public string? Prepare()
        {
            double tempC = options.TempC;
            if (source.DbuPerMicron <= 0)
                return $"The layout's resolution is {source.DbuPerMicron} DBU per micron, which is not a " +
                       "usable scale. Set a positive DbuPerMicron on the layout.";
            _perDbu = 1.0 / (source.DbuPerMicron * 1e6);

            // ── The geometry: flattened, then the solve region (R-em3d3-5h) ─────────────────
            // An instance takes the whole layout: the solve region is a .cem's, and a sub-cell's .cem is not consulted.
            var geometry = options.RegionSetup is { } regionSetup && !options.Instance
                ? EmGeometry.ForSetup(regionSetup, source)
                : EmGeometry.Flatten(source.View, source.AbsolutePath);
            Notes.AddRange(geometry.Notes);
            if (geometry.RegionRefusal is { } regionRefusal) return regionRefusal;

            var bands = PlanarExtractor.StackBands(tech.Stackup);
            Bands = bands;
            if (bands.Count == 0)
                return $"Technology '{tech.Name}' has no stackup layers, so nothing says how thick the " +
                       "metal is, what is under it, or where the ground plane sits. Add a stackup in " +
                       "the technology editor.";
            _bandByIndex = bands.ToDictionary(b => b.Index);

            // ── Classify: the planar extractor's bindings, read the planar extractor's way ───
            var conductorBinding = new Dictionary<LayerKey, PlanarExtractor.StackBand>();
            foreach (var b in bands.Where(b => b.Layer.Kind == StackupKind.Conductor))
                foreach (var key in b.Layer.DrawingLayers)
                    if (!conductorBinding.TryGetValue(key, out var have) ||
                        (have.Layer.IsGroundReference && !b.Layer.IsGroundReference))
                        conductorBinding[key] = b;   // a signal binding wins, as PlanarExtractor's does

            var viaBinding    = PlanarExtractor.ViaBinding(tech.Stackup, out int nonPlated);
            var outlineLayers = Em3dGenerator.BoardOutlineLayers(tech);
            var byLayer       = new Dictionary<LayerKey, List<LayoutShape>>();

            var conductorShapes = new List<(LayoutShape Shape, int Level)>();
            var viaShapes       = new List<(LayoutShape Shape, StackupLayer Entry)>();
            var outlineShapes   = new List<LayoutShape>();
            int outlineStrokes  = 0, zeroWidthPaths = 0;

            foreach (var s in geometry.Shapes)
            {
                if (s is LabelShape or BitmapShape) continue;
                (byLayer.TryGetValue(s.Layer, out var list) ? list : byLayer[s.Layer] = []).Add(s);

                if (outlineLayers.Contains(s.Layer))
                {
                    if (s is PathShape) outlineStrokes++;
                    else outlineShapes.Add(s);
                    continue;
                }
                if (s is ViaShape vs)
                {
                    if (viaBinding.TryGetValue(vs.Layer, out var entry)) viaShapes.Add((vs, entry));
                    continue;
                }
                if (s is PathShape { Width: <= 0 }) { zeroWidthPaths++; continue; }
                if (conductorBinding.TryGetValue(s.Layer, out var band)) { conductorShapes.Add((s, band.Index)); continue; }
                if (viaBinding.TryGetValue(s.Layer, out var regionEntry)) viaShapes.Add((s, regionEntry));
            }

            if (nonPlated > 0)
                Notes.Add($"{nonPlated} via stackup entr(y/ies) are marked NON-PLATED and are not in the 3D " +
                          "problem as metal — a non-plated hole is a hole. Their artwork is unchanged.");
            if (zeroWidthPaths > 0)
                Notes.Add($"{zeroWidthPaths} zero-width path(s) are centrelines, not artwork, and are not " +
                          "in the 3D problem.");
            if (outlineStrokes > 0)
                Notes.Add($"{outlineStrokes} shape(s) on the board-outline layer are strokes, which bound " +
                          "no region; only closed shapes there give the board its outline.");

            // ── Merge (R-em3d3-5b): the planar extractor's union, per stackup entry ───────────
            var merged = PlanarExtractor.MergeOverlapping(conductorShapes, tech, out int mergedShapes, out int mergedInto, touching: true);
            if (mergedShapes > 0)
                Notes.Add($"{mergedShapes} overlapping conductor shape(s) were merged into {mergedInto} — " +
                          "copper that overlaps on one level is one conductor, in 3D as in the planar model.");

            var polysByBand = new Dictionary<int, List<(PlanarPolygon Poly, string? Net, LayerKey Layer)>>();
            foreach (var (shape, level) in merged)
                foreach (var poly in PlanarExtractor.ToPolygons(shape, tech, _perDbu))
                    (polysByBand.TryGetValue(level, out var l) ? l : polysByBand[level] = [])
                        .Add((poly, shape.Net is { Length: > 0 } n ? n : null, shape.Layer));

            if (polysByBand.Count == 0)
                return $"This EM setup is pointed at geometry with nothing on a layer bound to a " +
                       $"conductor entry in technology '{tech.Name}', so there is no metal to put in a 3D " +
                       "problem. Draw the artwork on a conductor layer, or bind the layer it is on to a " +
                       "conductor entry in the technology editor's Stackup tab.";

            // ── Vias: spans resolved once, geometry after the floor is known ─────────────────
            int unspanned = 0;
            foreach (var (shape, entry) in viaShapes)
            {
                if (ViaSpanResolver.Resolve(entry, tech) is not { } span ||
                    bands.FirstOrDefault(b => ReferenceEquals(b.Layer, span.Top)) is not { } top ||
                    bands.FirstOrDefault(b => ReferenceEquals(b.Layer, span.Bottom)) is not { } bottom)
                { unspanned++; continue; }
                _viaSpans.Add((shape, entry, top, bottom));
            }
            if (unspanned > 0)
                Notes.Add($"{unspanned} via(s) are on an entry whose span does not resolve to two conductors " +
                          "of the stackup, and are not in the 3D problem. " +
                          "Name both ends on the via entry (SpanFromLayer / SpanToLayer).");

            // ── The floor (R-em3d3-6a) ────────────────────────────────────────────────────────
            //
            // PEC on the lowest ground-reference conductor when that conductor is the planar model's
            // plane: UNDRAWN (so it is the laterally infinite return the planar kernel terminates on)
            // and with nothing drawn below it. A DRAWN plane is metal with holes in it — an antipad
            // closed by a PEC floor would short the via it exists to clear — so it becomes solids and
            // the floor goes below everything, absorbing.
            var ground = bands.Where(b => b.Layer.Kind == StackupKind.Conductor && b.Layer.IsGroundReference)
                              .OrderBy(b => b.BottomM).FirstOrDefault();
            Ground = ground;
            // A via's own pad on that plane does not DRAW it: a VIA/VIAGND cell puts a pad (and a pin) on
            // every layer it joins, and on a microstrip board one of them is the undrawn plane. Counting
            // those pads as a drawn plane turned the floor off, left every port with nothing to return
            // through, and refused the whole setup. When pads are all there is, they are the plane's.
            bool groundPadsOnly = ground is not null
                && polysByBand.TryGetValue(ground.Index, out var onGround)
                && onGround.All(p => IsViaPadOn(p.Poly, ground));
            bool pecFloor = ground is not null
                && (!polysByBand.ContainsKey(ground.Index) || groundPadsOnly)
                && polysByBand.Keys.Where(i => i != ground.Index).All(i => _bandByIndex[i].BottomM >= ground.TopM - 1e-15)
                && _viaSpans.All(v => v.Bottom.BottomM >= ground.BottomM - 1e-15)
                && tech.Bodies.All(body => bands.FirstOrDefault(b => b.Layer.Name == body.SitsOn) is not { } on
                                           || on.TopM >= ground.TopM - 1e-15);
            // The .cem's floor steps §6's rule aside only when it states a DIFFERENT boundary. A ZMin
            // that says Pec, or states only a padding, is the default floor restated: turning the floor
            // off for it left an undrawn ground with no representation and refused every port on it.
            FloorStatedAway = !options.FloorAllowed;
            if (FloorStatedAway) pecFloor = false;
            // brief-em3d-42 R-em3d42-2c — an instance has no air box to be floored: the plane the floor
            // would have been is metal, bounded like the slabs.
            if (options.Instance && pecFloor)
            {
                _instancePlane = ground;
                pecFloor = false;
            }
            if (groundPadsOnly && (pecFloor || _instancePlane is not null))
            {
                Notes.Add($"{polysByBand[ground!.Index].Count} via pad(s) on '{ground.Layer.Name}' are part of that " +
                          "plane rather than separate metal on it.");
                polysByBand.Remove(ground.Index);
            }
            PecFloor = pecFloor;
            FloorZ = pecFloor ? ground!.TopM : double.NaN;
            double floorZ = FloorZ;

            // ── Conductor pieces: named, sheet or solid ──────────────────────────────────────
            foreach (var band in bands.Where(b => polysByBand.ContainsKey(b.Index)))
            {
                var list = polysByBand[band.Index];
                string material = ConductorMaterial(band.Layer);
                var mat = _materialByName[material];
                double t = band.TopM - band.BottomM;
                double delta = mat.SigmaSm > 0 && options.FMaxHz is { } fMax
                    ? 1.0 / Math.Sqrt(Math.PI * fMax * Mu0 * mat.Mur * mat.SigmaSm) : 0;
                double sheetZ = band.Layer.SheetAt == ConductorSheetSurface.Top ? band.TopM : band.BottomM;

                var netCounts = list.Where(p => p.Net is not null).GroupBy(p => p.Net!, StringComparer.Ordinal)
                                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                var netSeen = new Dictionary<string, int>(StringComparer.Ordinal);
                int unnamed = 0;
                var made = new List<Piece>(list.Count);
                foreach (var (poly, net, layer) in list)
                {
                    // R-em3d3-5g — what the user already calls it: the net, else a piece ordinal in
                    // merge order. Both are deterministic: merge order is document order.
                    string name;
                    if (net is not null)
                    {
                        int k = netSeen[net] = netSeen.GetValueOrDefault(net) + 1;
                        name = netCounts[net] == 1 ? $"{band.Layer.Name}/{net}" : $"{band.Layer.Name}/{net}/{k}";
                    }
                    else name = $"{band.Layer.Name}/{++unnamed}";

                    double width = CharacteristicWidth(poly);
                    var (sheet, reason) = SheetRule(band, t, delta, width);
                    made.Add(new Piece(poly, name, material, sheet, band.BottomM, band.TopM, sheetZ, layer, reason, net));
                }
                Pieces[band.Index] = made;
            }

            // ── Board outline (R-em3d3-5c) ────────────────────────────────────────────────────
            if (outlineShapes.Count > 0)
                foreach (var (shape, _) in PlanarExtractor.MergeOverlapping(
                             [.. outlineShapes.Select(s => (s, 0))], tech, out _, out _))
                    _outline.AddRange(PlanarExtractor.ToPolygons(shape, tech, _perDbu));

            // ── Which bands are in the problem, and each dielectric's z after absorption ──────
            _inProblem = bands.Where(b => !pecFloor || b.BottomM >= floorZ - 1e-15).ToList();
            foreach (var b in _inProblem.Where(b => b.Layer.Kind == StackupKind.Dielectric))
                _dielZ[b.Index] = (b.BottomM, b.TopM);
            // An INNER conductor band — dielectric directly below AND above — is filled, where no metal
            // is drawn, by the dielectric its sheet faces away from: the one above for a sheet at the
            // band's bottom (the planar rule that makes a microstrip's height the substrate), the one
            // below for SheetAt = Top. An OUTER band is air: the metal sits on the board.
            for (int i = 1; i + 1 < _inProblem.Count; i++)
            {
                var c = _inProblem[i];
                if (c.Layer.Kind != StackupKind.Conductor) continue;
                var below = _inProblem[i - 1];
                var above = _inProblem[i + 1];
                if (below.Layer.Kind != StackupKind.Dielectric || above.Layer.Kind != StackupKind.Dielectric) continue;
                if (c.Layer.SheetAt == ConductorSheetSurface.Top)
                    _dielZ[below.Index] = (_dielZ[below.Index].Bottom, c.TopM);
                else
                    _dielZ[above.Index] = (c.BottomM, _dielZ[above.Index].Top);
            }

            // ── Patterned films (PresentWithLayer is simply true in 3D, §4.1a) ─────────────────
            foreach (var b in _inProblem.Where(b => b.Layer.Kind == StackupKind.Dielectric &&
                                                    b.Layer.PresentWithLayer is { Length: > 0 }))
            {
                string tie = b.Layer.PresentWithLayer!;
                // A name in NEITHER namespace leaves the film active, as the planar extractor leaves
                // it (StackupLayer.PresentWithLayer): dropping it on a typo would thin the medium.
                if (!bands.Any(x => x.Layer.Kind == StackupKind.Conductor && x.Layer.Name == tie)
                    && !tech.Layers.Any(l => l.Name == tie))
                {
                    Notes.Add($"Dielectric '{b.Layer.Name}' is tied to '{tie}', which is neither a conductor " +
                              "entry nor a drawing layer, so it is kept everywhere, as the planar solver keeps it.");
                    continue;
                }
                var polys = new List<PlanarPolygon>();
                if (bands.FirstOrDefault(x => x.Layer.Kind == StackupKind.Conductor && x.Layer.Name == tie) is { } plate)
                    polys.AddRange(Pieces.TryGetValue(plate.Index, out var pl) ? pl.Select(p => p.Poly) : []);
                else if (tech.Layers.FirstOrDefault(l => l.Name == tie) is { } mask &&
                         byLayer.TryGetValue(mask.Key, out var maskShapes))
                    foreach (var (shape, _) in PlanarExtractor.MergeOverlapping(
                                 [.. maskShapes.Select(s => (s, 0))], tech, out _, out _))
                        polys.AddRange(PlanarExtractor.ToPolygons(shape, tech, _perDbu));
                _filmPolys[b.Index] = polys;
                if (polys.Count == 0)
                    Notes.Add($"Dielectric '{b.Layer.Name}' exists only where '{tie}' is drawn, and nothing " +
                              "is drawn there, so it is not in the 3D problem.");
            }

            // ── Bodies (brief 2's, R-em3d3-5e) ────────────────────────────────────────────────
            foreach (var body in tech.Bodies)
            {
                var on = bands.FirstOrDefault(b => b.Layer.Name == body.SitsOn);
                if (on is null)
                    return $"Body '{body.Name}' sits on '{body.SitsOn}', which is not a conductor or " +
                           $"dielectric entry of technology '{tech.Name}'s stackup, so it has no height to " +
                           "start from.";
                if (tech.FindMaterial(body.Material) is null)
                    return $"Body '{body.Name}' is made of '{body.Material}', which technology " +
                           $"'{tech.Name}' does not define in its Materials.";
                var polys = new List<PlanarPolygon>();
                var outlineOperands = body.OutlineLayers.SelectMany(k => byLayer.TryGetValue(k, out var l) ? l : [])
                                                        .Where(s => s is not PathShape { Width: <= 0 }).ToList();
                if (outlineOperands.Count > 0)
                    foreach (var (shape, _) in PlanarExtractor.MergeOverlapping(
                                 [.. outlineOperands.Select(s => (s, 0))], tech, out _, out _))
                        polys.AddRange(PlanarExtractor.ToPolygons(shape, tech, _perDbu));
                else if (body.OutlineLayers.Count > 0)
                {
                    Notes.Add($"Body '{body.Name}' takes its outline from layers on which nothing is drawn, " +
                              "so it is not in the 3D problem.");
                    continue;
                }
                _bodies.Add((body, on.TopM, on.TopM + body.ThicknessDbu * StackPerDbu, polys));
            }

            // ── Bond wires (brief-em3d-4): the stem-paired .wBond, landed on this problem's pieces ──
            var wireSource = wires;
            if (wireSource is null)
            {
                wireSource = Em3dWireSource.ForLayout(source.AbsolutePath, out string? wbNote, out string? wbRefusal);
                if (wbRefusal is not null) return wbRefusal;
                if (wbNote is not null) Notes.Add(wbNote);
            }
            if (wireSource is { Design.WireCount: > 0 })
            {
                // The wire model's z = 0 is the top of the lowest ground-reference conductor
                // (WBondLayerHeights' convention, the plane kernel W images in).
                double zOrigin;
                if (ground is not null) zOrigin = ground.TopM;
                else
                {
                    zOrigin = bands.Min(b => b.BottomM);
                    Notes.Add($"Technology '{tech.Name}' designates no ground-reference conductor, so the wires' " +
                              "z = 0 is taken as the bottom of the stack. Mark the ground plane to place them " +
                              "where kernel W does.");
                }
                var pads = Pieces.Values.SelectMany(l => l)
                                 .Select(p => new Em3dWirePad(p.Name, p.Poly, p.IsSheet ? p.SheetZ : p.ZTop)).ToList();
                string wbondSource = wireSource.Path is { } wbPath
                    ? $".wBond '{Path.GetFileName(wbPath)}' Materials" : "the .wBond's Materials";
                WireBuild = Em3dWires.Build(wireSource, pads, zOrigin, tech, tempC,
                                            (m, fromWBond) => Add(m, fromWBond ? wbondSource : TechnologySource(m.Name)));
                Notes.AddRange(WireBuild.Notes);
                WarningList.AddRange(WireBuild.Warnings);
                if (WireBuild.Refusal is { } wireRefusal) return wireRefusal;
            }

            Extent();
            return null;
        }

        /// <summary>The content bounds (R-em3d3-6), from everything Prepare built.</summary>
        private void Extent()
        {
            double cx0 = double.PositiveInfinity, cy0 = cx0, cx1 = double.NegativeInfinity, cy1 = cx1;
            void Grow(PlanarPolygon p)
            {
                var (a, b, c, d) = p.Bounds();
                cx0 = Math.Min(cx0, a); cy0 = Math.Min(cy0, b); cx1 = Math.Max(cx1, c); cy1 = Math.Max(cy1, d);
            }
            foreach (var list in Pieces.Values) foreach (var p in list) Grow(p.Poly);
            foreach (var p in _outline) Grow(p);
            foreach (var list in _filmPolys.Values) foreach (var p in list) Grow(p);
            foreach (var bd in _bodies) foreach (var p in bd.Polys) Grow(p);
            foreach (var v in _viaSpans)
                foreach (var p in ViaFootprint(v.Shape)) Grow(p);

            double zLow = double.PositiveInfinity, zHigh = double.NegativeInfinity;
            foreach (var (b, tp) in _dielZ)
                if (!_filmPolys.TryGetValue(b, out var fp) || fp.Count > 0)
                { zLow = Math.Min(zLow, tp.Bottom); zHigh = Math.Max(zHigh, tp.Top); }
            foreach (var list in Pieces.Values)
                foreach (var p in list)
                {
                    zLow  = Math.Min(zLow,  p.IsSheet ? p.SheetZ : p.ZBottom);
                    zHigh = Math.Max(zHigh, p.IsSheet ? p.SheetZ : p.ZTop);
                }
            foreach (var bd in _bodies) { zLow = Math.Min(zLow, bd.ZBottom); zHigh = Math.Max(zHigh, bd.ZTop); }
            foreach (var v in _viaSpans)
            {
                zLow  = Math.Min(zLow, PecFloor && ReferenceEquals(v.Bottom.Layer, Ground!.Layer) ? FloorZ : v.Bottom.BottomM);
                zHigh = Math.Max(zHigh, v.Top.TopM);
            }
            foreach (var (_, _, prim) in WireBuild?.Solids ?? [])
            {
                var (x0, y0, z0, x1, y1, z1) = Em3dProblem.Bounds(prim);
                cx0 = Math.Min(cx0, x0); cy0 = Math.Min(cy0, y0); cx1 = Math.Max(cx1, x1); cy1 = Math.Max(cy1, y1);
                zLow = Math.Min(zLow, z0); zHigh = Math.Max(zHigh, z1);
            }
            if (_instancePlane is { } plane)
            {
                zLow = Math.Min(zLow, plane.BottomM);
                zHigh = Math.Max(zHigh, plane.TopM);
            }
            (Cx0, Cy0, Cx1, Cy1, ZLow, ZHigh) = (cx0, cy0, cx1, cy1, zLow, zHigh);
        }

        /// <summary>
        /// The solids and sheets, in construction order (R-em3d3-1d). <paramref name="fullExtent"/> is what a
        /// laterally unbounded slab spans in x and y: the generator's air box, or — null, an instance — the
        /// board outline if there is one (which every slab already takes) else the drawn geometry's bounding
        /// box. <paramref name="reserveAir"/> reserves the air-above-the-stack's slot, for the generator.
        /// </summary>
        public void BuildSolids(Em3dAirBox? fullExtent, bool reserveAir)
        {
            var solids = SolidList;
            var sheets = SheetList;
            int order = 0;
            double x0 = fullExtent?.Min.X ?? Cx0, y0 = fullExtent?.Min.Y ?? Cy0;
            double x1 = fullExtent?.Max.X ?? Cx1, y1 = fullExtent?.Max.Y ?? Cy1;
            int bounded = 0;
            // An instance (no air box) or a planar preview: a slab with no outline of its own takes SlabLateralBound's.
            bool bound = fullExtent is null || options.DisplaySlabs;
            _displayBox = options.DisplaySlabs ? fullExtent : null;

            Em3dPrimitive FullExtent(double z0, double z1)
            {
                if (fullExtent is null) bounded++;
                return new Em3dBox(new Point3(x0, y0, z0), new Point3(x1, y1, z1));
            }

            //
            // Bottom-up: dielectrics, then the air above the stack, then bodies, then conductors (metal
            // wins over what it is embedded in), then vias, then bond wires (brief 4). STATED
            // here so no backend infers it.
            double airBottom = PecFloor ? FloorZ : double.NaN;
            foreach (var b in _inProblem.Where(b => _dielZ.ContainsKey(b.Index)))
            {
                var (z0, z1) = _dielZ[b.Index];
                string material = DielectricMaterial(b.Layer);
                IReadOnlyList<PlanarPolygon>? lateral =
                    _filmPolys.TryGetValue(b.Index, out var film) ? film : _outline.Count > 0 ? _outline : null;
                if (lateral is null && bound) lateral = Bounded(z0, z1, b.Layer.Name);
                if (lateral is { Count: 0 }) continue;
                order++;
                if (lateral is null)
                    solids.Add(Origin(new Em3dSolid(b.Layer.Name, material, Em3dRole.Dielectric, FullExtent(z0, z1), order),
                                      Em3dObjectKind.Dielectric, b.Layer.Name));
                else
                    for (int k = 0; k < lateral.Count; k++)
                        solids.Add(Origin(new Em3dSolid(lateral.Count == 1 ? b.Layer.Name : $"{b.Layer.Name}/{k + 1}",
                                                        material, Em3dRole.Dielectric, Extrude(lateral[k], z0, z1), order),
                                          Em3dObjectKind.Dielectric, b.Layer.Name));
                if (film is null) airBottom = double.IsNaN(airBottom) ? z1 : Math.Max(airBottom, z1);
            }

            if (reserveAir && fullExtent is { } box)
            {
                if (double.IsNaN(airBottom)) airBottom = box.Min.Z;
                if (box.Max.Z > airBottom)
                    AirSlot = (solids.Count, ++order, AirMaterialName(), airBottom);
            }

            foreach (var (body, z0, z1, drawnPolys) in _bodies)
            {
                string material = BodyMaterial(tech.FindMaterial(body.Material)!, out var role);
                order++;
                IReadOnlyList<PlanarPolygon> polys = drawnPolys.Count == 0 && bound ? Bounded(z0, z1, body.Name) : drawnPolys;
                if (polys.Count == 0)
                    solids.Add(Origin(new Em3dSolid(body.Name, material, role, FullExtent(z0, z1), order),
                                      Em3dObjectKind.Body, null));
                else
                    for (int k = 0; k < polys.Count; k++)
                        solids.Add(Origin(new Em3dSolid(polys.Count == 1 ? body.Name : $"{body.Name}/{k + 1}",
                                                        material, role, Extrude(polys[k], z0, z1), order),
                                          Em3dObjectKind.Body, null));
            }

            foreach (var b in Bands)
            {
                if (_instancePlane is { } plane && ReferenceEquals(b, plane)) Plane(b, ref order);
                if (!Pieces.ContainsKey(b.Index)) continue;
                foreach (var p in Pieces[b.Index])
                {
                    order++;
                    OriginMap[p.Name] = new Em3dObjectOrigin(Em3dObjectKind.Conductor, b.Layer.Name, p.Layer, p.SheetReason);
                    Net(p.Name, p.Net);
                    if (b.Layer.IsGroundReference) GroundBandObjectList.Add(p.Name);
                    if (p.IsSheet)
                        sheets.Add(new Em3dSheet(p.Name, p.Material, Ring(p.Poly.Outer),
                                                 [.. p.Poly.HoleRings.Select(Ring)], p.SheetZ, p.ZTop - p.ZBottom, order));
                    else
                        solids.Add(new Em3dSolid(p.Name, p.Material, Em3dRole.Conductor,
                                                 Extrude(p.Poly, p.ZBottom, p.ZTop), order));
                }
            }
            int sheetCount = sheets.Count;
            if (sheetCount > 0)
                Notes.Add($"{sheetCount} conductor piece(s) are thinner than {Em3dGenerator.SheetMaxSkinDepths:G} skin depths " +
                          $"at {options.FMaxHz / 1e9:G4} GHz and than {Em3dGenerator.SheetMaxFractionOfWidth:G} of their own width, so " +
                          "they are sheets carrying their conductivity and thickness rather than meshed " +
                          "volumes. Both thresholds are provisional.");

            // Vias: R-em3d3-5d. A plated barrel with a wall is a TUBE — the plating cylinder, and inside
            // it a higher-order cylinder of air, which is §1d's order rule doing the subtraction.
            int viaN = 0;
            foreach (var (shape, entry, top, bottom) in _viaSpans)
            {
                viaN++;
                string material = ConductorMaterial(entry);
                double z1 = top.TopM;
                double z0 = PecFloor && ReferenceEquals(bottom.Layer, Ground!.Layer) ? FloorZ : bottom.BottomM;
                string name = $"via/{viaN}";
                // A via is on its own net when it names one, else on the nets of the metal it lands on.
                var viaNets = shape.Net is { Length: > 0 } vn ? [vn] : ViaLandingNets(shape, top, bottom);
                if (shape is ViaShape v)
                {
                    double r = (v.DrillSize > 0 ? v.DrillSize : v.PadSize) * _perDbu / 2;
                    var a = new Point3(v.X * _perDbu, v.Y * _perDbu, z0);
                    var e = new Point3(v.X * _perDbu, v.Y * _perDbu, z1);
                    solids.Add(Origin(new Em3dSolid(name, material, Em3dRole.Conductor, new Em3dCylinder(a, e, r), ++order),
                                      Em3dObjectKind.Via, entry.Name, shape.Layer));
                    foreach (string n in viaNets) Net(name, n);
                    double wall = (entry.WallThicknessDbu ?? 0) * StackPerDbu;
                    if (entry.Fill == ViaFillKind.Plated && wall > 0 && wall < r)
                        solids.Add(Origin(new Em3dSolid(name + "/fill", AirMaterialName(), Em3dRole.Air,
                                                        new Em3dCylinder(a, e, r - wall), ++order),
                                          Em3dObjectKind.Air, entry.Name));
                }
                else
                {
                    var fp = PlanarExtractor.ToPolygons(shape, tech, _perDbu);
                    order++;
                    for (int k = 0; k < fp.Count; k++)
                    {
                        string piece = fp.Count == 1 ? name : $"{name}/{k + 1}";
                        solids.Add(Origin(new Em3dSolid(piece, material, Em3dRole.Conductor, Extrude(fp[k], z0, z1), order),
                                          Em3dObjectKind.Via, entry.Name, shape.Layer));
                        foreach (string n in viaNets) Net(piece, n);
                    }
                }
            }

            // Bond wires after vias (R-em3d3-1d's order, as brief 3 left room for): each swept wire,
            // then its balls, which meet it face to face on the ball's top.
            var pieceNet = Pieces.Values.SelectMany(l => l).ToDictionary(p => p.Name, p => p.Net, StringComparer.Ordinal);
            foreach (var (name, material, prim) in WireBuild?.Solids ?? [])
            {
                solids.Add(Origin(new Em3dSolid(name, material, Em3dRole.Conductor, prim, ++order),
                                  Em3dObjectKind.Wire, null));
                // A wire (and its balls) is on its array's name and on the nets of the pads it joins.
                if (WireBuild!.Reports.FirstOrDefault(r => name == r.Name || name.StartsWith(r.Name + "/", StringComparison.Ordinal))
                    is { } wr)
                {
                    Net(name, wr.Array);
                    Net(name, pieceNet.GetValueOrDefault(wr.Start.Pad));
                    Net(name, pieceNet.GetValueOrDefault(wr.End.Pad));
                }
            }

            if (fullExtent is null && (bounded > 0 || _slabBounds.Count > 0))
                Notes.Add(SlabBoundNote());
        }

        // ── Lateral bounds (SlabLateralBound) ───────────────────────────────────────────────────────
        private readonly List<(string Slab, SlabBoundKind Kind, string? From)> _slabBounds = [];
        private readonly Dictionary<(int Above, int Below), IReadOnlyList<PlanarPolygon>> _hulls = [];
        private Em3dAirBox? _displayBox;

        /// <summary>The dielectric stack's height, metres — what SlabLateralBound's two distances scale with.</summary>
        private double StackHeightM()
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var (_, z) in _dielZ) { lo = Math.Min(lo, z.Bottom); hi = Math.Max(hi, z.Top); }
            return hi > lo ? hi - lo : ZHigh > ZLow ? ZHigh - ZLow : 0;
        }

        /// <summary>
        /// The lateral shape of a slab spanning <paramref name="z0"/>..<paramref name="z1"/> that has no outline of
        /// its own: the board outline; else the closed hull of the nearest copper above and the nearest copper below
        /// its middle (walking outward past a conductor entry with nothing drawn on it); else the bounding box.
        /// </summary>
        private IReadOnlyList<PlanarPolygon> Bounded(double z0, double z1, string slab)
        {
            if (_outline.Count > 0)
            {
                _slabBounds.Add((slab, SlabBoundKind.Outline, null));
                return _outline;
            }
            double zc = (z0 + z1) / 2, h = StackHeightM();
            var drawn = Bands.Where(b => Pieces.TryGetValue(b.Index, out var l) && l.Count > 0).ToList();
            var above = drawn.Where(b => b.BottomM >= zc).OrderBy(b => b.BottomM).FirstOrDefault();
            var below = drawn.Where(b => b.TopM <= zc).OrderByDescending(b => b.TopM).FirstOrDefault();
            if (h > 0 && (above is not null || below is not null))
            {
                var key = (above?.Index ?? -1, below?.Index ?? -1);
                if (!_hulls.TryGetValue(key, out var hull))
                {
                    var copper = new[] { above, below }.Where(b => b is not null)
                                                       .SelectMany(b => Pieces[b!.Index].Select(p => p.Poly));
                    hull = SlabLateralBound.CopperHull(copper,
                        SlabLateralBound.CloseRadiusInStackHeights * h, SlabLateralBound.MarginInStackHeights * h);
                    if (_displayBox is { } box)
                        hull = SlabLateralBound.ClipToRect(hull, box.Min.X, box.Min.Y, box.Max.X, box.Max.Y);
                    _hulls[key] = hull;
                }
                if (hull.Count > 0)
                {
                    string from = string.Join(" and ", new[] { above, below }.Where(b => b is not null)
                                                                          .Select(b => $"'{b!.Layer.Name}'"));
                    _slabBounds.Add((slab, SlabBoundKind.CopperHull, from));
                    return hull;
                }
            }
            _slabBounds.Add((slab, SlabBoundKind.BoundingBox, null));
            return [new PlanarPolygon([new(Cx0, Cy0), new(Cx1, Cy0), new(Cx1, Cy1), new(Cx0, Cy1)])];
        }

        /// <summary>What an instance's note says: which bound each slab took.</summary>
        private string SlabBoundNote()
        {
            if (_outline.Count > 0)
                return "Placed in a 3D view, the layout's slabs are bounded by its board outline.";
            var hulls = _slabBounds.Where(b => b.Kind == SlabBoundKind.CopperHull).ToList();
            var boxes = _slabBounds.Where(b => b.Kind == SlabBoundKind.BoundingBox).Select(b => $"'{b.Slab}'").ToList();
            var parts = new List<string>();
            if (hulls.Count > 0)
                parts.Add("each laterally unbounded slab takes " + SlabLateralBound.HullRule(StackHeightM()) + " — " +
                          string.Join("; ", hulls.Select(b => $"'{b.Slab}' from {b.From}")));
            if (boxes.Count > 0)
                parts.Add($"{string.Join(", ", boxes)} {(boxes.Count == 1 ? "has" : "have")} no copper above or below " +
                          $"and {(boxes.Count == 1 ? "is" : "are")} bounded by the bounding box of the drawn geometry");
            return "Placed in a 3D view, the layout draws no board outline, so " + string.Join("; and ", parts) +
                   ". Draw a board outline when the board's edge matters.";
        }

        /// <summary>brief-em3d-42 R-em3d42-2c — an instance's ground plane: the undrawn lowest ground-reference
        /// conductor a .cem would have made its PEC floor, as metal over the outline or the copper hull above it.</summary>
        private void Plane(PlanarExtractor.StackBand band, ref int order)
        {
            string material = ConductorMaterial(band.Layer);
            var mat = _materialByName[material];
            double t = band.TopM - band.BottomM;
            double delta = mat.SigmaSm > 0 && options.FMaxHz is { } fMax
                ? 1.0 / Math.Sqrt(Math.PI * fMax * Mu0 * mat.Mur * mat.SigmaSm) : 0;
            double sheetZ = band.Layer.SheetAt == ConductorSheetSurface.Top ? band.TopM : band.BottomM;
            IReadOnlyList<PlanarPolygon> lateral = Bounded(band.BottomM, band.TopM, band.Layer.Name);
            for (int k = 0; k < lateral.Count; k++)
            {
                string name = lateral.Count == 1 ? band.Layer.Name : $"{band.Layer.Name}/{k + 1}";
                var (sheet, reason) = SheetRule(band, t, delta, CharacteristicWidth(lateral[k]));
                order++;
                OriginMap[name] = new Em3dObjectOrigin(Em3dObjectKind.Conductor, band.Layer.Name, null, reason);
                GroundBandObjectList.Add(name);
                if (sheet)
                    SheetList.Add(new Em3dSheet(name, material, Ring(lateral[k].Outer), [.. lateral[k].HoleRings.Select(Ring)],
                                                sheetZ, t, order));
                else
                    SolidList.Add(new Em3dSolid(name, material, Em3dRole.Conductor, Extrude(lateral[k], band.BottomM, band.TopM), order));
            }
        }

        /// <summary>The temperature notes, which the generator writes after its terminals.</summary>
        public void FinishNotes()
        {
            if (_unknownTemperature.Count > 0)
                Notes.Add($"{string.Join(", ", _unknownTemperature.Select(n => $"'{n}'"))} " +
                          $"name{(_unknownTemperature.Count == 1 ? "s" : "")} no material, so " +
                          $"{(_unknownTemperature.Count == 1 ? "its" : "their")} σ is a number of unknown " +
                          $"temperature and is used as given at {Fmt(options.TempC)} °C. Name a material with an " +
                          "α₂₀ to have it evaluated at the operating temperature.");
            if (_noAlpha.Count > 0)
                Notes.Add($"{string.Join(", ", _noAlpha.Select(n => $"'{n}'"))} state" +
                          $"{(_noAlpha.Count == 1 ? "s" : "")} no α₂₀, so σ₂₀ is used at every temperature, " +
                          $"{Fmt(options.TempC)} °C included.");
        }

        public IReadOnlyList<Em3dMaterial> Materials => _materials;
        public IReadOnlyDictionary<string, string> MaterialSources => _materialSource;
        public IReadOnlyList<string> NoAlphaList => [.. _noAlpha.Union(WireBuild?.NoAlpha ?? []).Order(StringComparer.Ordinal)];
        public IReadOnlyList<string> UnknownTemperatureList => _unknownTemperature;

        public Em3dLayoutSolidsResult Failed(string refusal) => new([], [], [], refusal, Notes) { Warnings = WarningList };

        public Em3dLayoutSolidsResult Result() => new(SolidList, SheetList, _materials, null, Notes)
        {
            Warnings = WarningList,
            Origins = OriginMap,
            MaterialSources = _materialSource,
            ObjectNets = ObjectNetMap.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal),
            GroundBandObjects = GroundBandObjectList,
            Wires = WireBuild?.Reports ?? [],
            NoAlpha = NoAlphaList,
            UnknownTemperature = _unknownTemperature,
            StackBottomM = Bands.Count > 0 ? Bands.Min(b => b.BottomM) : 0,
        };

        // ── Materials resolved at T (R-em3d3-4) ──────────────────────────────────────────────────

        private string ConductorMaterial(StackupLayer entry)
        {
            double tempC = options.TempC;
            if (tech.FindMaterial(entry.Material) is { } m)
            {
                double sigma = m.Sigma20 ?? entry.SigmaSm;
                if (m.Sigma20 is { } s20)
                {
                    if (m.Alpha20 is { } a20) sigma = new WireMaterial(m.Name, s20, a20, 0).SigmaAt(tempC);
                    else _noAlpha.Add(m.Name);
                }
                // A material that states no σ₂₀ leaves the entry's own σ in force — a number of
                // unknown temperature, from the stackup entry, and reported as both.
                else if (!_unknownTemperature.Contains(entry.Name)) _unknownTemperature.Add(entry.Name);
                return Add(new Em3dMaterial(m.Name, m.Epsr ?? 1, null, m.TanD ?? 0, m.Mur ?? 1, sigma),
                           m.Sigma20 is null ? EntrySource(entry) : TechnologySource(m.Name));
            }
            if (!_unknownTemperature.Contains(entry.Name)) _unknownTemperature.Add(entry.Name);
            return Add(new Em3dMaterial(entry.Name, 1, null, 0, entry.Mur, entry.SigmaSm), EntrySource(entry));
        }

        private string DielectricMaterial(StackupLayer entry)
        {
            // Resolve-on-read (brief 2) already wrote a named material's εr/tanδ/μr into the entry;
            // what only the material carries is the tensor.
            var m = tech.FindMaterial(entry.Material);
            return Add(new Em3dMaterial(m?.Name ?? entry.Name, entry.Epsr,
                                        m?.EpsrTensor is { Length: 3 } t ? [.. t] : null,
                                        entry.TanD, entry.Mur, 0),
                       m is null ? EntrySource(entry) : TechnologySource(m.Name));
        }

        private string BodyMaterial(TechMaterial m, out Em3dRole role)
        {
            double sigma = 0;
            if (m.Sigma20 is { } s20)
            {
                if (m.Alpha20 is { } a20) sigma = new WireMaterial(m.Name, s20, a20, 0).SigmaAt(options.TempC);
                else { sigma = s20; _noAlpha.Add(m.Name); }
            }
            role = m.Epsr is null && m.EpsrTensor is null && sigma > 0 ? Em3dRole.Conductor : Em3dRole.Dielectric;
            return Add(new Em3dMaterial(m.Name, m.Epsr ?? 1, m.EpsrTensor is { Length: 3 } t ? [.. t] : null,
                                        m.TanD ?? 0, m.Mur ?? 1, sigma), TechnologySource(m.Name));
        }

        /// <summary>R-em3d3-5f — a conductor is a sheet when it has no thickness, or is thinner than both
        /// thresholds. With no sweep there is no skin depth, so only a zero thickness makes a sheet.</summary>
        private (bool Sheet, string? Reason) SheetRule(PlanarExtractor.StackBand band, double t, double delta, double width)
        {
            bool sheet = t <= 0 ||
                         (delta > 0 && t < Em3dGenerator.SheetMaxSkinDepths * delta && t < Em3dGenerator.SheetMaxFractionOfWidth * width);
            string? reason = !sheet ? null
                : t <= 0 ? $"'{band.Layer.Name}' has zero thickness in the stackup"
                : $"{Fmt(t * 1e6)} µm is under {Em3dGenerator.SheetMaxSkinDepths:G} skin depths " +
                  $"({Fmt(Em3dGenerator.SheetMaxSkinDepths * delta * 1e6)} µm at {Fmt(options.FMaxHz!.Value / 1e9)} GHz) and under " +
                  $"{Em3dGenerator.SheetMaxFractionOfWidth:G} of its width ({Fmt(width * 1e6)} µm)";
            return (sheet, reason);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────

        public void Net(string obj, string? net)
        {
            if (net is not { Length: > 0 }) return;
            (ObjectNetMap.TryGetValue(obj, out var set) ? set : ObjectNetMap[obj] = new(StringComparer.Ordinal)).Add(net);
        }

        /// <summary>True when every vertex of <paramref name="poly"/> lies within the pad of a via that ends on
        /// <paramref name="plane"/> — a pad, or pads merged, and nothing else. The 2 % allows for a circle's polygon.</summary>
        private bool IsViaPadOn(PlanarPolygon poly, PlanarExtractor.StackBand plane)
        {
            var pads = _viaSpans
                .Where(v => v.Shape is ViaShape && (ReferenceEquals(v.Top, plane) || ReferenceEquals(v.Bottom, plane)))
                .Select(v => (ViaShape)v.Shape)
                .Select(v => (X: v.X * _perDbu, Y: v.Y * _perDbu, R: v.PadSize * _perDbu / 2 * 1.02))
                .ToList();
            return pads.Count > 0 &&
                   poly.Outer.All(pt => pads.Any(p => (pt.X - p.X) * (pt.X - p.X) + (pt.Y - p.Y) * (pt.Y - p.Y) <= p.R * p.R));
        }

        /// <summary>The nets of the pieces a via's footprint centre lands on, at its top and bottom.</summary>
        private List<string> ViaLandingNets(LayoutShape shape, PlanarExtractor.StackBand top, PlanarExtractor.StackBand bottom)
        {
            var fp = ViaFootprint(shape).ToList();
            if (fp.Count == 0) return [];
            var (x0, y0, x1, y1) = fp[0].Bounds();
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            var nets = new List<string>();
            foreach (var band in new[] { top, bottom })
                if (Pieces.TryGetValue(band.Index, out var list))
                    foreach (var p in list)
                        if (p.Net is { } n && p.Poly.Contains(cx, cy) && !nets.Contains(n)) nets.Add(n);
            return nets;
        }

        public string Add(Em3dMaterial m, string source)
        {
            if (_materialByName.TryGetValue(m.Name, out var have))
            {
                if (have == m || SameValues(have, m)) return m.Name;
                // An anonymous entry sharing a name with a named material, with different numbers: kept
                // apart rather than silently merged into whichever came first.
                return Add(m with { Name = "stackup/" + m.Name }, source);
            }
            _materials.Add(m);
            _materialByName[m.Name] = m;
            _materialSource[m.Name] = source;
            return m.Name;
        }

        /// <summary>brief-em3d-53 R-em3d53-6 — names the library a material came from, when it came from one.</summary>
        private string TechnologySource(string? material = null)
            => tech.LibrarySourceOf(material) is { } lib
                ? $"technology '{tech.Name}' via library '{MaterialLibraries.Display(lib)}'"
                : $"technology '{tech.Name}' Materials";

        private static string EntrySource(StackupLayer entry) => $"stackup entry '{entry.Name}' (its own numbers)";

        public Em3dSolid Origin(Em3dSolid s, Em3dObjectKind kind, string? entry, LayerKey? layer = null)
        {
            OriginMap[s.Name] = new Em3dObjectOrigin(kind, entry, layer, null);
            return s;
        }

        private static bool SameValues(Em3dMaterial a, Em3dMaterial b) =>
            a.Epsr == b.Epsr && a.TanD == b.TanD && a.Mur == b.Mur && a.SigmaSm == b.SigmaSm &&
            (a.EpsrTensor ?? []).SequenceEqual(b.EpsrTensor ?? []);

        public string AirMaterialName()
        {
            if (tech.FindMaterial(Em3dGenerator.AirMaterial) is { } m)
                return Add(new Em3dMaterial(m.Name, m.Epsr ?? 1, null, m.TanD ?? 0, m.Mur ?? 1, 0), TechnologySource(m.Name));
            return Add(new Em3dMaterial(Em3dGenerator.AirMaterial, 1, null, 0, 1, 0), "built in (free space)");
        }

        private IEnumerable<PlanarPolygon> ViaFootprint(LayoutShape shape)
        {
            if (shape is ViaShape v)
            {
                double r = (v.DrillSize > 0 ? v.DrillSize : v.PadSize) * _perDbu / 2;
                double cx = v.X * _perDbu, cy = v.Y * _perDbu;
                return [new PlanarPolygon([new(cx - r, cy - r), new(cx + r, cy - r), new(cx + r, cy + r), new(cx - r, cy + r)])];
            }
            return PlanarExtractor.ToPolygons(shape, tech, _perDbu);
        }

        private static Em3dExtrudedPolygon Extrude(PlanarPolygon p, double z0, double z1)
            => new(Ring(p.Outer), [.. p.HoleRings.Select(Ring)], z0, z1);

        private static IReadOnlyList<Point2> Ring(IReadOnlyList<EmPoint> ring)
            => [.. ring.Select(q => new Point2(q.X, q.Y))];

        /// <summary>2·Area / Perimeter: a strip's width, however long it is drawn.</summary>
        private static double CharacteristicWidth(PlanarPolygon poly)
        {
            double perimeter = RingLength(poly.Outer);
            foreach (var h in poly.HoleRings) perimeter += RingLength(h);
            return perimeter > 0 ? 2 * poly.Area() / perimeter : 0;
        }

        private static double RingLength(IReadOnlyList<EmPoint> ring)
        {
            double s = 0;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                s += Math.Sqrt((ring[i].X - ring[j].X) * (ring[i].X - ring[j].X) +
                               (ring[i].Y - ring[j].Y) * (ring[i].Y - ring[j].Y));
            return s;
        }

        private static string Fmt(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    }
}
