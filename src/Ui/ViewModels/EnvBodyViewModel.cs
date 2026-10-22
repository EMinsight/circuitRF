using System;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The Envelope analysis card body (type=env). Every field is the directive's own raw text, as the HB
/// body keeps it — expressions resolve at run time. The modulated stimulus is the schematic's MOD_Source:
/// the body only says how long, how accurately, where to look and how to split the work.
/// </summary>
public sealed partial class EnvBodyViewModel : ObservableObject
{
    public static readonly string[] AccuracyOptions = ["draft", "standard", "fine"];
    public static readonly string[] AccuracyHelp =
    [
        "draft — 1x the signal rate: fastest; ACPR2 and bias-node memory read ~0.4 dB / 20 % off on a measured PA",
        "standard — 4x the signal rate: converged on every measured case (ACPR within 0.03 dB of 8x)",
        "fine — 8x the signal rate: reference runs",
    ];

    public static readonly string[] EnvelopeOptions = ["auto", "single", "lattice"];
    public static readonly string[] EnvelopeHelp =
    [
        "auto — carriers farther apart than 3x their bandwidth each get their own fundamental (a two-fundamental lattice); otherwise one envelope",
        "single — ONE envelope around Fc spans every carrier and their IM3: the whole multi-carrier signal in one time vector, at a step set by the spacing (slower when wide)",
        "lattice — as auto: widely spaced carriers get their own fundamentals and the mixing lattice up to the mix order",
    ];

    [ObservableProperty] private string _stop = "500e-6";
    [ObservableProperty] private string _envelope = "auto";
    [ObservableProperty] private string _accuracy = "standard";
    [ObservableProperty] private string _maxHarm = "5";
    [ObservableProperty] private string _maxMixOrder = "7";
    [ObservableProperty] private string _probes = "";
    [ObservableProperty] private string _zmeas = "50";
    [ObservableProperty] private string _segments = "auto";
    [ObservableProperty] private string _warmup = "auto";
    [ObservableProperty] private bool _saveWaveforms = true;

    /// <summary>What the schematic's MOD_Sources will play — read-only context for the card.</summary>
    public string StimulusSummary { get; }

    public string AccuracyDescription => AccuracyHelp[Math.Max(0, Array.IndexOf(AccuracyOptions, Accuracy))];

    partial void OnAccuracyChanged(string value) => OnPropertyChanged(nameof(AccuracyDescription));

    public string EnvelopeDescription => EnvelopeHelp[Math.Max(0, Array.IndexOf(EnvelopeOptions, Envelope))];

    partial void OnEnvelopeChanged(string value) => OnPropertyChanged(nameof(EnvelopeDescription));

    public EnvBodyViewModel(SchematicEditModel model)
    {
        var mods = model.Components.Where(c => c.Symbol == SymbolKind.ModSource).ToList();
        StimulusSummary = mods.Count == 0
            ? "No MOD_Source on the schematic — place one (Library ▸ Sources) to give the envelope its signal."
            : string.Join("\n", mods.Select(m =>
            {
                int n = m.Parameters.Count(p => p.Name.StartsWith("Carrier[", StringComparison.OrdinalIgnoreCase));
                var fc = m.Parameters.FirstOrDefault(p => p.Name == "Fc");
                return $"{m.InstanceName}: {Math.Max(1, n)} carrier(s) around {fc?.Expression} {fc?.Unit}".TrimEnd();
            }));
    }

    public static EnvBodyViewModel FromAnalysis(EnvelopeAnalysis a, SchematicEditModel model) => new(model)
    {
        Stop = a.StopExpr, Accuracy = a.Accuracy, MaxHarm = a.MaxHarmExpr, MaxMixOrder = a.MaxMixOrderExpr,
        Envelope = Array.IndexOf(EnvelopeOptions, a.Envelope.ToLowerInvariant()) >= 0 ? a.Envelope.ToLowerInvariant() : "auto",
        Probes = a.Probes, Zmeas = a.ZmeasExpr, Segments = a.SegmentsExpr, Warmup = a.WarmupExpr,
        SaveWaveforms = !a.SaveWaveforms.Equals("no", StringComparison.OrdinalIgnoreCase),
    };

    public EnvelopeAnalysis BuildAnalysis(string name, bool enabled) => new(name)
    {
        Enabled = enabled,
        StopExpr = Stop.Trim(), StepExpr = "auto", Accuracy = Accuracy, MaxHarmExpr = MaxHarm.Trim(),
        MaxMixOrderExpr = MaxMixOrder.Trim(), Probes = Probes.Trim(), ZmeasExpr = Zmeas.Trim(),
        SegmentsExpr = Segments.Trim(), WarmupExpr = Warmup.Trim(), SaveWaveforms = SaveWaveforms ? "yes" : "no",
        Envelope = Envelope,
    };
}
