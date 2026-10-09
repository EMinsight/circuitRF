// The parts table as a document — brief-artsch-4-parts-and-parts-table.md R-as4-8; overview D10.
//
// UTF-8, comma-delimited, a fixed header in R-as4-7's order, one row per part sorted by designator in
// natural order. THE FORMAT IS THE CONTRACT: the GUI's grid, `--parts-out` and `--parts` all go through
// this file, so a user or an agent edits the table the same way.
//
// Reading back is an OVERLAY on a fresh recognition, never a replacement for one:
//   - a row whose designator the board has overrides the EDITABLE columns (Kind, Value, Variable, Model,
//     ModelFile); a value clears the variable;
//   - the MEASURED columns (Connection, Case, X, Y, Evidence, Confidence, PartNumber) are read off the
//     board again and the table's are ignored — with a note where the two differ, so an edit that did
//     nothing says so;
//   - a designator the board does not have is reported and ignored: the table never invents a part;
//   - a column the table does not have is a REFUSAL naming it, because a misspelt "Vaule" would
//     otherwise do nothing, silently.
// Write then read is the identity.

using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>What reading a parts table back did.</summary>
/// <param name="Table">The recognised table with the edits applied, or null on a refusal.</param>
/// <param name="Notes">One sentence per edit not applied or measured column that differed.</param>
/// <param name="NotOnBoard">Designators the table names and the board does not have.</param>
/// <param name="Refusal">Why the table could not be read at all, or null.</param>
public sealed record PartsCsvReading(
    PartsTable? Table, IReadOnlyList<string> Notes, IReadOnlyList<string> NotOnBoard, string? Refusal);

/// <summary>Writes and reads the parts table's CSV.</summary>
public static class PartsTableCsv
{
    /// <summary>The header, in order.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "Refdes", "Kind", "Connection", "Case", "Value", "Variable", "Model", "ModelFile",
        "PartNumber", "X", "Y", "Evidence", "Confidence", "Notes",
    ];

    private static readonly HashSet<string> Measured =
        new(["Connection", "Case", "X", "Y", "Evidence", "Confidence", "PartNumber"], StringComparer.OrdinalIgnoreCase);

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    // ── writing ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The table's CSV text — LF line ends; the caller writes it as UTF-8.</summary>
    public static string Write(PartsTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Columns)).Append('\n');
        foreach (var row in table.Rows.OrderBy(r => r.Refdes, PartsTable.NaturalOrder))
            sb.Append(string.Join(",", Columns.Select(c => Escape(Cell(table, row, c))))).Append('\n');
        return sb.ToString();
    }

    /// <summary>Writes the table to <paramref name="path"/>, UTF-8 without a byte order mark.</summary>
    public static void WriteFile(string path, PartsTable table) =>
        File.WriteAllText(path, Write(table), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>One cell as the table writes it.</summary>
    public static string Cell(PartsTable table, PartRow row, string column) => column switch
    {
        "Refdes" => row.Refdes,
        "Kind" => PartsTable.KindText(row.Kind),
        "Connection" => PartsTable.ConnectionText(row.Connection),
        "Case" => row.Case?.Code ?? "",
        "Value" => row.Value is { } v ? PartsTable.ValueText(v, row.GeneratedKind) : "",
        "Variable" => row.Variable ?? "",
        "Model" => row.Model.ToString(),
        "ModelFile" => row.ModelFile ?? "",
        "PartNumber" => row.PartNumber ?? "",
        "X" => table.Format.Length(row.X),
        "Y" => table.Format.Length(row.Y),
        "Evidence" => PartEvidenceText.Format(row.Evidence),
        "Confidence" => PartsTable.ConfidenceText(row.Confidence),
        "Notes" => string.Join("; ", row.Notes),
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "not a parts-table column"),
    };

    private static string Escape(string field) =>
        field.IndexOfAny([',', '"', '\n', '\r']) >= 0 || (field.Length > 0 && (char.IsWhiteSpace(field[0]) || char.IsWhiteSpace(field[^1])))
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;

    // ── reading ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads a parts table from disk and lays it over <paramref name="recognised"/>.</summary>
    public static PartsCsvReading ReadFile(string path, PartsTable recognised)
    {
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PartsCsvReading(null, [], [], $"The parts table '{Path.GetFileName(path)}' could not be read: {ex.Message}");
        }
        return Read(text, recognised);
    }

    /// <summary>Lays the table in <paramref name="text"/> over <paramref name="recognised"/>.</summary>
    public static PartsCsvReading Read(string text, PartsTable recognised)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(recognised);
        var parsed = DelimitedTables.Parse(text, ',');
        if (parsed.Rows.Count == 0)
            return new PartsCsvReading(null, [], [], "The parts table is empty: it has no header row.");

        // ── the header: every column known, none twice, Refdes present ────────────────────────────
        var header = parsed.Rows[0].Fields.Select(f => f.Trim()).ToList();
        var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Count; i++)
        {
            if (header[i].Length == 0 && i == header.Count - 1) continue;   // a trailing separator
            string? known = Columns.FirstOrDefault(c => string.Equals(c, header[i], StringComparison.OrdinalIgnoreCase));
            if (known is null)
                return new PartsCsvReading(null, [], [],
                    $"The parts table has a column '{header[i]}' that is not one of its columns ({string.Join(", ", Columns)}). " +
                    "Rename or remove it — a misspelt column would otherwise change nothing, silently.");
            if (!at.TryAdd(known, i))
                return new PartsCsvReading(null, [], [], $"The parts table has the column '{known}' twice.");
        }
        if (!at.ContainsKey("Refdes"))
            return new PartsCsvReading(null, [], [], "The parts table has no Refdes column, so no row can be matched to a part.");

        var notes = new List<string>();
        var notOnBoard = new List<string>();
        var rows = recognised.Rows.ToDictionary(r => r.Refdes, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in parsed.Rows.Skip(1))
        {
            if (line.Populated == 0) continue;
            string? Field(string column) => at.TryGetValue(column, out int i) ? (line.Field(i) ?? "").Trim() : null;

            string refdes = Field("Refdes")!;
            if (refdes.Length == 0) { notes.Add($"Line {line.Line} names no part and was ignored."); continue; }
            if (!rows.TryGetValue(refdes, out var row)) { notOnBoard.Add(refdes); continue; }
            if (!seen.Add(refdes)) { notes.Add($"{refdes} is in the table twice; line {line.Line} was ignored."); continue; }

            // Measured: the board's answer stands; say so where the table's differs.
            foreach (string column in Columns.Where(Measured.Contains))
                if (Field(column) is { } stated && !string.Equals(stated, Cell(recognised, row, column), StringComparison.OrdinalIgnoreCase))
                    notes.Add($"{refdes}: {column} '{stated}' is measured on the board as '{Cell(recognised, row, column)}'; the board's is kept.");

            rows[row.Refdes] = Edit(recognised, row, Field, notes);
        }

        var table = recognised with { Rows = [.. rows.Values.OrderBy(r => r.Refdes, PartsTable.NaturalOrder)] };
        return new PartsCsvReading(table, notes, notOnBoard, null);
    }

    /// <summary>The editable columns of one row applied.</summary>
    private static PartRow Edit(PartsTable table, PartRow row, Func<string, string?> field, List<string> notes)
    {
        var evidence = new Dictionary<PartField, PartEvidenceSource>(row.Evidence);
        string oldDefault = row.DefaultVariable;
        var edited = row;

        // ── kind ──────────────────────────────────────────────────────────────────────────────────
        if (field("Kind") is { Length: > 0 } kindText)
        {
            if (PartsTable.ParseKind(kindText) is not { } kind)
                notes.Add($"{row.Refdes}: '{kindText}' is not a kind ({string.Join(", ", Enum.GetValues<PartKind>().Select(PartsTable.KindText))}); the kind was kept.");
            else if (kind != row.Kind)
            {
                bool multiPad = row.PadCount > 2;
                if (multiPad && kind is not (PartKind.MultiPin or PartKind.Connector or PartKind.Ignore))
                    notes.Add($"{row.Refdes} has {row.PadCount} pads and cannot be a two-terminal {PartsTable.KindText(kind)}; the kind was kept.");
                else if (!multiPad && kind == PartKind.MultiPin)
                    notes.Add($"{row.Refdes} has {row.PadCount} pads and cannot be a multi-pin part; the kind was kept.");
                else
                {
                    bool sameDimension = edited.TakesValue && (edited with { Kind = kind }).TakesValue
                                         && edited.GeneratedKind == (edited with { Kind = kind }).GeneratedKind;
                    edited = edited with { Kind = kind, Value = sameDimension ? edited.Value : null };
                    evidence[PartField.Kind] = PartEvidenceSource.User;
                    if (!sameDimension) evidence.Remove(PartField.Value);
                }
            }
        }

        // ── value: a value clears the variable ────────────────────────────────────────────────────
        if (field("Value") is { } valueText)
        {
            if (valueText.Length == 0)
            {
                if (edited.Value is not null) { edited = edited with { Value = null }; evidence.Remove(PartField.Value); }
            }
            else if (!edited.TakesValue)
                notes.Add($"{row.Refdes} is {PartsTable.KindText(edited.Kind)} and takes no value; '{valueText}' was ignored.");
            else if (!TryReadValue(valueText, edited.GeneratedKind, out double si, out string? why))
                notes.Add($"{row.Refdes}: {why}; the value was kept.");
            else if (edited.Value is not { } old || Math.Abs(old - si) > 1e-9 * Math.Max(Math.Abs(old), Math.Abs(si)))
            {
                edited = edited with { Value = si };
                evidence[PartField.Value] = PartEvidenceSource.User;
            }
        }

        // ── model ─────────────────────────────────────────────────────────────────────────────────
        string? modelText = field("Model");
        string? fileText = field("ModelFile");
        PartModelKind? model = modelText is { Length: > 0 }
            ? Enum.TryParse<PartModelKind>(modelText, ignoreCase: true, out var m) && Enum.IsDefined(m) ? m : null
            : fileText is { Length: > 0 } ? PartModelKind.SnP : null;
        if (modelText is { Length: > 0 } && model is null)
            notes.Add($"{row.Refdes}: '{modelText}' is not a model (Ideal or SnP); the model was kept.");
        else if (model == PartModelKind.Ideal && edited.Model != PartModelKind.Ideal)
        {
            edited = edited with { Model = PartModelKind.Ideal, ModelFile = null };
            evidence[PartField.Model] = PartEvidenceSource.User;
        }
        else if (model == PartModelKind.SnP)
        {
            string? file = fileText is { Length: > 0 } ? fileText : edited.ModelFile;
            if (file is null) notes.Add($"{row.Refdes}: an SnP model needs a ModelFile; the model was kept.");
            else if (!string.Equals(file, edited.ModelFile, StringComparison.Ordinal) || edited.Model != PartModelKind.SnP)
            {
                string full = table.ResolveModelFile(edited with { ModelFile = file })!;
                var why = new List<string>();
                if (PartModelResolver.TwoPort(full, why))
                {
                    edited = edited with { Model = PartModelKind.SnP, ModelFile = table.StoredPath(full) };
                    evidence[PartField.Model] = PartEvidenceSource.User;
                }
                else notes.Add($"{row.Refdes}: {string.Join("; ", why)}.");
            }
        }

        // ── the variable: a value or a measured model clears it; else the user's name or the default ──
        bool wantsVariable = edited.IsModelled && edited.TakesValue && edited.Value is null && edited.Model == PartModelKind.Ideal;
        string? variable = wantsVariable ? edited.Variable is { } kept && kept != oldDefault ? kept : edited.DefaultVariable : null;
        if (wantsVariable && field("Variable") is { Length: > 0 } named && named != variable)
        {
            if (Identifier.IsMatch(named)) variable = named;
            else notes.Add($"{row.Refdes}: '{named}' is not a variable name (a letter or underscore, then letters, digits and underscores); it was kept as {variable}.");
        }

        return edited with { Variable = variable, Evidence = evidence };
    }

    /// <summary>
    /// A Value cell as the table reads it, for a part generated as <paramref name="kind"/>: a value with its unit in
    /// the part's own dimension, in base SI. The rule an edited CSV is read with, and the one the dialog's Value cell
    /// borders red on (brief-artsch-8 R-as8-3) — so the two cannot disagree about what "10nH" on a capacitor is.
    /// </summary>
    public static bool TryReadValue(string text, PartKind kind, out double si, out string? why)
    {
        why = null;
        var want = PartReading.DimensionOf(kind);
        if (!Schematic.BomTablePaste.TryReadValue(text, PartReading.ToSymbol(kind), description: false, out si, out var dim))
        {
            why = $"'{text}' is not a value with a unit";
            return false;
        }
        if (dim == want) return true;
        why = $"'{text}' is not a {want.ToString().ToLowerInvariant()}";
        return false;
    }
}
