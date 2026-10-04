// brief-em3d-108 R-em3d108-1a / R-em3d108-2a — ONE appearance editor, with two homes: a material's detail pane in the Materials editor
// and the 3D Inspector's Appearance group. Both show the same nine fields and the same Like, and both read the ONE resolver's answer
// (AppearanceResolver): a field a target states is shown as stated; a field it does not is shown GREYED with the value it resolves
// to, its tooltip the resolver's provenance string verbatim ("role default", "Like 'Gold'", "material 'Copper' (…)"). Several
// targets whose values differ read "mixed", as the Transparency row does, and an edit writes all of them.
//
// No appearance rule lives here. The ranges are MaterialValidation's (AppearanceRanges), the values the resolver's, and every write
// goes through the host: a material's row edits its record (one entry on that file's stack, or a copy in the Materials dialog), an
// Inspector edit writes the selected objects' overrides (one undo entry). A slider PREVIEWS while dragged and COMMITS on release, the
// pattern brief 92's Transparency row set; a box commits on Enter or when it loses focus; Reset writes "not stated".

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.ThreeD.Appearance;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Appearance;

/// <summary>What an appearance editor edits: one material, or the Inspector's selection.</summary>
public interface IAppearanceHost
{
    /// <summary>Each target's own statement (null: it states none).</summary>
    IReadOnlyList<TechAppearance?> Stated { get; }

    /// <summary>Each target's look as the resolver answers it now (null: it has none, e.g. a part that did not elaborate).</summary>
    IReadOnlyList<ResolvedAppearance?> Resolved { get; }

    /// <summary>The materials a <c>Like</c> may name.</summary>
    IReadOnlyList<string> LikeChoices { get; }

    /// <summary>Why nothing here may be written, or null.</summary>
    string? ReadOnlyReason { get; }

    /// <summary>A drag's value, shown and not written.</summary>
    void Preview(string key, object? value);

    /// <summary>A drag left where it was (its targets went away): shown no longer.</summary>
    void EndPreview();

    /// <summary>Writes <paramref name="key"/> = <paramref name="value"/> (null: not stated) into every target, as one entry — an EMPTY
    /// key clears every target's whole appearance (Clear override). Returns the refusal, or null.</summary>
    string? Commit(string key, object? value);
}

/// <summary>A field's kind: a number with a slider (0–1, an optical index), a positive length with no slider, or a colour.</summary>
public enum AppearanceFieldKind { Slider, Length, Colour }

/// <summary>One of the nine fields.</summary>
public sealed partial class AppearanceFieldViewModel : ObservableObject
{
    private readonly AppearanceEditorViewModel _owner;

    internal AppearanceFieldViewModel(AppearanceEditorViewModel owner, string key, string label, AppearanceFieldKind kind, double min, double max, string tip)
    {
        _owner = owner;
        Key = key;
        Label = label;
        Kind = kind;
        Minimum = min;
        Maximum = max;
        Description = tip;
        ResetCommand = new RelayCommand(() => _owner.Write(Key, null), () => IsStated && !_owner.IsReadOnly);
    }

    public string Key { get; }
    public string Label { get; }
    public AppearanceFieldKind Kind { get; }
    public double Minimum { get; }
    public double Maximum { get; }

    /// <summary>What the field means (the label's tooltip).</summary>
    public string Description { get; }

    public bool HasSlider => Kind == AppearanceFieldKind.Slider;
    public bool IsColour => Kind == AppearanceFieldKind.Colour;
    public bool IsNumber => Kind != AppearanceFieldKind.Colour;

    /// <summary>The stated value as text, or empty when not stated (or mixed).</summary>
    [ObservableProperty] private string _text = "";

    /// <summary>Greyed in the empty box: the resolved value (or "mixed").</summary>
    [ObservableProperty] private string _placeholder = "";

    /// <summary>The slider: the stated value, else the resolved one.</summary>
    [ObservableProperty] private double _slider;

    /// <summary>Every target states it (to one value or several).</summary>
    [ObservableProperty] private bool _isStated;

    /// <summary>Where the value comes from: the resolver's provenance string, verbatim.</summary>
    [ObservableProperty] private string _provenance = "";

    /// <summary>A colour field's swatch: the stated colour, else the resolved one.</summary>
    [ObservableProperty] private Avalonia.Media.Color _colour = Avalonia.Media.Colors.Transparent;

    public IRelayCommand ResetCommand { get; }

    /// <summary>The value being dragged and not yet written; written on release.</summary>
    internal double? Dragged;

    partial void OnSliderChanged(double value)
    {
        if (_owner.Loading || !HasSlider || _owner.IsReadOnly) return;
        double v = Math.Round(Math.Clamp(value, Minimum, Maximum), 3);
        Dragged = v;
        _owner.Loading = true;
        try { Text = AppearanceEditorViewModel.Show(v); }
        finally { _owner.Loading = false; }
        _owner.Host.Preview(Key, v);
        _owner.Previewing = this;
    }

    /// <summary>The slider was released (or its keys let go): the dragged value, written as one entry.</summary>
    public void CommitSlider()
    {
        if (Dragged is not { } v) return;
        Dragged = null;
        _owner.Write(Key, v);
    }

    /// <summary>The box, on Enter or when it loses focus. Empty writes nothing (Reset clears a value).</summary>
    public void CommitText()
    {
        if (_owner.Loading) return;
        string t = Text.Trim();
        if (t.Length == 0) return;
        if (Kind == AppearanceFieldKind.Colour)
        {
            if (!t.StartsWith('#')) t = "#" + t;
            if (CircuitRF.Design.Layout.MaterialValidation.ParseColour(t) is null)
            {
                _owner.Error = $"'{Text.Trim()}' is not a colour: write #rrggbb.";
                return;
            }
            _owner.Write(Key, t.ToUpperInvariant());
            return;
        }
        if (!Layout.MaterialsTableViewModel.TryParse(t, out double? v) || v is not { } d)
        {
            _owner.Error = $"'{t}' is not a number.";
            return;
        }
        _owner.Write(Key, d);
    }

    /// <summary>A colour picked (the swatch's flyout): previewed while it moves; <see cref="CommitColour"/> writes the last one.</summary>
    public void PreviewColour(Avalonia.Media.Color c)
    {
        if (_owner.Loading || _owner.IsReadOnly) return;
        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        if (_pickedColour is null && hex == _openedColour) return;
        _pickedColour = hex;
        _owner.Host.Preview(Key, hex);
        _owner.Previewing = this;
    }

    private string? _pickedColour;
    private string? _openedColour;

    /// <summary>The colour flyout opened on <see cref="Colour"/>: the picker announcing that same colour as it shows it is not an edit.</summary>
    public void BeginColour()
    {
        _openedColour = $"#{Colour.R:X2}{Colour.G:X2}{Colour.B:X2}";
        _pickedColour = null;
    }

    /// <summary>The colour flyout closed: the colour it was left on, written as one entry — nothing when it was never moved.</summary>
    public void CommitColour()
    {
        if (_pickedColour is not { } hex) return;
        _pickedColour = null;
        _owner.Write(Key, hex);
    }

    internal void NotifyReset() => ResetCommand.NotifyCanExecuteChanged();
}

/// <summary>R-em3d108-1a / R-em3d108-2a — the appearance editor over a host.</summary>
public sealed partial class AppearanceEditorViewModel : ObservableObject
{
    public AppearanceEditorViewModel(IAppearanceHost host)
    {
        Host = host;
        string Tip(string key) => key switch
        {
            nameof(TechAppearance.BaseColor) => "A metal's reflectance, a dielectric's body colour (sRGB).",
            nameof(TechAppearance.Metallic) => "1 for a metal, 0 for a dielectric; in between only for blending.",
            nameof(TechAppearance.Roughness) => "0 a mirror, 1 matte.",
            nameof(TechAppearance.Transmission) => "How see-through it is: glass, quartz, a thin laminate.",
            nameof(TechAppearance.Ior) => "The OPTICAL index (1 to 3), a visual value: never derived from εr.",
            nameof(TechAppearance.Clearcoat) => "A glossy layer over the body: solder mask, a glossy laminate.",
            nameof(TechAppearance.ClearcoatRoughness) => "The clear coat's own roughness.",
            nameof(TechAppearance.AttenuationColor) => "The tint light picks up passing through it (sRGB).",
            nameof(TechAppearance.AttenuationDistance) => "How far light travels before it takes that tint, metres (an SI prefix is fine: 2m is 2 mm).",
            _ => "",
        };
        foreach (string key in TechAppearance.Keys)
        {
            if (TechAppearance.IsNameKey(key)) continue;
            if (TechAppearance.IsColourKey(key))
            {
                Fields.Add(new AppearanceFieldViewModel(this, key, LabelOf(key), AppearanceFieldKind.Colour, 0, 0, Tip(key)));
                continue;
            }
            var (_, lo, hi, _) = CircuitRF.Design.Layout.MaterialValidation.AppearanceRanges.First(r => r.Key == key);
            var kind = double.IsPositiveInfinity(hi) ? AppearanceFieldKind.Length : AppearanceFieldKind.Slider;
            Fields.Add(new AppearanceFieldViewModel(this, key, LabelOf(key), kind, lo, hi, Tip(key)));
        }
        ClearOverrideCommand = new RelayCommand(() => Write(null, null), () => !IsReadOnly && Host.Stated.Any(s => s is not null));
        Reload();
    }

    internal IAppearanceHost Host { get; }

    /// <summary>The nine fields, in file order (<c>Like</c> is the combo above them).</summary>
    public ObservableCollection<AppearanceFieldViewModel> Fields { get; } = [];

    public AppearanceFieldViewModel Field(string key) => Fields.First(f => f.Key == key);

    /// <summary>The Like combo's rows: "(none)" first, then every material in scope.</summary>
    [ObservableProperty] private IReadOnlyList<string> _likeChoices = [];

    /// <summary>What the targets' Like says; "(none)" for none, null while they differ.</summary>
    [ObservableProperty] private string? _like;

    [ObservableProperty] private string _likePlaceholder = "";

    [ObservableProperty] private bool _isReadOnly;
    [ObservableProperty] private string? _readOnlyReason;

    /// <summary>The last refused gesture's sentence; cleared by the next one that commits.</summary>
    [ObservableProperty] private string _error = "";

    /// <summary>The group's note, set by the host: the Inspector's "Shown in the realistic view" while it is off.</summary>
    [ObservableProperty] private string _note = "";

    /// <summary>What the note's button does (the Inspector's: turn the realistic view on), or null for no button.</summary>
    public System.Windows.Input.ICommand? NoteCommand { get; init; }

    public bool HasNote => Note.Length > 0;

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    public bool IsEditable => !IsReadOnly;

    partial void OnIsReadOnlyChanged(bool value) => OnPropertyChanged(nameof(IsEditable));

    public bool HasError => Error.Length > 0;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Clear override: every target's own appearance removed (the Inspector's; a material's row has no use for it).</summary>
    public IRelayCommand ClearOverrideCommand { get; }

    /// <summary>Whether Clear override is offered (the Inspector).</summary>
    public bool CanClearOverride { get; init; }

    public const string NoLike = "(none)";

    internal bool Loading;
    internal AppearanceFieldViewModel? Previewing;

    /// <summary>Re-reads the host: after every write, an undo, a new selection, a new scene.</summary>
    public void Reload()
    {
        Loading = true;
        try { Load(); }
        finally { Loading = false; }
        ClearOverrideCommand.NotifyCanExecuteChanged();
        foreach (var f in Fields) f.NotifyReset();
    }

    private void Load()
    {
        var stated = Host.Stated;
        var resolved = Host.Resolved;
        ReadOnlyReason = Host.ReadOnlyReason;
        IsReadOnly = ReadOnlyReason is not null;
        foreach (var f in Fields)
        {
            var values = stated.Select(s => s?.Get(f.Key)).ToList();
            var distinct = values.Distinct().ToList();
            bool stating = values.Count > 0 && values.All(v => v is not null);
            var known = resolved.OfType<ResolvedAppearance>().ToList();
            var resValues = known.Select(r => ValueOf(r.Values, f.Key)).Distinct().ToList();
            var provenances = known.Select(r => r.Provenance.GetValueOrDefault(f.Key) ?? "").Distinct().ToList();
            f.Provenance = provenances.Count == 1 ? provenances[0] : provenances.Count == 0 ? "" : "varies";
            f.IsStated = stating;
            if (distinct.Count > 1)
            {
                f.Text = "";
                f.Placeholder = "mixed";
            }
            else if (distinct.Count == 1 && distinct[0] is { } v)
            {
                f.Text = Show(v);
                f.Placeholder = "";
            }
            else
            {
                f.Text = "";
                f.Placeholder = resValues.Count == 1 ? Show(resValues[0]) : resValues.Count > 1 ? "mixed" : "";
            }
            object? shown = distinct.Count == 1 && distinct[0] is { } sv ? sv : resValues.Count == 1 ? resValues[0] : null;
            if (f.HasSlider) f.Slider = shown is double d ? d : f.Minimum;
            if (f.IsColour)
                f.Colour = shown is string hex && CircuitRF.Design.Layout.MaterialValidation.ParseColour(hex) is { } c
                    ? Avalonia.Media.Color.FromRgb(c.R, c.G, c.B) : Avalonia.Media.Colors.Transparent;
        }
        var likes = stated.Select(s => s?.Like).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        LikeChoices = [NoLike, .. Host.LikeChoices];
        Like = likes.Count == 1 ? likes[0] ?? NoLike : null;
        LikePlaceholder = likes.Count > 1 ? "mixed" : "";
    }

    partial void OnLikeChanged(string? value)
    {
        if (Loading || value is null || IsReadOnly) return;
        string? like = value == NoLike ? null : value;
        var now = Host.Stated.Select(s => s?.Like).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (now.Count == 1 && string.Equals(now[0], like, StringComparison.OrdinalIgnoreCase)) return;
        Write(nameof(TechAppearance.Like), like);
    }

    /// <summary>Writes one field (a null key: every target's override cleared) through the host, then reads it back.</summary>
    internal void Write(string? key, object? value)
    {
        if (IsReadOnly) { Error = ReadOnlyReason ?? "This cannot be edited here."; return; }
        foreach (var f in Fields) f.Dragged = null;
        Previewing = null;
        string? why = Host.Commit(key ?? "", value);
        Error = why ?? "";
        Reload();
    }

    /// <summary>After the host's targets changed under a drag: the drag's preview is shown no longer.</summary>
    public void EndStrayPreview()
    {
        if (Previewing is null) return;
        Previewing = null;
        foreach (var f in Fields) f.Dragged = null;
        Host.EndPreview();
    }

    /// <summary>A resolved value as the field shows it: a number, or a colour as <c>#RRGGBB</c>.</summary>
    internal static object ValueOf(AppearanceValues v, string key) => key switch
    {
        nameof(TechAppearance.BaseColor) => v.BaseColor.ToHex(),
        nameof(TechAppearance.Metallic) => v.Metallic,
        nameof(TechAppearance.Roughness) => v.Roughness,
        nameof(TechAppearance.Transmission) => v.Transmission,
        nameof(TechAppearance.Ior) => v.Ior,
        nameof(TechAppearance.Clearcoat) => v.Clearcoat,
        nameof(TechAppearance.ClearcoatRoughness) => v.ClearcoatRoughness,
        nameof(TechAppearance.AttenuationColor) => v.AttenuationColor.ToHex(),
        nameof(TechAppearance.AttenuationDistance) => v.AttenuationDistance,
        _ => "",
    };

    internal static string Show(object v) => v switch
    {
        double d when double.IsPositiveInfinity(d) => "∞",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        string s => s.ToUpperInvariant(),
        _ => v.ToString() ?? "",
    };

    private static string LabelOf(string key) => key switch
    {
        nameof(TechAppearance.BaseColor) => "Base colour",
        nameof(TechAppearance.Ior) => "IOR",
        nameof(TechAppearance.Clearcoat) => "Clear coat",
        nameof(TechAppearance.ClearcoatRoughness) => "Clear-coat roughness",
        nameof(TechAppearance.AttenuationColor) => "Attenuation colour",
        nameof(TechAppearance.AttenuationDistance) => "Attenuation distance",
        _ => key,
    };
}
