// What reading a .csch notices and does not refuse it for (SchematicLoadAudit). Found by an agent
// writing a schematic from the reference topics alone: a component whose type it spelt "Kind" was read
// as a resistor and simulated, a Parameters map made a component an Unknown placeholder, a
// {"Name","Value"} parameter reached the netlist as a quoted string, and an analysis whose Type was
// "sparam" was dropped — each with nothing said.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class SchematicLoadAuditTests
{
    private static SchematicEditModel Read(string componentsAndRest)
        => SchematicPersistence.Deserialize(
            "{ \"FormatVersion\": 2, \"CellName\": \"t\", " + componentsAndRest + " }").model;

    /// <summary>The case that produced a wrong circuit: no "Symbol" reads as the enum's first member, a
    /// resistor, and that is an ERROR naming the key the author used instead — which is not then
    /// reported a second time as an unknown field.</summary>
    [Fact]
    public void AComponentWithNoSymbol_IsAnError_NamingTheKeyItUsedInstead()
    {
        var model = Read("""
            "Components": [ { "InstanceName": "TL1", "Kind": "Mlin", "X": 0, "Y": 0,
                              "Parameters": [ { "Name": "W", "Expression": "30um" } ] } ]
            """);

        Assert.Equal(SymbolKind.Resistor, model.Components.Single().Symbol);   // what the reader does
        var finding = Assert.Single(model.LoadFindings);
        Assert.Equal(SchematicLoadFindingKind.MissingSymbol, finding.Kind);
        Assert.True(finding.IsError);
        Assert.Contains("'TL1'", finding.Message);
        Assert.Contains("'Kind'", finding.Message);
        Assert.Contains("Resistor", finding.Message);
    }

    [Fact]
    public void ParametersWrittenAsAnObject_IsAWrongShapeError()
    {
        var model = Read("""
            "Components": [ { "InstanceName": "C1", "Symbol": "Capacitor", "Parameters": { "C": "1pF" } } ]
            """);

        var finding = Assert.Single(model.LoadFindings);
        Assert.Equal(SchematicLoadFindingKind.WrongShape, finding.Kind);
        Assert.True(finding.IsError);
        Assert.Contains("Components[0].Parameters", finding.Message);
        Assert.Contains("placeholder", finding.Message);
    }

    /// <summary>A string <c>Value</c> is the expression itself. Read as its JSON spelling it reached the
    /// netlist as <c>C="2pF"</c>.</summary>
    [Fact]
    public void AParameterValueGivenAsAString_IsReadAsItsExpression()
    {
        var model = Read("""
            "Components": [ { "InstanceName": "C1", "Symbol": "Capacitor",
                              "Parameters": [ { "Name": "C", "Value": "2pF" } ] } ]
            """);

        Assert.Equal("2pF", model.Components.Single().Parameters.Single(p => p.Name == "C").Expression);
        Assert.Empty(model.LoadFindings);
    }

    /// <summary>A misspelt key is a WARNING (a newer file carries keys this build lacks), names the
    /// spelling it meant, and is one finding however many components carry it.</summary>
    [Fact]
    public void AMisspeltKey_IsOneWarningNamingTheSpellingItMeant()
    {
        var model = Read("""
            "Components": [ { "InstanceName": "R1", "Symbol": "Resistor", "Rot": "R90" },
                            { "InstanceName": "R2", "Symbol": "Resistor", "Rot": "R90" } ]
            """);

        var finding = Assert.Single(model.LoadFindings);
        Assert.Equal(SchematicLoadFindingKind.UnknownField, finding.Kind);
        Assert.False(finding.IsError);
        Assert.Contains("'Rot'", finding.Message);
        Assert.Contains("'Rotation'", finding.Message);
        Assert.Contains("on 2 of them", finding.Message);
    }

    [Fact]
    public void AnAnalysisTheReaderDrops_IsReportedWithItsReason()
    {
        var model = Read("""
            "Components": [], "Analyses": [ { "Type": "sparam", "Name": "SP1" } ]
            """);

        Assert.Empty(model.Analyses);
        var finding = Assert.Single(model.LoadFindings);
        Assert.Equal(SchematicLoadFindingKind.AnalysisSkipped, finding.Kind);
        Assert.Contains("'sparam'", finding.Message);
        Assert.Contains("dc, sp, hb, sweep, lp, lpp", finding.Message);
    }

    /// <summary>The top-level Measurements list is read and saved but a run never evaluates it — the
    /// rows of a Meas block are what become measurements.</summary>
    [Fact]
    public void TopLevelMeasurements_AreReportedAsNotEvaluated()
    {
        var model = Read("""
            "Components": [], "Measurements": [ { "Name": "g", "Expression": "dB(SP1.S(2,1))" } ]
            """);

        var finding = Assert.Single(model.LoadFindings);
        Assert.Equal(SchematicLoadFindingKind.MeasurementsNotEvaluated, finding.Kind);
        Assert.Contains("Meas", finding.Message);
    }

    /// <summary>No false positive on any schematic the repository ships — every one was written by the
    /// application, so a finding on any of them is the audit misreading the format, not the file.</summary>
    [Fact]
    public void EveryShippedSchematic_ReadsWithNoFindings()
    {
        string root = RepoRoot();
        var files = new[] { "examples", "testdata" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.csch", SearchOption.AllDirectories))
            .ToArray();
        Assert.NotEmpty(files);

        var flagged = files
            .Select(f => (File: Path.GetRelativePath(root, f), Findings: SchematicPersistence.LoadFromFile(f).model.LoadFindings))
            .Where(x => x.Findings.Count > 0)
            .Select(x => x.File + ": " + string.Join(" | ", x.Findings.Select(f => f.Message)))
            .ToArray();

        Assert.True(flagged.Length == 0, string.Join(Environment.NewLine, flagged));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
