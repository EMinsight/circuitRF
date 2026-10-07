// GDSII stream writer (docs/sonnet-briefs/brief-L4a-gdsii-interchange.md §2). Streaming write, one
// BGNSTR…ENDSTR per structure — hierarchy is preserved (no flattening of instances), only curved
// primitives are flattened (§3.2 R9e) and holes are keyholed (§3.1a). Format-specific: touches only
// bytes and records; the caller (GdsiiExport) supplies already-mangled structure names and already
// rebased CellRef values — this file has no CellFolder/Messages/dialog concerns.
//
// The flattening, keyholing, via pads and placeholder renumbering are StreamLowering's (brief-oasis-gdstk
// §6b): this file serialises the elements it produces, and the gdstk route sends the same elements to
// its worker.

using System.Linq;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>What actually happened during a write — the SAME counters <see cref="GdsiiExport.Analyze"/>
/// reports in the pre-flight fidelity dialog, produced by the identical code path (a dry run into
/// <see cref="Stream.Null"/>) so the preview can never disagree with the real write.</summary>
public sealed record GdsiiExportSummary(
    int CurvedShapesFlattened,
    int HolesKeyholed,
    int BitmapsSkipped,
    IReadOnlyList<string> Diagnostics,
    /// <summary>brief-layout-testing-fixes.md item 6/R-fix-5: the number of TEXT records written —
    /// text a user did not knowingly place (an invisible, sub-pixel label authored by accident) is
    /// exactly what an export report should surface, never leave silent.</summary>
    int LabelRecordsWritten = 0,
    /// <summary>§4.3/R-via-9 (docs/sonnet-briefs/brief-via-primitive-and-stackup.md): a
    /// <see cref="ViaShape"/> with no <see cref="ViaShape.LandingLayer"/> set exports its barrel
    /// (<see cref="LayoutShape.Layer"/>) only — the pad is skipped and named in
    /// <see cref="Diagnostics"/>, never silently dropped.</summary>
    int ViaPadsSkipped = 0,
    /// <summary>brief-gdsii-native-fixes.md D7: one sentence per placeholder layer — the negative
    /// number a DXF or board import gives a layer the technology had no number for — saying which free
    /// GDSII layer number it was written as. Also in <see cref="Diagnostics"/>.</summary>
    IReadOnlyList<string>? LayersRenumbered = null);

public static class GdsiiWriter
{
    public static GdsiiExportSummary Write(
        Stream stream, IReadOnlyList<InterchangeStructure> structures, GdsiiUnits units, Technology? tech) =>
        Write(stream, StreamLowering.Lower(structures, tech), units);

    /// <summary>Serialises an already-lowered library. Its counts are the summary's.</summary>
    public static GdsiiExportSummary Write(Stream stream, StreamLibrary library, GdsiiUnits units)
    {
        var w = new GdsiiRecordWriter(stream);
        var time = BuildTimeFields(DateTime.UtcNow);

        w.WriteInt16Array(GdsiiRecordType.Header, [600]);
        w.WriteInt16Array(GdsiiRecordType.BgnLib, time);
        w.WriteAscii(GdsiiRecordType.LibName, "LIB");
        // The spec's first real is the database unit in USER units (0.001 for 1 nm in µm), not the user
        // unit in metres — GdsiiReader.ReadPreamble takes the quotient back.
        w.WriteReal8Array(GdsiiRecordType.Units, [units.DbUnitMeters / units.UserUnitMeters, units.DbUnitMeters]);

        foreach (var s in library.Structures)
        {
            w.WriteInt16Array(GdsiiRecordType.BgnStr, time);
            w.WriteAscii(GdsiiRecordType.StrName, s.Name);

            foreach (var element in s.Shapes)
            {
                switch (element)
                {
                    case StreamPolygon p: WriteBoundary(w, p); break;
                    case StreamPath path: WritePath(w, path); break;
                    case StreamLabel label: WriteText(w, label); break;
                }
            }

            foreach (var reference in s.References)
                WriteInstance(w, reference);

            w.WriteNoData(GdsiiRecordType.EndStr);
        }

        w.WriteNoData(GdsiiRecordType.EndLib);

        return new GdsiiExportSummary(
            library.CurvedShapesFlattened, library.HolesKeyholed, library.BitmapsSkipped, library.Diagnostics,
            library.LabelRecordsWritten, library.ViaPadsSkipped, library.LayersRenumbered);
    }

    // ── Elements ───────────────────────────────────────────────────────────────

    private static void WriteBoundary(GdsiiRecordWriter w, StreamPolygon p)
    {
        w.WriteNoData(GdsiiRecordType.Boundary);
        w.WriteInt16Array(GdsiiRecordType.Layer, [(short)p.Layer.Layer]);
        w.WriteInt16Array(GdsiiRecordType.Datatype, [(short)p.Layer.Datatype]);
        w.WriteInt32Array(GdsiiRecordType.Xy, ToClosedIntArray(p.Xy)); // §2.1 item 3 — explicitly closed
        w.WriteNoData(GdsiiRecordType.EndEl);
    }

    private static void WritePath(GdsiiRecordWriter w, StreamPath path)
    {
        int pathType = path.End switch
        {
            PathEndStyle.Flush => 0,
            PathEndStyle.Round => 1,
            PathEndStyle.Square => 2,
            PathEndStyle.Extended => 4,
            _ => 0,
        };

        // Canonical PATH record order (per spec): LAYER, DATATYPE, PATHTYPE, WIDTH, [BGNEXTN,
        // ENDEXTN], XY, ENDEL — PATHTYPE before WIDTH, not the other way around. A strict reader
        // (KLayout) enforces this exact order and desyncs its own element parser when it isn't
        // followed, even though every individual record here is otherwise correctly framed.
        w.WriteNoData(GdsiiRecordType.Path);
        w.WriteInt16Array(GdsiiRecordType.Layer, [(short)path.Layer.Layer]);
        w.WriteInt16Array(GdsiiRecordType.Datatype, [(short)path.Layer.Datatype]);
        w.WriteInt16Array(GdsiiRecordType.PathType, [(short)pathType]);
        w.WriteInt32Array(GdsiiRecordType.Width, [(int)path.Width]);
        if (pathType == 4)
        {
            w.WriteInt32Array(GdsiiRecordType.BgnExtn, [(int)path.Extension]);
            w.WriteInt32Array(GdsiiRecordType.EndExtn, [(int)path.Extension]);
        }
        w.WriteInt32Array(GdsiiRecordType.Xy, ToOpenIntArray(path.Xy));
        w.WriteNoData(GdsiiRecordType.EndEl);
    }

    private static void WriteText(GdsiiRecordWriter w, StreamLabel label)
    {
        w.WriteNoData(GdsiiRecordType.Text);
        w.WriteInt16Array(GdsiiRecordType.Layer, [(short)label.Layer.Layer]);
        w.WriteInt16Array(GdsiiRecordType.TextType, [(short)label.Layer.Datatype]); // D1: TEXTTYPE is the datatype
        // GDSII has no native text-height record; WIDTH on a TEXT element is this codebase's own,
        // internally-consistent convention for carrying LabelShape.Height (GdsiiReader reads it back).
        w.WriteInt32Array(GdsiiRecordType.Width, [(int)label.Height]);
        w.WriteBitArray(GdsiiRecordType.Strans, 0);
        w.WriteReal8Array(GdsiiRecordType.Angle, [label.AngleDegrees]);
        w.WriteInt32Array(GdsiiRecordType.Xy, [(int)label.X, (int)label.Y]);
        w.WriteAscii(GdsiiRecordType.StringRec, label.Text);
        if (label.IsPort)
        {
            // D3: the port flag is a property — after the element's own records, before ENDEL — which
            // a reader that does not know it drops harmlessly.
            w.WriteInt16Array(GdsiiRecordType.PropAttr, [GdsiiReader.PortPropertyAttribute]);
            w.WriteAscii(GdsiiRecordType.PropValue, GdsiiReader.PortPropertyValue);
        }
        w.WriteNoData(GdsiiRecordType.EndEl);
    }

    private static void WriteInstance(GdsiiRecordWriter w, StreamReference inst)
    {
        ushort stransBits = inst.Reflect ? (ushort)0x8000 : (ushort)0;

        w.WriteNoData(inst.IsArray ? GdsiiRecordType.ARef : GdsiiRecordType.SRef);
        w.WriteAscii(GdsiiRecordType.SName, inst.Cell);
        w.WriteBitArray(GdsiiRecordType.Strans, stransBits);
        w.WriteReal8Array(GdsiiRecordType.Mag, [inst.Mag]);
        w.WriteReal8Array(GdsiiRecordType.Angle, [inst.AngleDegrees]);

        if (inst.IsArray)
        {
            w.WriteInt16Array(GdsiiRecordType.ColRow, [(short)inst.Cols, (short)inst.Rows]);
            // §2.1 item 5 — COLROW plus the three already-transformed reference points: origin, the
            // column reference point (origin displaced by Cols×PitchX), the row reference point
            // (origin displaced by Rows×PitchY). Written literally in OUR OWN unrotated-pitch
            // convention (LayoutInstanceTransform.ArrayCellOrigin never rotates the pitch) — a
            // compliant reader takes these three points as-is, with no rotation math of its own
            // needed to recover the grid (see GdsiiReader.ReadRef's own note on the reverse
            // direction), so this is exact for every rotation our own writer ever emits.
            long colRefX = inst.X + (long)inst.Cols * inst.PitchX;
            long rowRefY = inst.Y + (long)inst.Rows * inst.PitchY;
            w.WriteInt32Array(GdsiiRecordType.Xy,
                [(int)inst.X, (int)inst.Y, (int)colRefX, (int)inst.Y, (int)inst.X, (int)rowRefY]);
        }
        else
        {
            w.WriteInt32Array(GdsiiRecordType.Xy, [(int)inst.X, (int)inst.Y]);
        }
        w.WriteNoData(GdsiiRecordType.EndEl);
    }

    // ── Small helpers ──────────────────────────────────────────────────────────

    private static short[] BuildTimeFields(DateTime t) =>
    [
        (short)t.Year, (short)t.Month, (short)t.Day, (short)t.Hour, (short)t.Minute, (short)t.Second,
        (short)t.Year, (short)t.Month, (short)t.Day, (short)t.Hour, (short)t.Minute, (short)t.Second,
    ];

    private static int[] ToClosedIntArray(long[] ring)
    {
        var result = new int[ring.Length + 2];
        for (int i = 0; i < ring.Length; i++) result[i] = checked((int)ring[i]);
        result[ring.Length] = result[0];
        result[ring.Length + 1] = result[1];
        return result;
    }

    private static int[] ToOpenIntArray(long[] xy)
    {
        var result = new int[xy.Length];
        for (int i = 0; i < xy.Length; i++) result[i] = checked((int)xy[i]);
        return result;
    }
}
