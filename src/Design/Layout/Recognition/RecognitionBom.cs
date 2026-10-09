// The bill of materials Create Schematic from Artwork reads beside a board — its BOM… button and `recognize --bom`.
//
// ── WHY NOT BomFile ALONE ────────────────────────────────────────────────────────────────────────────
//
// BomFile is railRF's reader, and its rule suits railRF: a bill of materials there is EVIDENCE about artwork, so a
// header it cannot place is a refusal. Recognition asks less of the file — a designator, and where the file says
// them, a kind, a value and a case — and the files a designer actually has to hand did not get through
// (designer report, round 15):
//
//   * a spreadsheet BOM headed Component,Type,…,Value,Package — no part-number, quantity or description column, so
//     BomFile found no header at all — with the unit in the column after the number ("100","pF");
//   * circuitRF's OWN netlist of the drawn schematic, saved as .txt — the one file that states every value exactly;
//   * the drawn schematic itself.
//
// So, in order: a circuitRF schematic or netlist is read as the circuit it is; anything else goes to BomFile, and a
// file BomFile refuses goes to the schematic paste reader (BomTablePaste), which recognises each cell by what it
// looks like. What comes back is a BomTable either way — the type recognition already reads — and how the file was
// read is stated in its diagnostics, never silently.

using System.Globalization;
using System.Text.RegularExpressions;

using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>Reads the bill of materials recognition is given. See the file header.</summary>
public static class RecognitionBom
{
    // "C:M1  n1  RF_in  C=100 pF" — a netlist's instance line, whatever the file is called.
    private static readonly Regex NetlistLine =
        new(@"^[A-Za-z][A-Za-z0-9_]*:[A-Za-z0-9_.\-]+\s", RegexOptions.CultureInvariant);

    /// <summary>Reads <paramref name="path"/>; null when the file cannot be opened. A refused file comes back with
    /// its <see cref="BomTable.Refusal"/> set, as <see cref="BomFile.ReadFile"/>'s does.</summary>
    public static BomTable? ReadFile(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (string.Equals(Path.GetExtension(full), ".csch", StringComparison.OrdinalIgnoreCase))
                return FromSchematic(full);
            return Read(full, Decode(File.ReadAllBytes(full)));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// UTF-8 (a byte order mark of any Unicode kind is honoured), else Windows-1252 — what a spreadsheet on Windows saves
    /// a CSV in. Decoded as UTF-8, its "µ" (0xB5) becomes a replacement character and "0.1","µF" a value with no unit
    /// (designer report, round 15: four 0.1 µF capacitors came through valueless). Latin-1 agrees with Windows-1252 on µ
    /// and needs no code-page provider.
    /// </summary>
    internal static string Decode(byte[] bytes)
    {
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true),
                                                detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (System.Text.DecoderFallbackException)
        {
            return System.Text.Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Reads <paramref name="text"/>, which came from <paramref name="path"/> (its name and folder are all
    /// that is used of the path).</summary>
    public static BomTable Read(string path, string text)
    {
        string full = Path.GetFullPath(path);
        if (string.Equals(Path.GetExtension(full), ".cnl", StringComparison.OrdinalIgnoreCase) || IsNetlist(text))
            return FromNetlist(full, text);

        var strict = BomFile.Read(full, text);
        if (strict.Refusal is null) return strict;
        return FromTable(full, text) ?? strict;
    }

    private static bool IsNetlist(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            return NetlistLine.IsMatch(line);
        }
        return false;
    }

    // ── a circuitRF netlist or schematic ─────────────────────────────────────────────────────────────

    private static BomTable FromNetlist(string full, string text)
    {
        TestBench tb;
        try { (_, tb) = new CnlReader().Read(text, "tb", Path.GetDirectoryName(full)); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Refuse(full, $"{Path.GetFileName(full)} reads as a circuitRF netlist and did not parse: {ex.Message}");
        }
        return FromCircuit(full, tb, $"{Path.GetFileName(full)} was read as a circuitRF netlist.");
    }

    private static BomTable FromSchematic(string full)
    {
        try
        {
            var (model, _, _) = SchematicPersistence.LoadFromFile(full);
            var extracted = NetExtractor.Extract(model, "tb", DiskCellResolver.Instance);
            return FromCircuit(full, extracted.TestBench, $"{Path.GetFileName(full)} was read as a circuitRF schematic.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Refuse(full, $"{Path.GetFileName(full)} did not read as a schematic: {ex.Message}");
        }
    }

    /// <summary>One row per R, L, C and ferrite bead the circuit places. A value is taken only where it is a
    /// number; one written as an expression is left for recognition to make a variable of.</summary>
    private static BomTable FromCircuit(string full, TestBench tb, string how)
    {
        var rows = new List<BomRow>();
        foreach (var inst in tb.Instances)
        {
            if (inst.InstanceName is not { Length: > 0 } refdes) continue;
            string? Override(string name) =>
                inst.Overrides.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) is { } o
                    ? NumberWithUnit(o) : null;
            string? footprint = inst.Overrides.FirstOrDefault(o => o.Name == "Footprint")?.Expression.Trim('"');

            (string Kind, string? Value)? part = inst.Reference.ToUpperInvariant() switch
            {
                "R" => ("Resistor", Override("R")),
                "C" => ("Capacitor", Override("C")),
                "L" => ("Inductor", Override("L")),
                // A bead is an inductor where its L is stated, and its DC resistance where it is not — which is
                // how a designer writes one into a bill of materials for a board model.
                "BEAD" => Override("L") is { } l && !IsZero(l) ? ("Inductor", l) : ("Resistor", Override("Rdc")),
                _ => null,
            };
            if (part is not { } p) continue;
            rows.Add(new BomRow(refdes, null, p.Value, footprint, p.Kind) { Line = rows.Count + 1 });
        }
        if (rows.Count == 0)
            return Refuse(full, $"{Path.GetFileName(full)} places no resistor, capacitor, inductor or bead, so it names no part.");
        return new BomTable(full, null, ',', rows, rows.Count, 0, [], [how]);
    }

    private static string? NumberWithUnit(ParameterAssignment o)
    {
        string expr = o.Expression.Trim();
        if (!double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return null;
        return o.Unit is { Length: > 0 } unit ? $"{expr} {unit}" : expr;
    }

    private static bool IsZero(string value) =>
        double.TryParse(value.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v == 0;

    // ── a table BomFile could not place ──────────────────────────────────────────────────────────────

    private static BomTable? FromTable(string full, string text)
    {
        if (BomTablePaste.TryParse(text) is not { } read) return null;
        var rows = new List<BomRow>();
        foreach (var part in read.Parts)
        {
            string? value = part.Value is { } v ? (part.Unit is { Length: > 0 } u ? $"{v} {u}" : v) : null;
            string kind = part.Kind switch
            {
                SymbolKind.Resistor => "Resistor",
                SymbolKind.Inductor => "Inductor",
                _ => "Capacitor",
            };
            rows.Add(new BomRow(part.Refdes, null, value, part.Case is { } c ? $"smt:{c.Code}" : null, kind) { Line = part.Line });
        }
        foreach (var skip in read.Skipped.Where(s => s.Reason == "marked do-not-populate"))
            foreach (string refdes in RefdesCell.Parse(skip.What).Refdes)
                rows.Add(new BomRow(refdes, null, "DNP", null, null) { Line = skip.Line });
        if (rows.Count == 0) return null;

        var diagnostics = new List<string>
        {
            $"{Path.GetFileName(full)} names no part-number, quantity or description column, so it was read cell by cell " +
            "— each cell by what it looks like — as a table pasted onto a schematic is.",
        };
        diagnostics.AddRange(read.Notes);
        diagnostics.AddRange(read.Skipped.Where(s => s.Reason != "marked do-not-populate")
                                         .Select(s => $"Line {s.Line}, '{s.What}': {s.Reason}."));
        return new BomTable(full, null, ',', rows, rows.Count + read.Skipped.Count, 0, [], diagnostics);
    }

    private static BomTable Refuse(string full, string why) => new(full, why, ',', [], 0, 0, [], []);
}
