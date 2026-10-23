using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CircuitRF.Render;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.Signals;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Signals;
using RfCore.Signals;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The MOD_Source Signal Designer, captured from the live window: a MOD_Source carrying the given
/// carriers, the Designer opened on it, and its preview shown on a signal generated through the same
/// cache a simulation reads. Hosted the way <see cref="DocMatchFixtures"/> hosts the Match Designer —
/// the window's content with its own styles moved onto it.
/// </summary>
public static class DocSignalFixtures
{
    /// <summary>One NR 20 MHz carrier (30 kHz, 64QAM) at Fc = RFfreq, Pavg = Pin — the Envelope example's source.</summary>
    public static FigureScene DesignerNr20() => Designer(
        "\"NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1\"");

    /// <summary>Two NR 20 MHz carriers 400 MHz apart — the wide two-carrier case.</summary>
    public static FigureScene DesignerTwoCarrier() => Designer(
        "\"NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=-200MHz;P=0;Seed=1\"",
        "\"NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=200MHz;P=0;Seed=2\"");

    /// <summary>One NR 20 MHz carrier on the EVM tab: an unclipped source, so its EVM is the clean-source floor.</summary>
    public static FigureScene DesignerEvm() => Designer(tab: 1, clip: null,
        carriers: "\"NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1\"");

    private static FigureScene Designer(params string[] carriers) => Designer(0, null, carriers);

    private static FigureScene Designer(int tab, string? clip, params string[] carriers)
    {
        var model = new SchematicEditModel();
        var src = new EditableComponent { InstanceName = "SRC1", Symbol = SymbolKind.ModSource };
        src.Parameters.Add(new EditableParameter { Name = "Fc", Expression = "RFfreq", Dimension = UnitDimension.Frequency });
        src.Parameters.Add(new EditableParameter { Name = "Pavg", Expression = "Pin", Unit = "dBm", Dimension = UnitDimension.Power });
        src.Parameters.Add(new EditableParameter { Name = "Z", Expression = "50", Unit = "Ω", Dimension = UnitDimension.Resistance });
        for (int i = 0; i < carriers.Length; i++)
            src.Parameters.Add(new EditableParameter { Name = $"Carrier[{i + 1}]", Expression = carriers[i] });
        src.Parameters.Add(new EditableParameter { Name = "Record", Expression = "1e-3" });
        if (clip is not null) src.Parameters.Add(new EditableParameter { Name = "Clip", Expression = clip });
        model.Components.Add(src);

        var vm = new SignalDesignerViewModel();
        vm.SetTarget(new SchematicViewModel(model, messageSink: null), src, preview: false);
        // Generated on the thread pool (the cache's own Task.Run), never on this dispatcher.
        vm.ShowPreview(SignalCache.Get(vm.BuildSpec()));
        vm.PreviewTab = tab;

        var window = new SignalDesignerWindow { DataContext = vm };
        var content = window.Content as Control
            ?? throw new InvalidOperationException("SignalDesignerWindow has no content control to capture.");
        if (window.Styles.Count == 0)
            throw new InvalidOperationException("SignalDesignerWindow declares no Styles; a capture without them is silently wrong.");
        var styles = window.Styles.ToList();
        window.Content = null;
        window.Styles.Clear();
        foreach (var style in styles) content.Styles.Add(style);
        content.DataContext = vm;

        return new FigureScene(content)
        {
            AfterLayout = root =>
            {
                var theme = ThemeService.CurrentVariant == ColorVariant.Dark ? RenderTheme.Dark : RenderTheme.Light;
                vm.PlotHost.Theme = theme;
                foreach (var plot in root.GetVisualDescendants().OfType<PlotControl>())
                {
                    plot.PlotTheme = theme;
                    plot.InvalidateVisual();
                }
            },
            Cleanup = vm.Dispose,
        };
    }
}
