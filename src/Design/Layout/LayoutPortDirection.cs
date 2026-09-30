// Framework-free. No Avalonia, no SkiaSharp — the renderer, the editor and the EM extractor all
// read this, and only the renderer is allowed to know about Skia.
//
// It lives in src/Design rather than src/Render because that sentence has to be TRUE of the EM
// extractor as well: `EmRunService` and `EmPortExtraction` are below the firewall in src/Design,
// which does not reference src/Render, so a port's type could not be inferred from the artwork in
// the one place the RUN reads it. The alternative was a second interior test in src/Design, which is
// exactly the second copy every other note in this file exists to prevent. Both src/Render and
// src/Ui already say `global using CircuitRF.Design.Layout`, so no call site changed.

using System.Collections.Generic;
using CircuitRF.Engine.Mom;   // PlanarPortKind — the port TYPE an EM setup drives a label as.

namespace CircuitRF.Design.Layout;

/// <summary>
/// An EM port's DIRECTION — the way current flows INTO the structure — and the conductor width it
/// spans, resolved from a port <see cref="LabelShape"/> and the artwork beneath it.
///
/// <para><b>The convention, stated once and never re-derived at a call site:</b> <c>R0</c> = +x̂,
/// <c>R90</c> = +ŷ, <c>R180</c> = −x̂, <c>R270</c> = −ŷ — the usual counter-clockwise convention in
/// layout's y-up world. A port whose current flows +x̂ sits on the conductor's LOW-x end, so the
/// direction points AWAY from the end the label is on and INTO the metal. Get that backwards and
/// S₂₁ picks up a hard π that no magnitude plot can show.</para>
///
/// <para><b>Why this exists as its own file (owner report, 2026-08-09).</b> Before it, a port was a
/// label with a flag: it drew as text, the Properties Inspector called it a Label, and its side was
/// inferred from geometry at extraction time and nowhere else — so there was nothing on screen
/// saying which way it faced and nothing to rotate. This is the one place the direction↔geometry
/// relationship is written down, shared by the Port tool (which seeds it), the renderer (which draws
/// it) and <c>EmPortExtraction</c> (which consumes it).</para>
/// </summary>
public static class LayoutPortDirection
{
    /// <summary>What a resolved port marker needs to draw itself, and what extraction needs to
    /// know: which way current flows in, how wide the conductor is across that direction, and —
    /// owner report, 2026-08-09 ("I can't tell from the port glyph where the actual reference plane
    /// is") — WHERE that plane sits.
    ///
    /// <para><b><see cref="PlaneX"/>/<see cref="PlaneY"/> is the centre of the conductor END the port
    /// names, not the label's own anchor.</b> The two differ whenever the user clicked somewhere
    /// other than exactly the end of the metal, which is nearly always — and drawing the width bar at
    /// the anchor is what made the plane's position ambiguous. The kernel's own plane is fixed one
    /// mesh cell IN from this edge (<c>PlanarPort</c> D2), a sub-cell offset no drawn glyph can
    /// honestly resolve; the post-run overlay (§10.6) draws the engine's real planes over its own
    /// coordinates.</para>
    ///
    /// <para><see cref="LengthDbu"/> is the conductor's extent ALONG the direction — how much metal
    /// there is for the arrow to point into. Without it the marker has no way to know it is about to
    /// draw past the end of the thing it annotates (owner report, 2026-08-09).</para></summary>
    /// <param name="Interior">
    /// <b>True when the label does NOT sit at the conductor face its own direction names</b> — it is
    /// standing in the middle of a piece of metal.
    ///
    /// <para><b>Why this is on the hint (owner, 2026-09-09: "port placement always snaps to edge of
    /// geometry … SAME DAMN BUG WITH DRAGGING TOO").</b> A <c>.clay</c> deliberately carries no port
    /// type — the same artwork can be gapped in one EM setup and edge-driven in another — so a layout
    /// with no <c>.cem</c> claiming it drew EVERY port as an edge port, and an edge port's bar and
    /// arrow are drawn at <see cref="PlaneX"/>/<see cref="PlaneY"/>, the conductor END. Click the
    /// middle of a rectangle and the label was stored exactly where you clicked while the mark you
    /// see appeared at the metal's edge; drag it and the label moved while the mark stayed put.
    /// Measured on a 20 x 2.9 mm rect: click (10000, 1450) → label (10000, 1450), mark (0, 1450), with
    /// geometry snap ON and OFF alike. Nothing was snapping — the port was being DRAWN somewhere it
    /// was not.</para>
    ///
    /// <para>So where no setup has spoken, the drawing infers what it can honestly infer from the one
    /// thing the layout does know — where the label is. At the face it names, it is an edge port and
    /// the bar belongs at that face, which is the 2026-08-09 request ("where is the reference plane"
    /// had no readable answer with the bar drawn wherever the user clicked) and is unchanged. Deep
    /// inside the metal, "edge port" is not a tenable reading of the drawing at all, and the mark is
    /// drawn at the label — <see cref="InferredKind"/>. A <c>.cem</c> that has claimed the layout
    /// still overrules this outright, Edge included.</para>
    /// </param>
    public readonly record struct PortHint(
        LayoutRotation Direction, long WidthDbu, bool Inferred, long PlaneX, long PlaneY, long LengthDbu,
        bool Interior = false);

    /// <summary>
    /// <b>What to draw a port as when nothing has said what it is</b> — read only where
    /// <see cref="LabelShape.PortKind"/> is null. See <see cref="PortHint.Interior"/> for the report.
    /// </summary>
    public static PlanarPortKind InferredKind(PortHint hint) =>
        hint.Interior ? PlanarPortKind.Internal : PlanarPortKind.Edge;

    /// <summary>
    /// <b>THE port type: what this label is, stated or inferred.</b> The single answer the renderer,
    /// the hit test, the Port tool's ghost, the <c>.cem</c>'s port list and the EM extractor all
    /// take — so none of them can disagree about whether a port is an edge port, and the picture the
    /// tool draws under the cursor is the one the run drives.
    ///
    /// <para><b>There used to be two answers, and that was the bug</b> (owner report, 2026-09-14).
    /// The drawing inferred from the artwork; the <c>.cem</c> answered <c>Edge</c> for any port slot
    /// it had never been told about, which is every port in a setup whose <c>PortKinds</c> was empty
    /// — the normal case. A port placed in the middle of a trace was therefore drawn as an internal
    /// port by the ghost, committed, and redrawn one frame later as an edge port with its
    /// reference-plane bar at the far end of the conductor. The type lives on the label now
    /// (<see cref="LabelShape.PortKind"/>) and this is the one place silence is resolved.</para>
    ///
    /// <para>Edge is the answer for a port whose conductor cannot be resolved at all. Such a port is
    /// refused BY NAME at extraction time; it still has to draw as something in the meantime, and
    /// an edge port's bar-and-arrow on the label's own anchor is what it has always drawn as.</para>
    /// </summary>
    public static PlanarPortKind KindOf(ConductorLookup? conductorAt, LabelShape label)
        => label.PortKind
           ?? (Resolve(conductorAt, label) is { } hint ? InferredKind(hint) : PlanarPortKind.Edge);

    /// <summary>
    /// <b>Whether a port's mark is drawn at the LABEL's own anchor rather than at the conductor
    /// end.</b> The single answer the renderer, the selection outline and the pick region all take,
    /// so none of them can disagree about where a port is — which is the drift
    /// <see cref="MarkerBbox"/> exists to prevent.
    /// </summary>
    public static bool MarkAtAnchor(PlanarPortKind kind, PortHint hint) =>
        kind != PlanarPortKind.Edge || hint.Interior;

    // ── THE MARKER'S OWN EXTENT ───────────────────────────────────────────────────────────────
    //
    // Owner request, 2026-08-25: "make the hitbox/highlight the anchor arrow area + padding."
    //
    // These fractions and PortArrowGeometry were private to LayoutRenderer, which is fine while the
    // only thing that needs to know how big a port marker is the code that DRAWS it. A pick region
    // measured from the marker needs the same numbers, and LayoutHitTest is framework-free and
    // cannot reach into a Skia file for them — so they live here, with PortHint, and the renderer
    // reads them from here. **Two copies of a marker's size, one in the renderer and one in the hit
    // test, is precisely the drift that produced the mismatch this replaces.**

    /// <summary>The arrow's preferred reach, as a fraction of the port width.</summary>
    public const double ArrowLengthOverWidth = 0.35;

    /// <summary>The hard ceiling on that reach, as a fraction of the conductor's extent ALONG the
    /// direction, so a short conductor clamps the arrow rather than the arrow dwarfing it.</summary>
    public const double ArrowMaxLengthOverConductorLength = 0.7;

    /// <summary>The arrowhead's barbs, as a fraction of the arrow's FINAL reach.</summary>
    public const double ArrowBarbOverReach = 0.35;

    /// <summary>A cap on the barbs, as a fraction of the port width.</summary>
    public const double ArrowMaxBarbOverWidth = 0.22;

    /// <summary>The edge port's plane serif, as a fraction of the port width.</summary>
    public const double PlaneSerifOverWidth = 0.12;

    /// <summary>Half the internal gap's break, as a fraction of the port width.</summary>
    public const double GapHalfOverWidth = 0.11;

    /// <summary>The internal gap's turned-back flanges, as a fraction of the port width.</summary>
    public const double GapFlangeOverWidth = 0.22;

    /// <summary>How far a gap's bars run PAST the conductor at each end, over the port width.</summary>
    public const double GapOverhangOverWidth = 0.10;

    /// <summary>The internal (to-ground) port's ring, as a fraction of the conductor width.</summary>
    public const double RingOverWidth = 0.55;

    /// <summary>
    /// How long the direction arrow is, and how long its barbs are, in DBU.
    ///
    /// <para><b>The arrow is bounded by the metal it points into, never by its own preferred
    /// size</b> — <c>reach = min(width-preferred, length × the ceiling)</c> — and the barbs are tied
    /// to that final reach rather than to the width, so the whole arrow shrinks together when a short
    /// conductor clamps it instead of leaving a stub with an enormous head on it.</para>
    /// </summary>
    public static (double Reach, double BarbLen) ArrowGeometry(PortHint hint)
    {
        double preferred = hint.WidthDbu * ArrowLengthOverWidth;
        double available = hint.LengthDbu > 0
            ? hint.LengthDbu * ArrowMaxLengthOverConductorLength
            : preferred;
        double reach = System.Math.Min(preferred, available);

        double barb = System.Math.Min(reach * ArrowBarbOverReach, hint.WidthDbu * ArrowMaxBarbOverWidth);
        return (reach, barb);
    }

    /// <summary>
    /// <b>The bounding box of the mark a port DRAWS, plus <paramref name="padding"/>.</b> One
    /// function, so the hit test and the selection highlight cannot disagree about where a port is —
    /// which is the whole reason it exists (owner, 2026-08-25: "the hitbox of the port does not match
    /// with the select highlight rendering", then "make the hitbox/highlight the arrow boundary box
    /// for edge and internal ports and make it the gap boundary rendering for the gap port").
    ///
    /// <para><b>The port's TEXT is not part of it.</b> A port is picked and highlighted by its mark;
    /// the name is a label beside it. Two earlier attempts sized this from the glyphs and both were
    /// rejected — the box was either far from the mark or far bigger than it.</para>
    ///
    /// <para><b>The split is EDGE versus the two internal kinds, and it follows what is drawn rather
    /// than the request's wording.</b> Only an edge port has an arrow: its plane bar and arrowhead
    /// are drawn about <see cref="PortHint.PlaneX"/>/<see cref="PortHint.PlaneY"/>, the conductor
    /// end. An internal-to-ground port draws a RING at the label's own anchor
    /// (<c>LayoutRenderer.DrawInternalPortMarker</c> centres on <c>label.X/Y</c>, or on the meshed via
    /// footprint), and a gap port draws its break there too. So the ring goes with the gap, not with
    /// the arrow — boxing it at the plane would put its highlight where nothing is drawn.</para>
    ///
    /// <para>An edge port's box being at the conductor end is why
    /// <c>LayoutSpatialIndex.ConservativeBboxOf</c> stops culling ports: the anchor-to-plane distance
    /// is unbounded, so any finite pad about the anchor would sometimes prune a port before its exact
    /// test ran.</para>
    /// </summary>
    /// <param name="atAnchor">
    /// True for the two INTERNAL kinds (gap and to-ground), whose marks are drawn at the label's own
    /// anchor; false for an edge port, whose mark is at the conductor end.
    /// </param>
    public static Bbox MarkerBbox(LabelShape label, PortHint hint, bool atAnchor, long padding)
    {
        bool alongX = hint.Direction is LayoutRotation.R0 or LayoutRotation.R180;

        double across, along;
        long cx, cy;

        if (atAnchor)
        {
            // The gap's break — two bracketed bars either side of the cut, overhanging the metal at
            // both ends and turned back by their flanges — or the internal port's ring, whichever is
            // larger. Both are drawn at the anchor.
            across = System.Math.Max(hint.WidthDbu * (0.5 + GapOverhangOverWidth),
                                     hint.WidthDbu * RingOverWidth);
            along  = System.Math.Max(hint.WidthDbu * (GapHalfOverWidth + GapFlangeOverWidth),
                                     hint.WidthDbu * RingOverWidth);
            cx = label.X; cy = label.Y;
        }
        else
        {
            // The plane bar and the arrow, about the conductor end: across, half the conductor width
            // plus the serif's turn; along, the arrow's own reach.
            var (reach, _) = ArrowGeometry(hint);
            across = hint.WidthDbu * (0.5 + PlaneSerifOverWidth);
            along  = reach;
            cx = hint.PlaneX; cy = hint.PlaneY;
        }

        long halfX = (long)System.Math.Round(alongX ? along : across) + padding;
        long halfY = (long)System.Math.Round(alongX ? across : along) + padding;

        return new Bbox(cx - halfX, cy - halfY, cx + halfX, cy + halfY);
    }


    /// <summary>
    /// A cell PIN's own exact statement about the conductor end at a point: where the edge is, how
    /// wide it is, and which way current flows into the metal from it.
    ///
    /// <para><b>This is a MEASUREMENT, where <see cref="Bbox"/> is an approximation</b> — see
    /// <see cref="ConductorInfo"/> for why the distinction is load-bearing.</para>
    /// </summary>
    public readonly record struct PinFacts(long X, long Y, long WidthDbu, LayoutRotation Direction);

    /// <summary>
    /// What the artwork under a point says about the conductor there: always a bounding box, and —
    /// when the point names a cell pin — that pin's own exact facts.
    ///
    /// <para><b>Why the pin has to be carried separately (owner report, 2026-08-09: "the Port
    /// snapping and resultant port width is incorrect on Port 1 of my MKLOPF").</b> A port placed on
    /// a placed <see cref="LayoutInstance"/> used to resolve against the instance's array-expanded
    /// BOUNDING BOX, because that was the only thing the lookup could return. For a straight run of
    /// metal that is a fair approximation. For a TAPER it is not remotely one: on the reported
    /// design the box spans the whole 63 × 9 mm envelope, so the port reported a width of 9.23 mm
    /// against the pin's real 1.06 mm (8.7× too wide) and drew its reference-plane bar at the box's
    /// mid-height — 4.01 mm away from where pin 1 actually is, which is what reads as the snap being
    /// wrong: the marker lands nowhere near the point that was clicked.</para>
    ///
    /// <para><b>The cell already knew the answer.</b> <see cref="LayoutPin"/> has carried
    /// <see cref="LayoutPin.WidthDbu"/> and <see cref="LayoutPin.OutwardDeg"/> since pins became
    /// first-class — precisely "an edge, with a width and a direction". Nothing needed measuring;
    /// the lookup simply had no way to say it. <b>A bbox is what you fall back to when there is no
    /// pin, not the thing you prefer.</b></para>
    /// </summary>
    /// <param name="Shape">
    /// The top-level shape the point landed on, when there IS one — so the port's width can be
    /// MEASURED at the end face instead of taken from the bounding box.
    ///
    /// <para><b>This is the same defect the paragraph above records, arriving by the other route.</b>
    /// There it was an INSTANCE's array-expanded box and the cure was the cell's own pin. Here it is
    /// a top-level polygon with no pin at all — a drawn taper, or an MKLOPF flattened into the
    /// layout — and the box is just as wrong: a Klopfenstein taper's box is its WIDE end's width at
    /// both ports, so the narrow end draws a bar several times the metal that is actually there, and
    /// an arrow scaled to it. Null when the conductor is an instance (nothing to measure), which is
    /// exactly when <paramref name="Pin"/> or the box is the best available answer.</para>
    /// </param>
    /// <param name="CopperLayer">The drawing layer of the copper under the point when that copper is
    /// inside a placed instance — where <paramref name="Shape"/> is null, so the layer would otherwise be
    /// lost. Read through <see cref="ConductorInfo.Layer"/>.</param>
    public readonly record struct ConductorInfo(Bbox Box, PinFacts? Pin, LayoutShape? Shape = null,
                                                LayerKey? CopperLayer = null)
    {
        /// <summary>
        /// The drawing layer of the metal this answer is about — a top-level shape's own, else the
        /// instance copper's. Null only when neither is known (an instance answered from its box alone).
        ///
        /// <para><b>What a port COMMITS to (field report, 2026-09-29).</b> A port placed on a generated
        /// line — which is an instance — used to commit to no layer at all, so the EM run fell back to the
        /// label's own drawing layer: whatever layer was current when the Port tool was used, which right
        /// after placing a via is the drill layer.</para>
        /// </summary>
        public LayerKey? Layer => Shape?.Layer ?? CopperLayer;
    }

    /// <summary>
    /// Where a conductor is looked up from a point. A delegate rather than a shape list because the
    /// answer has two sources that cannot be unified into one collection: top-level shapes, and
    /// geometry that only exists inside a placed <see cref="LayoutInstance"/> (a PCell's artwork,
    /// which is exactly what "Update Layout from Schematic" produces — see
    /// <see cref="LookupFor(LayoutView, Technology?, string, long)"/>).
    /// </summary>
    /// <param name="onLayer">
    /// <b>The layer the port has already COMMITTED to, when it has one</b>
    /// (<see cref="LabelShape.PortLayer"/>) — restrict the search to it, and pay no attention to
    /// whether that layer is currently shown. Null asks the other question: <i>what visible metal is
    /// here</i>, which is what a placement or a drag is entitled to ask and what re-stamps the
    /// commitment.
    ///
    /// <para>The split is the whole of how two owner requirements that pull opposite ways are both
    /// kept — see <see cref="LookupFor(LayoutView, Technology?, string, long)"/>.</para>
    /// </param>
    public delegate ConductorInfo? ConductorLookup(long x, long y, LayerKey? onLayer);

    /// <summary>The unit vector current flows along, entering the structure. Integer, because the
    /// four cases are axis-aligned by construction.</summary>
    public static (int X, int Y) UnitVector(LayoutRotation r) => r switch
    {
        LayoutRotation.R0   => (1, 0),
        LayoutRotation.R90  => (0, 1),
        LayoutRotation.R180 => (-1, 0),
        _                   => (0, -1),
    };

    /// <summary>The perpendicular unit vector — the axis the port's width bar is drawn across.</summary>
    public static (int X, int Y) PerpendicularVector(LayoutRotation r)
    {
        var (ux, uy) = UnitVector(r);
        return (-uy, ux);
    }

    /// <summary>
    /// Which direction a port at <paramref name="x"/>,<paramref name="y"/> faces, given the
    /// conductor bounding box it sits on: the NEAREST side is the end the port names, and current
    /// flows away from it into the metal.
    /// </summary>
    public static LayoutRotation FromBbox(Bbox bb, long x, long y)
    {
        long dMinX = System.Math.Abs(x - bb.MinX);
        long dMaxX = System.Math.Abs(bb.MaxX - x);
        long dMinY = System.Math.Abs(y - bb.MinY);
        long dMaxY = System.Math.Abs(bb.MaxY - y);

        long best = dMinX;
        var dir = LayoutRotation.R0;                 // low-x end  -> current flows +x̂
        if (dMaxX < best) { best = dMaxX; dir = LayoutRotation.R180; }
        if (dMinY < best) { best = dMinY; dir = LayoutRotation.R90;  }
        if (dMaxY < best) {               dir = LayoutRotation.R270; }
        return dir;
    }

    /// <summary>
    /// The centre of the conductor END the port names — the reference plane's own position. Current
    /// flows away from this edge into the metal, so the edge is the one OPPOSITE the direction:
    /// <c>R0</c> (current +x̂) names the LOW-x edge, and so on.
    /// </summary>
    public static (long X, long Y) PlaneOf(Bbox bb, LayoutRotation direction)
    {
        long midX = bb.MinX + (bb.MaxX - bb.MinX) / 2;
        long midY = bb.MinY + (bb.MaxY - bb.MinY) / 2;
        return direction switch
        {
            LayoutRotation.R0   => (bb.MinX, midY),
            LayoutRotation.R180 => (bb.MaxX, midY),
            LayoutRotation.R90  => (midX, bb.MinY),
            _                   => (midX, bb.MaxY),
        };
    }

    /// <summary>The conductor's extent ACROSS <paramref name="direction"/> — the port's width.
    /// <b>The fallback</b>, for a conductor with neither a pin nor a measurable outline; prefer
    /// <see cref="SpanAt"/>, which measures the metal that is actually at the end face.</summary>
    public static long WidthAcross(Bbox bb, LayoutRotation direction) =>
        direction is LayoutRotation.R0 or LayoutRotation.R180
            ? bb.MaxY - bb.MinY
            : bb.MaxX - bb.MinX;

    /// <summary>
    /// How far inside the end face the width is measured, as a fraction of the conductor's own length
    /// along the port's axis. <b>Not zero, and that is arithmetic rather than caution:</b> a cut
    /// exactly ON the end face lies along that face's own edge, where a scanline's crossings are
    /// degenerate — the answer would be 0, 1 or the whole span depending on rounding. One part in a
    /// thousand is far inside the numerical problem and far outside anything a taper's flank can
    /// change: on a 10 mm taper it is 10 µm.
    /// </summary>
    private const double SpanInsetOverLength = 1e-3;

    /// <summary>
    /// <b>The metal actually present at a port's end face: its width, and the centre of it.</b>
    /// A scanline across the shape's flattened outline, just inside the face, keeping the one run of
    /// metal that contains the port's own transverse position.
    ///
    /// <para>Returns null when the shape cannot be flattened, or when the cut finds no metal at the
    /// port's own position — both of which mean "there is nothing here to measure", and the caller
    /// falls back to the bounding box rather than to a guess.</para>
    ///
    /// <para><b>The run CONTAINING the port, not the total.</b> A cut across a tee or a pair of
    /// coupled lines crosses several separate pieces of metal, and the port is on exactly one of
    /// them; summing them would report a width the port does not have. This mirrors what
    /// <c>PlanarPorts</c> does on the mesh, where the run is the contiguous row of rooftops the
    /// port's own cell is part of — a different mechanism answering the same question the same way.</para>
    /// </summary>
    /// <param name="acrossAt">The port's own TRANSVERSE position, which picks the run.</param>
    /// <param name="alongAt">
    /// Where along the conductor to cut. <b>Null — the default — means the END FACE</b>, which is
    /// what an edge port's width is. An INTERNAL delta gap passes its own longitudinal position
    /// instead: its cut is in the middle of the metal, and on anything that changes width along its
    /// length the two are different numbers. Measuring a gap at the end face is the same class of
    /// error as measuring an end at the bounding box.
    /// </param>
    public static (long Width, long Centre)? SpanAt(
        LayoutShape shape, Bbox box, LayoutRotation direction, long acrossAt, long? alongAt = null)
    {
        var ring = OutlineOf(shape);
        if (ring is null || ring.Length < 6) return null;

        bool alongX = direction is LayoutRotation.R0 or LayoutRotation.R180;
        bool fromLow = direction is LayoutRotation.R0 or LayoutRotation.R90;

        long length = alongX ? box.MaxX - box.MinX : box.MaxY - box.MinY;
        if (length <= 0) return null;

        long inset = System.Math.Max(1, (long)(length * SpanInsetOverLength));
        long face  = alongX ? (fromLow ? box.MinX : box.MaxX) : (fromLow ? box.MinY : box.MaxY);

        // An interior station is used as given — it is already inside the metal, so the degeneracy
        // the inset exists to avoid cannot arise there.
        double cut = alongAt ?? (fromLow ? face + inset : face - inset);

        // Crossings of the cut line with the outline, in the transverse coordinate.
        var hits = new List<double>();
        int n = ring.Length / 2;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            double a  = alongX ? ring[2 * i] : ring[2 * i + 1];
            double b  = alongX ? ring[2 * j] : ring[2 * j + 1];
            double ta = alongX ? ring[2 * i + 1] : ring[2 * i];
            double tb = alongX ? ring[2 * j + 1] : ring[2 * j];

            // Half-open in the along coordinate, so a vertex exactly on the cut is counted once.
            if ((a <= cut && b > cut) || (b <= cut && a > cut))
                hits.Add(ta + (cut - a) / (b - a) * (tb - ta));
        }

        if (hits.Count < 2) return null;
        hits.Sort();

        // Pairs of crossings bound the metal. Keep the pair the port itself is inside; if the port
        // sits between two runs (a gap), take the nearest pair rather than nothing — the label is on
        // the metal in every case that reaches here, and a rounding-width miss must not blank the
        // marker.
        double best = double.MaxValue;
        double lo = 0, hi = 0;
        for (int i = 0; i + 1 < hits.Count; i += 2)
        {
            double a = hits[i], b = hits[i + 1];
            double d = acrossAt < a ? a - acrossAt : acrossAt > b ? acrossAt - b : 0;
            if (d < best) { best = d; lo = a; hi = b; }
        }

        long width = (long)System.Math.Round(hi - lo);
        return width > 0 ? (width, (long)System.Math.Round(0.5 * (lo + hi))) : null;
    }

    /// <summary>
    /// <b>Where the conductor's edge is, LOCALLY, in the direction the port names.</b> Walks the
    /// shape's own outline from the anchor, OPPOSITE <paramref name="direction"/> (current flows away
    /// from the face and into the metal), and returns the first crossing — the along-coordinate of
    /// the face the port is standing on.
    ///
    /// <para><b>Why this exists (owner report, 2026-09-09: a port dropped on the big rectangle at
    /// one end of a polygon kept drawing itself over the trace at the other end).</b> Everything about an
    /// edge port's plane was measured from the conductor's BOUNDING BOX:
    /// <see cref="PlaneOf"/> returns a box edge, and <see cref="SpanAt"/>'s default cut is at that
    /// box edge. For a straight run of metal the box IS the conductor and both are exact — which is
    /// the case they were written for. <b>A real imported polygon is not one feature.</b> On the
    /// reporting board a single Top Copper polygon carries three: a blob at x≈103.5 mm (1.21 mm
    /// tall), a narrow 0.60 mm trace at x≈105.4, and a 1.80 mm rectangle from x≈106.0 to 109.63. Its
    /// bounding box spans all three and describes none of them, so a port on the narrow trace had its
    /// plane drawn at the BOX's bottom edge — 0.73 mm below the anchor and 1.8 mm to the left of it,
    /// measuring 0.37 mm of the far blob's metal instead of the trace it was placed on. The port had
    /// not moved; it had never been drawn where it was put.</para>
    ///
    /// <para>Only the port sitting exactly on a box edge escaped it, which is why P1 — at the
    /// polygon's own <c>MaxX</c> — always looked right and nothing else did.</para>
    ///
    /// <para>Null when the anchor is not on the metal in that direction, which is the caller's cue to
    /// fall back to the bounding box exactly as before — an instance, or a shape the flattener
    /// declines, has no outline to walk.</para>
    /// </summary>
    private static long? FaceAlong(LayoutShape shape, LayoutRotation direction, long x, long y)
    {
        var ring = OutlineOf(shape);
        if (ring is null || ring.Length < 6) return null;

        bool alongX  = direction is LayoutRotation.R0 or LayoutRotation.R180;
        bool fromLow = direction is LayoutRotation.R0 or LayoutRotation.R90;

        double at     = alongX ? x : y;   // the coordinate the face is measured in
        double across = alongX ? y : x;   // the line the walk runs along

        var hits = new List<double>();
        int n = ring.Length / 2;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            double ta = alongX ? ring[2 * i + 1] : ring[2 * i];
            double tb = alongX ? ring[2 * j + 1] : ring[2 * j];

            // Half-open in the transverse coordinate, so a vertex exactly on the line counts once.
            if (!((ta <= across && tb > across) || (tb <= across && ta > across))) continue;

            double a = alongX ? ring[2 * i] : ring[2 * i + 1];
            double b = alongX ? ring[2 * j] : ring[2 * j + 1];
            hits.Add(a + (across - ta) / (tb - ta) * (b - a));
        }

        if (hits.Count < 2) return null;
        hits.Sort();

        // ── THE RUN THE PORT IS IN, NOT THE NEAREST CROSSING ──────────────────────────────────
        // Pairs of crossings bound the metal, exactly as SpanAt reads them. Taking the nearest
        // crossing on the naming side instead is wrong for the case that matters most: a port
        // sitting EXACTLY on an end face (which is where a user puts one) has a crossing at distance
        // zero on BOTH sides, so R0 and R180 tie at 0 and the wrong one wins the tie-break. Reading
        // the run gives that port a far face 4,000 DBU away and a near face at 0, which is the whole
        // difference between "this port faces the end" and "this port faces backwards".
        double best = double.MaxValue, lo = 0, hi = 0;
        for (int i = 0; i + 1 < hits.Count; i += 2)
        {
            double a = hits[i], b = hits[i + 1];
            double d = at < a ? a - at : at > b ? at - b : 0;
            if (d >= best) continue;
            best = d; lo = a; hi = b;
        }
        if (best == double.MaxValue) return null;

        return (long)System.Math.Round(fromLow ? lo : hi);
    }

    /// <summary>
    /// Which way a port at <paramref name="x"/>,<paramref name="y"/> faces, from the shape's OWN
    /// outline: the nearest of the four cardinal edges under it, with current flowing away from that
    /// edge into the metal.
    ///
    /// <para>The local counterpart of <see cref="FromBbox"/>, and preferred over it wherever there is
    /// an outline to walk — see <see cref="FaceAlong"/> for the board that made the difference
    /// visible. Falls back to null (and so to the box) when the anchor is not on the metal.</para>
    /// </summary>
    private static LayoutRotation? FromOutline(LayoutShape shape, long x, long y)
    {
        LayoutRotation? best = null;
        double bestD = double.MaxValue;

        foreach (var dir in new[] { LayoutRotation.R0, LayoutRotation.R90, LayoutRotation.R180, LayoutRotation.R270 })
        {
            if (FaceAlong(shape, dir, x, y) is not { } face) continue;
            double d = System.Math.Abs((dir is LayoutRotation.R0 or LayoutRotation.R180 ? x : y) - face);
            if (d >= bestD) continue;
            bestD = d;
            best = dir;
        }
        return best;
    }

    /// <summary>How far off the face a boundary segment may lie and still be part of it. The outline
    /// is flattened at <c>tolDbu: 1</c> and an axis-aligned face's crossing is exact, so this is a
    /// rounding allowance and nothing more.</summary>
    private const long FaceTouchTolDbu = 2;

    /// <summary>
    /// <b>The conductor EDGE the port stands on</b> — the maximal chain of outline segments lying ON
    /// the face, measured across <paramref name="direction"/>, containing the port's own transverse
    /// position. Its length and its centre.
    ///
    /// <para><b>Why this is not <see cref="SpanAt"/> (owner report, 2026-09-09: the port's marks
    /// overlapped artwork above the conductor it names, and should be limited to the edge it stands
    /// on).</b> <see cref="SpanAt"/> cuts a scanline just INSIDE the face and keeps the
    /// contiguous run of METAL it crosses. Where the face is a conductor's end that is the same
    /// answer, which is every case it was written for. It is a different answer wherever the metal
    /// keeps going past the face — a NOTCH. On the reporting board the port sits on a notch's left
    /// wall: the boundary at x = 103.935 mm runs y 41.437 → 42.290 (0.853 mm), but above 42.290 the
    /// metal continues to the RIGHT toward the feed, so a scanline one part-in-a-thousand inside the
    /// wall stays in metal all the way to y = 42.650 and reported 1.213 mm — a bar 42% too long,
    /// centred 0.18 mm above the port, overlapping artwork the port does not touch.</para>
    ///
    /// <para><b>Where both apply they agree</b>, so this is a refinement and not a second opinion: a
    /// rectangle's end face, a taper's narrow end, an isolated feed all have a boundary chain exactly
    /// as long as the metal behind it. Where they differ, the EDGE is what an edge port drives.</para>
    ///
    /// <para>It is also better at something <see cref="SpanAt"/> could not do: two fingers ending on
    /// the SAME face line are two chains, and the port gets the one it is standing on rather than a
    /// scanline's nearest run.</para>
    ///
    /// <para>Null when no boundary lies on the face — an oblique end cut, a curve, or a face that
    /// came from the bounding box rather than from the outline. The caller then falls back to
    /// <see cref="SpanAt"/>, which is what every port had before.</para>
    /// </summary>
    private static (long Width, long Centre)? EdgeAt(
        LayoutShape shape, LayoutRotation direction, long faceAlong, long acrossAt)
    {
        var ring = OutlineOf(shape);
        if (ring is null || ring.Length < 6) return null;

        bool alongX = direction is LayoutRotation.R0 or LayoutRotation.R180;

        var spans = new List<(long Lo, long Hi)>();
        int n = ring.Length / 2;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            long a = alongX ? ring[2 * i] : ring[2 * i + 1];
            long b = alongX ? ring[2 * j] : ring[2 * j + 1];

            // BOTH ends on the face — a segment merely crossing it is not part of it.
            if (System.Math.Abs(a - faceAlong) > FaceTouchTolDbu) continue;
            if (System.Math.Abs(b - faceAlong) > FaceTouchTolDbu) continue;

            long ta = alongX ? ring[2 * i + 1] : ring[2 * i];
            long tb = alongX ? ring[2 * j + 1] : ring[2 * j];
            if (ta == tb) continue;                       // a degenerate segment spans no edge
            spans.Add(ta < tb ? (ta, tb) : (tb, ta));
        }
        if (spans.Count == 0) return null;

        // Chain them: a flattened outline arrives as many short collinear pieces, and an edge broken
        // into fragments would report a fraction of itself.
        spans.Sort(static (p, q) => p.Lo.CompareTo(q.Lo));
        var merged = new List<(long Lo, long Hi)> { spans[0] };
        foreach (var sp in spans.Skip(1))
        {
            var last = merged[^1];
            if (sp.Lo - last.Hi <= FaceTouchTolDbu) merged[^1] = (last.Lo, System.Math.Max(last.Hi, sp.Hi));
            else merged.Add(sp);
        }

        (long Lo, long Hi) best = merged[0];
        long bestD = long.MaxValue;
        foreach (var m in merged)
        {
            long d = acrossAt < m.Lo ? m.Lo - acrossAt : acrossAt > m.Hi ? acrossAt - m.Hi : 0;
            if (d >= bestD) continue;
            bestD = d; best = m;
        }

        long width = best.Hi - best.Lo;
        return width > 0 ? (width, best.Lo + width / 2) : null;
    }

    /// <summary>
    /// <b>The nearest point on the conductor's own boundary</b> — where a port goes when geometry
    /// snap is on and there is no snap FEATURE within tolerance to take it.
    ///
    /// <para><b>Why it ignores the tolerance</b> (owner, 2026-09-09: "when geometry snap is on, the
    /// port should be snapping to the edge for placement and for drags. when snap is off, then port
    /// can be placed anywhere and renders as internal port does"). Ordinary geometry snap is a
    /// tolerance query, so in the middle of a wide piece of metal there is no candidate and the click
    /// stands — which would make the toggle do nothing at all exactly where the two answers differ
    /// most, and leave the user with no way to say "on the edge, please" other than zooming until the
    /// edge came within eight pixels. A port is not an ordinary shape: it belongs on a conductor
    /// boundary or deliberately away from one, and the toggle is the sentence that says which.</para>
    ///
    /// <para>The boundary, not the nearest END face: a corner, a side and an end are all metal edges,
    /// and picking the end would move the port along the trace to somewhere the user was not pointing.
    /// Null for artwork with no outline to walk — an instance's box — where there is nothing to
    /// measure and the click stands.</para>
    /// </summary>
    public static (long X, long Y)? NearestBoundaryPoint(ConductorInfo info, long x, long y)
    {
        if (info.Shape is not { } shape || OutlineOf(shape) is not { Length: >= 6 } xy) return null;

        long bestX = 0, bestY = 0;
        double bestD2 = double.MaxValue;

        int n = xy.Length / 2;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            var (px, py) = NearestOnSegment(xy[2 * i], xy[2 * i + 1], xy[2 * j], xy[2 * j + 1], x, y);
            double dx = px - (double)x, dy = py - (double)y;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; bestX = px; bestY = py; }
        }

        return bestD2 == double.MaxValue ? null : (bestX, bestY);
    }

    /// <summary>The point on segment AB closest to P, clamped to the segment's own ends.</summary>
    private static (long X, long Y) NearestOnSegment(long ax, long ay, long bx, long by, long px, long py)
    {
        double vx = bx - (double)ax, vy = by - (double)ay;
        double len2 = vx * vx + vy * vy;
        if (len2 <= 0) return (ax, ay);

        double t = ((px - (double)ax) * vx + (py - (double)ay) * vy) / len2;
        t = t < 0 ? 0 : t > 1 ? 1 : t;
        return ((long)System.Math.Round(ax + t * vx), (long)System.Math.Round(ay + t * vy));
    }

    /// <summary>The shape's outer ring as a flat x,y array, or null when it has none to give.
    /// A polygon answers directly; anything curved is flattened at a tolerance fine enough that the
    /// span it yields is exact to the DBU.</summary>
    private static long[]? OutlineOf(LayoutShape shape)
    {
        if (shape is PolygonShape p) return p.Xy;
        if (shape is RectShape r)
            return [System.Math.Min(r.X1, r.X2), System.Math.Min(r.Y1, r.Y2),
                    System.Math.Max(r.X1, r.X2), System.Math.Min(r.Y1, r.Y2),
                    System.Math.Max(r.X1, r.X2), System.Math.Max(r.Y1, r.Y2),
                    System.Math.Min(r.X1, r.X2), System.Math.Max(r.Y1, r.Y2)];

        try
        {
            return LayoutFlattenToPolygon.FlattenToPolygon(shape, tolDbu: 1) is PolygonShape f ? f.Xy : null;
        }
        catch (System.Exception ex) when (ex is System.ArgumentException or System.InvalidOperationException)
        {
            // A shape the flattener declines is one there is nothing to measure on. The bounding box
            // is then the honest fallback, and the marker still draws.
            return null;
        }
    }

    /// <summary>
    /// How much metal runs AHEAD of a pin — the same quantity <see cref="LengthAlong"/> reports for a
    /// whole box, measured from the pin's own position instead of from the box's far edge, because a
    /// pin sits ON one end rather than spanning the extent.
    /// </summary>
    public static long LengthAheadOf(Bbox bb, long x, long y, LayoutRotation direction)
    {
        long len = direction switch
        {
            LayoutRotation.R0   => bb.MaxX - x,
            LayoutRotation.R180 => x - bb.MinX,
            LayoutRotation.R90  => bb.MaxY - y,
            _                   => y - bb.MinY,
        };
        return len > 0 ? len : 0;
    }

    /// <summary>
    /// The direction current flows INTO the structure at a pin — the OPPOSITE of the pin's own
    /// outward direction, which points out of the cell. Snapped to the nearest quadrant: a pin whose
    /// outward direction is not axis-aligned has no representable port direction, and rounding is a
    /// better seed than refusing one the user can rotate.
    /// </summary>
    public static LayoutRotation FromPinOutward(double outwardDeg)
    {
        double inward = outwardDeg + 180.0;
        int q = ((int)System.Math.Round(inward / 90.0) % 4 + 4) % 4;
        return q switch
        {
            0 => LayoutRotation.R0,
            1 => LayoutRotation.R90,
            2 => LayoutRotation.R180,
            _ => LayoutRotation.R270,
        };
    }

    /// <summary>The conductor's extent ALONG <paramref name="direction"/> — how far the current has
    /// to run, and therefore how much room the direction arrow actually has.</summary>
    public static long LengthAlong(Bbox bb, LayoutRotation direction) =>
        direction is LayoutRotation.R0 or LayoutRotation.R180
            ? bb.MaxX - bb.MinX
            : bb.MaxY - bb.MinY;

    /// <summary>
    /// The smallest-area shape whose bounding box contains the point, excluding labels and bitmaps
    /// (neither is conductor). Bounding boxes rather than exact containment, deliberately: this
    /// answer only ever SEEDS a direction the user can then rotate, and it is recomputed per frame
    /// for a handful of port labels. Exact geometry is <c>EmPortExtraction</c>'s job, where a wrong
    /// answer is refused rather than merely drawn.
    /// </summary>
    public static Bbox? ConductorUnder(IReadOnlyList<LayoutShape> shapes, long x, long y)
        => ConductorUnderShape(shapes, x, y) is { } s ? LayoutGeometry.BboxOf(s) : null;

    /// <summary>The same search, returning the SHAPE — which is what a width measurement needs and
    /// what a bounding box has already thrown away.</summary>
    public static LayoutShape? ConductorUnderShape(IReadOnlyList<LayoutShape> shapes, long x, long y,
                                                   LayerKey? onLayer = null)
    {
        LayoutShape? best = null;
        double bestArea = double.MaxValue;
        foreach (var s in shapes)
        {
            if (s is LabelShape or BitmapShape) continue;
            if (onLayer is { } want && s.Layer != want) continue;
            var bb = LayoutGeometry.BboxOf(s);
            if (bb.IsEmpty) continue;
            if (x < bb.MinX || x > bb.MaxX || y < bb.MinY || y > bb.MaxY) continue;

            double area = (double)(bb.MaxX - bb.MinX) * (bb.MaxY - bb.MinY);
            if (area < bestArea) { bestArea = area; best = s; }
        }
        return best;
    }

    /// <summary>
    /// Resolve a port label's direction and width.
    ///
    /// <para><b>The width is the CONDUCTOR's, always — the label's own text size never enters into
    /// it</b> (owner report, 2026-08-09: <i>"I made my port Text size 60 and placed it on the edge of
    /// a 42 mil wide MLIN. Now the Port width is saying it's 60. I thought the port width was always
    /// a function of the edge it touches."</i>). Both branches below used to floor the width at
    /// <c>label.Height</c> — a legibility hack for a marker that would otherwise draw thin — and that
    /// floor leaked straight into the number the Properties Inspector reports and into the width bar
    /// the marker draws. A port's width is a property of the metal; making it a function of a font
    /// size means the same artwork reports two different excitation widths depending on how big
    /// someone typed the label. <b>A marker too small to see is a zoom problem, not a data
    /// problem.</b></para>
    ///
    /// <para>An explicit
    /// <see cref="LabelShape.PortDirection"/> is honoured as given (the user pointed it); a null one
    /// is inferred from the artwork, which is what every <c>.clay</c> written before the field
    /// existed carries. Returns null only when the label is not a port at all.
    /// </summary>
    public static PortHint? Resolve(IReadOnlyList<LayoutShape> shapes, LabelShape label) =>
        Resolve(LookupFor(shapes), label);

    /// <summary>
    /// Resolve against an arbitrary conductor source — the form the renderer and the Port tool both
    /// use, so a port sitting on a PCell INSTANCE (which owns no top-level shape at all) resolves
    /// exactly like one sitting on a drawn rectangle.
    /// </summary>
    public static PortHint? Resolve(ConductorLookup? conductorAt, LabelShape label)
    {
        if (!label.IsPort) return null;

        var info = conductorAt?.Invoke(label.X, label.Y, label.PortLayer);

        if (label.PortDirection is { } stated)
        {
            // No conductor found: the direction is still the user's, so the marker is still drawn —
            // but the plane can only be the anchor, and the width is a legible stand-in rather than
            // a measurement. EmPortExtraction refuses such a port by name at run time.
            if (info is not { } si)
                return new PortHint(stated, label.Height * 2, Inferred: false, label.X, label.Y,
                                    LengthDbu: label.Height * 2);

            // A pin's width is measured across the pin's OWN axis, so it only answers the question
            // being asked while the stated direction still agrees with it. A user who rotated the
            // port has overruled the geometry; measuring the box across their chosen axis is then the
            // honest answer, even though it is the coarser one.
            if (si.Pin is { } sp && sp.Direction == stated)
                return new PortHint(stated, sp.WidthDbu, Inferred: false, sp.X, sp.Y,
                                    LengthAheadOf(si.Box, sp.X, sp.Y, stated));

            return Measured(si, stated, label, Inferred: false);
        }

        if (info is not { } inf) return null;

        if (inf.Pin is { } pin)
            return new PortHint(pin.Direction, pin.WidthDbu, Inferred: true, pin.X, pin.Y,
                                LengthAheadOf(inf.Box, pin.X, pin.Y, pin.Direction));

        // DirectionAt, not FromBbox: the SAME inference the Port tool stamps with, which prefers the
        // shape's own outline over its bounding box. Deriving it a second way here is how a placed
        // port came to disagree with its own marker, and it is why a port on one feature of a
        // multi-feature polygon was pointed at a face belonging to a different one.
        return Measured(inf, DirectionAt(inf, label.X, label.Y), label, Inferred: true);
    }

    /// <summary>
    /// A hint whose width and plane centre are MEASURED at the end face when the conductor is a shape
    /// that can be measured, and taken from the bounding box when it is not.
    ///
    /// <para>Both callers used to take the box unconditionally. That is right for a straight run of
    /// metal — the box IS the metal there, to the DBU — and wrong for anything that changes width
    /// along its length, which is most of what an EM port is ever placed on.</para>
    /// </summary>
    private static PortHint Measured(ConductorInfo info, LayoutRotation dir, LabelShape label, bool Inferred)
    {
        var (px, py) = PlaneOf(info.Box, dir);
        long width = WidthAcross(info.Box, dir);
        long length = LengthAlong(info.Box, dir);

        bool alongX = dir is LayoutRotation.R0 or LayoutRotation.R180;
        bool fromLow = dir is LayoutRotation.R0 or LayoutRotation.R90;

        if (info.Shape is { } shape)
        {
            // The LOCAL face, when the outline can give one — the box's own edge otherwise, which is
            // what every port got before and is still exact for a straight run of metal.
            long? face = FaceAlong(shape, dir, label.X, label.Y);
            if (face is { } f)
            {
                if (alongX) px = f; else py = f;
                // How much metal runs ahead of THIS face, rather than the whole box's extent — the
                // arrow is clamped by it (ArrowGeometry), so a port on a short feature of a long
                // polygon no longer draws an arrow sized for the polygon.
                long far = alongX ? (fromLow ? info.Box.MaxX : info.Box.MinX)
                                  : (fromLow ? info.Box.MaxY : info.Box.MinY);
                length = System.Math.Abs(far - f);
            }

            // Cut just INSIDE the face rather than at it: a cut lying along the face's own edge is
            // degenerate (SpanInsetOverLength states why). Measured from the local face when there
            // is one, so the width is the metal the port is standing on.
            long? cut = null;
            if (face is { } ff)
            {
                long inset = System.Math.Max(1, (long)(System.Math.Max(length, 1) * SpanInsetOverLength));
                cut = fromLow ? ff + inset : ff - inset;
            }

            long across = alongX ? label.Y : label.X;

            // The EDGE first — what an edge port actually drives — and the metal behind the face only
            // where there is no boundary on it to measure (an oblique cut, a curve, a box-derived
            // face). The two agree wherever both apply; see EdgeAt for the notch where they do not.
            var span = (face is { } fe ? EdgeAt(shape, dir, fe, across) : null)
                       ?? SpanAt(shape, info.Box, dir, across, cut);

            if (span is { } sp)
            {
                width = sp.Width;
                // The plane's own transverse centre moves with the metal: on an off-centre or curved
                // run the face's midpoint is not the box's midpoint, and a bar centred on the box
                // would sit beside the conductor rather than across it.
                if (alongX) py = sp.Centre; else px = sp.Centre;
            }
        }

        // The metal's two boundaries along this axis, THROUGH the label — the near one is the face
        // the direction names and is already resolved into px/py above; the far one is the same
        // question asked the other way. Measured on the outline where there is one, because a
        // multi-feature polygon's bounding box describes its faces not at all (FaceAlong says why):
        // on a notched polygon the box's ends are 6 mm from the wall the port is standing on.
        long nearFace = alongX ? px : py;
        long farFace  = (info.Shape is { } outline ? FaceAlong(outline, Opposite(dir), label.X, label.Y) : null)
                        ?? (alongX ? (fromLow ? info.Box.MaxX : info.Box.MinX)
                                   : (fromLow ? info.Box.MaxY : info.Box.MinY));

        return new PortHint(dir, width, Inferred, px, py, length,
                            Interior: IsInterior(label, dir, nearFace, farFace, width));
    }

    /// <summary>The reverse of a direction — the other end of the same axis.</summary>
    private static LayoutRotation Opposite(LayoutRotation r) => r switch
    {
        LayoutRotation.R0   => LayoutRotation.R180,
        LayoutRotation.R180 => LayoutRotation.R0,
        LayoutRotation.R90  => LayoutRotation.R270,
        _                   => LayoutRotation.R90,
    };

    /// <summary>
    /// <b>Whether the label stands clear of BOTH ends of the metal, along the axis its direction
    /// runs on</b> — that is, in the middle of a piece of conductor rather than at an end of it.
    ///
    /// <para><b>Both ends, not the one the direction names.</b> A port standing on the low-x face
    /// while pointing R180 is a port the user deliberately rotated to drive current out of that end,
    /// and its bar belongs at the face it names — that is
    /// <c>LayoutPortMoveReseatsDirectionTests</c>' case and it is unchanged. What makes the owner's
    /// port different is not that it disagrees with its direction: it is that there is no end
    /// anywhere near it. Measuring only the named face would have swept the rotated port up with it.</para>
    ///
    /// <para>The tolerance is the metal's OWN size, both ways round, because neither alone is enough:
    /// half the width is the natural reach of an end face and is what a user aiming at one lands
    /// within with geometry snap switched OFF, but on a square pad half the width is the whole shape
    /// and every point would read as an end. A quarter of the metal's extent along the axis bounds it
    /// there. So a port dropped NEAR the end of a trace is still an edge port — it does not have to be
    /// exactly on the face — while a port in the middle of a rectangle is not one, however square the
    /// rectangle is.</para>
    /// </summary>
    private static bool IsInterior(LabelShape label, LayoutRotation dir,
                                   long nearFace, long farFace, long width)
    {
        bool alongX = dir is LayoutRotation.R0 or LayoutRotation.R180;
        long along  = alongX ? label.X : label.Y;
        long span   = System.Math.Abs(farFace - nearFace);

        long tol = System.Math.Max(1, System.Math.Min(width / 2, System.Math.Max(span, 1) / 4));
        return System.Math.Abs(along - nearFace) > tol && System.Math.Abs(along - farFace) > tol;
    }

    /// <summary>
    /// Which direction a port placed at a point should face, from whatever the artwork there says —
    /// the pin's own inward direction when the point names a pin, else the nearest-side inference
    /// from the conductor's box. The Port tool stamps this at placement, so it must be the SAME
    /// answer <see cref="Resolve(ConductorLookup?, LabelShape)"/> would infer; deriving it a second
    /// way is how a placed port comes to disagree with its own marker.
    /// </summary>
    public static LayoutRotation DirectionAt(ConductorInfo info, long x, long y) =>
        info.Pin is { } pin ? pin.Direction
        // The shape's own outline when there is one — the bounding box describes a multi-feature
        // polygon's faces not at all (see FaceAlong). The box remains the answer for an instance,
        // and for anything the flattener declines.
        : info.Shape is { } shape && FromOutline(shape, x, y) is { } local ? local
        : FromBbox(info.Box, x, y);

    /// <summary>What a port should COMMIT to after being moved by <paramref name="dx"/>,
    /// <paramref name="dy"/> — the direction it faces and the conductor layer it measures.
    ///
    /// <para><b>One function, called from two places that must not disagree:</b> the live drag
    /// preview (which draws the answer while the pointer is still down) and the drag's commit (which
    /// writes it). Owner report, 2026-09-09: a port's orientation did not update while it was being
    /// dragged, only once the mouse was released. The rule lived only in the commit
    /// path, so the arrow sat at its old angle for the whole gesture and then snapped round on
    /// release, which is the one moment a user cannot aim with it. Deriving it a second time in the
    /// preview is how the two come to disagree, so there is only one derivation.</para>
    ///
    /// <para><b><paramref name="visibleAt"/> must be a lookup asked with a null <c>onLayer</c></b> —
    /// a move is a GESTURE, so it asks about metal the user can see, and its answer is what re-stamps
    /// <see cref="LabelShape.PortLayer"/>.</para>
    ///
    /// <para><b>An explicit rotation still survives an ordinary nudge.</b> The trigger is not "the
    /// port moved" but "the ARTWORK's own answer under the port changed" — the inference at the old
    /// anchor versus the new one. Sliding a port along the face it already names infers the same
    /// direction at both ends and leaves a user's rotation alone; crossing to a different face is the
    /// case where the stated direction has stopped describing where the port is.</para>
    /// </summary>
    public readonly record struct PortReseat(LayoutRotation? Direction, LayerKey? Layer,
                                             PlanarPortKind? Kind,
                                             bool DirectionChanged, bool LayerChanged, bool KindChanged)
    {
        public bool Changed => DirectionChanged || LayerChanged || KindChanged;
    }

    /// <inheritdoc cref="PortReseat"/>
    public static PortReseat Reseat(ConductorLookup visibleAt, LabelShape port, long dx, long dy)
    {
        var unchanged = new PortReseat(port.PortDirection, port.PortLayer, port.PortKind,
                                       false, false, false);
        if (!port.IsPort) return unchanged;

        // Landed off the metal: nothing under the new anchor has anything to say, so the port keeps
        // what it had rather than being reset to a guess.
        if (visibleAt(port.X + dx, port.Y + dy, null) is not { } after) return unchanged;

        var beforeInfo = visibleAt(port.X, port.Y, null);
        var before = beforeInfo is { } b ? DirectionAt(b, port.X, port.Y) : (LayoutRotation?)null;
        var adopted = DirectionAt(after, port.X + dx, port.Y + dy);

        bool dirChanged = adopted != before && port.PortDirection != adopted;
        var layer = after.Layer;
        bool layerChanged = layer != port.PortLayer;

        // ── AND THE TYPE, ON EXACTLY THE RULE ABOVE ─────────────────────────────────────────────
        //
        // A property the owner named in 2026-09-09: dragging a port across the toggle changed what
        // it drew as, which a stored mode could not have done. Dragging a port out of the middle of a
        // trace onto its end face really does make it an edge port, and that has to keep working now
        // that the type is STORED rather than re-inferred every frame.
        //
        // The trigger is the one Direction already uses: not "the port moved" but "the ARTWORK's own
        // answer under the port CHANGED". Nudging an internal port a few DBU infers Internal at both
        // anchors and leaves an explicit choice — an InternalDeltaGap the user picked, say — alone.
        // Crossing from the interior to an end face is the case where the stored type has stopped
        // describing where the port is.
        var kindBefore = beforeInfo is { } bi ? InferredKind(Measured(bi, before ?? adopted,
                                                                      port, Inferred: true))
                                              : (PlanarPortKind?)null;
        var kindAfter  = InferredKind(Measured(after, adopted,
                                               WithAnchor(port, port.X + dx, port.Y + dy),
                                               Inferred: true));
        bool kindChanged = kindAfter != kindBefore && port.PortKind != kindAfter;

        return new PortReseat(dirChanged ? adopted : port.PortDirection,
                              layerChanged ? layer : port.PortLayer,
                              kindChanged ? kindAfter : port.PortKind,
                              dirChanged, layerChanged, kindChanged);
    }

    /// <summary>The same label at a different anchor — <see cref="Measured"/> reads
    /// <see cref="LabelShape.X"/>/<see cref="LabelShape.Y"/>, so asking it about the DESTINATION of a
    /// drag means asking about a label that is already there. Only the fields that method reads are
    /// carried; it is a question, not a shape anyone keeps.</summary>
    private static LabelShape WithAnchor(LabelShape port, long x, long y) => new()
    {
        Layer = port.Layer, X = x, Y = y, Text = port.Text, Height = port.Height,
        Rotation = port.Rotation, IsPort = port.IsPort, PortDirection = port.PortDirection,
        PortLayer = port.PortLayer, PortKind = port.PortKind, Style = port.Style,
    };

    /// <summary>Top-level shapes only — the cheap form, and all a hand-drawn layout ever needs.
    /// It knows no technology, so it has no visibility to consult; a port's committed layer still
    /// narrows it, which is what keeps an exported picture measuring the same metal the editor
    /// drew.</summary>
    public static ConductorLookup LookupFor(IReadOnlyList<LayoutShape> shapes) =>
        (x, y, onLayer) => ConductorUnderShape(shapes, x, y, onLayer) is { } s
            ? new ConductorInfo(LayoutGeometry.BboxOf(s), null, s)
            : null;

}
