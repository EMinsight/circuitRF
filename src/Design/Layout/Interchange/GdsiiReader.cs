// GDSII stream reader (docs/sonnet-briefs/brief-L4a-gdsii-interchange.md §2). Streams one structure
// at a time — never materializes the whole file (§2.5). Format-specific: touches only bytes and
// records, never CellFolder/Messages/dialogs — that orchestration lives in GdsiiImport.

using System.Text;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>Parsed from the GDSII UNITS record — the user unit and database unit, both in meters.</summary>
public readonly record struct GdsiiUnits(double UserUnitMeters, double DbUnitMeters)
{
    /// <summary>The source file's own DBU-per-micron, for comparison against a destination
    /// <see cref="LayoutView.DbuPerMicron"/> (§2.2).</summary>
    public double SourceDbuPerMicron => 1e-6 / DbUnitMeters;
}

/// <summary>Streams a GDSII library's structures lazily. <see cref="Units"/> is available immediately
/// after <see cref="Open"/> (HEADER/BGNLIB/LIBNAME/UNITS always precede the first structure).</summary>
public sealed class GdsiiReader
{
    /// <summary>GDSII has no native text-height record; this codebase's own writer always emits a
    /// WIDTH record on TEXT to carry <c>LabelShape.Height</c> (a documented, valid use of WIDTH per
    /// the spec). A third-party file lacking it falls back to this constant.</summary>
    public const long DefaultTextHeightDbu = 1000;

    /// <summary>The most single instances <see cref="ReadStructures"/> will create across one file by
    /// expanding AREFs whose lattice is not axis-aligned (brief-gdsii-native-fixes.md §3a). Above it
    /// the read throws, and because <see cref="GdsiiImport"/> reads every structure before it creates
    /// anything, nothing is created.</summary>
    public const int MaxExpandedInstances = 100_000;

    /// <summary>D3: the GDSII property a circuitRF port label carries on its TEXT element. TEXTTYPE is
    /// the label's datatype (D1), so the port flag travels as a property the format lets any other
    /// reader ignore.</summary>
    public const short PortPropertyAttribute = 126;
    public const string PortPropertyValue = "circuitrf:port";

    private readonly GdsiiRecordReader _records;
    private readonly List<string> _diagnostics = [];
    private bool _bgnStrAlreadyConsumed;
    private int _expandedInstances, _boxCount, _nodeCount;

    public GdsiiUnits Units { get; private set; }

    /// <summary>Notes accumulated while reading (non-standard PATHTYPE 4 extensions, AREFs placed as
    /// separate instances, BOX and NODE counts) — read after fully enumerating
    /// <see cref="ReadStructures"/>, since the per-file counts are added when it ends.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    private GdsiiReader(Stream stream) => _records = new GdsiiRecordReader(stream);

    public static GdsiiReader Open(Stream stream)
    {
        var reader = new GdsiiReader(stream);
        reader.ReadPreamble();
        return reader;
    }

    private void ReadPreamble()
    {
        double userUnit = 1e-6, dbUnit = 1e-9;
        while (_records.TryReadNext(out var rec))
        {
            if (rec.Type == GdsiiRecordType.Units)
            {
                // The spec's first real is the database unit IN USER UNITS (0.001 for 1 nm in µm), the
                // second the database unit in metres — so the user unit in metres is their quotient.
                var v = rec.AsReal8Array();
                if (v.Length < 2 || !(v[0] > 0) || !(v[1] > 0))
                    throw new InvalidDataException("GDSII UNITS record must hold two positive reals.");
                userUnit = v[1] / v[0];
                dbUnit = v[1];
            }
            else if (rec.Type == GdsiiRecordType.BgnStr)
            {
                _bgnStrAlreadyConsumed = true;
                break;
            }
            else if (rec.Type == GdsiiRecordType.EndLib)
            {
                break;
            }
        }
        Units = new GdsiiUnits(userUnit, dbUnit);
    }

    public IEnumerable<InterchangeStructure> ReadStructures()
    {
        while (true)
        {
            if (!_bgnStrAlreadyConsumed)
            {
                if (!_records.TryReadNext(out var rec) || rec.Type == GdsiiRecordType.EndLib)
                {
                    AddFileCounts();
                    yield break;
                }
                if (rec.Type != GdsiiRecordType.BgnStr) continue;
            }
            _bgnStrAlreadyConsumed = false;
            yield return ReadOneStructure();
        }
    }

    /// <summary>D6: one line per file for each kind of element read in an unusual way, not one per
    /// element — a file of BOXes would otherwise bury every other message.</summary>
    private void AddFileCounts()
    {
        if (_boxCount > 0)
            _diagnostics.Add($"{_boxCount} BOX element(s) read as polygons.");
        if (_nodeCount > 0)
            _diagnostics.Add($"{_nodeCount} NODE element(s) skipped: they carry connectivity, not artwork.");
        _boxCount = _nodeCount = 0;
    }

    private InterchangeStructure ReadOneStructure()
    {
        string name = "";
        var shapes = new List<LayoutShape>();
        var instances = new List<LayoutInstance>();
        var expandedByCell = new Dictionary<string, int>(StringComparer.Ordinal);

        while (_records.TryReadNext(out var rec))
        {
            switch (rec.Type)
            {
                case GdsiiRecordType.StrName: name = rec.AsAscii(); break;
                case GdsiiRecordType.EndStr:
                    foreach (var (cell, n) in expandedByCell)
                        _diagnostics.Add($"AREF \"{cell}\": its lattice is not axis-aligned; placed as {n} separate instances.");
                    return new InterchangeStructure(name, shapes, instances);
                case GdsiiRecordType.Boundary: shapes.Add(ReadBoundary()); break;
                case GdsiiRecordType.Box: shapes.Add(ReadBox()); break;
                case GdsiiRecordType.Node: SkipElement(); _nodeCount++; break;
                case GdsiiRecordType.Path: shapes.Add(ReadPath()); break;
                case GdsiiRecordType.Text: shapes.Add(ReadText()); break;
                case GdsiiRecordType.SRef: ReadRef(isArray: false, instances, expandedByCell); break;
                case GdsiiRecordType.ARef: ReadRef(isArray: true, instances, expandedByCell); break;
                default: break; // BGNSTR sub-fields, unsupported/unknown records — ignore, forward-compat
            }
        }
        throw new InvalidDataException("GDSII structure is missing its ENDSTR record.");
    }

    private PolygonShape ReadBoundary()
    {
        int layer = 0, datatype = 0;
        long[] xy = [];
        while (_records.TryReadNext(out var rec))
        {
            switch (rec.Type)
            {
                case GdsiiRecordType.Layer: layer = Unsigned16(rec); break;
                case GdsiiRecordType.Datatype: datatype = Unsigned16(rec); break;
                case GdsiiRecordType.Xy: xy = ToLongPairs(rec.AsInt32Array()); break;
                case GdsiiRecordType.EndEl:
                    return new PolygonShape { Layer = new LayerKey(layer, datatype), Xy = DropClosingDuplicate(xy) };
            }
        }
        throw new InvalidDataException("GDSII BOUNDARY is missing its ENDEL record.");
    }

    /// <summary>D6: a BOX is a closed five-point outline by definition, so it is a polygon on
    /// <c>(LAYER, BOXTYPE)</c> — the BOXTYPE playing DATATYPE's part, as gdstk reads it.</summary>
    private PolygonShape ReadBox()
    {
        int layer = 0, boxType = 0;
        long[] xy = [];
        while (_records.TryReadNext(out var rec))
        {
            switch (rec.Type)
            {
                case GdsiiRecordType.Layer: layer = Unsigned16(rec); break;
                case GdsiiRecordType.BoxType: boxType = Unsigned16(rec); break;
                case GdsiiRecordType.Xy: xy = ToLongPairs(rec.AsInt32Array()); break;
                case GdsiiRecordType.EndEl:
                    _boxCount++;
                    return new PolygonShape { Layer = new LayerKey(layer, boxType), Xy = DropClosingDuplicate(xy) };
            }
        }
        throw new InvalidDataException("GDSII BOX is missing its ENDEL record.");
    }

    private void SkipElement()
    {
        while (_records.TryReadNext(out var rec))
            if (rec.Type == GdsiiRecordType.EndEl) return;
        throw new InvalidDataException("GDSII NODE is missing its ENDEL record.");
    }

    private PathShape ReadPath()
    {
        int layer = 0, datatype = 0;
        long width = 0;
        int pathType = 0;
        long bgnExtn = 0, endExtn = 0;
        long[] xy = [];
        while (_records.TryReadNext(out var rec))
        {
            switch (rec.Type)
            {
                case GdsiiRecordType.Layer: layer = Unsigned16(rec); break;
                case GdsiiRecordType.Datatype: datatype = Unsigned16(rec); break;
                case GdsiiRecordType.Width: width = Math.Abs(rec.AsInt32Array()[0]); break;
                case GdsiiRecordType.PathType: pathType = rec.AsInt16Array()[0]; break;
                case GdsiiRecordType.BgnExtn: bgnExtn = rec.AsInt32Array()[0]; break;
                case GdsiiRecordType.EndExtn: endExtn = rec.AsInt32Array()[0]; break;
                case GdsiiRecordType.Xy: xy = ToLongPairs(rec.AsInt32Array()); break;
                case GdsiiRecordType.EndEl:
                    var end = PathEndOf(pathType, width, bgnExtn, endExtn);
                    return new PathShape { Layer = new LayerKey(layer, datatype), Xy = xy, Width = width, End = end };
            }
        }
        throw new InvalidDataException("GDSII PATH is missing its ENDEL record.");
    }

    /// <summary>PATHTYPE 0/1/2 map directly; PATHTYPE 4 maps to <see cref="PathEndStyle.Extended"/>
    /// (our model has no configurable extension length) — exact when both extensions equal
    /// <c>Width/2</c> (our own writer's convention), reported as an approximation otherwise.</summary>
    private PathEndStyle PathEndOf(int pathType, long width, long bgnExtn, long endExtn)
    {
        switch (pathType)
        {
            case 0: return PathEndStyle.Flush;
            case 1: return PathEndStyle.Round;
            case 2: return PathEndStyle.Square;
            case 4:
                long expected = width / 2;
                if (bgnExtn != expected || endExtn != expected)
                    _diagnostics.Add(
                        $"PATH with PATHTYPE 4 and non-standard BGNEXTN/ENDEXTN ({bgnExtn}/{endExtn}, " +
                        $"expected {expected}) approximated as Extended.");
                return PathEndStyle.Extended;
            default:
                _diagnostics.Add($"PATH with unrecognized PATHTYPE {pathType} approximated as Flush.");
                return PathEndStyle.Flush;
        }
    }

    private LabelShape ReadText()
    {
        int layer = 0;
        int textType = 0;
        double angle = 0, mag = 1.0;
        bool reflect = false, isPort = false;
        long width = DefaultTextHeightDbu;
        long x = 0, y = 0;
        string text = "";
        short propAttr = 0;
        while (_records.TryReadNext(out var rec))
        {
            switch (rec.Type)
            {
                case GdsiiRecordType.Layer: layer = Unsigned16(rec); break;
                case GdsiiRecordType.TextType: textType = Unsigned16(rec); break;
                case GdsiiRecordType.Strans: reflect = (rec.AsInt16Array()[0] & 0xFFFF & 0x8000) != 0; break;
                case GdsiiRecordType.Mag: mag = rec.AsReal8Array()[0]; break;
                case GdsiiRecordType.Angle: angle = rec.AsReal8Array()[0]; break;
                case GdsiiRecordType.PropAttr: propAttr = rec.AsInt16Array()[0]; break;
                case GdsiiRecordType.PropValue:
                    if (propAttr == PortPropertyAttribute && rec.AsAscii() == PortPropertyValue) isPort = true;
                    break;
                case GdsiiRecordType.Width: width = Math.Abs(rec.AsInt32Array()[0]); break;
                case GdsiiRecordType.Xy:
                    var pts = rec.AsInt32Array();
                    x = pts[0]; y = pts[1];
                    break;
                case GdsiiRecordType.StringRec: text = rec.AsAscii(); break;
                case GdsiiRecordType.EndEl:
                    // Reflection on TEXT is not represented in LabelShape (no MirrorX field there) —
                    // a documented, minor limitation; only the rotation angle carries through.
                    if (reflect)
                        _diagnostics.Add($"TEXT \"{text}\" reflection flag ignored (labels carry rotation only).");
                    // A label's angle is carried exactly, like an instance's — LabelShape.RotationDegrees
                    // was widened past the cardinals on 2026-08-25, so the snap this path used to apply
                    // (and report) is gone along with the codec's own, R-L3d-8.
                    var (_, textDeg) = GdsiiTransformCodec.FromGdsii(false, angle);
                    // D1: TEXTTYPE is the label's datatype. D3/D4: the port flag comes from the property
                    // alone, so a TEXTTYPE 1 label from any writer is an ordinary label on datatype 1.
                    // D5: MAG scales the drawn height, which is what LabelShape.Height holds.
                    return new LabelShape
                    {
                        Layer = new LayerKey(layer, textType),
                        X = x, Y = y, Text = text,
                        Height = (long)Math.Round(width * Math.Abs(mag), MidpointRounding.AwayFromZero),
                        RotationDegrees = textDeg, IsPort = isPort,
                    };
            }
        }
        throw new InvalidDataException("GDSII TEXT is missing its ENDEL record.");
    }

    /// <summary>Reads one SREF or AREF into <paramref name="instances"/> — one instance, or for an AREF
    /// whose lattice our array model cannot hold, one per lattice point (D2), counted per referenced
    /// cell in <paramref name="expandedByCell"/> for the structure's single message.</summary>
    private void ReadRef(bool isArray, List<LayoutInstance> instances, Dictionary<string, int> expandedByCell)
    {
        string sname = "";
        bool reflect = false;
        double mag = 1.0, angle = 0.0;
        int cols = 1, rows = 1;
        long[] xy = [];
        while (_records.TryReadNext(out var rec))
        {
            switch (rec.Type)
            {
                case GdsiiRecordType.SName: sname = rec.AsAscii(); break;
                case GdsiiRecordType.Strans: reflect = (rec.AsInt16Array()[0] & 0xFFFF & 0x8000) != 0; break;
                case GdsiiRecordType.Mag: mag = rec.AsReal8Array()[0]; break;
                case GdsiiRecordType.Angle: angle = rec.AsReal8Array()[0]; break;
                case GdsiiRecordType.ColRow:
                    var cr = rec.AsInt16Array();
                    cols = cr[0]; rows = cr[1];
                    break;
                case GdsiiRecordType.Xy: xy = ToLongPairs(rec.AsInt32Array()); break;
                case GdsiiRecordType.EndEl:
                    // R-L3d-8: no snap, no loss report — an instance carries the file's own angle.
                    var (mirrorX, rotDeg) = GdsiiTransformCodec.FromGdsii(reflect, angle);
                    LayoutInstance At(long x, long y, int r = 1, int c = 1, long px = 0, long py = 0) => new()
                    {
                        CellRef = sname, // resolved to a real relative path by GdsiiImport
                        X = x, Y = y,
                        RotationDegrees = rotDeg, MirrorX = mirrorX, Mag = mag,
                        Rows = r, Cols = c, PitchX = px, PitchY = py,
                    };

                    if (!isArray)
                    {
                        if (xy.Length < 2) throw new InvalidDataException($"GDSII SREF \"{sname}\" has no XY point.");
                        instances.Add(At(xy[0], xy[1]));
                        return;
                    }
                    if (cols <= 0 || rows <= 0)
                        throw new InvalidDataException($"GDSII AREF \"{sname}\" has a non-positive COLROW count ({cols} × {rows}).");
                    if (xy.Length < 6)
                        throw new InvalidDataException($"GDSII AREF \"{sname}\" does not hold its three XY points.");
                    ReadLattice(sname, cols, rows, xy, At, instances, expandedByCell);
                    return;
            }
        }
        throw new InvalidDataException($"GDSII {(isArray ? "AREF" : "SREF")} is missing its ENDEL record.");
    }

    /// <summary>D2. An AREF's three points — origin, column reference, row reference — are absolute
    /// coordinates, so the lattice is <c>P0 + c·vc + r·vr</c> with <c>vc = (Pc − P0)/cols</c> and
    /// <c>vr = (Pr − P0)/rows</c>. Our array model keeps its pitch in the parent's unrotated frame
    /// (<see cref="LayoutInstanceTransform.ArrayCellOrigin"/>), so an axis-aligned lattice in EITHER
    /// orientation is one array, exactly — columns along y is how other writers spell a 90°/270° array,
    /// and it is the same lattice with columns and rows named the other way. Any other lattice cannot
    /// be one array, but it can be N instances exactly; approximating it is never the answer.</summary>
    private void ReadLattice(
        string sname, int cols, int rows, long[] xy,
        Func<long, long, int, int, long, long, LayoutInstance> at,
        List<LayoutInstance> instances, Dictionary<string, int> expandedByCell)
    {
        long x0 = xy[0], y0 = xy[1];
        long dcx = xy[2] - x0, dcy = xy[3] - y0;
        long drx = xy[4] - x0, dry = xy[5] - y0;

        bool exact = dcx % cols == 0 && dcy % cols == 0 && drx % rows == 0 && dry % rows == 0;
        if (exact)
        {
            long vcx = dcx / cols, vcy = dcy / cols, vrx = drx / rows, vry = dry / rows;

            // A count of 1 leaves its vector unused, and writers disagree on what they put there.
            if (cols == 1) { vcx = 0; vcy = 0; }
            if (rows == 1) { vrx = 0; vry = 0; }

            if (vcy == 0 && vrx == 0) { instances.Add(at(x0, y0, rows, cols, vcx, vry)); return; }
            if (vcx == 0 && vry == 0) { instances.Add(at(x0, y0, cols, rows, vrx, vcy)); return; }
        }

        long n = (long)cols * rows;
        if (_expandedInstances + n > MaxExpandedInstances)
            throw new InvalidDataException(
                $"GDSII AREF \"{sname}\" would bring the instances placed one by one to {_expandedInstances + n}, " +
                $"above the limit of {MaxExpandedInstances}.");
        _expandedInstances += (int)n;

        if (!exact)
            _diagnostics.Add(
                $"AREF \"{sname}\": its column or row reference point is not a whole number of pitches from " +
                "its origin; each instance is placed at the nearest database unit.");
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                instances.Add(at(
                    x0 + RoundDiv((long)c * dcx, cols) + RoundDiv((long)r * drx, rows),
                    y0 + RoundDiv((long)c * dcy, cols) + RoundDiv((long)r * dry, rows),
                    1, 1, 0, 0));
        expandedByCell[sname] = expandedByCell.GetValueOrDefault(sname) + (int)n;
    }

    private static long RoundDiv(long num, long den) =>
        (long)Math.Round((double)num / den, MidpointRounding.AwayFromZero);

    /// <summary>D7: LAYER, DATATYPE, TEXTTYPE and BOXTYPE are unsigned 16-bit, as every common reader
    /// takes them — read signed, 40000 is −25536, a different layer.</summary>
    private static int Unsigned16(GdsiiRecord rec) => (ushort)rec.AsInt16Array()[0];

    private static long[] ToLongPairs(int[] flat)
    {
        var result = new long[flat.Length];
        for (int i = 0; i < flat.Length; i++) result[i] = flat[i];
        return result;
    }

    /// <summary>BOUNDARY's XY explicitly repeats the first point as the last (§2.1 item 3); our own
    /// <c>Xy</c> convention is implicitly closed and never repeats it.</summary>
    private static long[] DropClosingDuplicate(long[] xy)
    {
        if (xy.Length < 4) return xy;
        int last = xy.Length - 2;
        if (xy[0] == xy[last] && xy[1] == xy[last + 1])
            return xy[..last];
        return xy;
    }
}
