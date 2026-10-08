// ================================================================
//  CorrelationsEditorViewModel.cs  —  the Correlations… grid
//  (brief-yield-10 R-ya10-3, yield overview D3)
//
//  A small square grid over the statistical entries: the cells above
//  the diagonal are the ρ of each pair, the diagonal is 1 and the cells
//  below mirror the ones above. Under the grid, when the matrix as
//  written is not positive definite, the repair Higham's method makes
//  — the matrix a run will actually use, never silently the one
//  written. Commit writes the correlate lines as one undo step.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Design.Statistics;

namespace CircuitRF.Ui.Yield;

public sealed partial class CorrelationsEditorViewModel : ObservableObject
{
    public CorrelationsEditorViewModel(TuningSetup setup)
    {
        Keys = [.. setup.Variables.Where(v => v.IsStatistical).Select(v => v.Key)];
        for (int i = 0; i < Keys.Count; i++)
        {
            var row = new CorrelationRow(Keys[i]);
            for (int j = 0; j < Keys.Count; j++)
            {
                var c = setup.Correlations.LastOrDefault(c =>
                    (c.First == Keys[i] && c.Second == Keys[j]) || (c.First == Keys[j] && c.Second == Keys[i]));
                row.Cells.Add(new CorrelationCell(this, i, j, i == j ? 1 : c?.Rho ?? 0));
            }
            Rows.Add(row);
        }
        Recompute();
    }

    /// <summary>The statistical entries, in setup order.</summary>
    public IReadOnlyList<string> Keys { get; }

    public ObservableCollection<CorrelationRow> Rows { get; } = [];

    /// <summary>The repair a run would apply, or empty when the matrix is valid as written.</summary>
    [ObservableProperty] private string _repairText = "";

    /// <summary>A cell that is not a number in (−1, 1).</summary>
    [ObservableProperty] private string _problem = "";

    /// <summary>What the panel said about the commit — a refusal in check's words; null when it landed.</summary>
    [ObservableProperty] private string? _refusal;

    /// <summary>The correlate lines, written back by the panel as one undo step.</summary>
    public event Action<IReadOnlyList<StatCorrelation>>? Committed;

    /// <summary>The pairs above the diagonal with a non-zero ρ.</summary>
    public IReadOnlyList<StatCorrelation> Correlations()
    {
        var list = new List<StatCorrelation>();
        for (int i = 0; i < Keys.Count; i++)
            for (int j = i + 1; j < Keys.Count; j++)
                if (Rows[i].Cells[j].Rho is { } rho && rho != 0)
                    list.Add(new StatCorrelation { First = Keys[i], Second = Keys[j], Rho = rho });
        return list;
    }

    internal void CellChanged(int i, int j, double? rho)
    {
        if (i != j && Rows[j].Cells[i].Rho != rho) Rows[j].Cells[i].Set(rho);
        Recompute();
    }

    private void Recompute()
    {
        var bad = Rows.SelectMany(r => r.Cells).FirstOrDefault(c => c.Row != c.Col && c.Rho is not (> -1 and < 1));
        Problem = bad is null ? "" : $"ρ({Keys[bad.Row]}, {Keys[bad.Col]}) must be a number between −1 and 1.";
        if (bad is not null) { RepairText = ""; CommitCommand.NotifyCanExecuteChanged(); return; }

        var setup = new TuningSetup
        {
            Variables = [.. Keys.Select(k => new TunableEntry { Key = k, Stat = true, Distribution = StatDistribution.Gauss })],
            Correlations = [.. Correlations()],
        };
        RepairText = StatisticsValidator.CorrelationOf(setup) is { Repaired: true } m
            ? $"Not positive definite — a run uses the nearest valid matrix (largest change {m.LargestChange.ToString("0.###", CultureInfo.InvariantCulture)})."
            : "";
        CommitCommand.NotifyCanExecuteChanged();
    }

    private bool CanCommit() => Problem.Length == 0;

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private void Commit() => Committed?.Invoke(Correlations());
}

public sealed class CorrelationRow(string key)
{
    public string Key { get; } = key;
    public ObservableCollection<CorrelationCell> Cells { get; } = [];
}

/// <summary>One ρ. The diagonal is read-only 1; editing a cell edits its mirror.</summary>
public sealed partial class CorrelationCell : ObservableObject
{
    private readonly CorrelationsEditorViewModel _owner;
    private bool _syncing;

    internal CorrelationCell(CorrelationsEditorViewModel owner, int row, int col, double rho)
    {
        _owner = owner;
        Row = row;
        Col = col;
        Rho = rho;
        _text = Format(rho);
    }

    public int Row { get; }
    public int Col { get; }

    public bool IsDiagonal => Row == Col;
    public bool IsEditable => Row < Col;

    public double? Rho { get; private set; }

    [ObservableProperty] private string _text;

    partial void OnTextChanged(string value)
    {
        if (_syncing) return;
        Rho = double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v
            : value.Trim().Length == 0 ? 0 : null;
        _owner.CellChanged(Row, Col, Rho);
    }

    internal void Set(double? rho)
    {
        Rho = rho;
        _syncing = true;
        Text = rho is { } r ? Format(r) : "?";
        _syncing = false;
    }

    private static string Format(double rho) => rho.ToString("0.###", CultureInfo.InvariantCulture);
}
