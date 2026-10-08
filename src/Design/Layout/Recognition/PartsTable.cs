// The parts table — brief-artsch-4-parts-and-parts-table.md R-as4-2 … R-as4-7; overview D10.
//
// Every two-terminal part on the board, read as well as the evidence allows, in one table the user (or
// an agent) reviews and corrects before anything is generated. The table is a DOCUMENT: its CSV
// (PartsTableCsv) is the contract the GUI's grid and the CLI's --parts / --parts-out share, so nothing
// here may hold a fact the CSV cannot carry except what is MEASURED off the board again on every run
// (the terminals, which AS-6 wires).

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Footprints;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>What a row is (R-as4-2). <see cref="Unknown"/> is shown as <c>?</c> and generated as a C.</summary>
public enum PartKind
{
    Unknown,
    R,
    L,
    C,

    /// <summary>A 0 Ω link or a jumper — a wire.</summary>
    Short,

    /// <summary>Not fitted — an open.</summary>
    Open,

    /// <summary>More than two pads: cut out, its pads are ports (D9).</summary>
    MultiPin,

    /// <summary>A connector: cut out like a multi-pin part.</summary>
    Connector,

    /// <summary>Left out: a series one is an open, a shunt one is removed.</summary>
    Ignore,
}

/// <summary>How a part's two pads sit on the board (R-as4-3) — measured, never stated.</summary>
public enum PartConnection
{
    /// <summary>Both pads on signal islands, different ones.</summary>
    Series,

    /// <summary>One pad on ground.</summary>
    Shunt,

    /// <summary>Both pads on ground: the part does nothing and is left out.</summary>
    Shorted,

    /// <summary>Both pads on one signal island: copper runs round it, and it is left out.</summary>
    Bridged,

    /// <summary>More than two pads (D9).</summary>
    MultiPin,

    /// <summary>A pad lands on no copper in scope.</summary>
    Unplaced,
}

/// <summary>Which model a part gets (R-as4-6).</summary>
public enum PartModelKind
{
    /// <summary>The ideal R, L or C.</summary>
    Ideal,

    /// <summary>A two-port Touchstone file.</summary>
    SnP,
}

/// <summary>How sure the reading is (R-as4-7).</summary>
public enum PartConfidence
{
    /// <summary>Land pattern only, kind unknown.</summary>
    Low,

    /// <summary>One source only.</summary>
    Medium,

    /// <summary>A placed instance, or a bill of materials that agrees with pads on the board.</summary>
    High,
}

/// <summary>One end of a two-pad part, measured: where its pad is and what it lands on.</summary>
/// <param name="Island">The board island the pad is on, or -1 when it is on ground copper or no copper.</param>
/// <param name="OnGround">Whether it is on ground copper or a ground pad's own island.</param>
public sealed record PartTerminal(long X, long Y, LayerKey? Layer, int Island, bool OnGround);

/// <summary>One part.</summary>
public sealed record PartRow
{
    public required string Refdes { get; init; }
    public PartKind Kind { get; init; }
    public PartConnection Connection { get; init; }

    /// <summary>The case, where any evidence states one.</summary>
    public SmtCase? Case { get; init; }

    /// <summary>The value in base SI (Ω, H, F), or null when unknown.</summary>
    public double? Value { get; init; }

    /// <summary>The global variable an unknown value becomes (<c>C6_C</c>), or null.</summary>
    public string? Variable { get; init; }

    public PartModelKind Model { get; init; }

    /// <summary>The Touchstone file, as stored: relative to <see cref="PartsTable.BaseDirectory"/> where
    /// it is under it, forward slashes.</summary>
    public string? ModelFile { get; init; }

    public string? PartNumber { get; init; }

    /// <summary>The part's centre, DBU.</summary>
    public long X { get; init; }
    public long Y { get; init; }

    /// <summary>The source of each field that has one.</summary>
    public IReadOnlyDictionary<PartField, PartEvidenceSource> Evidence { get; init; } =
        new Dictionary<PartField, PartEvidenceSource>();

    public PartConfidence Confidence { get; init; }

    /// <summary>What a reader should know about this row — a runner-up case, a value refused, a part
    /// number with several bill-of-materials rows.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>How many pads the part has on the board.</summary>
    public int PadCount { get; init; }

    /// <summary>The two ends, for a two-pad part — measured on every run, never read from the CSV.</summary>
    public IReadOnlyList<PartTerminal> Terminals { get; init; } = [];

    /// <summary>Whether the part is modelled at all: a shorted or bridged part, a multi-pin part, a
    /// connector and an ignored one are left out of the circuit.</summary>
    public bool IsModelled =>
        Connection is PartConnection.Series or PartConnection.Shunt
        && Kind is not (PartKind.MultiPin or PartKind.Connector or PartKind.Ignore);

    /// <summary>The kind the circuit is built with: an unknown kind is a capacitor (D10).</summary>
    public PartKind GeneratedKind => Kind == PartKind.Unknown ? PartKind.C : Kind;

    /// <summary>Whether the row's kind takes a value.</summary>
    public bool TakesValue => GeneratedKind is PartKind.R or PartKind.L or PartKind.C;

    /// <summary>
    /// The initial value of the variable an unknown value becomes (R-as4-5): TRANSPARENT, so the first
    /// simulation shows the lines alone — series C 100 pF, L 0.1 nH, R 0 Ω; shunt C 0.01 pF, L 1 µH, R 1 MΩ.
    /// </summary>
    public double TransparentValue => (GeneratedKind, Connection == PartConnection.Shunt) switch
    {
        (PartKind.C, false) => 100e-12,
        (PartKind.L, false) => 0.1e-9,
        (PartKind.R, false) => 0,
        (PartKind.C, true) => 0.01e-12,
        (PartKind.L, true) => 1e-6,
        (PartKind.R, true) => 1e6,
        _ => 0,
    };

    /// <summary>The parameter a value is: <c>C</c>, <c>L</c> or <c>R</c>.</summary>
    public string ParameterName => GeneratedKind switch { PartKind.R => "R", PartKind.L => "L", _ => "C" };

    /// <summary>The variable name an unknown value takes: <c>&lt;Refdes&gt;_&lt;Param&gt;</c>, the
    /// designator's non-identifier characters made underscores.</summary>
    public string DefaultVariable => $"{Regex.Replace(Refdes, "[^A-Za-z0-9_]", "_")}_{ParameterName}";
}

/// <summary>Every part of one recognition, sorted by designator in natural order.</summary>
/// <param name="Rows">The rows.</param>
/// <param name="Format">How X and Y are written — the layout's display unit.</param>
/// <param name="BaseDirectory">What a model file is relative to: the workspace root, else the layout's
/// folder, else null.</param>
public sealed record PartsTable(IReadOnlyList<PartRow> Rows, RailRf.RailLengthFormat Format, string? BaseDirectory)
{
    public static PartsTable Empty { get; } = new([], RailRf.RailLengthFormat.Dbu, null);

    /// <summary>The row with this designator, or null.</summary>
    public PartRow? Row(string refdes) =>
        Rows.FirstOrDefault(r => string.Equals(r.Refdes, refdes, StringComparison.OrdinalIgnoreCase));

    /// <summary>The model file's full path, or null.</summary>
    public string? ResolveModelFile(PartRow row) =>
        row.ModelFile is not { Length: > 0 } f ? null
        : Path.IsPathRooted(f) || BaseDirectory is null ? f
        : Path.GetFullPath(Path.Combine(BaseDirectory, f));

    /// <summary>A file path as a row stores it — relative to <see cref="BaseDirectory"/> where it is under it.</summary>
    public string StoredPath(string fullPath)
    {
        if (BaseDirectory is null) return fullPath;
        string rel = Path.GetRelativePath(BaseDirectory, fullPath);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? fullPath : rel.Replace('\\', '/');
    }

    /// <summary>Kind as the table spells it.</summary>
    public static string KindText(PartKind k) => k == PartKind.Unknown ? "?" : k.ToString();

    public static PartKind? ParseKind(string? text)
    {
        string t = (text ?? "").Trim();
        if (t is "?" or "") return t == "?" ? PartKind.Unknown : null;
        foreach (var k in Enum.GetValues<PartKind>())
            if (k != PartKind.Unknown && string.Equals(t, k.ToString(), StringComparison.OrdinalIgnoreCase)) return k;
        return null;
    }

    public static string ConnectionText(PartConnection c) => c switch
    {
        PartConnection.MultiPin => "multi-pin",
        _ => c.ToString().ToLowerInvariant(),
    };

    public static string ConfidenceText(PartConfidence c) => c.ToString().ToLowerInvariant();

    /// <summary>A value with its unit, the way the table writes it — <c>4.7 Ohm</c>, <c>100 pF</c>,
    /// <c>1 uH</c> — which the bill-of-materials value reader reads back.</summary>
    public static string ValueText(double si, PartKind kind)
    {
        string unit = kind switch { PartKind.R => "Ohm", PartKind.L => "H", _ => "F" };
        if (si == 0) return $"0 {unit}";
        (double Scale, string Prefix)[] prefixes = kind == PartKind.R
            ? [(1e9, "G"), (1e6, "M"), (1e3, "k"), (1, ""), (1e-3, "m")]
            : [(1, ""), (1e-3, "m"), (1e-6, "u"), (1e-9, "n"), (1e-12, "p"), (1e-15, "f")];
        double a = Math.Abs(si);
        var (scale, prefix) = prefixes.FirstOrDefault(p => a >= p.Scale * 0.999_999_999, prefixes[^1]);
        return $"{(si / scale).ToString("0.############", CultureInfo.InvariantCulture)} {prefix}{unit}";
    }

    /// <summary>Natural order: <c>C2</c> before <c>C10</c>, <c>C_A2</c> before <c>C_A10</c>.</summary>
    public static int NaturalCompare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        int tail = (a.Length - i).CompareTo(b.Length - j);
        return tail != 0 ? tail : string.CompareOrdinal(a, b);
    }

    /// <summary>The comparer <see cref="NaturalCompare"/> is.</summary>
    public static IComparer<string> NaturalOrder { get; } = Comparer<string>.Create(NaturalCompare);
}
