// brief-artsch-4-parts-and-parts-table.md §4 — the parts table's CSV: write then read is the identity;
// an edited Value clears the Variable; an edited measured column is ignored with a note; an unknown
// column is the refusal; a designator the board does not have is reported.

using System;
using System.Linq;
using System.Text;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class PartsTableCsvTests
{
    private static PartsTable Recognised()
    {
        var (view, placement, bom) = PartsBoard();
        return RecognizeParts(view, placement, bom).Parts;
    }

    /// <summary>The table's CSV with one cell changed, every field quoted, and any extra lines and
    /// columns appended.</summary>
    private static string Csv(PartsTable table, Func<string, string, string?>? edit = null,
                              string extraColumn = "", string extraLine = "")
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder(string.Join(",", PartsTableCsv.Columns.Select(Q)) + (extraColumn.Length > 0 ? "," + Q(extraColumn) : "") + "\n");
        foreach (var row in table.Rows)
            sb.Append(string.Join(",", PartsTableCsv.Columns.Select(c => Q(edit?.Invoke(row.Refdes, c) ?? PartsTableCsv.Cell(table, row, c)))))
              .Append(extraColumn.Length > 0 ? ",\"\"" : "").Append('\n');
        return sb.Append(extraLine).ToString();
    }

    [Fact]
    public void WriteThenReadIsTheIdentity()
    {
        var table = Recognised();
        string text = PartsTableCsv.Write(table);

        var read = PartsTableCsv.Read(text, table);

        Assert.Null(read.Refusal);
        Assert.Empty(read.Notes);
        Assert.Equal(text, PartsTableCsv.Write(read.Table!));
    }

    [Fact]
    public void AnEditedValueClearsTheVariable()
    {
        var table = Recognised();
        Assert.Equal("C2_C", table.Row("C2")!.Variable);

        var read = PartsTableCsv.Read(Csv(table, (r, c) => r == "C2" && c == "Value" ? "2.2 pF" : null), table);

        var c2 = read.Table!.Row("C2")!;
        Assert.Equal(2.2, c2.Value!.Value * 1e12, 9);
        Assert.Null(c2.Variable);
        Assert.Equal(PartEvidenceSource.User, c2.Evidence[PartField.Value]);
    }

    [Fact]
    public void AnEditedConnectionIsIgnoredWithANote()
    {
        var table = Recognised();

        var read = PartsTableCsv.Read(Csv(table, (r, c) => r == "R1" && c == "Connection" ? "shunt" : null), table);

        Assert.Equal(PartConnection.Series, read.Table!.Row("R1")!.Connection);
        Assert.Contains(read.Notes, n => n.StartsWith("R1: Connection 'shunt'", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownColumnIsTheRefusal()
    {
        var read = PartsTableCsv.Read(Csv(Recognised(), extraColumn: "Vaule"), Recognised());

        Assert.Null(read.Table);
        Assert.Contains("'Vaule'", read.Refusal);
    }

    [Fact]
    public void ADesignatorNotOnTheBoardIsReportedAndIgnored()
    {
        var table = Recognised();

        var read = PartsTableCsv.Read(Csv(table, extraLine: "C99,C,series,,1 pF\n"), table);

        Assert.Equal(["C99"], read.NotOnBoard);
        Assert.Null(read.Table!.Row("C99"));
        Assert.Equal(table.Rows.Count, read.Table.Rows.Count);
    }
}
