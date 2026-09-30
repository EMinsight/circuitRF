// brief-em3d-3 R-em3d3-5 — Tier A: a layout and its technology, as the solver-neutral 3D problem
// (docs/design/em-3d.md §4.1, §4.1a, §6.3 Tier A, §6.4).
//
// Nothing here solves, writes or starts a process. Brief 5 makes the result visible; briefs 7 and 9
// lower it. Framework-free, like everything in src/Design.
//
// ── REUSE, NOT RESTATEMENT ────────────────────────────────────────────────────────────────────
//
// The 3D model and the planar one are cross-checked against each other (brief 10), so wherever the
// two need the same answer this file asks the planar code for it rather than re-deriving it:
//
//   * the geometry        EmGeometry.ForSetup — flatten, then the .cem's solve region;
//   * connectivity        PlanarExtractor.MergeOverlapping — the planar extractor's own union;
//   * shape -> polygon    PlanarExtractor.ToPolygons — its outline/flatten/degenerate-ring chain;
//   * z                   PlanarExtractor.StackBands — the two-DBU-scales rule;
//   * the return plane    PlanarExtractor.InferredReturnPlane — R-em-4, and RP-3's flip;
//   * port numbering,     EmPortExtraction.Extract — the planar ports, their numbers and their
//     sides, refusals       refusals verbatim, run over this file's own conductor polygons;
//   * via spans           ViaSpanResolver, and the planar via binding (plated entries only);
//   * σ(T)                WireMaterial.SigmaAt.
//
// What is genuinely 3D — dielectric lateral extent, the air box, sheets vs solids, construction
// order, the port sheet's rectangle — is decided here, ONCE, so both backends inherit one answer.

using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using Piece = CircuitRF.Design.Layout.Em3d.Em3dLayoutSolids.Builder.Piece;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>The generator's answer: a problem, or a refusal naming what is missing, plus every
/// note it made on the way. A problem is returned unvalidated — <see cref="Em3dProblem.Validate"/>
/// is the backend's first call, and <c>check</c>'s.</summary>
public sealed record Em3dGenerationResult(Em3dProblem? Problem, string? Refusal, IReadOnlyList<string> Notes)
{
    public bool Ok => Problem is not null && Refusal is null;

    /// <summary>brief-em3d-87 — for a <c>.c3d</c>'s problem, every file its elaboration read (C3dElaboration.FilesRead): what
    /// the run's input manifest hashes. Empty for a layout's.</summary>
    public IReadOnlyList<string> FilesRead { get; init; } = [];

    /// <summary>Findings a user should act on that do not stop the problem being built — a foot that
    /// overhangs its pad, a metal the technology and the <c>.wBond</c> define differently.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>What the model made of each bond wire (brief-em3d-4), in array then member order.</summary>
    public IReadOnlyList<Em3dWireReport> Wires { get; init; } = [];

    /// <summary>
    /// brief-em3d-5 — where each solid and sheet came from, by name: the stackup entry, the drawing
    /// layer whose colour a picture paints it in, and why a conductor became a sheet. Kept BESIDE the
    /// problem rather than in it: the problem is what a backend reads, and none of this is physics.
    /// </summary>
    public IReadOnlyDictionary<string, Em3dObjectOrigin> Origins { get; init; } =
        new Dictionary<string, Em3dObjectOrigin>();

    /// <summary>brief-em3d-5 R-em3d5-3a — where each material's values resolved from, by material
    /// name: the technology's Materials, the <c>.wBond</c>'s own list, a stackup entry's own numbers,
    /// or free space.</summary>
    public IReadOnlyDictionary<string, string> MaterialSources { get; init; } = new Dictionary<string, string>();

    /// <summary>Materials that state σ₂₀ but no α₂₀, so σ₂₀ is used at every temperature.</summary>
    public IReadOnlyList<string> NoAlpha { get; init; } = [];

    /// <summary>Conductor stackup entries that name no material, so their σ is a number of unknown
    /// temperature, used as given.</summary>
    public IReadOnlyList<string> UnknownTemperature { get; init; } = [];

    /// <summary>Why the problem has no ports, when it was generated with <c>portsOptional</c> and the ports refused:
    /// the refusal a solve would have stopped on. Null otherwise.</summary>
    public string? PortRefusal { get; init; }
}

/// <summary>What kind of thing in the design a solid or sheet of the 3D problem is.</summary>
public enum Em3dObjectKind { Dielectric, Air, Body, Conductor, Via, Wire }

/// <summary>
/// Where one named object of the 3D problem came from (brief-em3d-5).
/// </summary>
/// <param name="StackupEntry">The stackup entry it was built from; null for a body, a wire, or the
/// air above the stack.</param>
/// <param name="DrawingLayer">The drawing layer whose shapes it was built from — a conductor's or a
/// via's. What a picture paints it with, through the technology's own layer palette.</param>
/// <param name="SheetReason">Why the generator made it a sheet rather than a volume; null for a
/// solid.</param>
public sealed record Em3dObjectOrigin(Em3dObjectKind Kind, string? StackupEntry, LayerKey? DrawingLayer,
                                      string? SheetReason);

public static class Em3dGenerator
{
    /// <summary>
    /// <b>R-em3d3-6a — the default air-box padding, as a fraction of the LONGEST free-space
    /// wavelength in the band</b> (c / the lowest non-zero frequency), on the four lateral faces and
    /// the top. An eighth: far enough that a first-order absorbing face sees a field that has mostly
    /// decayed, near enough that the air is not most of the mesh. A backend may ENLARGE the box
    /// (Palace's absorbing face wants more room than openEMS's PML) and says so when it does; it
    /// never shrinks it. A <c>.cem</c> overrides any face with <c>AirBox</c>.
    /// </summary>
    public const double DefaultPaddingFractionOfLongestWavelength = 1.0 / 8.0;

    /// <summary>
    /// <b>brief-em3d-31 — the default padding when a driven setup asks for a radiation pattern: a QUARTER.</b>
    /// The pattern is transformed from a closed surface that must sit a few cells inside the absorber and a cell
    /// clear of every conductor (OpenEmsFarField), and Palace's far-field integral runs over first-order
    /// absorbing faces that want distance from the radiator. At an eighth the shipped 5.8 GHz patch leaves
    /// ~7 mm where openEMS's surface needs ~10, and every such setup would be refused out of the box. A padding
    /// the <c>.cem</c> states still wins, and is still refused, naming the face, if it is too tight.
    /// </summary>
    public const double PatternPaddingFractionOfLongestWavelength = 1.0 / 4.0;

    /// <summary>
    /// brief-em3d-23 R-em3d23-2b — the default wave-port region, in multiples of the line's width w and its
    /// height h above its return. <b>The rule is the microstrip one repeated across full-wave tools'
    /// documentation and application notes</b> (their names are not written in this repository): a port
    /// about ten line widths wide — ten substrate heights when the line is narrower than its substrate,
    /// where the field spreads by h rather than by w — and six to ten substrate heights tall. The middle
    /// of the height range is taken: at 6, a region 20 % smaller (4.8 h) moved a 50 Ω microstrip's |S21| by
    /// 0.023 dB, outside gate 5's 0.02 dB; the top of the range is the first to support a second
    /// propagating mode (R-em3d23-3 warns when it does). Gate 5 checks the answer does not depend on it.
    /// Stripline and coax cannot be built from a layout's edge port in this version, so only this rule
    /// is needed. A <c>.cem</c> Ports3D entry's Width and Height override it per port.
    /// </summary>
    public const double DefaultWaveWidthFactor = 10, DefaultWaveHeightFactor = 8;

    /// <summary>
    /// <b>R-em3d3-5f — a conductor is a sheet when its thickness is below this many skin depths at
    /// the top frequency…</b> Three skin depths carries ~95 % of the current, so metal thinner than
    /// that is not "thick" in the sense a volume mesh resolves. <b>Provisional</b>: F0's Q6
    /// (docs/design/em-3d-f0-findings.md) measured that a sheet matches loss but not phase on a
    /// 35 µm line, and said the threshold needs a number well below t/h = 0.17; these two constants
    /// are the first number, not the measured one.
    /// </summary>
    public const double SheetMaxSkinDepths = 3.0;

    /// <summary>…<b>and</b> below this fraction of its own smallest lateral dimension (2·Area /
    /// Perimeter, which is a strip's width). Provisional, as <see cref="SheetMaxSkinDepths"/>.</summary>
    public const double SheetMaxFractionOfWidth = 0.1;

    /// <summary>The material that fills the air box, and a hollow via's barrel.</summary>
    public const string AirMaterial = "Air";

    /// <summary>The name of the air solid above the stack.</summary>
    public const string AirSolidName = "air";

    private const double C0  = 299_792_458.0;

    /// <summary>
    /// The 3D problem <paramref name="setup"/> describes. <paramref name="source"/> is what
    /// <see cref="EmSetupResolver"/> resolved — the SAME walk-ups the planar run takes (the layout
    /// relative to the <c>.cem</c>'s workspace, the technology relative to the layout's), which this
    /// method does not repeat.
    /// </summary>
    /// <param name="displaySlabs">True for a PLANAR setup previewed in 3D: every laterally unbounded slab is drawn to
    /// <see cref="SlabLateralBound"/>'s shape instead of across the air box — for viewing only, never for a solve.</param>
    /// <param name="wires">The bond wires to include. Null — the ordinary case — takes the
    /// <c>.wBond</c> stem-paired with the layout (WB40), if it has one.</param>
    /// <param name="portsOptional">True for a picture, never for a solve: ports that refuse leave the problem with no
    /// ports and the refusal in <see cref="Em3dGenerationResult.PortRefusal"/>, so the geometry is still drawn.</param>
    public static Em3dGenerationResult Generate(EmSetup setup, EmLayoutSource source, Technology tech,
                                                Em3dWireSource? wires = null, bool displaySlabs = false,
                                                bool portsOptional = false)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tech);
        return new Run(setup, source, tech, wires, displaySlabs, portsOptional).Go();
    }

    // ── One generation ─────────────────────────────────────────────────────────────────────────

    private sealed class Run(EmSetup setup, EmLayoutSource source, Technology tech, Em3dWireSource? wires, bool displaySlabs,
                             bool portsOptional)
    {
        // brief-em3d-42 R-em3d42-2 — the geometry lives in Em3dLayoutSolids now; this class adds the sweep,
        // the ports, the air box, the air above the stack and the terminals around its stages. The notes
        // and materials lists are the builder's, appended to between its stages, so their ORDER is the one
        // the generator always wrote (the pre-split dumps hold it).
        private Em3dLayoutSolids.Builder _g = null!;
        private List<string> _notes = [];
        private double _tempC;
        private string? _portRefusal;

        public Em3dGenerationResult Go()
        {
            // ── The sweep ─────────────────────────────────────────────────────────────────────
            double[] freqs;
            try { freqs = setup.Frequency.Expand(); }
            catch (Exception ex) { return No(EmDiagnostics.FrequencySweepUnresolvable(ex.Message).Render()); }
            var positive = freqs.Where(f => f > 0).ToArray();
            if (positive.Length == 0) return No(EmDiagnostics.FrequencySweepEmpty().Render());
            double fMin = positive.Min(), fMax = positive.Max();
            if (positive.Length < freqs.Length)
                _notes.Add("The sweep's 0 Hz point is not in the 3D problem: a full-wave solver has no DC " +
                           "solution to give it.");
            var frequency = new Em3dFrequency(fMin, fMax, positive.Length,
                setup.Frequency.Kind == CircuitRF.Core.Design.SweepKind.Log ? Em3dSweepKind.Log : Em3dSweepKind.Linear);

            // ── Temperature (R-em3d3-4) ───────────────────────────────────────────────────────
            _tempC = setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC;

            // The .cem's floor steps §6's rule aside only when it states a DIFFERENT boundary (Em3dLayoutSolids).
            var zMinFace = setup.AirBox?.ZMin;
            bool floorStatedAway = zMinFace?.Boundary is { } zMinKind && zMinKind != Em3dBoundaryKind.Pec;

            // ── The geometry, up to the bond wires (Em3dLayoutSolids' first stage) ────────────
            var earlier = _notes;
            _g = new Em3dLayoutSolids.Builder(source, tech, wires,
                new Em3dLayoutSolidsOptions(fMax, _tempC, Instance: false) { RegionSetup = setup, FloorAllowed = !floorStatedAway, DisplaySlabs = displaySlabs });
            _g.Notes.AddRange(earlier);
            _notes = _g.Notes;
            if (_g.Prepare() is { } geometryRefusal) return No(geometryRefusal);
            var bands = _g.Bands;
            var pieces = _g.Pieces;
            var ground = _g.Ground;
            bool pecFloor = _g.PecFloor;
            double floorZ = _g.FloorZ;

            // ── Ports (R-em3d3-2) ─────────────────────────────────────────────────────────────
            // Built BEFORE the air box (brief-em3d-23): a wave port's side of the box has no padding.
            var ports = new List<Em3dPort>();
            var waves = new List<WaveSpec>();
            // brief-em3d-22 R-em3d22-1c — an electrostatic solve has no ports; a magnetostatic one keeps
            // only the ports its terminals are driven through, each a source sheet. brief-em3d-23 — an
            // eigenmode solve needs none: a closed cavity has no port, and a port it has is a load.
            bool anyPortLabel = source.View.Shapes.Any(sh => sh is LabelShape { IsPort: true });
            if (setup.Problem3D != Em3dProblemType.Electrostatic &&
                (setup.Problem3D != Em3dProblemType.Eigenmode || anyPortLabel) &&
                BuildPorts(bands, pieces, pecFloor ? ground : null, floorZ, ports, waves) is { } portRefusal)
            {
                if (!portsOptional) return No(portRefusal);
                _portRefusal = portRefusal;
                ports.Clear();
                waves.Clear();
            }
            foreach (int n in setup.Ports3D.Where(q => q.Kind == Em3dPortKind.Wave).Select(q => q.Port).Distinct())
                if (_portRefusal is null && ports.All(q => q.Number != n))
                    return No($"The setup makes port {n} a wave port, and this layout has no port {n}.");

            // ── Content bounds (the builder's), then the air box (R-em3d3-6) ─────────────────
            var (cx0, cy0, cx1, cy1, zLow, zHigh) = (_g.Cx0, _g.Cy0, _g.Cx1, _g.Cy1, _g.ZLow, _g.ZHigh);
            var airBox = PaddedAirBox(setup, (cx0, cy0, cx1, cy1, zLow, zHigh), fMin, pecFloor ? floorZ : null,
                                waves.Select(w => (w.Face, w.EdgeAt, w.Port.Number)).ToList(), _notes, out string? boxRefusal);
            if (boxRefusal is not null) return No(boxRefusal);
            foreach (var w in waves)
            {
                var (wave, why) = WavePort(w, airBox!);
                if (wave is null) return No(why!);
                ports[ports.FindIndex(q => q.Number == w.Port.Number)] = wave;
            }

            // ── Solids, in construction order (R-em3d3-1d): the builder's, and the air above the stack ──
            _g.BuildSolids(airBox, reserveAir: true);
            var solids = _g.SolidList;
            var sheets = _g.SheetList;
            if (_g.AirSlot is { } air)
            {
                // The builder numbered everything after the dielectrics as if this slot were taken, so the
                // air solid only has to be put into it.
                var box = airBox!;
                solids.Insert(air.Index, _g.Origin(new Em3dSolid(AirSolidName, air.Material, Em3dRole.Air,
                    new Em3dBox(new Point3(box.Min.X, box.Min.Y, air.Bottom), new Point3(box.Max.X, box.Max.Y, box.Max.Z)), air.Order),
                    Em3dObjectKind.Air, null));
            }

            // ── Terminals and ground (R-em3d22-2), by net ─────────────────────────────────────
            List<Em3dTerminal> terminals = [];
            List<string> groundObjects = [];
            if (setup.IsStatic3D)
            {
                if (Terminals(setup, solids, sheets, ports, pecFloor, _g.ObjectNetMap, _g.GroundBandObjectList,
                              out terminals, out groundObjects) is { } terminalRefusal)
                    return No(terminalRefusal);
                if (setup.Problem3D == Em3dProblemType.Magnetostatic)
                {
                    var driven = terminals.Select(t => t.SourcePort).OfType<string>().ToHashSet(StringComparer.Ordinal);
                    int dropped = ports.RemoveAll(p => !driven.Contains(p.Name));
                    if (dropped > 0)
                        _notes.Add($"{dropped} port(s) drive no terminal and are not in the magnetostatic problem.");
                }
            }

            // ── Notes on what was resolved and how ──────────────────────────────────────────
            _g.FinishNotes();
            _notes.Add(pecFloor
                ? $"The air box's floor is '{ground!.Layer.Name}' as a PEC plane at {Fmt(floorZ * 1e6)} µm: it is " +
                  "the lowest ground-reference conductor, nothing is drawn on it and nothing is drawn below it." +
                  (zMinFace?.PaddingUm is not null ? " The AirBox ZMin padding does not apply to a floor on the plane." : "")
                : "The air box's floor is below the geometry" +
                  (floorStatedAway ? ", as this setup's AirBox states." :
                   ground is null ? ": the technology designates no ground-reference conductor."
                                  : $": '{ground.Layer.Name}' is drawn or has metal below it, so it is " +
                                    "geometry rather than a floor."));

            var problem = new Em3dProblem(solids, sheets, _g.Materials, ports, airBox!, frequency, _tempC)
            {
                Type = setup.Problem3D,
                Terminals = terminals,
                GroundObjects = groundObjects,
                EigenmodeCount = setup.Eigenmode?.Count ?? EmEigenmode3D.DefaultCount,
                EigenmodeTargetHz = setup.Eigenmode?.TargetGHz is { } target ? target * 1e9 : fMin,
            };
            return new Em3dGenerationResult(problem, null, _notes)
            {
                Warnings = _g.WarningList,
                Wires = _g.WireBuild?.Reports ?? [],
                Origins = _g.OriginMap,
                MaterialSources = _g.MaterialSources,
                NoAlpha = _g.NoAlphaList,
                UnknownTemperature = _g.UnknownTemperatureList,
                PortRefusal = _portRefusal,
            };
        }

        // ── Terminals (brief-em3d-22 R-em3d22-2) ─────────────────────────────────────────────

        // ── Ports ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The planar port extractor, run over this problem's own signal-conductor polygons — so the
        /// numbering (R-em3d3-2d), the side inference and every refusal (R-em3d3-2b) are its own —
        /// then each port turned into a vertical sheet from its conductor to its return.
        /// </summary>
        private string? BuildPorts(IReadOnlyList<PlanarExtractor.StackBand> bands,
                                   Dictionary<int, List<Piece>> pieces,
                                   PlanarExtractor.StackBand? floorPlane, double floorZ, List<Em3dPort> ports,
                                   List<WaveSpec> waves)
        {
            var signal = bands.Where(b => pieces.ContainsKey(b.Index) && !b.Layer.IsGroundReference).ToList();
            if (signal.Count == 0)
                return "This layout draws metal only on ground-reference conductors, so there is no signal " +
                       "conductor for a port to drive.";

            var layers = signal.Select(b => new PlanarConductorLayer(
                b.Layer.Name, [.. pieces[b.Index].Select(p => p.Poly)], b.Layer.SigmaSm, b.TopM - b.BottomM)).ToList();
            var portProblem = new PlanarProblem(layers, new GroundedSlab(1, new EmMaterial(1, 0)), 0);

            // Every signal conductor is a level here, so a port over copper on another layer is resolved by
            // its own layer (the technology says which level that is), and no refusal offers analysis levels.
            var extracted = EmPortExtraction.Extract(source.View.Shapes, portProblem, source.DbuPerMicron,
                                                     setup.ResolvePortZ0, source.View.DisplayUnit,
                                                     technology: source.Technology, analysisLevelsApply: false);
            _notes.AddRange(extracted.Notes);
            if (!extracted.Ok) return extracted.Refusal;

            foreach (var p in extracted.Ports)
            {
                if (p.Kind != PlanarPortKind.Edge || p.IsConductorReferenced)
                    return $"Port {p.Number} is {(p.IsConductorReferenced ? "referenced to a drawn conductor" : "an internal port")}. " +
                           "A 3D setup builds lumped EDGE ports in this version — a vertical sheet from the " +
                           "conductor's end down (or up) to its return plane. Make it an edge port at a " +
                           "conductor end, or solve this layout with a planar setup.";

                var band  = signal[p.LayerIndex ?? 0];
                var piece = PieceAt(pieces[band.Index], p.Location.X, p.Location.Y, p.Side, out var edge);
                if (piece is null || edge is null)
                    return $"Port {p.Number} at ({Fmt(p.Location.X * 1e6)}, {Fmt(p.Location.Y * 1e6)}) µm is on " +
                           $"'{band.Layer.Name}' but no conductor end could be found under it.";
                var (edgeAt, lo, hi) = edge.Value;

                // The return: the .cem's named plane, else R-em-4 (RP-3's flip when the plane is above).
                StackupLayer? ret;
                bool above;
                if (setup.GroundStackupLayerName is { Length: > 0 } named)
                {
                    ret = bands.FirstOrDefault(b => b.Layer.Kind == StackupKind.Conductor && b.Layer.Name == named)?.Layer;
                    if (ret is null)
                        return $"This EM setup names '{named}' as its return plane, but technology '{tech.Name}' " +
                               "has no conductor stackup layer with that name.";
                    above = bands.First(b => ReferenceEquals(b.Layer, ret)).BottomM >= band.TopM;
                }
                else
                {
                    ret = PlanarExtractor.InferredReturnPlane(tech.Stackup, band.Index, out above);
                    if (ret is null)
                        return $"Port {p.Number} is on '{band.Layer.Name}', and technology '{tech.Name}' " +
                               "designates no ground-reference conductor above or below it for the port to " +
                               "return through. Mark the plane as a ground reference, or name it as this " +
                               "setup's return plane.";
                }
                var retBand = bands.First(b => ReferenceEquals(b.Layer, ret));

                // What the port's sheet ends on at the return side, and at which height.
                string negative;
                double retFace;
                if (floorPlane is not null && ReferenceEquals(retBand.Layer, floorPlane.Layer))
                {
                    negative = Em3dAirBox.FaceName("zmin");
                    retFace  = floorZ;
                }
                else if (pieces.TryGetValue(retBand.Index, out var retPieces) &&
                         PieceContaining(retPieces, p.Side, edgeAt, (lo + hi) / 2) is { } rp)
                {
                    negative = rp.Name;
                    retFace  = rp.IsSheet ? rp.SheetZ : above ? rp.ZBottom : rp.ZTop;
                }
                else
                    return $"Port {p.Number} returns through '{retBand.Layer.Name}', but there is no metal on " +
                           "it under the port, and it is not the air box's floor — so the port's sheet has " +
                           "nothing to end on. Draw the plane under the port, or name a different return plane.";

                double sigFace = piece.IsSheet ? piece.SheetZ : above ? piece.ZTop : piece.ZBottom;
                double zLo = above ? sigFace : retFace, zHi = above ? retFace : sigFace;
                if (!(zHi > zLo))
                    return $"Port {p.Number}'s return plane '{retBand.Layer.Name}' is not separated from " +
                           $"'{band.Layer.Name}' by any height, so the port's sheet would be empty.";

                bool alongX = p.Side is PlanarPortSide.MinX or PlanarPortSide.MaxX;
                var min = alongX ? new Point3(edgeAt, lo, zLo) : new Point3(lo, edgeAt, zLo);
                var max = alongX ? new Point3(edgeAt, hi, zHi) : new Point3(hi, edgeAt, zHi);
                var normal = p.Side switch
                {
                    PlanarPortSide.MinX => new Point3(1, 0, 0),
                    PlanarPortSide.MaxX => new Point3(-1, 0, 0),
                    PlanarPortSide.MinY => new Point3(0, 1, 0),
                    _                   => new Point3(0, -1, 0),
                };
                var centre = new Point3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
                var lumped = new Em3dPort(p.Number, $"port/{p.Number}", piece.Name, negative, min, max,
                                          new Point3(0, 0, above ? -1 : 1), p.Z0,
                                          // R-em3d3-2c — a Tier A port's reference plane IS its sheet.
                                          new Em3dReferencePlane(centre, normal, 0));
                ports.Add(lumped);
                // brief-em3d-23 — a wave port starts as the lumped sheet (its centre line is the voltage path)
                // and becomes a region of the box face once the box exists.
                if (setup.PortKind3D(p.Number) == Em3dPortKind.Wave)
                    waves.Add(new WaveSpec(lumped, p.Side switch
                    {
                        PlanarPortSide.MinX => "xmin", PlanarPortSide.MaxX => "xmax",
                        PlanarPortSide.MinY => "ymin", _ => "ymax",
                    }, edgeAt, hi - lo, zHi - zLo, above));
            }
            return null;
        }

        /// <summary>brief-em3d-23 — a wave port before the air box exists: the lumped sheet it grows from,
        /// the box face its line ends on, where the line ends, and the line's width and height above its
        /// return.</summary>
        private sealed record WaveSpec(Em3dPort Port, string Face, double EdgeAt, double WidthM, double HeightM, bool ReturnAbove);

        /// <summary>The wave port's region on its face, clipped to the face; or why it cannot be one.</summary>
        private (Em3dPort? Port, string? Refusal) WavePort(WaveSpec w, Em3dAirBox box)
        {
            var stated = setup.Ports3D.LastOrDefault(q => q.Port == w.Port.Number);
            double wf = stated?.WidthFactor ?? DefaultWaveWidthFactor * Math.Max(1, w.HeightM / w.WidthM);
            double hf = stated?.HeightFactor ?? DefaultWaveHeightFactor;
            if (!(wf > 1) || !(hf > 1))
                return (null, $"Port {w.Port.Number}'s wave-port region must be wider than its line and taller than the line's " +
                              $"height (Width and Height above 1); the setup states {Fmt(wf)} × {Fmt(hf)}.");
            double offset = (stated?.OffsetUm ?? 0) * 1e-6;
            if (!(offset >= 0))
                return (null, $"Port {w.Port.Number}'s wave-port offset is {Fmt(offset * 1e6)} µm; it is a distance into the " +
                              "structure, so it is zero or positive.");

            var p = w.Port;
            bool alongX = w.Face is "xmin" or "xmax";
            double at = w.Face switch { "xmin" => box.Min.X, "xmax" => box.Max.X, "ymin" => box.Min.Y, _ => box.Max.Y };
            double across = alongX ? (p.Min.Y + p.Max.Y) / 2 : (p.Min.X + p.Max.X) / 2;
            double half = wf * w.WidthM / 2;
            double lo = Math.Max(across - half, alongX ? box.Min.Y : box.Min.X);
            double hi = Math.Min(across + half, alongX ? box.Max.Y : box.Max.X);
            // The line's return face is the sheet's low end when the return is below, its high end above.
            double retZ = w.ReturnAbove ? p.Max.Z : p.Min.Z, sigZ = w.ReturnAbove ? p.Min.Z : p.Max.Z;
            double z0 = w.ReturnAbove ? Math.Max(retZ - hf * w.HeightM, box.Min.Z) : retZ;
            double z1 = w.ReturnAbove ? retZ : Math.Min(retZ + hf * w.HeightM, box.Max.Z);
            if (lo > across - half + 1e-12 || hi < across + half - 1e-12 ||
                (w.ReturnAbove ? z0 > retZ - hf * w.HeightM + 1e-12 : z1 < retZ + hf * w.HeightM - 1e-12))
                _notes.Add($"Port {p.Number}'s wave-port region is clipped to the air box's {w.Face} face; the box is " +
                           "smaller than the sizing rule asks for there.");
            var min = alongX ? new Point3(at, lo, z0) : new Point3(lo, at, z0);
            var max = alongX ? new Point3(at, hi, z1) : new Point3(hi, at, z1);
            var from = alongX ? new Point3(at, across, retZ) : new Point3(across, at, retZ);
            var to   = alongX ? new Point3(at, across, sigZ) : new Point3(across, at, sigZ);
            var n = p.ReferencePlane.Normal;
            var origin = new Point3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
            _notes.Add($"Port {p.Number} is a wave port on the air box's {w.Face} face: {Fmt((hi - lo) * 1e6)} × " +
                       $"{Fmt((z1 - z0) * 1e6)} µm ({Fmt(wf)} line widths × {Fmt(hf)} heights" +
                       $"{(stated?.WidthFactor is null && stated?.HeightFactor is null ? ", the sizing rule's" : "")}), its reference " +
                       $"plane {(offset > 0 ? $"{Fmt(offset * 1e6)} µm into the structure" : "on the face")}.");
            return (p with
            {
                Min = min, Max = max,
                ReferencePlane = new Em3dReferencePlane(origin, n, offset),
                Kind = Em3dPortKind.Wave,
                VoltagePath = new Em3dSegment(from, to),
            }, null);
        }

        /// <summary>The piece whose metal the port sits on, and the end it names: the edge's
        /// coordinate along the port's normal axis, and the metal's extent across it there.</summary>
        private static Piece? PieceAt(List<Piece> pieces, double x, double y, PlanarPortSide side,
                                      out (double At, double Lo, double Hi)? edge)
        {
            edge = null;
            Piece? best = null;
            double bestD = double.PositiveInfinity;
            foreach (var p in pieces)
            {
                var e = EdgeOf(p.Poly, x, y, side);
                if (e is null) continue;
                double d = Math.Abs(e.Value.At - (side is PlanarPortSide.MinX or PlanarPortSide.MaxX ? x : y));
                if (d < bestD) { bestD = d; best = p; edge = e; }
            }
            return best;
        }

        /// <summary>The return piece under a port's sheet, tested just inside the conductor end.</summary>
        private static Piece? PieceContaining(List<Piece> pieces, PlanarPortSide side, double at, double across)
        {
            foreach (var p in pieces)
            {
                var (x0, y0, x1, y1) = p.Poly.Bounds();
                double eps = 1e-6 * Math.Max(x1 - x0, y1 - y0);
                double inward = side is PlanarPortSide.MinX or PlanarPortSide.MinY ? eps : -eps;
                bool alongX = side is PlanarPortSide.MinX or PlanarPortSide.MaxX;
                double qx = alongX ? at + inward : across, qy = alongX ? across : at + inward;
                if (p.Poly.Contains(qx, qy) || p.Poly.Contains(alongX ? at - inward : qx, alongX ? qy : at - inward))
                    return p;
            }
            return null;
        }

        /// <summary>
        /// The conductor end a port at (x, y) names on <paramref name="poly"/>: the boundary crossing
        /// nearest the label along the port's normal axis, and the interval of metal across that axis
        /// just inside it. Null when the label's line meets the polygon nowhere.
        /// </summary>
        private static (double At, double Lo, double Hi)? EdgeOf(PlanarPolygon poly, double x, double y, PlanarPortSide side)
        {
            bool alongX = side is PlanarPortSide.MinX or PlanarPortSide.MaxX;
            double u = alongX ? x : y, v = alongX ? y : x;

            // Crossings of the line v = const with every ring, in the u coordinate.
            var along = Crossings(poly, v, alongX);
            if (along.Count == 0) return null;
            double at = along.OrderBy(c => Math.Abs(c - u)).First();

            var (x0, y0, x1, y1) = poly.Bounds();
            double eps = 1e-6 * Math.Max(x1 - x0, y1 - y0);
            double inside = at + (side is PlanarPortSide.MinX or PlanarPortSide.MinY ? eps : -eps);

            var across = Crossings(poly, inside, !alongX);
            across.Sort();
            for (int i = 0; i + 1 < across.Count; i += 2)
                if (v >= across[i] - eps && v <= across[i + 1] + eps)
                    return (at, across[i], across[i + 1]);
            return null;
        }

        /// <summary>Where the line (second coordinate = <paramref name="c"/>) crosses the polygon's
        /// rings, as values of the first coordinate. <paramref name="xFirst"/> picks x as the first.</summary>
        private static List<double> Crossings(PlanarPolygon poly, double c, bool xFirst)
        {
            var hits = new List<double>();
            void Ring(IReadOnlyList<EmPoint> ring)
            {
                int n = ring.Count;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double a1 = xFirst ? ring[j].X : ring[j].Y, b1 = xFirst ? ring[j].Y : ring[j].X;
                    double a2 = xFirst ? ring[i].X : ring[i].Y, b2 = xFirst ? ring[i].Y : ring[i].X;
                    if (b1 > c == b2 > c) continue;
                    hits.Add(a1 + (c - b1) * (a2 - a1) / (b2 - b1));
                }
            }
            Ring(poly.Outer);
            foreach (var h in poly.HoleRings) Ring(h);
            return hits;
        }


        private Em3dGenerationResult No(string refusal)
            => new(null, refusal, _notes) { Warnings = _g is null ? [] : _g.WarningList };

        private static string Fmt(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// R-em3d22-2a — each terminal's conductors (every one on its net), and the ground: the
    /// setup's Ground3D net, else the ground-reference conductors and anything sharing their net.
    /// Refuses a terminal whose net holds no conductor, naming it (R-em3d22-2c), and a magnetostatic
    /// terminal whose source is not a port of the layout.
    /// </summary>
    internal static string? Terminals(EmSetup setup, List<Em3dSolid> solids, List<Em3dSheet> sheets, List<Em3dPort> ports,
                                     bool pecFloor, Dictionary<string, HashSet<string>> _objectNets,
                                     List<string> _groundBandObjects,
                                     out List<Em3dTerminal> terminals, out List<string> ground)
    {
        terminals = [];
        ground = [];
        var conductors = solids.Where(s => s.Role == Em3dRole.Conductor).Select(s => s.Name)
                               .Concat(sheets.Select(s => s.Name)).ToList();
        List<string> OnNet(string net) =>
            [.. conductors.Where(c => _objectNets.TryGetValue(c, out var nets) && nets.Contains(net))];

        if (setup.Ground3D is { Length: > 0 } groundNet)
        {
            ground = OnNet(groundNet);
            if (ground.Count == 0 && !pecFloor)
                return $"This setup's ground is net '{groundNet}' (Ground3D), and no conductor in the 3D problem is on it. " +
                       "Name a net the layout draws, or leave Ground3D empty for the ground-reference conductors.";
        }
        else
        {
            var groundNets = _groundBandObjects.SelectMany(o => _objectNets.GetValueOrDefault(o) ?? []).ToHashSet(StringComparer.Ordinal);
            ground = [.. conductors.Where(c => _groundBandObjects.Contains(c) ||
                                               (_objectNets.TryGetValue(c, out var nets) && nets.Overlaps(groundNets)))];
        }

        if (setup.Terminals3D.Count == 0)
            return $"This {(setup.Problem3D == Em3dProblemType.Electrostatic ? "electrostatic" : "magnetostatic")} setup " +
                   "names no terminal, so there is no matrix to compute. List each conductor's net under Terminals3D.";
        foreach (var t in setup.Terminals3D)
        {
            var groundSet = ground;
            List<string> objects;
            // brief-em3d-49 — a terminal may name its conductors instead of a net (a drawn object carries none).
            if (t.ByObjects)
            {
                if (t.Net is { Length: > 0 })
                    return $"Terminal '{t.Name}' states both a net ('{t.Net}') and objects; a terminal is one or the other.";
                if (t.Objects!.FirstOrDefault(o => !conductors.Contains(o)) is { } missing)
                    return $"Terminal '{t.Name}' names '{missing}', which is not a conductor of the 3D problem" +
                           (solids.Any(s => s.Name == missing) ? " (it is a dielectric or air solid)" : "") + ".";
                if (t.Objects!.FirstOrDefault(groundSet.Contains) is { } grounded)
                    return $"Terminal '{t.Name}' names '{grounded}', which is the ground; a conductor is at one potential.";
                objects = [.. t.Objects!.Distinct(StringComparer.Ordinal)];
            }
            else objects = OnNet(t.Net).Where(o => !groundSet.Contains(o)).ToList();
            if (objects.Count == 0)
                return $"Terminal '{t.Name}' names net '{t.Net}', and no conductor in the 3D problem is on it" +
                       (OnNet(t.Net).Count > 0 ? " that is not also the ground" : "") +
                       ". A terminal is the metal of one net: name a net the layout (or a .wBond wire array) carries.";
            string? source = null;
            if (setup.Problem3D == Em3dProblemType.Magnetostatic)
            {
                if (t.Source is not { Length: > 0 } src)
                    return $"Magnetostatic terminal '{t.Name}' names no source. Its current needs a path in and back: " +
                           "name the port whose sheet drives it (Source: the port's number).";
                source = src.StartsWith("port/", StringComparison.Ordinal) ? src : "port/" + src.Trim();
                if (ports.All(p => p.Name != source))
                    return $"Terminal '{t.Name}' is driven through port '{src}', and the layout has no such port " +
                           $"(it has {(ports.Count == 0 ? "none" : string.Join(", ", ports.Select(p => p.Number)))}).";
            }
            terminals.Add(new Em3dTerminal(t.Name, objects, source));
        }
        return null;
    }

    /// <summary>
    /// <b>R-em3d3-6 — the air box around a content extent</b>: the setup's padding per face (its default
    /// an eighth of the longest wavelength, a quarter when a radiation pattern is asked for, the structure's
    /// own largest extent for a static solve), a wave port's face on its line's end, and the floor on the
    /// plane when there is one. brief-em3d-42 R-em3d42-5c — SHARED: a .cem's generator and a .c3d's problem
    /// assembly pad by this one method, so the two cannot disagree about a box.
    /// </summary>
    /// <param name="extent">The content's extent, metres.</param>
    /// <param name="floorZ">The PEC floor's height, or null when the box has none.</param>
    /// <param name="waves">Each wave port's face, where its line ends, and its number.</param>
    public static Em3dAirBox? PaddedAirBox(EmSetup setup, (double X0, double Y0, double X1, double Y1, double Z0, double Z1) extent,
                                           double fMin, double? floorZ, IReadOnlyList<(string Face, double EdgeAt, int Port)> waves,
                                           List<string> notes, out string? refusal)
    {
        refusal = null;
        var (cx0, cy0, cx1, cy1, zLow, zHigh) = extent;
        // brief-em3d-22 — a static solve has no wavelength, so its default padding is the structure's
        // own largest extent: far enough that the box's faces barely touch the field, and a length a
        // package-sized problem can mesh.
        bool radiating = setup.RadiationPattern && setup.Problem3D == Em3dProblemType.Driven;
        double pad = setup.IsStatic3D
            ? Math.Max(Math.Max(cx1 - cx0, cy1 - cy0), Math.Max(zHigh - zLow, 1e-6))
            : (radiating ? PatternPaddingFractionOfLongestWavelength : DefaultPaddingFractionOfLongestWavelength) * C0 / fMin;
        if (radiating)
            notes.Add($"The air box's default padding is a quarter of the longest wavelength ({Fmt(pad * 1e3)} mm) rather than " +
                      "an eighth, because a radiation pattern was asked for: its equivalence surface sits inside the absorber " +
                      "with room to spare around the structure. A padding the setup's AirBox states is used as stated.");
        var box = setup.AirBox ?? new EmAirBox();
        // brief-em3d-23 R-em3d23-2a — a wave port lies on the box, so its side has no padding and its
        // line must run to the structure's edge there.
        var waveFaces = waves.Select(w => w.Face).ToHashSet(StringComparer.Ordinal);
        box = box with
        {
            XMin = waveFaces.Contains("xmin") ? new EmAirBoxFace(0, box.XMin?.Boundary) : box.XMin,
            XMax = waveFaces.Contains("xmax") ? new EmAirBoxFace(0, box.XMax?.Boundary) : box.XMax,
            YMin = waveFaces.Contains("ymin") ? new EmAirBoxFace(0, box.YMin?.Boundary) : box.YMin,
            YMax = waveFaces.Contains("ymax") ? new EmAirBoxFace(0, box.YMax?.Boundary) : box.YMax,
        };
        foreach (var (face, edgeAt, port) in waves)
        {
            double at = face switch { "xmin" => cx0, "xmax" => cx1, "ymin" => cy0, _ => cy1 };
            if (Math.Abs(edgeAt - at) > 1e-9 * Math.Max(1e-3, Math.Abs(at)))
            {
                refusal = $"Port {port} is a wave port, and a wave port lies on the air box — but its line ends at " +
                          $"{face[0]} = {Fmt(edgeAt * 1e6)} µm, short of the box's {face} face at {Fmt(at * 1e6)} µm, " +
                          "where other geometry (the board outline, a plane, another conductor) reaches. Run the line to " +
                          $"the {face} edge of the layout, or make port {port} a lumped port.";
                return null;
            }
            if (setup.AirBox is { } stated && FaceOf(stated, face) is { PaddingUm: > 0 } padded)
                notes.Add($"Port {port} is a wave port, so the air box's {face} face lies on its line's end: the " +
                          $"setup's {Fmt(padded.PaddingUm!.Value)} µm padding there is not used.");
            else if (setup.AirBox is { } statedPct && FaceOf(statedPct, face) is { PaddingPercent: > 0 } paddedPct)
                notes.Add($"Port {port} is a wave port, so the air box's {face} face lies on its line's end: the " +
                          $"setup's {Fmt(paddedPct.PaddingPercent!.Value)} % padding there is not used.");
        }
        // 3D editor round 1 — a face may state its padding as a percentage of the content's extent along its axis.
        foreach (var (name, f) in new[] { ("xmin", box.XMin), ("xmax", box.XMax), ("ymin", box.YMin),
                                          ("ymax", box.YMax), ("zmin", box.ZMin), ("zmax", box.ZMax) })
        {
            if (f is { PaddingUm: not null, PaddingPercent: not null })
            {
                refusal = $"The air box's {name} face states both PaddingUm and PaddingPercent; state one of them.";
                return null;
            }
            if (f?.PaddingPercent is < 0)
            {
                refusal = $"The air box's {name} face has a negative PaddingPercent; a padding is a distance from the geometry, zero or more.";
                return null;
            }
        }
        var flatAxes = new SortedSet<char>();
        double Pad(EmAirBoxFace? f, char axis, double span)
        {
            if (f?.PaddingUm is { } um) return um * 1e-6;
            if (f?.PaddingPercent is not { } pct) return pad;
            if (span > 0) return pct / 100 * span;
            flatAxes.Add(axis);
            return pad;
        }
        Em3dBoundaryKind Kind(EmAirBoxFace? f) => f?.Boundary ?? Em3dBoundaryKind.Absorbing;

        double sx = cx1 - cx0, sy = cy1 - cy0, sz = zHigh - zLow;
        var boxMin = new Point3(cx0 - Pad(box.XMin, 'x', sx), cy0 - Pad(box.YMin, 'y', sy), floorZ is { } fz ? fz : zLow - Pad(box.ZMin, 'z', sz));
        var boxMax = new Point3(cx1 + Pad(box.XMax, 'x', sx), cy1 + Pad(box.YMax, 'y', sy), zHigh + Pad(box.ZMax, 'z', sz));
        foreach (char a in flatAxes)
            notes.Add($"The air box's padding along {a} is stated as a percentage of the content's extent along {a}, which is " +
                      $"zero (the content is flat there), so those faces take the default padding ({Fmt(pad * 1e3)} mm) instead.");
        var faces = new Em3dFaces(Kind(box.XMin), Kind(box.XMax), Kind(box.YMin), Kind(box.YMax),
                                  floorZ is not null ? Em3dBoundaryKind.Pec : Kind(box.ZMin), Kind(box.ZMax));
        return new Em3dAirBox(boxMin, boxMax, faces);
    }

    private static EmAirBoxFace? FaceOf(EmAirBox b, string face) => face switch
    {
        "xmin" => b.XMin, "xmax" => b.XMax, "ymin" => b.YMin, "ymax" => b.YMax, "zmin" => b.ZMin, _ => b.ZMax,
    };

    private static string Fmt(double v) => v.ToString("G6", CultureInfo.InvariantCulture);


    /// <summary>
    /// <b>R-em3d3-5c — the drawing layers that are the board outline, found the way the interchange
    /// code already names them</b>, not by a new convention: a layer whose board-format name is
    /// <c>Edge.Cuts</c> (what the board reader and writer map the outline to, and what the shipped
    /// PCB technologies declare), or whose Gerber file function is <c>Profile</c> (X2's own word for
    /// the outline, which a Gerber import records on the layer it creates). A layer merely NAMED
    /// "Outline" is not enough: a name decides a sentence, never geometry (GerberLayerCascade's rule).
    /// </summary>
    public static HashSet<LayerKey> BoardOutlineLayers(Technology tech)
    {
        var keys = new HashSet<LayerKey>();
        foreach (var l in tech.Layers)
        {
            if (string.Equals(l.Interchange?.PcbLayerName, "Edge.Cuts", StringComparison.Ordinal) ||
                GerberLayerCascade.ParseFileFunction(l.Interchange?.GerberFileFunction) is { } fn &&
                string.Equals(fn.Kind, "Profile", StringComparison.OrdinalIgnoreCase))
                keys.Add(l.Key);
        }
        return keys;
    }
}
