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
///
/// <para>When the kit ships its own drawings of its cells, a section lists the schematic symbols made from them
/// (brief-gdf-4) and says what a symbol does not give: a circuit model.</para>
///
/// <para>When the kit states a layer stack, the footer offers <b>"Also build the kit's technology"</b>,
/// checked (brief-gdf-3 R-gdf3-3a). The dialog closes with that choice; what it builds and where it
/// writes is the caller's.</para>
/// </summary>
public static class GdsfactoryImportReportDialog
{
    /// <summary>What the kit's technology would be built from, for the offer's line.</summary>
    /// <param name="Levels">Stack levels the kit states.</param>
    /// <param name="Cards">Material cards it states.</param>
    /// <param name="FileName">The <c>.ctech</c> it would be written to, in the workspace's <c>tech</c> folder.</param>
    public sealed record TechnologyOffer(int Levels, int Cards, string FileName);

    public static Window Build(
        GdsfactoryKit kit, GdsfactoryImportReport? report, string? problem,
        IReadOnlyList<KitPaletteMerge.ModelPairing> pairings, bool allowed, TechnologyOffer? technology = null,
        KitGlyphSymbol.InstallResult? symbols = null)
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

            body.Children.Add(Section($"Schematic symbols from the kit's drawings ({symbols?.Count ?? 0})",
                symbols is null ? [] : SymbolLines(symbols),
                note: KitGlyphSymbol.NoModelNote));
        }

        // The offer sits in the footer, outside the scrolled report, so it is on screen however long the
        // report is. Closing — by the button or Escape — carries the box's state either way.
        CheckBox? build = null;
        var footer = new DockPanel { [DockPanel.DockProperty] = Avalonia.Controls.Dock.Bottom, Margin = new Thickness(0, 12, 0, 0) };
        var close = new Button { Content = technology is null ? "Close" : "OK", Width = 100, IsCancel = true, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
        DockPanel.SetDock(close, Avalonia.Controls.Dock.Right);
        footer.Children.Add(close);
        if (technology is not null)
        {
            build = new CheckBox { Content = "Also build the kit's technology", IsChecked = true, FontSize = 12 };
            footer.Children.Add(new StackPanel
            {
                Spacing = 2, Margin = new Thickness(0, 0, 12, 0),
                Children =
                {
                    build,
                    Faint($"{technology.Levels} stack level(s), {technology.Cards} material card(s) → tech/{technology.FileName}. " +
                          "It becomes the workspace's default only if the workspace has none."),
                },
            });
        }
        close.Click += (_, _) => dialog.Close(build?.IsChecked == true);

        dialog.Content = new DockPanel
        {
            Margin = new Thickness(20, 16),
            Children =
            {
                footer,
                new ScrollViewer { MaxHeight = 520, Content = body },
            },
        };
        return dialog;
    }

    /// <summary>Shows the report; true when the reader left "Also build the kit's technology" checked.</summary>
    public static Task<bool> ShowAsync(Window owner, GdsfactoryKit kit, GdsfactoryImportReport? report, string? problem,
                                       IReadOnlyList<KitPaletteMerge.ModelPairing> pairings, bool allowed,
                                       TechnologyOffer? technology = null, KitGlyphSymbol.InstallResult? symbols = null)
        => Build(kit, report, problem, pairings, allowed, technology, symbols).ShowDialog<bool>(owner);

    /// <summary>One line per cell: where its symbol came from, then what the conversion could not carry.</summary>
    private static IEnumerable<string> SymbolLines(KitGlyphSymbol.InstallResult symbols)
    {
        var cells = symbols.Built.Keys.Concat(symbols.Copied.Keys).Concat(symbols.Kept)
                           .Concat(symbols.Lines.Select(l => l.Cell)).Distinct().Order(StringComparer.Ordinal);
        foreach (string cell in cells)
        {
            string source = symbols.Built.ContainsKey(cell) ? "built from its glyph"
                          : symbols.Copied.ContainsKey(cell) ? "the kit's own .csym"
                          : symbols.Kept.Contains(cell) ? "already in the workspace, kept"
                          : "not made";
            yield return $"{cell} — {source}";
            foreach (var line in symbols.Lines.Where(l => l.Cell == cell))
                yield return $"    {line.Text}";
        }
    }

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
