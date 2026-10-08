using System;
using System.Collections.Generic;
using System.IO;
using CircuitRF.Design.Results;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using RfCore.Data;

namespace CircuitRF.Ui.Tuning;

/// <summary>
/// A session's results, sent to every open Data Display under the schematic's own results path
/// (brief-tuneopt-3 R-to3-5, D8) and, on Stop, written there by <see cref="RunResultsWriter"/> — the
/// same file Simulate writes, so a display that was showing tuned data keeps showing it.
/// </summary>
public sealed class DisplayTuneSink(
    string baseDir,
    string schematicKey,
    string? fileNameOverride,
    Func<IEnumerable<DataSourceLibraryViewModel>> openLibraries,
    IMessageSink? messages,
    string chip = DataSourceLibraryViewModel.TuningChip) : ITuneResultSink, Optimization.IOptimizerDisplay
{
    /// <summary>The results file every published DataSet stands in for.</summary>
    public string ResultsPath { get; } = Path.GetFullPath(Path.Combine(
        ResultsWriter.ResultsDirectory(baseDir), RunResultsWriter.ResolveFileName(fileNameOverride, schematicKey)));

    public void Publish(DataSet data)
    {
        foreach (var lib in openLibraries()) lib.Publish(ResultsPath, data, chip);
    }

    /// <summary>The Optimizer's best point; <paramref name="partial"/> when it ran only the goals'
    /// analyses, so the others keep the file's data and draw dimmed (brief-tuneopt-10 R-to10-7).</summary>
    public void Publish(DataSet data, bool partial)
    {
        foreach (var lib in openLibraries()) lib.Publish(ResultsPath, data, chip, partial);
    }

    public void Commit(DataSet data, IReadOnlyDictionary<string, string> values)
    {
        var written = RunResultsWriter.WriteRun(
            baseDir, schematicKey, TunedProvenance.Stamp(data, values), messages, fileNameOverride);
        if (written.Count == 0) return;
        // The file now holds what every display is showing: keep it as the file's version, so nothing
        // flickers back to the old run and nothing re-reads what is already in memory.
        foreach (var lib in openLibraries()) lib.KeepPublishedAsFile(ResultsPath);
    }

    public void Drop()
    {
        foreach (var lib in openLibraries())
        {
            lib.Unpublish(ResultsPath);
            lib.ClearSnapshot(ResultsPath);
        }
    }

    public void Snapshot()
    {
        foreach (var lib in openLibraries()) lib.TakeSnapshot(ResultsPath);
    }

    public void ClearSnapshot()
    {
        foreach (var lib in openLibraries()) lib.ClearSnapshot(ResultsPath);
    }
}
