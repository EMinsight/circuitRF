// brief-em3d-46 R-em3d46-6b / -6c / -6d — the Measure card's numbers: both points' x, y and z, Δ = P2 − P1 and the
// distance, in the display unit — and what each copies as.
//
// A COPIED NUMBER IS THE UNIT-BEARING SPELLING LayoutUnits.Spell WRITES (12.5mil, -3mm), at the lossless precision
// SpellDecimals derives, invariant culture. So pasting one into any typed field reads back the SAME DBU — not a
// rounded neighbour (at a fixed four places a mil is quantised to 2.54 nm, and 35 µm became 35.001 µm). The card
// DISPLAYS the same digits, so what is copied is what is shown. The distance is not a whole number of DBU; it is
// spelled at the same precision, which resolves one DBU, and a pasted distance lands on the nearest one.

using System.Globalization;
using System.Text;
using CircuitRF.Design.Layout;

namespace CircuitRF.Ui.Viewer3D;

/// <summary>A measured point, world metres, and whether it is an exact database-unit point of the document.</summary>
public readonly record struct Viewer3DMeasurePoint(double X, double Y, double Z, bool Exact);

/// <summary>One number of the card: what it shows (<c>12.5 mil</c>) and what it copies as (<c>12.5mil</c>).</summary>
public sealed record Viewer3DMeasureValue(string Text, string Copy, string Number);

/// <summary>A row of the card: P1, P2 or Δ.</summary>
public sealed record Viewer3DMeasureRow(string Label, Viewer3DMeasureValue X, Viewer3DMeasureValue Y, Viewer3DMeasureValue Z, bool Approx)
{
    /// <summary>The label as the card shows it — with <c>≈</c> when the row is not exact.</summary>
    public string Heading => Approx ? Label + " ≈" : Label;
}

/// <summary>The card, built from two points in a unit. Immutable: a change of unit builds a new one.</summary>
public sealed record Viewer3DMeasureReadout(IReadOnlyList<Viewer3DMeasureRow> Rows, Viewer3DMeasureValue? Distance, bool DistanceApprox,
                                            LayoutUnit Unit, string? Frame)
{
    public string DistanceHeading => DistanceApprox ? "Distance ≈" : "Distance";

    /// <summary>
    /// Builds the card for <paramref name="p1"/> and, once there is one, <paramref name="p2"/> (live or fixed): each
    /// coordinate rounded once to the document's DBU (<paramref name="dbuPerMicron"/>), and every number spelled in
    /// <paramref name="unit"/> at its lossless precision.
    /// </summary>
    public static Viewer3DMeasureReadout Build(Viewer3DMeasurePoint p1, Viewer3DMeasurePoint? p2, LayoutUnit unit, int dbuPerMicron,
                                               string? frame = null)
    {
        double per = 1e-6 / Math.Max(1, dbuPerMicron);          // metres per DBU
        long D(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);
        var a = (X: D(p1.X), Y: D(p1.Y), Z: D(p1.Z));
        var rows = new List<Viewer3DMeasureRow> { Row("P1", a.X, a.Y, a.Z, !p1.Exact) };
        if (p2 is not { } q) return new(rows, null, false, unit, frame);
        var b = (X: D(q.X), Y: D(q.Y), Z: D(q.Z));
        bool approx = !p1.Exact || !q.Exact;
        rows.Add(Row("P2", b.X, b.Y, b.Z, !q.Exact));
        long dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        rows.Add(Row("Δ", dx, dy, dz, approx));
        double dist = Math.Sqrt((double)dx * dx + (double)dy * dy + (double)dz * dz);
        return new(rows, Real(dist), approx, unit, frame);

        Viewer3DMeasureRow Row(string label, long x, long y, long z, bool ap) => new(label, Value(x), Value(y), Value(z), ap);

        Viewer3DMeasureValue Value(long dbu)
        {
            string n = LayoutUnits.Format(dbu, unit, dbuPerMicron, LayoutUnits.SpellDecimals(unit, dbuPerMicron));
            return new($"{n} {LayoutUnits.Suffix(unit)}", LayoutUnits.Spell(dbu, unit, dbuPerMicron), n);
        }

        Viewer3DMeasureValue Real(double dbu)
        {
            int places = LayoutUnits.SpellDecimals(unit, dbuPerMicron);
            decimal perDbu = LayoutUnits.FromDbu(1, unit, dbuPerMicron);
            decimal v = Math.Round((decimal)dbu * perDbu, places, MidpointRounding.AwayFromZero);
            string n = v.ToString("0." + new string('#', places), CultureInfo.InvariantCulture);
            return new($"{n} {LayoutUnits.Suffix(unit)}", n + LayoutUnits.AsciiSuffix(unit), n);
        }
    }

    /// <summary>
    /// R-em3d46-6c — the whole card as tab-separated text: a header row, then P1, P2, Δ and Distance, the numbers in
    /// the display unit with a unit column — so it pastes into a spreadsheet as a table of numbers. A row that is
    /// not exact says so in its label.
    /// </summary>
    public string CopyAll()
    {
        var sb = new StringBuilder();
        sb.Append("\tx\ty\tz\tunit\n");
        string unit = LayoutUnits.AsciiSuffix(Unit);
        foreach (var r in Rows) sb.Append(r.Heading).Append('\t').Append(r.X.Number).Append('\t').Append(r.Y.Number).Append('\t')
                                  .Append(r.Z.Number).Append('\t').Append(unit).Append('\n');
        if (Distance is { } d) sb.Append(DistanceHeading).Append('\t').Append(d.Number).Append("\t\t\t").Append(unit).Append('\n');
        return sb.ToString();
    }
}
