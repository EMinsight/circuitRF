// ================================================================
//  TrialActions.cs  —  what a designer does next with ONE selected
//  trial (brief-yield-9 R-ya9-6): Send trial to Tuning, Re-run trial,
//  Copy values, Save as corner…
//
//  The plot's context menu offers them on a single selected trial, and
//  the Yield panel's trial table (YA-10) is meant to call the same four.
//  A trial's values are TrialPick.ValuesOf — the result's own drawn
//  values — and a re-run is TrialReplay, the function `yield trial`
//  calls; what this file adds is where each lands in the window.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Statistics;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay;

public static class TrialActions
{
    /// <summary>The single selected trial on <paramref name="plot"/>, and the source it is in; null for none or several.</summary>
    public static (string Source, int Trial)? SingleSelected(Plot plot)
    {
        foreach (var t in plot.Traces)
            if (t.SourcePath is { } p && t.ElementTrials is not null && TrialSelection.Shared.For(p) is { Count: 1 } one)
                return (Path.GetFullPath(p), one.First());
        return null;
    }

    /// <summary>Loads the trial's values into the Tuning sliders of the schematic the source was run from (through
    /// <see cref="DataSourceLibraryViewModel.SendTrialToTuning"/>, which the workspace sets). False when it cannot.</summary>
    public static bool SendToTuning(DataSourceLibraryViewModel library, string source, int trial)
    {
        if (library.SendTrialToTuning is not { } send || library.DataFor(source) is not { } ds) return false;
        var values = TrialPick.ValuesOf(ds, trial);
        if (values.Count == 0) return false;
        send(values, source, $"Trial {trial}");
        return true;
    }

    /// <summary>The trial's values as text, one <c>key = value</c> per line — what Copy values puts on the clipboard.</summary>
    public static string ValuesText(DataSet ds, int trial)
        => string.Join(Environment.NewLine, TrialPick.ValuesOf(ds, trial).Select(kv => $"{kv.Key} = {kv.Value}"));

    /// <summary>
    /// Runs the trial again in full (YA-4 R-ya4-7) and shows its result as the snapshot ghost of every trace bound to
    /// the source, in every open display (tuning D9). Returns the refusal's sentence, or null.
    /// </summary>
    public static async Task<string?> RerunAsync(DataSourceLibraryViewModel library, string source, int trial)
    {
        var mode = library.DataFor(source) is { } ds && ds.Contains("yield.mode")
                   && ds["yield.mode"].Axes[0].Labels is [ "montecarlo", .. ]
            ? StatisticalMode.MonteCarlo : StatisticalMode.Yield;
        var (data, refusal) = await Task.Run(() => TrialReplay.Run(source, trial, mode));
        if (data is null) return refusal?.Render() ?? $"Trial {trial} could not be run again.";
        foreach (var lib in library.AllLibraries?.Invoke() ?? [library])
            lib.ShowSnapshot(source, data);
        return refusal?.Render();
    }
}
