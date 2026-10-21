using System;
using System.Collections.Generic;
using CircuitRF.Design.Smith;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// The discrete-value toggle — restrict every inductance and capacitance to a ladder of buyable
/// parts (<c>docs/design/smith-chart.md</c> §5.6a; owner instruction, 2026-09-21).
/// </summary>
/// <remarks>
/// <b>It constrains what an EDIT produces and stores the result</b>, rather than drawing a snapped
/// value over a continuous one. Every write goes through one of two doors —
/// <c>SetElementValue</c> for a slider or a typed value, and <c>DragGripperTo</c> for a handle on
/// the chart — and both ask <see cref="SnapIfEnabled"/> on the way past. There is no third door,
/// which is what makes "the document holds ladder values" true rather than mostly true.
///
/// <para><b>The ladders live in <see cref="SmithPreferredValueStore"/>, not here and not in the
/// document</b> — per-user state, because it describes a parts drawer rather than this design. The
/// arithmetic is <see cref="SmithPreferredValues"/>'s, below the firewall.</para>
/// </remarks>
public sealed partial class SmithChartViewModel
{
    /// <summary>
    /// Restrict L and C to the preferred values. <b>Off by default</b>, and <b>turning it ON snaps
    /// what is already there</b> — a toggle that only constrained the NEXT edit would leave the
    /// window claiming a discrete design while showing 2.37 nH.
    /// </summary>
    /// <remarks>
    /// <b>The flag and the snap are ONE undo entry</b>, because they are one gesture. Splitting them
    /// would let an undo take the flag off and leave the values moved, which is a state the user
    /// never asked for and cannot get back to by pressing the button again.
    ///
    /// <para><b>Turning it OFF moves nothing.</b> The values on the ladder are the design now; a
    /// toggle that restored what was there before would need a memory of it, and the way back is the
    /// undo stack, which already has one.</para>
    /// </remarks>
    public bool SnapToPreferredValues
    {
        get => _design.SnapToPreferredValues;
        set
        {
            if (_design.SnapToPreferredValues == value) return;

            int moved = 0;
            Edit(value ? "Snap to preferred values" : "Allow continuous values", () =>
            {
                _design.SnapToPreferredValues = value;
                if (value) moved = SnapDesign();
            });

            OnPropertyChanged();
            if (moved > 0) StripNotice = SnapNotice(moved);
        }
    }

    /// <summary>Flips <see cref="SnapToPreferredValues"/> — what the staircase button in the network
    /// toolbar does. A command rather than a two-way binding, for the constant-Q toggle's own
    /// reason: the control is a square <c>Button</c> and has no checked state to bind.</summary>
    [RelayCommand]
    private void ToggleSnapToPreferredValues() => SnapToPreferredValues = !SnapToPreferredValues;

    /// <summary>
    /// Re-snaps the design onto whatever the ladders hold NOW — what editing the value list does to
    /// an open document that has the toggle on.
    /// </summary>
    /// <remarks>
    /// <b>A list edit that left the open design alone would leave the toggle saying something
    /// false</b>: the window would claim every value is on the ladder while the ladder had just been
    /// replaced under it. So it re-snaps — as its own undo entry, described in the list's terms
    /// rather than the toggle's, and saying how many values moved. With the toggle off it does
    /// nothing at all, because there is nothing it could be true about.
    /// </remarks>
    public void ReapplyPreferredValues()
    {
        // An E series does not read the edited list, so there is nothing to re-snap.
        if (!_design.SnapToPreferredValues || _design.DiscreteSeries != SmithDiscreteSeries.Preferred) return;

        int moved = 0;
        Edit("Apply the edited preferred values", () => moved = SnapDesign());

        if (moved > 0) StripNotice = SnapNotice(moved);
    }

    /// <summary>
    /// <paramref name="value"/> on the ladder, when the toggle is on and <paramref name="p"/> is on a
    /// ladder at all. The identity otherwise.
    /// </summary>
    internal double SnapIfEnabled(SmithParameter p, double value)
    {
        if (!_design.SnapToPreferredValues) return value;
        var (caps, inds) = Ladders();
        return SmithPreferredValues.Snap(value, SmithPreferredValues.LadderFor(p, caps, inds));
    }

    /// <summary>
    /// Which ladder the toggle snaps onto — the preferences ladder or an E series, the choice the Tune
    /// and Optimize panels' <i>Standard values</i> menu offers a tune line. <b>Choosing one turns the
    /// toggle on</b> and snaps the design onto it, as one undo entry: picking a series is asking for
    /// discrete values on it.
    /// </summary>
    public SmithDiscreteSeries DiscreteSeries => _design.DiscreteSeries;

    public bool IsSeriesPreferred => _design.DiscreteSeries == SmithDiscreteSeries.Preferred;
    public bool IsSeriesE6        => _design.DiscreteSeries == SmithDiscreteSeries.E6;
    public bool IsSeriesE12       => _design.DiscreteSeries == SmithDiscreteSeries.E12;
    public bool IsSeriesE24       => _design.DiscreteSeries == SmithDiscreteSeries.E24;
    public bool IsSeriesE48       => _design.DiscreteSeries == SmithDiscreteSeries.E48;
    public bool IsSeriesE96       => _design.DiscreteSeries == SmithDiscreteSeries.E96;

    /// <summary>What the discrete-values button's tooltip names as the ladder in force.</summary>
    public string DiscreteSeriesLabel
        => IsSeriesPreferred ? "preferences ladder" : _design.DiscreteSeries.ToString();

    /// <summary>Picks the ladder — see <see cref="DiscreteSeries"/>. The token is the enum member's name.</summary>
    [RelayCommand]
    private void SetDiscreteSeries(string token)
    {
        if (!Enum.TryParse<SmithDiscreteSeries>(token, ignoreCase: true, out var series)) return;
        if (series == _design.DiscreteSeries && _design.SnapToPreferredValues) return;

        string label = series == SmithDiscreteSeries.Preferred ? "preferred" : series.ToString();
        int moved = 0;
        Edit($"Snap to {label} values", () =>
        {
            _design.DiscreteSeries        = series;
            _design.SnapToPreferredValues = true;
            moved = SnapDesign();
        });

        OnPropertyChanged(nameof(SnapToPreferredValues));
        NotifyDiscreteSeries();
        if (moved > 0) StripNotice = SnapNotice(moved);
    }

    private void NotifyDiscreteSeries()
    {
        OnPropertyChanged(nameof(DiscreteSeries));
        OnPropertyChanged(nameof(DiscreteSeriesLabel));
        OnPropertyChanged(nameof(IsSeriesPreferred));
        OnPropertyChanged(nameof(IsSeriesE6));
        OnPropertyChanged(nameof(IsSeriesE12));
        OnPropertyChanged(nameof(IsSeriesE24));
        OnPropertyChanged(nameof(IsSeriesE48));
        OnPropertyChanged(nameof(IsSeriesE96));
    }

    /// <summary>The capacitance and inductance ladders the document's series names.</summary>
    private (IReadOnlyList<double> Caps, IReadOnlyList<double> Inds) Ladders()
        => SmithPreferredValues.SeriesLadders(_design.DiscreteSeries)
           ?? (SmithPreferredValueStore.Capacitors, SmithPreferredValueStore.Inductors);

    private int SnapDesign()
    {
        var (caps, inds) = Ladders();
        return SmithPreferredValues.SnapDesign(_design, caps, inds);
    }

    private string SnapNotice(int moved)
    {
        string onto = IsSeriesPreferred ? "the preferred-value list" : $"the {_design.DiscreteSeries} series";
        return moved == 1 ? $"One value moved onto {onto}." : $"{moved} values moved onto {onto}.";
    }
}
