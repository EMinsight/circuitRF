using System.Linq;
using CircuitRF.Design.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A parts table pasted into a schematic (<see cref="BomTablePaste"/>). The two fixtures are one
/// field report's table in the two forms it reaches a schematic: copied out of a spreadsheet (tab
/// separated), and copied out of a PDF, where wrapped cells arrive as broken words ("Capacito r",
/// "Valu e") and a wrapped reference list continues on the next line. Manufacturer and family
/// columns are neutral placeholders. Note the header is misaligned in the source: the unit sits
/// under "Package" and the case under "Size".
/// </summary>
public class BomTablePasteTests
{
    private const string Spreadsheet =
        "Component\tType\tManufacturer\tFamily\tValue\tPackage\tSize\tSubstitution\n" +
        "M1,M23\tCapacitor\tMfr-A\tFam-1\t100\tpF\t402\tok\n" +
        "M2\tResistor\tVarious\t1.00%\t49.9\tΩ\t402\tok\n" +
        "M3\tCapacitor\tMfr-A\tFam-2\t22\tpF\t402\tok\n" +
        "M4\tCapacitor\tMfr-A\tFam-2\t12\tpF\t402\tok\n" +
        "M5\tInductor\tMfr-A\tFam-3\t6.8\tnH\t402\tok\n" +
        "M6\tCapacitor\tMfr-A\tFam-2\t47\tpF\t402\tok\n" +
        "M7\tInductor\tMfr-A\tFam-3\t1\tnH\t402\tok\n" +
        "M8\tCapacitor\tMfr-A\tFam-2\t39\tpF\t402\tok\n" +
        "M9,M13,M18\tDNP\t--\t--\t--\t--\t--\t\n" +
        "M10\tResistor\tVarious\t1.00%\t360\tΩ\t402\tok\n" +
        "M11,M12,M16\tCapacitor\tMfr-A\tFam-1\t0.1\tµF\t402\tok\n" +
        "M14\tInductor\tMfr-A\tFam-4\t8.2\tnH\t805\tok\n" +
        "M15\tCapacitor\tMfr-A\tFam-1\t10\tµF\t402\tok\n" +
        "M17\tCapacitor\tMfr-A\tFam-2\t12\tpF\t402\tok\n" +
        "M19\tInductor\tMfr-A\tFam-3\t1.5\tnH\t402\tok\n" +
        "M20\tCapacitor\tMfr-A\tFam-2\t39\tpF\t402\tok\n" +
        "M21\tInductor\tMfr-B\tFam-5\t7.8\tnH\t402\tok\n" +
        "M22\tCapacitor\tMfr-A\tFam-2\t12\tpF\t402\tok\n";

    private const string PdfText =
        "Component Type Manufacture r Family Valu e Packag e Size Substitutio n\n" +
        "M1,M23 Capacito r Mfr-A Fam-1 100 pF 402 ok\n" +
        "M2 Resistor Various 1.00% 49.9 Ω 402 ok\n" +
        "M3 Capacito r Mfr-A Fam-2 22 pF 402 ok\n" +
        "M4 Capacito r Mfr-A Fam-2 12 pF 402 ok\n" +
        "M5 Inductor Mfr-A Fam-3 6.8 nH 402 ok\n" +
        "M6 Capacito r Mfr-A Fam-2 47 pF 402 ok\n" +
        "M7 Inductor Mfr-A Fam-3 1 nH 402 ok\n" +
        "M8 Capacito r Mfr-A Fam-2 39 pF 402 ok\n" +
        "M9,M13,M18 DNP -- -- -- -- --\n" +
        "M10 Resistor Various 1.00% 360 Ω 402 ok\n" +
        "M11,M12,M1 Capacito Mfr-A Fam-1 0.1 µF 402 ok\n" +
        "6 r\n" +
        "M14 Inductor Mfr-A Fam-4 8.2 nH 805 ok\n" +
        "M15 Capacito r Mfr-A Fam-1 10 µF 402 ok\n" +
        "M17 Capacito r Mfr-A Fam-2 12 pF 402 ok\n" +
        "M19 Inductor Mfr-A Fam-3 1.5 nH 402 ok\n" +
        "M20 Capacito r Mfr-A Fam-2 39 pF 402 ok\n" +
        "M21 Inductor Mfr-B Fam-5 7.8 nH 402 ok\n" +
        "M22 Capacito r Mfr-A Fam-2 12 pF 402 ok\n";

    private static string Summary(BomPasteResult r) => string.Join("; ",
        r.Parts.Select(p => $"{p.Refdes}:{p.Kind}:{p.Value}{p.Unit}:{p.Case?.Code}"));

    [Fact]
    public void SpreadsheetTable_ReadsEveryPart_UnitFromTheNextCell_CaseFromSize_DnpSkipped()
    {
        var r = BomTablePaste.TryParse(Spreadsheet);

        Assert.NotNull(r);
        Assert.True(r!.HeaderFound);
        Assert.Equal(20, r.Parts.Count);
        var m2 = r.Parts.Single(p => p.Refdes == "M2");
        Assert.Equal((SymbolKind.Resistor, "49.9", "Ω", "0402"), (m2.Kind, m2.Value, m2.Unit, m2.Case?.Code));
        Assert.Equal(["M11", "M12", "M16"],
            r.Parts.Where(p => p.Value == "0.1" && p.Unit == "µF").Select(p => p.Refdes));
        Assert.Equal("0805", r.Parts.Single(p => p.Refdes == "M14").Case?.Code);
        Assert.Equal(SymbolKind.Inductor, r.Parts.Single(p => p.Refdes == "M21").Kind);

        var dnp = Assert.Single(r.Skipped);
        Assert.Equal("M9, M13, M18", dnp.What);
        // "402" is read as imperial 0402, and a reader is told so.
        Assert.Contains(r.Notes, n => n.Contains("'0402'") && n.Contains("imperial"));
    }

    [Fact]
    public void PdfCopiedText_WithBrokenWordsAndAWrappedReferenceList_ReadsTheSameParts()
    {
        var fromPdf = BomTablePaste.TryParse(PdfText);
        var fromSheet = BomTablePaste.TryParse(Spreadsheet);

        Assert.NotNull(fromPdf);
        Assert.True(fromPdf!.HeaderFound);
        Assert.Equal(Summary(fromSheet!), Summary(fromPdf));
        Assert.Equal("M9, M13, M18", Assert.Single(fromPdf.Skipped).What);
    }

    [Fact]
    public void CsvWithoutATypeColumn_KindFromTheReference_GluedUnits_RangesAndRefusals()
    {
        const string csv =
            "Reference,Value,Footprint,Description\n" +
            "\"C1,C2\",10nF,0603,CAP CER 10NF\n" +
            "C3-C5,4.7uF,C0805,\n" +
            "R1,4k7,0402,\n" +
            "FB1,600Ω,0603,Ferrite bead\n" +
            "U1,,QFN,IC\n";

        var r = BomTablePaste.TryParse(csv);

        Assert.NotNull(r);
        Assert.Equal("C1:Capacitor:10nF:0603; C2:Capacitor:10nF:0603; C3:Capacitor:4.7µF:0805; " +
                     "C4:Capacitor:4.7µF:0805; C5:Capacitor:4.7µF:0805; R1:Resistor:4.7kΩ:0402",
                     Summary(r!));
        Assert.Contains(r.Skipped, s => s.What == "FB1" && s.Reason.Contains("not an inductance"));
        Assert.Contains(r.Skipped, s => s.What == "U1" && s.Reason.Contains("'IC'"));
    }

    [Fact]
    public void ASeriesCaseCode_ResolvesToItsTableRow()
    {
        var r = BomTablePaste.TryParse("Reference,Value,Case\nC6,120 pF,100B\nC7,2 pF,600F\n");
        Assert.Equal("C6:Capacitor:120pF:1111; C7:Capacitor:2pF:0805", Summary(r!));
    }

    // ── Round-9 field report: three tables whose values all fell to the default ───────────

    // A spreadsheet with no type column: kind from the reference, "100k" with the ohm left off, a
    // zero-ohm link fitted where an inductor was drawn, and "Not assembled" rows.
    private const string NoTypeColumn =
        "Reference\tValue\tNote\tPackage\n" +
        "C9\t100nF\tDecoupling cap\tSM/C_0402\n" +
        "R1, R2, R3\t100k\t\tSM/R_0402\n" +
        "L1, L6\t0R0 (resistor)\tTX LPF first series ind\tSM/L_0402\n" +
        "C15, C26\tNot assembled\tTX LPF first cap shunt\tSM/C_0402\n";

    [Fact]
    public void KindFromTheReference_IsKnownBeforeTheValue_SoAMultiplierWithoutItsUnitReads()
    {
        var r = BomTablePaste.TryParse(NoTypeColumn)!;
        Assert.Equal(["100kΩ"], r.Parts.Where(p => p.Refdes.StartsWith('R')).Select(p => p.Value + p.Unit).Distinct());
    }

    [Fact]
    public void ATypeWordInTheValueCell_WinsOverTheReferencePrefix()
    {
        var r = BomTablePaste.TryParse(NoTypeColumn)!;
        var l1 = r.Parts.Single(p => p.Refdes == "L1");
        Assert.Equal((SymbolKind.Resistor, "0", "Ω"), (l1.Kind, l1.Value, l1.Unit));
    }

    [Fact]
    public void NotAssembled_IsDoNotPopulate_AndADielectricCodeIsNot()
    {
        var r = BomTablePaste.TryParse(NoTypeColumn)!;
        Assert.Equal("C15, C26", Assert.Single(r.Skipped).What);
        Assert.DoesNotContain(r.Parts, p => p.Refdes is "C15" or "C26");

        var np0 = BomTablePaste.TryParse("Reference\tValue\tType\nC1\t10 pF\tNP0\nC2\tNM\tCapacitor\n")!;
        Assert.Equal("C1", Assert.Single(np0.Parts).Refdes);
        Assert.Equal("C2", Assert.Single(np0.Skipped).What);
    }

    // Values written in the shorthand a board tool exports: R for the ohm, a bare SI prefix on C.
    [Fact]
    public void ShorthandValues_ReadAgainstTheReferencesKind()
    {
        const string table =
            "Qty\tReference\tValue\tManufacturer\n" +
            "1\tC101\t10U\tMfrA\n" +
            "4\tC124,C200\t100N\tMfrA\n" +
            "1\tRP200\t100R\tMfrB\n" +
            "1\tR1\t0R\tMfrB\n" +
            "1\tR103\t330K\tMfrB\n";

        var r = BomTablePaste.TryParse(table)!;

        Assert.Equal("C101:Capacitor:10µF:; C124:Capacitor:100nF:; C200:Capacitor:100nF:; " +
                     "RP200:Resistor:100Ω:; R1:Resistor:0Ω:; R103:Resistor:330kΩ:", Summary(r));
    }

    // No Value column: the value is inside the description; a Z prefix names no kind.
    [Fact]
    public void AValueInsideTheDescription_IsRead_WithTheCaseBesideIt()
    {
        const string table =
            "Ref.\tPart name\tPcs/unit\tDescription\tManufacturer\tPart number\n" +
            "R21, R22\tR_100_0402_F\t2\tResistor, 100 ohms, 0402, ±1%\tMfrA\tPN-001\n" +
            "Z113, Z132\tL_27N_0402_J\t2\tInductor, 27n, 0402, ±5%\tMfrB\tPN-002\n" +
            "Z43\tC_8P2_0402_NP0_C_50\t1\tCapacitor, 8p2, 0402, NP0, +-0.25pF, 50V\tMfrB\tPN-003\n" +
            "Z52\tL_5N6_0402_S\t1\tInductor, 5n6, 0402, Monolithic type, +/-0.3 nH\tMfrB\tPN-004\n";

        var r = BomTablePaste.TryParse(table)!;

        Assert.Equal("R21:Resistor:100Ω:0402; R22:Resistor:100Ω:0402; Z113:Inductor:27nH:0402; " +
                     "Z132:Inductor:27nH:0402; Z43:Capacitor:8.2pF:0402; Z52:Inductor:5.6nH:0402", Summary(r));
    }

    [Theory]
    [InlineData("Remember to check the bias network, then re-run the sweep.")]
    [InlineData("C1 10 nF 0402")]                                   // one line is not a table
    [InlineData("{\"Components\":[]}")]                             // another paste's JSON
    public void TextThatIsNotAPartsTable_IsNotClaimed(string text)
        => Assert.Null(BomTablePaste.TryParse(text));
}
