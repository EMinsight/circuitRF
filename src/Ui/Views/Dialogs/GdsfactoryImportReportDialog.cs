using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// What a gdsfactory kit import found (brief-gdf-2 R-gdf2-5b): cells offered; cells not offered,
/// grouped by reason; parameters not offered, as a count with the list on expand; pins dropped as
/// <c>placement</c>; and every model pairing with the kit's parts — made, or refused with the
/// parameters that did not cover. Every section is shown even when empty, so the reader sees that a
/// count of nothing was checked rather than left out.
/// </summary>
public static class GdsfactoryImportReportDialog
{
    public static Window Build(
        GdsfactoryKit kit, GdsfactoryImportReport? report, string? problem,
        IReadOnlyList<KitPaletteMerge.ModelPairing> pairings, bool allowed)
    {
        var dialog = new Window
        {
            Title = $"Import PDK — {kit.Display}",
            Width = 600,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        // Right margin keeps the scroll bar off the sections' edges.
        var body = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 14, 0) };
        body.Children.Add(new TextBlock
        {
            Text = $"{kit.Display} — gdsfactory kit", FontSize = 14, FontWeight = FontWeight.SemiBold,
        });
        body.Children.Add(Faint(kit.InstalledDirectory));

        if (!allowed)
            body.Children.Add(Para("Its code has not been allowed to run on this machine, so what it offers is not " +
                                   "known yet and its cells draw as placeholders. Settings ▸ General ▸ Generated Artwork asks again."));
        else if (report is null || report.Problem is not null)
            body.Children.Add(Para($"The kit could not be described: {report?.Problem ?? problem}"));
        else
        {
            body.Children.Add(Para(report.Tally()));

            body.Children.Add(Section($"Cells offered ({report.Offered.Count})",
                report.Offered.Select(c => report.Models.TryGetValue(c, out var m) ? $"{c} — model {m}" : c)));

            var notOffered = report.NotOffered.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            if (notOffered.Count == 0) body.Children.Add(Section("Cells not offered (0)", []));
            foreach (var (reason, cells) in notOffered)
                body.Children.Add(Section($"Not offered — {reason} ({cells.Count})",
                    cells.Select(c => string.Join(": ", c))));

            body.Children.Add(Section($"Parameters not offered ({report.ParametersNotOfferedCount})",
                report.ParametersNotOffered.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                      .Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value)}"),
                note: "Kept at the kit's own default: None, sequences, functions and spec objects have no field to edit."));

            body.Children.Add(Section($"Pins dropped as placement ({report.PlacementPortCount})",
                report.PlacementPorts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}: {kv.Value}"),
                note: "placement ports are alignment marks; every other port type is a pin."));

            var made = pairings.Where(p => p.Made).ToList();
            var refused = pairings.Where(p => !p.Made).ToList();
            body.Children.Add(Section($"Paired with a part by model ({made.Count})",
                made.Select(p => $"{p.GeneratorId} → {p.Part} (model {p.Model})")));
            body.Children.Add(Section($"Pairings refused ({refused.Count})",
                refused.Select(p => $"{p.GeneratorId} (model {p.Model}): " + string.Join("; ",
                    p.Uncovered.Select(u => u.Missing.Count == 0
                        ? $"{u.Part} — more than one cell fits"
                        : $"{u.Part} — the cell does not take {string.Join(", ", u.Missing)}"))),
                note: "A refused pair keeps a layout-only tile; the part keeps its symbol."));
        }

        var close = new Button { Content = "Close", Width = 100, IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => dialog.Close();

        dialog.Content = new DockPanel
        {
            Margin = new Thickness(20, 16),
            Children =
            {
                new StackPanel { [DockPanel.DockProperty] = Avalonia.Controls.Dock.Bottom, Margin = new Thickness(0, 12, 0, 0), Children = { close } },
                new ScrollViewer { MaxHeight = 520, Content = body },
            },
        };
        return dialog;
    }

    public static Task ShowAsync(Window owner, GdsfactoryKit kit, GdsfactoryImportReport? report, string? problem,
                                 IReadOnlyList<KitPaletteMerge.ModelPairing> pairings, bool allowed)
        => Build(kit, report, problem, pairings, allowed).ShowDialog(owner);

    private static Control Section(string header, IEnumerable<string> lines, string? note = null)
    {
        var items = lines.ToList();
        var content = new StackPanel { Spacing = 2, Margin = new Thickness(4, 2, 0, 4) };
        if (note is not null) content.Children.Add(Faint(note));
        foreach (string line in items) content.Children.Add(new TextBlock { Text = line, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        if (items.Count == 0) content.Children.Add(Faint("None."));
        return new Expander
        {
            Header = header,
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsExpanded = false,
            FontSize = 12,
        };
    }

    private static TextBlock Para(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4) };

    private static TextBlock Faint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
}
