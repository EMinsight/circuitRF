// One row of Create Schematic from Artwork's parts table — brief-artsch-8-gui-command.md R-as8-3; overview D10.
//
// A PROJECTION of a PartRow, re-made on every recognition. The three editable cells do not change the row: they hand
// their text to the dialog, which keeps it as the override a parts CSV would carry and lays it over the next
// recognition (R-as8-2) — so the CSV stays the contract and a re-run never loses an edit. Every other cell is measured
// on the board and is read-only, as the CSV's reader treats it.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Design.Layout.Recognition;

namespace CircuitRF.Ui.Recognition;

public sealed partial class PartsTableRowViewModel : ObservableObject
{
    /// <summary>The Model cell's last row: picks a file.</summary>
    public const string BrowseModel = "Browse…";

    private readonly Action<PartsTableRowViewModel, string, string> _edited;
    private bool _refreshing;

    public PartsTableRowViewModel(PartsTable table, PartRow row, IReadOnlyList<string> modelFiles,
                                  Action<PartsTableRowViewModel, string, string> edited)
    {
        _edited = edited;
        Refdes = row.Refdes;
        Row = row;
        ModelOptions = ["Ideal", .. modelFiles, BrowseModel];
        _refreshing = true;
        KindText = PartsTable.KindText(row.Kind);
        ValueText = PartsTableCsv.Cell(table, row, "Value");
        ModelText = row.Model == PartModelKind.SnP && row.ModelFile is { Length: > 0 } f ? f : "Ideal";
        if (!ModelOptions.Contains(ModelText)) ModelOptions.Insert(1, ModelText);
        _refreshing = false;

        Connection = PartsTableCsv.Cell(table, row, "Connection");
        Case = PartsTableCsv.Cell(table, row, "Case");
        Variable = row.Variable ?? "";
        PartNumber = row.PartNumber ?? "";
        X = PartsTableCsv.Cell(table, row, "X");
        Y = PartsTableCsv.Cell(table, row, "Y");
        Confidence = row.Confidence;
        string evidence = PartsTableCsv.Cell(table, row, "Evidence");
        EvidenceTip = string.Join("\n", new[] { $"{PartsTable.ConfidenceText(row.Confidence)} confidence", evidence }
                                         .Concat(row.Notes).Where(s => s.Length > 0));
    }

    /// <summary>The recognised row this projects.</summary>
    public PartRow Row { get; }

    public string Refdes { get; }
    public string Connection { get; }
    public string Case { get; }
    public string Variable { get; }
    public string PartNumber { get; }
    public string X { get; }
    public string Y { get; }
    public PartConfidence Confidence { get; }

    /// <summary>The confidence dot's tooltip: the evidence and the row's notes — no prose under the table (R-as8-3).</summary>
    public string EvidenceTip { get; }

    /// <summary>The dot's colour, by confidence.</summary>
    public IBrush ConfidenceBrush => new ImmutableSolidColorBrush(Color.Parse(Confidence switch
    {
        PartConfidence.High => "#2E9D4A",
        PartConfidence.Medium => "#D9A21B",
        _ => "#C8463A",
    }));

    /// <summary>R-as4-2's kinds, as the table spells them.</summary>
    public static IReadOnlyList<string> KindOptions { get; } = [.. Enum.GetValues<PartKind>().Select(PartsTable.KindText)];

    /// <summary>Ideal, the workspace's two-port files, and <see cref="BrowseModel"/>.</summary>
    public List<string> ModelOptions { get; }

    [ObservableProperty] private string _kindText = "";
    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private string _modelText = "Ideal";

    /// <summary>The variable an unknown value stands for, shown greyed in the empty Value cell.</summary>
    public string ValueWatermark => Variable;

    /// <summary>True when the Value cell holds text the table would refuse for this kind — a dimension mismatch, or
    /// not a value at all. The same reader the CSV is read with (<see cref="PartsTableCsv.TryReadValue"/>).</summary>
    public bool ValueError
    {
        get
        {
            if (ValueText.Trim().Length == 0) return false;
            var kind = PartsTable.ParseKind(KindText) ?? Row.Kind;
            var generated = kind == PartKind.Unknown ? PartKind.C : kind;
            if (generated is not (PartKind.R or PartKind.L or PartKind.C)) return true;
            return !PartsTableCsv.TryReadValue(ValueText, generated, out _, out _);
        }
    }

    partial void OnKindTextChanged(string value)
    {
        OnPropertyChanged(nameof(ValueError));
        if (!_refreshing) _edited(this, "Kind", value);
    }

    partial void OnValueTextChanged(string value)
    {
        OnPropertyChanged(nameof(ValueError));
        if (!_refreshing) _edited(this, "Value", value);
    }

    partial void OnModelTextChanged(string value)
    {
        if (!_refreshing) _edited(this, "Model", value);
    }

    /// <summary>A cell's text as the table writes it, for sorting and filtering.</summary>
    public string SortKey(string column) => column switch
    {
        "Refdes" => Refdes,
        "Kind" => KindText,
        "Connection" => Connection,
        "Case" => Case,
        "Value" => ValueText,
        "Model" => ModelText,
        "PartNumber" => PartNumber,
        "X" => X,
        "Y" => Y,
        "Confidence" => ((int)Confidence).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => Refdes,
    };

    /// <summary>Whether the filter box's text is in any cell a reader would search.</summary>
    public bool Matches(string filter) =>
        filter.Length == 0
        || new[] { Refdes, KindText, ValueText, Variable, Case, PartNumber, Connection, ModelText }
               .Any(s => s.Contains(filter, StringComparison.OrdinalIgnoreCase));
}
