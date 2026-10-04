// brief-em3d-108 R-em3d108-3 — the Look panel: a small flyout from the drop-down beside the Realistic toggle (owner decision D1), and
// 3D ▸ View ▸ Look…. It edits the document's Look (brief 106 §5, brief 107's three lighting keys): the environment (a studio, or a
// Radiance .hdr loaded from disk, stored relative to the .c3d), its rotation, intensity and exposure, the background, one Show box per
// row of the realistic view's chrome table (RealisticLook.Chrome — GENERATED from it, so a new row needs no panel edit), shadows,
// contact shading and the ground, and the picture camera (overview D17).
//
// Every edit is one undo entry (C3dEditorViewModel.ChangeLook); a slider drag previews and writes once, on release. Opening the panel
// turns the realistic view on (owner decision D2), since changing how it looks is the panel's whole purpose, and the header says so.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D.Look;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One environment the combo offers: a studio (its Look spelling, null for the default), or a <c>.hdr</c>.</summary>
public sealed record LookEnvironmentChoice(string Label, string? Value)
{
    public override string ToString() => Label;
}

/// <summary>One on/off Look key the panel shows: a Show box (off unless stated), or a lighting box (on unless stated false).</summary>
public sealed partial class LookToggleRow : ObservableObject
{
    private readonly C3dLookPanelViewModel _owner;

    internal LookToggleRow(C3dLookPanelViewModel owner, string key, string label, bool defaultOn, string tip)
    {
        _owner = owner;
        Key = key;
        Label = label;
        DefaultOn = defaultOn;
        Tip = tip;
    }

    public string Key { get; }
    public string Label { get; }
    public string Tip { get; }
    public bool DefaultOn { get; }

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_owner.Loading) _owner.SetToggle(this, value);
    }

    internal void Load(bool value)
    {
#pragma warning disable MVVMTK0034
        if (_isChecked == value) return;
        _isChecked = value;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(IsChecked));
    }
}

public sealed partial class C3dLookPanelViewModel : ObservableObject
{
    private readonly C3dEditorViewModel _editor;

    /// <param name="showKeys">The Look keys of the realistic view's chrome table, one Show box each; null reads the table
    /// (<see cref="RealisticLook.Chrome"/>). A test hands a stub with one more.</param>
    public C3dLookPanelViewModel(C3dEditorViewModel editor, IEnumerable<string>? showKeys = null)
    {
        _editor = editor;
        foreach (string key in showKeys ?? RealisticLook.Chrome.Select(c => c.Key))
            ShowRows.Add(new LookToggleRow(this, key, ShowLabel(key), false,
                $"Draw the {ShowLabel(key).ToLowerInvariant()} in the realistic view, exactly as the default view draws them."));
        LightingRows.Add(new LookToggleRow(this, nameof(C3dLook.Shadows), "Shadows", true, "The key light's shadows. Glass casts none."));
        LightingRows.Add(new LookToggleRow(this, nameof(C3dLook.AmbientOcclusion), "Contact shading", true,
            "Darkening where surfaces meet (ambient occlusion on the environment's light)."));
        LightingRows.Add(new LookToggleRow(this, nameof(C3dLook.Ground), "Ground", true,
            "A floor under the model's lowest point that draws only its shadow."));
        Reload();
    }

    internal bool Loading;

    // ── what the panel shows ──────────────────────────────────────────────────────────────────────────────────────

    public ObservableCollection<LookToggleRow> ShowRows { get; } = [];
    public ObservableCollection<LookToggleRow> LightingRows { get; } = [];
    public ObservableCollection<LookEnvironmentChoice> EnvironmentChoices { get; } = [];

    [ObservableProperty] private LookEnvironmentChoice? _environment;
    [ObservableProperty] private double _rotation;
    [ObservableProperty] private double _intensity;
    [ObservableProperty] private double _exposure;
    [ObservableProperty] private string _rotationText = "";
    [ObservableProperty] private string _intensityText = "";
    [ObservableProperty] private string _exposureText = "";

    public static IReadOnlyList<string> BackgroundKinds { get; } = ["Theme", "Colour", "Gradient", "Environment"];
    [ObservableProperty] private string _backgroundKind = "Theme";
    [ObservableProperty] private Avalonia.Media.Color _backgroundTop = Avalonia.Media.Colors.White;
    [ObservableProperty] private Avalonia.Media.Color _backgroundBottom = Avalonia.Media.Color.FromRgb(0xD0, 0xD4, 0xDA);
    public bool HasBackgroundColour => BackgroundKind is "Colour" or "Gradient";
    public bool HasBackgroundGradient => BackgroundKind == "Gradient";

    [ObservableProperty] private bool _hasPictureCamera;
    public string PictureCameraText => HasPictureCamera
        ? "Pictures are taken from the saved view, whatever the live view shows."
        : "Pictures are taken from the live view.";

    partial void OnHasPictureCameraChanged(bool value) => OnPropertyChanged(nameof(PictureCameraText));

    /// <summary>The header: what the panel changes, and — when opening it turned the realistic view on — that it did.</summary>
    [ObservableProperty] private string _header = HeaderText;

    public const string HeaderText = "How the realistic view looks. Saved with the design and undoable; no result depends on it.";
    public const string TurnedOnText = "Opening this panel turned the realistic view on. " + HeaderText;

    public bool IsEditable => !_editor.IsViewOnly;

    /// <summary>The last refused gesture's sentence.</summary>
    [ObservableProperty] private string _error = "";

    /// <summary>R-em3d108-3e — the panel opened: the realistic view comes on (and the header says so when this is what turned it on).</summary>
    public void Opened()
    {
        bool was = _editor.Viewer.IsRealistic;
        if (!was) _editor.Viewer.IsRealistic = true;
        Header = was ? HeaderText : TurnedOnText;
        Reload();
    }

    /// <summary>The panel closed: a drag left mid-way is shown no longer.</summary>
    public void Closed()
    {
        _drag = null;
        _editor.EndLookPreview();
    }

    /// <summary>Re-reads the document's Look: after every edit, an undo, a reload.</summary>
    public void Reload()
    {
        Loading = true;
        try { Load(); }
        finally { Loading = false; }
    }

    private void Load()
    {
        var look = _editor.Document.Look ?? new C3dLook();
        EnvironmentChoices.Clear();
        EnvironmentChoices.Add(new("Studio", null));
        EnvironmentChoices.Add(new("High key", nameof(C3dStudio.HighKey)));
        EnvironmentChoices.Add(new("Dark", nameof(C3dStudio.Dark)));
        var (studio, path) = look.EnvironmentOf(out _);
        if (path is not null)
        {
            var hdr = new LookEnvironmentChoice(Path.GetFileName(path), look.Environment);
            EnvironmentChoices.Add(hdr);
            Environment = hdr;
        }
        else Environment = EnvironmentChoices[studio switch { C3dStudio.HighKey => 1, C3dStudio.Dark => 2, _ => 0 }];
        Rotation = look.RotationDegrees;
        Intensity = look.IntensityValue;
        Exposure = look.ExposureValue;
        RotationText = Show(Rotation);
        IntensityText = Show(Intensity);
        ExposureText = Show(Exposure);
        look.TryBackground(out var kind, out var top, out var bottom);
        BackgroundKind = kind switch
        {
            C3dBackgroundKind.Solid => "Colour", C3dBackgroundKind.Gradient => "Gradient",
            C3dBackgroundKind.Environment => "Environment", _ => "Theme",
        };
        if (kind is C3dBackgroundKind.Solid or C3dBackgroundKind.Gradient)
        {
            BackgroundTop = Avalonia.Media.Color.FromRgb(top.R, top.G, top.B);
            BackgroundBottom = Avalonia.Media.Color.FromRgb(bottom.R, bottom.G, bottom.B);
        }
        foreach (var row in ShowRows.Concat(LightingRows)) row.Load(Read(look, row.Key) ?? row.DefaultOn);
        HasPictureCamera = look.Camera is not null;
    }

    partial void OnBackgroundKindChanged(string value)
    {
        OnPropertyChanged(nameof(HasBackgroundColour));
        OnPropertyChanged(nameof(HasBackgroundGradient));
        if (!Loading) WriteBackground();
    }

    // ── the environment ───────────────────────────────────────────────────────────────────────────────────────────

    partial void OnEnvironmentChanged(LookEnvironmentChoice? value)
    {
        if (Loading || value is null) return;
        Change("Environment", l => l.Environment = value.Value);
    }

    /// <summary>Load .hdr…: the file, stored relative to the <c>.c3d</c> (R-em3d106-4d). It is prefiltered once, off the UI thread, and the
    /// view keeps the environment it has until the new one is ready.</summary>
    public void LoadHdr(string absolutePath)
    {
        string dir = Path.GetDirectoryName(_editor.FilePath) ?? "";
        string stored = Path.GetRelativePath(dir, Path.GetFullPath(absolutePath)).Replace('\\', '/');
        Change("Environment", l => l.Environment = stored);
    }

    // ── the three sliders: preview while dragged, one entry on release ────────────────────────────────────────────

    private (string Key, double Value)? _drag;

    partial void OnRotationChanged(double value) => Drag(nameof(C3dLook.Rotation), Math.Round(value, 1));
    partial void OnIntensityChanged(double value) => Drag(nameof(C3dLook.Intensity), Math.Round(value, 2));
    partial void OnExposureChanged(double value) => Drag(nameof(C3dLook.Exposure), Math.Round(value, 2));

    private void Drag(string key, double value)
    {
        if (Loading || !IsEditable) return;
        _drag = (key, value);
        Loading = true;
        try
        {
            switch (key)
            {
                case nameof(C3dLook.Rotation): RotationText = Show(value); break;
                case nameof(C3dLook.Intensity): IntensityText = Show(value); break;
                default: ExposureText = Show(value); break;
            }
        }
        finally { Loading = false; }
        _editor.PreviewLook(l => SetNumber(l, key, value));
    }

    /// <summary>A slider was released (or its keys let go): the dragged value, written as ONE undo entry.</summary>
    public void CommitDrag()
    {
        if (_drag is not { } d) return;
        _drag = null;
        Change(d.Key, l => SetNumber(l, d.Key, d.Value));
    }

    /// <summary>A number box, on Enter or when it loses focus.</summary>
    public void CommitText(string key)
    {
        if (Loading) return;
        string text = (key switch { nameof(C3dLook.Rotation) => RotationText, nameof(C3dLook.Intensity) => IntensityText, _ => ExposureText }).Trim();
        if (!double.TryParse(text.TrimEnd('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
        {
            Error = $"'{text}' is not a number.";
            return;
        }
        var (lo, hi) = key switch
        {
            nameof(C3dLook.Intensity) => (C3dLook.IntensityMin, C3dLook.IntensityMax),
            nameof(C3dLook.Exposure) => (C3dLook.ExposureMin, C3dLook.ExposureMax),
            _ => (double.NegativeInfinity, double.PositiveInfinity),
        };
        if (v < lo || v > hi)
        {
            Error = string.Create(CultureInfo.InvariantCulture, $"{key} is from {lo} to {hi}.");
            return;
        }
        Change(key, l => SetNumber(l, key, v));
    }

    private static void SetNumber(C3dLook l, string key, double v)
    {
        switch (key)
        {
            case nameof(C3dLook.Rotation): l.Rotation = v; break;
            case nameof(C3dLook.Intensity): l.Intensity = v; break;
            default: l.Exposure = v; break;
        }
    }

    // ── the background ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A background colour picked (its flyout): previewed while it moves, written when the flyout closes.</summary>
    public void PreviewBackground(bool top, Avalonia.Media.Color c)
    {
        Loading = true;
        try { if (top) BackgroundTop = c; else BackgroundBottom = c; }
        finally { Loading = false; }
        string? spelled = BackgroundSpelling();
        _editor.PreviewLook(l => l.Background = spelled);
    }

    /// <summary>The colour flyout closed: the background it was left on, one entry.</summary>
    public void CommitBackground() => WriteBackground();

    private void WriteBackground() => Change("Background", l => l.Background = BackgroundSpelling());

    private string? BackgroundSpelling() => BackgroundKind switch
    {
        "Colour" => Hex(BackgroundTop),
        "Gradient" => Hex(BackgroundTop) + "," + Hex(BackgroundBottom),
        "Environment" => C3dLook.BackgroundEnvironment,
        _ => null,
    };

    private static string Hex(Avalonia.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ── the on/off keys ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A Show or lighting box ticked: its key written (its default as "not stated"), one entry.</summary>
    internal void SetToggle(LookToggleRow row, bool on)
    {
        bool? value = on == row.DefaultOn ? null : on;
        Change(row.Label, l => Write(l, row.Key, value));
    }

    /// <summary>An on/off key by name: a property of the Look, or (a key this build does not read) an unread key kept in the file.</summary>
    private static bool? Read(C3dLook look, string key)
    {
        if (typeof(C3dLook).GetProperty(key, BindingFlags.Public | BindingFlags.Instance) is { } p && p.PropertyType == typeof(bool?))
            return (bool?)p.GetValue(look);
        return look.Unread?.TryGetValue(key, out var e) == true && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
    }

    private static void Write(C3dLook look, string key, bool? value)
    {
        if (typeof(C3dLook).GetProperty(key, BindingFlags.Public | BindingFlags.Instance) is { } p && p.PropertyType == typeof(bool?))
        {
            p.SetValue(look, value);
            return;
        }
        look.Unread ??= [];
        if (value is { } v) look.Unread[key] = JsonSerializer.SerializeToElement(v);
        else look.Unread.Remove(key);
        if (look.Unread.Count == 0) look.Unread = null;
    }

    // ── R-em3d108-3d — the picture camera ─────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void UseThisView() { _editor.UseViewForPictures(); Reload(); }

    [RelayCommand]
    private void GoToPictureView() => _editor.GoToPictureView();

    [RelayCommand]
    private void ClearPictureView() { _editor.ClearPictureView(); Reload(); }

    // ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void Change(string what, Action<C3dLook> mutate)
    {
        if (!IsEditable) return;
        Error = "";
        _editor.ChangeLook($"Look: {what}", mutate);
        Reload();
    }

    /// <summary>A Show key's label: <c>ShowAirBox</c> is "Air box".</summary>
    public static string ShowLabel(string key)
    {
        string name = key.StartsWith("Show", StringComparison.Ordinal) ? key[4..] : key;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c)) { sb.Append(' ').Append(char.ToLowerInvariant(c)); }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Show(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
