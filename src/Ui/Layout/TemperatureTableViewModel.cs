using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Materials editor redesign (2026-09-29) — one of a material's temperature tables, σ(T) or k(T), edited point by point in
/// the Materials editor. Until then a table was carried and never shown ("has a σ(T) or k(T) table — preserved"), so the
/// values a thermal run actually reads could not be seen, let alone changed.
///
/// <para>Every gesture commits the WHOLE table through the row's list as one undo entry, sorted by temperature, the order
/// the record requires. What <see cref="MaterialValidation"/> would refuse — a repeated temperature, a value that is not
/// positive — is refused here before it is written. An empty table is written as no table (the key omitted, §1e).</para>
/// </summary>
public sealed partial class TemperatureTableViewModel : ObservableObject
{
    private readonly MaterialRowViewModel _row;
    private readonly Func<TechMaterial, List<TechTemperaturePoint>?> _get;
    private readonly Action<TechMaterial, List<TechTemperaturePoint>?> _set;
    private readonly Func<TechMaterial, double?> _scalar;
    private readonly string _scalarName;

    internal TemperatureTableViewModel(MaterialRowViewModel row, string title, string valueUnit,
                                       Func<TechMaterial, List<TechTemperaturePoint>?> get, Action<TechMaterial, List<TechTemperaturePoint>?> set,
                                       Func<TechMaterial, double?> scalar, string scalarName)
    {
        _row = row;
        Title = title;
        ValueUnit = valueUnit;
        _get = get;
        _set = set;
        _scalar = scalar;
        _scalarName = scalarName;
        Reload();
    }

    /// <summary>"σ(T)" or "k(T)".</summary>
    public string Title { get; }

    /// <summary>The value column's unit.</summary>
    public string ValueUnit { get; }

    public ObservableCollection<TemperaturePointViewModel> Points { get; } = [];

    public bool HasPoints => Points.Count > 0;

    /// <summary>The expander's line: "k(T) table — 9 points", or that there is none.</summary>
    public string Header => Points.Count switch
    {
        0 => $"{Title} table — none stated",
        1 => $"{Title} table — 1 point",
        var n => $"{Title} table — {n} points",
    };

    /// <summary>Whether its points may be edited — its row's.</summary>
    public bool IsEditable => _row.IsEditable;

    /// <summary>What the table does when stated, and what is read without it.</summary>
    public string Note => HasPoints
        ? $"Stated, the table wins over {_scalarName} for a thermal run, interpolated linearly and held at its end values."
        : $"No table: a thermal run reads the constant {_scalarName}.";

    /// <summary>Re-reads the record, reconciling the point rows IN PLACE so a box focus has just moved into survives a commit
    /// (the table's own rows are kept for the same reason).</summary>
    internal void Reload()
    {
        var points = _get(_row.Material) ?? [];
        for (int i = 0; i < points.Count; i++)
        {
            if (i >= Points.Count) Points.Add(new TemperaturePointViewModel(this, i));
            else Points[i].Refresh();
        }
        while (Points.Count > points.Count) Points.RemoveAt(Points.Count - 1);
        OnPropertyChanged(nameof(HasPoints));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(IsEditable));
    }

    internal TechTemperaturePoint? PointAt(int index)
        => _get(_row.Material) is { } list && index < list.Count ? list[index] : null;

    /// <summary>Adds a point 25 °C above the last, at the last's value — or, on an empty table, 20 °C at the constant.</summary>
    [RelayCommand]
    private void AddPoint()
    {
        if (_row.Refuse()) return;
        var list = Copy();
        double t, v;
        if (list.Count == 0)
        {
            if (_scalar(_row.Material) is not { } c || !(c > 0))
            {
                _row.Refused($"State {_scalarName} of {_row.Name} first: a table's first point is at 20 °C, where it should agree with it.");
                return;
            }
            (t, v) = (20, c);
        }
        else (t, v) = (list[^1].TempC + 25, list[^1].Value);
        list.Add(new TechTemperaturePoint { TempC = t, Value = v });
        Commit(list, $"Add a {Title} point to {_row.Name}");
    }

    [RelayCommand]
    private void RemovePoint(TemperaturePointViewModel? point)
    {
        if (point is null || _row.Refuse()) return;
        var list = Copy();
        if (point.Index >= list.Count) return;
        list.RemoveAt(point.Index);
        Commit(list, $"Remove a {Title} point of {_row.Name}");
    }

    [RelayCommand]
    private void Clear()
    {
        if (!HasPoints || _row.Refuse()) return;
        Commit([], $"Clear the {Title} table of {_row.Name}");
    }

    /// <summary>Writes one point's temperature or value, typed as text. Blank is not a point's: a point is removed with ✕.</summary>
    internal void SetPoint(int index, string? tempText, string? valueText)
    {
        var current = PointAt(index);
        if (current is null) return;
        bool tOk = MaterialsTableViewModel.TryParse(tempText, out double? t) && t is not null;
        bool vOk = MaterialsTableViewModel.TryParse(valueText, out double? v) && v is not null;
        if (tOk && vOk && t == current.TempC && v == current.Value) return;
        if (_row.Refuse()) { Reload(); return; }
        if (!tOk || !vOk)
        {
            _row.Refused($"A {Title} point needs a temperature and a value; remove the point with ✕ instead of clearing it.");
            Reload();
            return;
        }
        if (!(v > 0))
        {
            _row.Refused($"A {Title} value must be positive.");
            Reload();
            return;
        }
        var list = Copy();
        if (list.Where((_, i) => i != index).Any(p => p.TempC == t))
        {
            _row.Refused($"The {Title} table already has a point at {MaterialsTableViewModel.Show(t)} °C.");
            Reload();
            return;
        }
        list[index] = new TechTemperaturePoint { TempC = t!.Value, Value = v!.Value };
        Commit(list, $"Set a {Title} point of {_row.Name}");
    }

    private List<TechTemperaturePoint> Copy()
        => [.. (_get(_row.Material) ?? []).Select(p => new TechTemperaturePoint { TempC = p.TempC, Value = p.Value })];

    private void Commit(List<TechTemperaturePoint> list, string description)
    {
        var sorted = list.OrderBy(p => p.TempC).ToList();
        var material = _row.Material;
        _row.Edit(() => _set(material, sorted.Count == 0 ? null : sorted), description);
        Reload();
    }
}

/// <summary>One point of a <see cref="TemperatureTableViewModel"/>: its temperature and its value, each committed on its own.</summary>
public sealed partial class TemperaturePointViewModel : ObservableObject
{
    private readonly TemperatureTableViewModel _table;

    internal TemperaturePointViewModel(TemperatureTableViewModel table, int index)
    {
        _table = table;
        Index = index;
    }

    public int Index { get; }

    public TemperatureTableViewModel Table => _table;

    internal void Refresh() => OnPropertyChanged(string.Empty);

    public string TempText
    {
        get => MaterialsTableViewModel.Show(_table.PointAt(Index)?.TempC);
        set => _table.SetPoint(Index, value, ValueText);
    }

    public string ValueText
    {
        get => MaterialsTableViewModel.Show(_table.PointAt(Index)?.Value);
        set => _table.SetPoint(Index, TempText, value);
    }
}
