// gdstk's elements ↔ circuitRF's (brief-oasis-gdstk.md §6c, R-oas-2c). Import maps a GdstkCell to an
// InterchangeStructure for StreamLayoutImport — the function circuitRF's own GDSII reader feeds too — and
// export maps the elements StreamLowering produced to a GdstkCell for the worker. Wherever the native
// reader (GdsiiReader) already decides a case, this decides it the same way and says so in the same words,
// so the two routes disagree only where gdstk delivers something different, never because circuitRF
// interprets the same thing twice.

namespace CircuitRF.Design.Layout.Interchange.Gdstk;

/// <summary>
/// The mapping, both directions. Each row names the native reader's or writer's decision it follows.
/// <code>
/// gdstk                                   circuitRF (import)                       notes
/// ─────────────────────────────────────── ──────────────────────────────────────── ──────────────────────────────────
/// polygon                                 PolygonShape (never RectShape)           GdsiiReader reads every BOUNDARY,
///                                                                                  rectangles included, as a polygon.
///                                                                                  A CIRCLE's vertices stay a polygon.
/// path, end flush / round / halfwidth     PathShape Flush / Round / Square         PATHTYPE 0 / 1 / 2.
/// path, end extended, both = width/2      PathShape Extended                       exact: PATHTYPE 4, our convention.
/// path, end extended, any other length    PathShape Extended, one message each     GdsiiReader's approximation and its
///                                                                                  words (§6c asked for a polygon; see
///                                                                                  src/Design/RESOLVED.md, R-oas-2).
/// path, end smooth / function             PathShape Flush, one message each        gdstk-built paths only.
/// label                                   LabelShape on (layer, texttype)          D1: text type is the datatype.
///   rotation                                RotationDegrees, exact                 R-L3d-8; snapped to 1e-9° to undo
///                                                                                  gdstk's radians round trip.
///   magnification                           Height = 1000 × |magnification|        D5: GdsiiReader's height with no
///                                                                                  WIDTH record (gdstk writes none).
///   mirror                                  dropped, one message each              GdsiiReader's words.
///   properties (the port flag)              not read; counted                      the worker sends a count only.
/// reference                               LayoutInstance                           mirror then rotate, as GDSII.
///   rectangular repetition                  Rows / Cols / PitchX / PitchY          a count of 1 zeroes its pitch.
///   regular, axis-aligned either way        one array (columns along y swap)      GdsiiReader.ReadLattice.
///   any other repetition                    one instance per copy, one message     under MaxExpanded, else refused.
/// any shape's repetition (OASIS)          one shape per copy, counted              under MaxExpanded, else refused.
/// properties, robust paths, off-grid      not imported / rounded, one message each the cell reply's notes.
///
/// circuitRF (export, after StreamLowering)  gdstk
/// ───────────────────────────────────────── ──────────────────────────────────────────────────────────────────────
/// StreamPolygon                             polygon (its ring as lowered: holes keyholed, curves flattened)
/// StreamPath Flush/Round/Square/Extended    flush / round / halfwidth / extended (Width/2, Width/2)
/// StreamLabel                               label, anchor NW (no PRESENTATION bits, as our writer writes none),
///                                           magnification Height / 1000 (gdstk's TEXT carries no WIDTH, so this is
///                                           the one place the height can travel; GdsiiReader inverts it, D5);
///                                           the port flag is dropped and counted (the worker writes no properties)
/// StreamReference                           reference; an array is a rectangular repetition, pitch unrotated
/// </code>
/// </summary>
public static class GdstkMapping
{
    /// <summary>The most single instances or shapes one import creates by expanding repetitions. The native
    /// reader's limit (<see cref="GdsiiReader.MaxExpandedInstances"/>), shared so the two routes refuse the
    /// same file; R-oas-4c sets OASIS's own limit in G4.</summary>
    public const int MaxExpanded = GdsiiReader.MaxExpandedInstances;

    /// <summary>The height a label carries per unit of magnification — the native reader's default
    /// text height, which is what it multiplies a TEXT's MAG by when the TEXT has no WIDTH.</summary>
    public const double HeightPerMagnification = GdsiiReader.DefaultTextHeightDbu;

    // ── Import ─────────────────────────────────────────────────────────────────

    /// <summary>Accumulates what one file's import says, in the native reader's words, so a file of many
    /// elements read in an unusual way gives one line per kind rather than one per element.</summary>
    public sealed class ImportNotes(GdstkFormat format)
    {
        internal readonly GdstkFormat Format = format;
        internal readonly List<string> Messages = [];
        internal long Expanded, Properties, RobustPaths, MultiElementPaths, OffGrid, ShapesExpanded;
        internal int CellProperties;

        /// <summary>The messages, with the per-file counts appended. <paramref name="libraryProperties"/>:
        /// the library itself carried properties.</summary>
        public IReadOnlyList<string> Finish(bool libraryProperties)
        {
            var all = new List<string>(Messages);
            long props = Properties + CellProperties + (libraryProperties ? 1 : 0);
            if (props > 0)
                all.Add($"{props} propert{(props == 1 ? "y" : "ies")} not imported: circuitRF keeps no element, cell or library properties.");
            if (ShapesExpanded > 0)
                all.Add($"{ShapesExpanded} shape(s) placed one by one from repetitions.");
            if (RobustPaths > 0)
                all.Add($"{RobustPaths} robust path(s) skipped: gdstk holds them in a form circuitRF does not read.");
            if (MultiElementPaths > 0)
                all.Add($"{MultiElementPaths} multi-element path(s) read as one path per element.");
            if (OffGrid > 0)
                all.Add($"{OffGrid} coordinate(s) were off the file's grid and rounded to it.");
            return all;
        }
    }

    public static InterchangeStructure ToInterchange(GdstkCell cell, ImportNotes notes)
    {
        var shapes = new List<LayoutShape>();
        var instances = new List<LayoutInstance>();
        var expandedByCell = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var p in cell.Polygons)
            foreach (var (dx, dy) in Copies(p.Repetition, notes))
                shapes.Add(new PolygonShape { Layer = new LayerKey(p.Layer, p.Datatype), Xy = Shift(p.Xy, dx, dy) });

        foreach (var p in cell.Paths)
        {
            var end = PathEndOf(p, notes);
            foreach (var (dx, dy) in Copies(p.Repetition, notes))
                shapes.Add(new PathShape { Layer = new LayerKey(p.Layer, p.Datatype), Xy = Shift(p.Xy, dx, dy), Width = Math.Abs(p.Width), End = end });
        }

        foreach (var l in cell.Labels)
        {
            // Reflection on TEXT is not represented in LabelShape — GdsiiReader's limitation and its words.
            if (l.Mirror)
                notes.Messages.Add($"TEXT \"{l.Text}\" reflection flag ignored (labels carry rotation only).");
            var (_, deg) = GdsiiTransformCodec.FromGdsii(false, Snap(l.RotationDegrees));
            long height = (long)Math.Round(HeightPerMagnification * Math.Abs(l.Magnification), MidpointRounding.AwayFromZero);
            foreach (var (dx, dy) in Copies(l.Repetition, notes))
                shapes.Add(new LabelShape
                {
                    Layer = new LayerKey(l.Layer, l.TextType), X = l.X + dx, Y = l.Y + dy, Text = l.Text,
                    Height = height, RotationDegrees = deg,
                });
        }

        foreach (var r in cell.References)
            AddReference(r, instances, expandedByCell, notes);

        foreach (var (target, n) in expandedByCell)
            notes.Messages.Add(notes.Format == GdstkFormat.Gdsii
                ? $"AREF \"{target}\": its lattice is not axis-aligned; placed as {n} separate instances."
                : $"Repeated placement of \"{target}\" is not an orthogonal array; placed as {n} separate instances.");

        notes.Properties += cell.Notes.ElementsWithProperties;
        notes.CellProperties += cell.Notes.CellProperties ? 1 : 0;
        notes.RobustPaths += cell.Notes.RobustPaths;
        notes.MultiElementPaths += cell.Notes.MultiElementPaths;
        notes.OffGrid += cell.Notes.OffGridRounded;

        return new InterchangeStructure(cell.Name, shapes, instances);
    }

    /// <summary>GdsiiReader.PathEndOf's decisions and words, over gdstk's end names.</summary>
    private static PathEndStyle PathEndOf(GdstkPath p, ImportNotes notes)
    {
        switch (p.End)
        {
            case "flush": return PathEndStyle.Flush;
            case "round": return PathEndStyle.Round;
            case "halfwidth": return PathEndStyle.Square;
            case "extended":
                long expected = Math.Abs(p.Width) / 2;
                if (p.ExtensionStart != expected || p.ExtensionEnd != expected)
                    notes.Messages.Add(notes.Format == GdstkFormat.Gdsii
                        ? $"PATH with PATHTYPE 4 and non-standard BGNEXTN/ENDEXTN ({p.ExtensionStart}/{p.ExtensionEnd}, " +
                          $"expected {expected}) approximated as Extended."
                        : $"Path with extensions {p.ExtensionStart}/{p.ExtensionEnd} (expected {expected}) approximated as Extended.");
                return PathEndStyle.Extended;
            default:
                notes.Messages.Add($"Path with end \"{p.End}\" approximated as Flush.");
                return PathEndStyle.Flush;
        }
    }

    private static void AddReference(GdstkReference r, List<LayoutInstance> into, Dictionary<string, int> expandedByCell, ImportNotes notes)
    {
        var (mirrorX, deg) = GdsiiTransformCodec.FromGdsii(r.Mirror, Snap(r.RotationDegrees));
        LayoutInstance At(long x, long y, int rows = 1, int cols = 1, long px = 0, long py = 0) => new()
        {
            CellRef = r.Cell, // resolved to a real relative path by StreamLayoutImport
            X = x, Y = y, RotationDegrees = deg, MirrorX = mirrorX, Mag = r.Magnification,
            Rows = rows, Cols = cols, PitchX = px, PitchY = py,
        };

        var rep = r.Repetition;
        if (rep is null || rep.Count <= 1)
        {
            into.Add(At(r.X, r.Y));
            return;
        }

        if (rep.Kind is "rectangular" or "regular" && rep.Columns <= int.MaxValue && rep.Rows <= int.MaxValue)
        {
            int cols = (int)rep.Columns, rows = (int)rep.Rows;
            // A count of 1 leaves its vector unused, and writers disagree on what they put there.
            long v1x = cols == 1 ? 0 : rep.V1X, v1y = cols == 1 || rep.Kind == "rectangular" ? 0 : rep.V1Y;
            long v2x = rows == 1 || rep.Kind == "rectangular" ? 0 : rep.V2X, v2y = rows == 1 ? 0 : rep.V2Y;
            if (v1y == 0 && v2x == 0) { into.Add(At(r.X, r.Y, rows, cols, v1x, v2y)); return; }
            // Columns along y: the same lattice with columns and rows named the other way (ReadLattice).
            if (v1x == 0 && v2y == 0) { into.Add(At(r.X, r.Y, cols, rows, v2x, v1y)); return; }
        }

        int n = 0;
        foreach (var (dx, dy) in Copies(rep, notes, counted: false))
        {
            into.Add(At(r.X + dx, r.Y + dy));
            n++;
        }
        notes.Expanded += n;
        expandedByCell[r.Cell] = expandedByCell.GetValueOrDefault(r.Cell) + n;
    }

    /// <summary>Every copy's displacement — just (0, 0) for an element with no repetition — under
    /// <see cref="MaxExpanded"/>. Above it the read throws, and because the import reads every cell before
    /// it creates anything, nothing is created.</summary>
    private static IReadOnlyList<(long X, long Y)> Copies(GdstkRepetition? rep, ImportNotes notes, bool counted = true)
    {
        if (rep is null || rep.Count <= 1) return [(0, 0)];
        long n = rep.Count;
        long total = notes.Expanded + notes.ShapesExpanded + n;
        if (total > MaxExpanded)
            throw new GdstkException(GdstkFailure.Refused, GdstkDiagnostics.ExpansionLimit(n, total, MaxExpanded), "import.expansion-limit");
        if (counted) notes.ShapesExpanded += n;
        return rep.Offsets().ToList();
    }

    private static long[] Shift(long[] xy, long dx, long dy)
    {
        if (dx == 0 && dy == 0) return xy;
        var r = new long[xy.Length];
        for (int i = 0; i < xy.Length; i += 2) { r[i] = xy[i] + dx; r[i + 1] = xy[i + 1] + dy; }
        return r;
    }

    /// <summary>gdstk holds an angle in radians and the worker sends it back in degrees, so a GDSII ANGLE of
    /// 30 returns as 29.999999999999996. Nine decimal places is far below any angle a file states and far
    /// above that noise.</summary>
    private static double Snap(double degrees) => Math.Round(degrees, 9);

    // ── Export ─────────────────────────────────────────────────────────────────

    /// <summary>The lowered structure as a gdstk cell. <paramref name="portLabelsDropped"/> counts the port
    /// labels written as plain text, since the worker writes no properties.</summary>
    public static GdstkCell FromStream(StreamStructure s, ref int portLabelsDropped)
    {
        var polygons = new List<GdstkPolygon>();
        var paths = new List<GdstkPath>();
        var labels = new List<GdstkLabel>();
        foreach (var element in s.Shapes)
        {
            switch (element)
            {
                case StreamPolygon p:
                    polygons.Add(new GdstkPolygon(p.Layer.Layer, p.Layer.Datatype, p.Xy));
                    break;
                case StreamPath p:
                    string end = p.End switch
                    {
                        PathEndStyle.Round => "round",
                        PathEndStyle.Square => "halfwidth",
                        PathEndStyle.Extended => "extended",
                        _ => "flush",
                    };
                    paths.Add(new GdstkPath(p.Layer.Layer, p.Layer.Datatype, p.Width, end, p.Extension, p.Extension, p.Xy));
                    break;
                case StreamLabel l:
                    if (l.IsPort) portLabelsDropped++;
                    labels.Add(new GdstkLabel(l.Layer.Layer, l.Layer.Datatype, l.Text, l.X, l.Y, Anchor: 0,
                                              l.AngleDegrees, l.Height / HeightPerMagnification, Mirror: false));
                    break;
            }
        }
        var refs = s.References.Select(r => new GdstkReference(
            r.Cell, r.X, r.Y, r.AngleDegrees, r.Mag, r.Reflect,
            r.IsArray ? new GdstkRepetition("rectangular", r.Cols, r.Rows, V1X: r.PitchX, V2Y: r.PitchY) : null)).ToList();
        return new GdstkCell(s.Name, polygons, paths, labels, refs, GdstkCellNotes.None);
    }
}
