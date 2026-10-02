using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// brief-em3d-98 R-em3d98-9 — the "Is this solved?" legend: <see cref="SolveBadges"/> itself, one column per solver kind and one
/// row per look, each cell drawn from FIXTURE statuses through <see cref="SolveBadgeRules.Pick"/> — the real control and the
/// real rule, never a picture of them. No run directory is needed: the control takes statuses.
/// </summary>
public static class DocSolvedFixtures
{
    /// <summary>One legend row: the look, whether it carries the <c>*</c>, and the row's words.</summary>
    public sealed record LegendRow(SolveBadgeLook Look, bool Partial, string Words);

    /// <summary>The rows, top to bottom: the brief's table in the order a reader meets it.</summary>
    public static readonly IReadOnlyList<LegendRow> Rows =
    [
        new(SolveBadgeLook.Solid,  false, "Active setup: solved, current"),
        new(SolveBadgeLook.Hollow, false, "Active setup: solved, out of date"),
        new(SolveBadgeLook.Faded,  false, "Another setup: solved, current"),
        new(SolveBadgeLook.Solid,  true,  "Active setup: current, partial"),
        new(SolveBadgeLook.Hollow, true,  "Active setup: out of date, partial"),
        new(SolveBadgeLook.Faded,  true,  "Another setup: current, partial"),
    ];

    /// <summary>The statuses (and active setup) that make <see cref="SolveBadgeRules.Pick"/> draw exactly
    /// <paramref name="row"/>'s glyph for <paramref name="kind"/>.</summary>
    public static SolveBadgeSet SetFor(SolverKind kind, LegendRow row)
    {
        var solved = DateTime.Today.AddHours(14).AddMinutes(32);
        var took = TimeSpan.FromMinutes(18);
        var ended = row.Partial ? C3dRunState.Cancelled : C3dRunState.Complete;
        SetupSolveStatus Leg(string setup, SolveState state) => new(setup, kind, state, row.Partial,
            state == SolveState.OutOfDate ? "the model" : null, solved, took, null, EndedAs: ended);
        return row.Look switch
        {
            SolveBadgeLook.Solid  => new([Leg("EM1", SolveState.Current)], "EM1"),
            SolveBadgeLook.Hollow => new([Leg("EM1", SolveState.OutOfDate), Leg("EM2", SolveState.Current)], "EM1"),
            _                     => new([Leg("EM2", SolveState.Current)], "EM1"),
        };
    }

    private static string Capitalised(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>The legend grid.</summary>
    public static FigureScene Legend()
    {
        var kinds = SolveBadgeRules.Order;
        var grid = new Grid { Margin = new Thickness(14, 10), ColumnSpacing = 18, RowSpacing = 7 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        foreach (var _ in kinds) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (int r = 0; r <= Rows.Count; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int c = 0; c < kinds.Length; c++)
        {
            var head = new TextBlock
            {
                Text = Capitalised(C3dSolveStatus.Name(kinds[c])), FontWeight = FontWeight.SemiBold, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(head, c + 1);
            grid.Children.Add(head);
        }
        for (int r = 0; r < Rows.Count; r++)
        {
            var words = new TextBlock { Text = Rows[r].Words, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(words, r + 1);
            grid.Children.Add(words);
            for (int c = 0; c < kinds.Length; c++)
            {
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                cell.Children.Add(new SolveBadges { Badges = SetFor(kinds[c], Rows[r]), GlyphSize = 12 });
                cell.Children.Add(new TextBlock
                {
                    Text = SolveBadgeRules.ShortName(kinds[c]) + (Rows[r].Partial ? ", partial" : ""),
                    FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0x80, 0x80, 0x80)),
                });
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c + 1);
                grid.Children.Add(cell);
            }
        }
        return new FigureScene(grid);
    }
}
