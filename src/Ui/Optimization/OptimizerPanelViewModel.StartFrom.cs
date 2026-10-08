// ================================================================
//  OptimizerPanelViewModel.StartFrom.cs  —  a start point handed over
//  (brief-yield-14 R-ya14-7: the DOE model optimum's Send to Optimizer)
//
//  An optimizer run starts from the schematic's own values. A point
//  handed over here replaces them for the runs that follow — applied
//  to the prepared bench in memory with TunableOverrides, the
//  evaluator's own door for a value map — until it is cleared or the
//  panel moves to another schematic. Nothing is written to the design.
// ================================================================

using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Optimization;

public sealed partial class OptimizerPanelViewModel
{
    private IReadOnlyDictionary<string, string>? _startFrom;
    private SchematicViewModel? _startFor;

    /// <summary><c>Starts from the DOE optimum: a = 1.3, b = 0.6</c>; empty when the run starts from the schematic.</summary>
    [ObservableProperty] private string _startFromText = "";

    public bool HasStartFrom => _startFrom is not null && ReferenceEquals(_startFor, _tuned);

    /// <summary>The next runs start from <paramref name="values"/> (value key → text), not the schematic's values.</summary>
    public void StartFrom(IReadOnlyDictionary<string, string> values, string label)
    {
        _startFrom = values;
        _startFor = _tuned;
        StartFromText = $"Starts from the {label}: " + string.Join(", ", values.Select(kv => $"{kv.Key} = {kv.Value}"));
        OnPropertyChanged(nameof(HasStartFrom));
        ResetCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearStartFrom()
    {
        _startFrom = null;
        _startFor = null;
        StartFromText = "";
        OnPropertyChanged(nameof(HasStartFrom));
    }

    /// <summary>The prepared bench with the handed-over start applied; the bench itself when there is none.</summary>
    private PreparedCircuit WithStart(PreparedCircuit circuit)
    {
        if (!HasStartFrom || _startFrom is not { } values || circuit.Lib is not { } lib || circuit.Tb is not { } tb) return circuit;
        var tuned = TunableOverrides.Apply(tb, lib, values);
        if (tuned.Refusal is { } why || tuned.TestBench is not { } started)
        {
            StatusText = $"The start point was not applied: {tuned.Refusal}";
            return circuit;
        }
        return PreparedCircuit.FromBench(tuned.Library ?? lib, started, circuit.BaseDirectory);
    }
}
