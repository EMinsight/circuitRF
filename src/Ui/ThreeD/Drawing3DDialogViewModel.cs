// 3D vector copy and drawing export (2026-09-27) — Export Drawing…'s choices: the views, the sections, hidden edges,
// legend, text, page and format. It builds an Em3dDrawingRequest and nothing else; Em3dDrawingExport (src/Render) makes
// the file, so the dialog can be driven headlessly.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One section on the sheet: the plane it cuts in, and where, typed WITH a unit.</summary>
public sealed partial class Drawing3DSectionRow : ObservableObject
{
    public static IReadOnlyList<string> Planes { get; } = ["XY", "XZ", "YZ"];

    private readonly Func<int, string> _centreText;

    internal Drawing3DSectionRow(int plane, string position, Func<int, string> centreText)
    {
        _plane = plane;
        _position = position;
        _centreText = centreText;
    }

    /// <summary>0 XY (a cut at a height z), 1 XZ (at y), 2 YZ (at x).</summary>
    [ObservableProperty] private int _plane;
    [ObservableProperty] private string _position;

    /// <summary>"z =", "y =" or "x =": which coordinate the position is.</summary>
    public string Axis => Plane switch { 0 => "z =", 1 => "y =", _ => "x =" };

    partial void OnPlaneChanged(int value)
    {
        OnPropertyChanged(nameof(Axis));
        Position = _centreText(value);
    }

    /// <summary>The section this row asks for, or why it cannot be read. A bare number is refused, as the CLI's
    /// <c>--section</c> refuses it: nanometres, micrometres and millimetres are three plausible planes on one text.</summary>
    public (Em3dView? View, string? Error) Parse()
    {
        string text = (Position ?? "").Trim();
        string plane = Planes[Math.Clamp(Plane, 0, 2)];
        if (text.Length == 0) return (null, $"Section {plane}: say where it cuts, with a unit (e.g. 1.2mm).");
        if (!char.IsLetter(text[^1])) return (null, $"Section {plane} at '{text}': give the unit — '{text}mm' or '{text}um'.");
        const int PicometresPerMicron = 1_000_000;
        if (!LayoutUnits.TryParse(text, LayoutUnit.Um, PicometresPerMicron, out long pm))
            return (null, $"Section {plane} at '{text}' is not a length (nm, um, mm, mil or in).");
        var kind = Plane switch { 0 => Em3dViewKind.SectionZ, 1 => Em3dViewKind.SectionY, _ => Em3dViewKind.SectionX };
        return (new Em3dView(kind, pm * 1e-12), null);
    }
}

public sealed partial class Drawing3DDialogViewModel : ObservableObject
{
    public static IReadOnlyList<string> HiddenModes { get; } = ["Removed", "Dashed", "Shown (wire-frame)"];
    public static IReadOnlyList<string> Pages { get; } = ["A4", "Letter", "A3", "Tabloid"];
    public static IReadOnlyList<string> Formats { get; } = ["PDF", "SVG"];

    private readonly Point3 _min, _max;

    /// <param name="documentName">The title block's name.</param>
    /// <param name="min">The model's extent, metres — where a new section cuts (its centre) and the unit it is typed in.</param>
    public Drawing3DDialogViewModel(string documentName, Point3 min, Point3 max, Drawing3DChoices? remembered)
    {
        DocumentName = documentName;
        _min = min; _max = max;
        if (remembered is { } r)
        {
            if (r.Views is { } views)
            {
                bool Has(Em3dStandardView v) => views.Contains(v.ToString());
                Isometric = Has(Em3dStandardView.Isometric); Top = Has(Em3dStandardView.Top); Bottom = Has(Em3dStandardView.Bottom);
                Front = Has(Em3dStandardView.Front); Back = Has(Em3dStandardView.Back);
                Left = Has(Em3dStandardView.Left); Right = Has(Em3dStandardView.Right);
            }
            if (Enum.TryParse<Em3dHiddenEdges>(r.Hidden, out var h)) HiddenIndex = h switch { Em3dHiddenEdges.Dashed => 1, Em3dHiddenEdges.Shown => 2, _ => 0 };
            Legend = r.Legend ?? Legend;
            TextAsOutlines = r.TextAsOutlines ?? TextAsOutlines;
            OmitHidden = r.OmitHidden ?? OmitHidden;
            if (Enum.TryParse<Em3dPageSize>(r.Page, out var page)) PageIndex = (int)page;
            Landscape = r.Landscape ?? Landscape;
            if (Enum.TryParse<Em3dDrawingFormat>(r.Format, out var f)) FormatIndex = f == Em3dDrawingFormat.Svg ? 1 : 0;
        }
    }

    public string DocumentName { get; }
    public string Title => $"Export Drawing — {DocumentName}";

    [ObservableProperty] private bool _isometric = true;
    [ObservableProperty] private bool _top = true;
    [ObservableProperty] private bool _bottom;
    [ObservableProperty] private bool _front = true;
    [ObservableProperty] private bool _back;
    [ObservableProperty] private bool _left;
    [ObservableProperty] private bool _right = true;

    public ObservableCollection<Drawing3DSectionRow> Sections { get; } = [];

    /// <summary>0 Removed, 1 Dashed, 2 Shown.</summary>
    [ObservableProperty] private int _hiddenIndex;
    [ObservableProperty] private bool _legend = true;
    [ObservableProperty] private bool _textAsOutlines = true;

    /// <summary>Leave out what the 3D view has hidden, as the view shows it.</summary>
    [ObservableProperty] private bool _omitHidden = true;
    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private bool _landscape = true;
    /// <summary>0 PDF, 1 SVG.</summary>
    [ObservableProperty] private int _formatIndex;
    [ObservableProperty] private string? _error;

    /// <summary>The file extension the format writes, without the dot.</summary>
    public string Extension => FormatIndex == 1 ? "svg" : "pdf";

    /// <summary>Raised with true on Export (the request is then in <see cref="Request"/>), false on Cancel.</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>The request, once Export has been accepted.</summary>
    public Em3dDrawingRequest? Request { get; private set; }

    /// <summary>A cut through the model's centre along <paramref name="plane"/>'s normal, spelled in the unit the model's
    /// size reads in.</summary>
    public string CentreText(int plane)
    {
        double at = plane switch { 0 => (_min.Z + _max.Z) / 2, 1 => (_min.Y + _max.Y) / 2, _ => (_min.X + _max.X) / 2 };
        double size = Math.Max(_max.X - _min.X, Math.Max(_max.Y - _min.Y, _max.Z - _min.Z));
        var (scale, unit) = size >= 1e-3 ? (1e3, "mm") : (1e6, "um");
        return (at * scale).ToString("0.####", CultureInfo.InvariantCulture) + unit;
    }

    [RelayCommand]
    public void AddSection() => Sections.Add(new Drawing3DSectionRow(1, CentreText(1), CentreText));

    [RelayCommand]
    public void RemoveSection(Drawing3DSectionRow? row) { if (row is not null) Sections.Remove(row); }

    /// <summary>The request these choices make, or null with <see cref="Error"/> set.</summary>
    public Em3dDrawingRequest? BuildRequest(IReadOnlySet<string>? hiddenInView)
    {
        // Top first: on two columns it sits over Front, with Right beside Front (Em3dDrawingRequest.Views).
        var views = new List<Em3dStandardView>();
        if (Top) views.Add(Em3dStandardView.Top);
        if (Isometric) views.Add(Em3dStandardView.Isometric);
        if (Front) views.Add(Em3dStandardView.Front);
        if (Right) views.Add(Em3dStandardView.Right);
        if (Back) views.Add(Em3dStandardView.Back);
        if (Left) views.Add(Em3dStandardView.Left);
        if (Bottom) views.Add(Em3dStandardView.Bottom);
        var sections = new List<Em3dView>();
        foreach (var row in Sections)
        {
            var (v, why) = row.Parse();
            if (why is not null) { Error = why; return null; }
            sections.Add(v!.Value);
        }
        if (views.Count == 0 && sections.Count == 0) { Error = "Choose at least one view or add a section."; return null; }
        Error = null;
        return new Em3dDrawingRequest
        {
            Views = views, Sections = sections,
            Hidden = HiddenIndex switch { 1 => Em3dHiddenEdges.Dashed, 2 => Em3dHiddenEdges.Shown, _ => Em3dHiddenEdges.Removed },
            Legend = Legend, TextAsPaths = TextAsOutlines,
            Page = (Em3dPageSize)Math.Clamp(PageIndex, 0, 3), Landscape = Landscape,
            Format = FormatIndex == 1 ? Em3dDrawingFormat.Svg : Em3dDrawingFormat.Pdf,
            DocumentName = DocumentName,
            Omit = OmitHidden ? hiddenInView : null,
        };
    }

    /// <summary>What is remembered for next time.</summary>
    public Drawing3DChoices Choices(Em3dDrawingRequest r) => new()
    {
        Views = [.. new[] { (Isometric, Em3dStandardView.Isometric), (Top, Em3dStandardView.Top), (Bottom, Em3dStandardView.Bottom),
                            (Front, Em3dStandardView.Front), (Back, Em3dStandardView.Back), (Left, Em3dStandardView.Left),
                            (Right, Em3dStandardView.Right) }.Where(v => v.Item1).Select(v => v.Item2.ToString())],
        Hidden = r.Hidden.ToString(), Legend = r.Legend, TextAsOutlines = r.TextAsPaths, OmitHidden = OmitHidden,
        Page = r.Page.ToString(), Landscape = r.Landscape, Format = r.Format.ToString(),
    };

    /// <summary>The names the Export button's accept checks against: set by the host before the dialog opens.</summary>
    public IReadOnlySet<string>? HiddenInView { get; init; }

    [RelayCommand]
    private void Export()
    {
        if (BuildRequest(HiddenInView) is not { } r) return;
        Request = r;
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    partial void OnFormatIndexChanged(int value) => OnPropertyChanged(nameof(Extension));
}
