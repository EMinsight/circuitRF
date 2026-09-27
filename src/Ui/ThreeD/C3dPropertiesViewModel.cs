// brief-em3d-43 R-em3d43-6b — the Properties panel: the selection's fields in the document's display unit.
//
//   * an object: kind, material, role, placement, and each dimension;
//   * a face: its name, area and normal;
//   * a vertex: its coordinates.
//
// This brief makes an object's NAME, MATERIAL, ROLE and PLACEMENT editable; brief 47 adds a vertex's coordinates
// (Set Coordinates — the typed Vertex Move, one undo entry) and a face's perimeter, and the distance between two
// selected parallel faces (Face mode's Measure). Each edit is a typed edit — validated, and ONE undo entry — committed on Enter or when
// the field loses focus, never per keystroke. Every length follows the display unit at once (gate 10): the
// panel is reloaded when the unit changes, and it holds no length of its own.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.WBond;
using Point3 = CircuitRF.Engine.Em3d.Point3;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One read-only line of the panel; its value is selectable, so it can be copied.</summary>
public sealed record C3dPropertyRow(string Label, string Value);

/// <summary>3D editor round 1 — one face of the air box in the Inspector: its boundary, editable, and its padding now.</summary>
public sealed partial class C3dAirBoxFaceRow(string face, Em3dBoundaryKind kind, string padding, Action<string, Em3dBoundaryKind> set)
    : ObservableObject
{
    public string Face { get; } = face;
    public string Padding { get; } = padding;
    [ObservableProperty] private Em3dBoundaryKind _kind = kind;

    partial void OnKindChanged(Em3dBoundaryKind value) => set(Face, value);
}

/// <summary>
/// brief-em3d-51 R-em3d51-4b — one named dimension of the selected object: its text (a number in the display unit, or an
/// expression) and its resolved value, editable as either. A resolution error shows here, red, with the engine's message;
/// the document keeps the text, and elaboration refuses with the same message.
/// </summary>
public sealed partial class C3dDimensionField : ObservableObject
{
    public required string Path { get; init; }
    /// <summary>3D editor round 3 — what the field is called on screen ("X size", "Radius"); the path is the file's name for it.</summary>
    public string Label { get; init; } = "";
    /// <summary>3D editor round 4 — the row the field shares with its sibling components ("Size"), and its own letter in
    /// that row ("x"); an empty axis is a field alone on its row.</summary>
    public string Group { get; init; } = "";
    public string Axis { get; init; } = "";
    public C3dFieldKind Kind { get; init; }
    public required string ValueText { get; init; }
    public string? Error { get; init; }
    public bool IsExpression { get; init; }
    [ObservableProperty] private string _text = "";
    internal string Loaded { get; set; } = "";
}

/// <summary>
/// 3D editor round 4 — one line of the Inspector's dimensions: a vector's components side by side ("Size: x [ ] y [ ] z [ ]")
/// or one field alone, with the unit its numbers are read in. What an expression resolves to and what refused it are
/// listed under the line, each naming its component.
/// </summary>
public sealed class C3dDimensionRow(string label, string unit, IReadOnlyList<C3dDimensionField> fields)
{
    public string Label { get; } = label;
    public string Unit { get; } = unit;
    public IReadOnlyList<C3dDimensionField> Fields { get; } = fields;

    public string Notes { get; } = string.Join("\n", fields.Where(f => f.ValueText.Length > 0)
                                                        .Select(f => (f.Axis.Length > 0 ? f.Axis + " " : "") + f.ValueText));

    public string Errors { get; } = string.Join("\n", fields.Where(f => !string.IsNullOrEmpty(f.Error))
                                                         .Select(f => (f.Axis.Length > 0 ? f.Axis + ": " : "") + f.Error));
}

/// <summary>
/// 3D editor round 3 — one point of a selected bond wire, its world coordinates editable in the display unit. The first
/// and last are the bonded ends: each sits on the top of its pad, so its z follows the pad.
/// </summary>
public sealed partial class C3dWirePointRow : ObservableObject
{
    public required int Index { get; init; }
    public required string Label { get; init; }
    [ObservableProperty] private string _x = "";
    [ObservableProperty] private string _y = "";
    [ObservableProperty] private string _z = "";
    internal (string X, string Y, string Z) Loaded { get; set; }
}

public sealed partial class C3dPropertiesViewModel(C3dEditorViewModel editor) : ObservableObject
{
    /// <summary>The Role choices: the material's own, or an override.</summary>
    public const string RoleFromMaterial = "From material";

    public static IReadOnlyList<string> Roles { get; } = [RoleFromMaterial, .. Enum.GetNames<Em3dRole>()];

    private bool _loading;

    /// <summary>The document index of the object being edited, or −1.</summary>
    public int ObjectIndex { get; private set; } = -1;

    public ObservableCollection<C3dPropertyRow> Rows { get; } = [];

    /// <summary>brief-em3d-51 — the selected object's named dimensions, editable as a number or an expression.</summary>
    public ObservableCollection<C3dDimensionField> Fields { get; } = [];

    /// <summary>3D editor round 4 — <see cref="Fields"/> as the Inspector lays them out: a vector's components on one line.</summary>
    public ObservableCollection<C3dDimensionRow> FieldRows { get; } = [];
    /// <summary>The technology's materials and, last, New Material… (brief-em3d-53 R-em3d53-5).</summary>
    public IReadOnlyList<string> Materials => editor.MaterialChoices;

    [ObservableProperty] private string _heading = "Nothing selected";
    [ObservableProperty] private bool _isEditable;
    [ObservableProperty] private string _nameText = "";
    [ObservableProperty] private string? _material;
    [ObservableProperty] private string _role = RoleFromMaterial;
    [ObservableProperty] private string _rotateText = "";
    [ObservableProperty] private bool _mirrorX;
    [ObservableProperty] private string _error = "";

    /// <summary>3D editor round 3 — false for a bond wire, which has no placement (its points are world points).</summary>
    [ObservableProperty] private bool _isPlaced;
    [ObservableProperty] private string _materialPlaceholder = NoMaterialPlaceholder;

    private const string NoMaterialPlaceholder = "None — a solver needs one";

    // ── a bond wire (3D editor round 3) ─────────────────────────────────────────────────────

    [ObservableProperty] private bool _isWire;
    [ObservableProperty] private WireCrossSection _wireSection;
    [ObservableProperty] private BondStyle _wireStartStyle;
    [ObservableProperty] private BondStyle _wireEndStyle;

    /// <summary>3D editor round 4 — the wire's loop height (pad top to wire top, em-3d.md §6.6) and its foot-to-foot span in
    /// plan, in the display unit: the two a wBond or Layout wire offers, edited the same way.</summary>
    [ObservableProperty] private string _wireLoopHeight = "";
    [ObservableProperty] private string _wireSpan = "";
    private (string LoopHeight, string Span) _wireLoaded;

    /// <summary>The selected wire's points, start to end.</summary>
    public ObservableCollection<C3dWirePointRow> WirePoints { get; } = [];

    /// <summary>brief-em3d-47 R-em3d47-3e — the selected vertex's world coordinates, editable (Set Coordinates).</summary>
    [ObservableProperty] private bool _isVertexEditable;
    [ObservableProperty] private string _vertexX = "";
    [ObservableProperty] private string _vertexY = "";
    [ObservableProperty] private string _vertexZ = "";

    /// <summary>Raised when the Name field should take the caret (Rename… from a menu); the Inspector's view handles it.</summary>
    public event Action? RenameRequested;

    internal void RequestRename() => RenameRequested?.Invoke();

    /// <summary>Shows the current selection.</summary>
    public void Reload()
    {
        _loading = true;
        try { Load(); }
        finally { _loading = false; }
        OnPropertyChanged(nameof(Materials));
        OnPropertyChanged(nameof(LengthUnit));
    }

    /// <summary>The display unit's suffix — what a bare length in the panel is read in.</summary>
    public string LengthUnit => LayoutUnits.Suffix(editor.Document.DisplayUnit);

    private void Load()
    {
        Rows.Clear();
        Fields.Clear();
        FieldRows.Clear();
        Error = "";
        ObjectIndex = -1;
        IsEditable = false;
        IsPlaced = false;
        IsWire = false;
        WirePoints.Clear();
        MaterialPlaceholder = NoMaterialPlaceholder;
        IsVertexEditable = false;
        IsAirBox = false;
        IsAirBoxRow = false;
        AirBoxFaces.Clear();
        var viewer = editor.Viewer;
        var sel = viewer.Selection;
        if (sel.Count == 0)
        {
            // 3D editor round 1 — the tree's node when the scene holds nothing selected: an object elaboration refused
            // (its fields are where the refusal is put right), or the air box.
            if (editor.TreeOnlyObjectIndex() is >= 0 and var only) { LoadObject(only, inScene: false); return; }
            if (editor.SelectedTreeItem is { IsAirBox: true }) { LoadAirBox(); return; }
            Heading = "Nothing selected";
            return;
        }
        if (viewer.SelectMode == Scene3DSelectMode.Object &&
            sel.All(i => viewer.Scene.Object(i.Object) is { } b && C3dEditorViewModel.BoxFaceOf(b) is not null))
        {
            LoadAirBox();
            return;
        }
        if (sel.Count == 2 && viewer.SelectMode == Scene3DSelectMode.Face && sel[0].Face >= 0 && sel[1].Face >= 0) { TwoFaces(sel[0], sel[1]); return; }
        // 3D editor round 4 — every element of one wire row selected (the tree selects them all) is that one wire.
        if (sel.Count > 1 && !OneDocumentObject(sel)) { Heading = $"{sel.Count} selected"; return; }
        var item = sel[0];
        if (viewer.Scene.Object(item.Object) is not { } o) { Heading = "Nothing selected"; return; }
        Heading = viewer.Name(item);

        if (viewer.SelectMode == Scene3DSelectMode.Face && item.Face >= 0)
        {
            var (area, normal) = Scene3DFaces.AreaAndNormal(viewer.Scene, o.Id, item.Face);
            Rows.Add(new C3dPropertyRow("Face", o.FaceName(item.Face)));
            Rows.Add(new C3dPropertyRow("Area", AreaText(area)));
            if (Perimeter(o.Id, item.Face) is { } per) Rows.Add(new C3dPropertyRow("Perimeter", viewer.FormatLength(per)));
            Rows.Add(new C3dPropertyRow("Normal", normal is { } n ? $"({Num(n.X)}, {Num(n.Y)}, {Num(n.Z)})" : "varies (a curved face)"));
            Rows.Add(new C3dPropertyRow("Object", viewer.ObjectName(o)));
            return;
        }
        if (viewer.SelectMode == Scene3DSelectMode.Vertex)
        {
            // Set Coordinates: the vertex of one of this document's own objects, exactly where the document has it.
            if (editor.VertexSelection() is { Vertex: >= 0 } v)
            {
                IsVertexEditable = true;
                // Spelled losslessly, so a field left alone parses back to the same DBU and its lost focus moves nothing.
                string L(long dbu) => Tools.C3dDimension.Spell(dbu, editor.Document.DisplayUnit, editor.Document.DbuPerMicron);
                VertexX = L(v.World.X); VertexY = L(v.World.Y); VertexZ = L(v.World.Z);
            }
            var (x, y, z) = viewer.Scene.ToWorld(item.Point);
            Rows.Add(new C3dPropertyRow("x", viewer.FormatLength(x)));
            Rows.Add(new C3dPropertyRow("y", viewer.FormatLength(y)));
            Rows.Add(new C3dPropertyRow("z", viewer.FormatLength(z)));
            Rows.Add(new C3dPropertyRow("Object", viewer.ObjectName(o)));
            return;
        }

        if (editor.InstanceOf(o) is { } inst)
        {
            Rows.Add(new C3dPropertyRow("Instance", inst));
            if (editor.Elaboration?.Provenance.TryGetValue(o.Name, out var p) == true)
            {
                Rows.Add(new C3dPropertyRow("From", p.DocumentPath));
                Rows.Add(new C3dPropertyRow("Its name there", p.ObjectName));
            }
            if (o.Material is { } m) Rows.Add(new C3dPropertyRow("Material", m));
            Rows.Add(new C3dPropertyRow("Editing", "Read-only here: an instance's contents belong to its own cell."));
            return;
        }

        int index = editor.DocumentIndex(o);
        if (index < 0) return;
        LoadObject(index, inScene: true);
    }

    private bool OneDocumentObject(IReadOnlyList<Scene3DItem> sel)
        => editor.Viewer.SelectMode == Scene3DSelectMode.Object &&
           sel.Select(i => editor.Viewer.Scene.Object(i.Object) is { } o ? editor.DocumentIndex(o) : -1).Distinct().ToList() is [>= 0];

    /// <summary>A document object's editable fields — <paramref name="inScene"/> false when elaboration refused it, whose
    /// reason is then the first row.</summary>
    private void LoadObject(int index, bool inScene)
    {
        var obj = editor.Document.Objects[index];
        // A refused object is either not drawn at all, or — with a material its technology lacks — drawn as a wireframe
        // the solver never sees; either way the refusal Simulate will give is shown on the object it names. One with NO
        // material is drawn the same way and is not a refusal: the solver ignores it (3D editor bugs round 2), and this row
        // says so.
        string? why = editor.Elaboration?.Refusals.FirstOrDefault(r => r.Contains($"'{obj.Name}'", StringComparison.Ordinal))
                      ?? editor.Elaboration?.Warnings.FirstOrDefault(w => w == C3dElaborator.NoMaterialWarning(obj.Name));
        if (!inScene)
        {
            Heading = obj.Name;
            Rows.Add(new C3dPropertyRow("Not drawn", why ?? "It is not in the 3D model."));
        }
        else if (why is not null) Rows.Add(new C3dPropertyRow("Not simulated", why));
        ObjectIndex = index;
        IsEditable = true;
        NameText = obj.Name;
        Material = obj.Material;
        Role = obj.Role?.ToString() ?? RoleFromMaterial;
        var pl = obj.Placement;
        RotateText = string.Join(", ", pl.Rotate.Select(r => $"{r.Axis.ToString().ToLowerInvariant()} {r.Deg.ToString("G", CultureInfo.InvariantCulture)}"));
        MirrorX = pl.MirrorX;
        IsPlaced = obj is not C3dWire;
        if (obj is C3dWire wire) LoadWire(wire);
        Rows.Add(new C3dPropertyRow("Kind", C3dObject.KindOf(obj)));
        // brief-em3d-46 R-em3d46-4d — construction order is invisible unless it is shown: it decides overlap.
        Rows.Add(new C3dPropertyRow("Construction order",
            $"{index + 1} of {editor.Document.Objects.Count} — a later object wins where solids overlap (Modify ▸ Order)"));
        foreach (var row in Dimensions(obj)) Rows.Add(row);
        foreach (var f in editor.DimensionFields(obj)) Fields.Add(f);
        foreach (var row in RowsOf(Fields)) FieldRows.Add(row);
    }

    /// <summary>3D editor round 4 — consecutive components of one vector share a line; every other field has its own.</summary>
    private IEnumerable<C3dDimensionRow> RowsOf(IReadOnlyList<C3dDimensionField> fields)
    {
        for (int i = 0; i < fields.Count;)
        {
            var f = fields[i];
            int j = i + 1;
            if (f.Axis.Length > 0)
                while (j < fields.Count && fields[j].Axis.Length > 0 && fields[j].Group == f.Group) j++;
            string unit = f.Kind switch
            {
                C3dFieldKind.Length or C3dFieldKind.Microns => LayoutUnits.Suffix(editor.Document.DisplayUnit),
                _ => "",                                        // an angle's label says °; a count has no unit
            };
            yield return new C3dDimensionRow(f.Axis.Length > 0 ? f.Group : f.Label, unit, [.. fields.Skip(i).Take(j - i)]);
            i = j;
        }
    }

    /// <summary>
    /// 3D editor round 4 — the line a field sits on and its letter there: "Corner x" is ("Corner", "x"), "X size" is
    /// ("Size", "x"), "Placement origin z" is ("Placement origin", "z"). A field that is no component of a vector is
    /// (its label, "").
    /// </summary>
    public static (string Group, string Axis) FieldGroup(C3dObject obj, string path)
    {
        string label = FieldLabel(obj, path);
        if (label.Length == 6 && label.EndsWith(" size", StringComparison.Ordinal))
            return ("Size", label[..1].ToLowerInvariant());
        int space = label.LastIndexOf(' ');
        if (space > 0 && label.Length - space == 2 && "xyzuv".Contains(label[^1], StringComparison.Ordinal))
            return (label[..space], label[^1..]);
        return (label, "");
    }

    private void LoadWire(C3dWire wire)
    {
        IsWire = true;
        MaterialPlaceholder = $"{C3dWires.MaterialOf(wire)} (the default)";
        WireSection = C3dWires.SectionOf(wire);
        WireStartStyle = wire.Start.Style;
        WireEndStyle = wire.End.Style;
        string L(long dbu) => Tools.C3dDimension.Spell(dbu, editor.Document.DisplayUnit, editor.Document.DbuPerMicron);
        WireLoopHeight = editor.WireLoopHeightDbu(wire) is { } h ? L(h) : "";
        WireSpan = L(C3dEditorViewModel.WireSpanDbu(wire));
        _wireLoaded = (WireLoopHeight, WireSpan);
        for (int k = 0; k < wire.Points.Count; k++)
        {
            var p = wire.Points[k];
            string label = k == 0 ? "Start" : k == wire.Points.Count - 1 ? "End" : k.ToString(CultureInfo.InvariantCulture);
            var row = new C3dWirePointRow { Index = k, Label = label, X = L(p.X), Y = L(p.Y), Z = L(p.Z) };
            row.Loaded = (row.X, row.Y, row.Z);
            WirePoints.Add(row);
        }
    }

    /// <summary>3D editor round 3 — a wire point's Enter or lost focus: three lengths in the display unit (a suffix may name
    /// another), the point moved there and the feet re-seated exactly as a Vertex-mode drag's are, as one undo entry — or
    /// the refusal, with the fields left for correcting.</summary>
    public void CommitWirePoint(C3dWirePointRow row)
    {
        if (!IsWire || ObjectIndex < 0 || (row.X, row.Y, row.Z) == row.Loaded) return;
        var doc = editor.Document;
        if (!LayoutUnits.TryParse(row.X, doc.DisplayUnit, doc.DbuPerMicron, out long x) ||
            !LayoutUnits.TryParse(row.Y, doc.DisplayUnit, doc.DbuPerMicron, out long y) ||
            !LayoutUnits.TryParse(row.Z, doc.DisplayUnit, doc.DbuPerMicron, out long z))
        {
            Error = $"A point is three lengths, in {LayoutUnits.Suffix(doc.DisplayUnit)} unless a unit is written.";
            return;
        }
        Error = editor.SetWirePoint(ObjectIndex, row.Index, new C3dPoint3(x, y, z)) ?? "";
    }

    /// <summary>3D editor round 4 — the loop height's Enter or lost focus: one undo entry, or the refusal.</summary>
    public void CommitWireLoopHeight()
    {
        if (!IsWire || ObjectIndex < 0 || WireLoopHeight == _wireLoaded.LoopHeight) return;
        Error = editor.SetWireLoopHeight(ObjectIndex, WireLoopHeight) ?? "";
    }

    /// <summary>3D editor round 4 — the span's Enter or lost focus: the end foot moves, one undo entry, or the refusal.</summary>
    public void CommitWireSpan()
    {
        if (!IsWire || ObjectIndex < 0 || WireSpan == _wireLoaded.Span) return;
        Error = editor.SetWireSpan(ObjectIndex, WireSpan) ?? "";
    }

    partial void OnWireSectionChanged(WireCrossSection value) => ChangeWire("Section", w => w.Section = value);

    partial void OnWireStartStyleChanged(BondStyle value) => ChangeWire("Start bond", w => w.Start.Style = value);

    partial void OnWireEndStyleChanged(BondStyle value) => ChangeWire("End bond", w => w.End.Style = value);

    private void ChangeWire(string what, Action<C3dWire> mutate)
    {
        if (_loading || !IsWire || ObjectIndex < 0) return;
        int i = ObjectIndex;
        editor.ChangeObjects($"{what} of {editor.Document.Objects[i].Name}", [i], o => { if (o is C3dWire w) mutate(w); });
    }

    /// <summary>
    /// 3D editor round 3 — a dimension's name on screen, in the words of the object's own axes: a box's <c>Size[0]</c> is
    /// its "X size"; a sheet's <c>Rect.Size[1]</c> on the XZ plane is its "Z size". The path stays the file's name for it.
    /// </summary>
    public static string FieldLabel(C3dObject obj, string path)
    {
        static string Xyz(int k) => k switch { 0 => "x", 1 => "y", _ => "z" };
        static (string U, string V, string N) Axes(C3dPlane p) => p switch
        {
            C3dPlane.YZ => ("y", "z", "x"),
            C3dPlane.XZ => ("x", "z", "y"),
            _ => ("x", "y", "z"),
        };
        static string Up(string a) => a.ToUpperInvariant();
        int bracket = path.IndexOf('[');
        string head = bracket >= 0 ? path[..bracket] : path;
        int k = bracket >= 0 && bracket + 1 < path.Length && char.IsDigit(path[bracket + 1]) ? path[bracket + 1] - '0' : 0;
        if (head == "Placement.Rotate") return $"Rotation {k + 1} (°)";
        if (head == "Placement.Origin") return $"Placement origin {Xyz(k)}";
        switch (obj)
        {
            case C3dBox:
                if (head == "Min") return $"Corner {Xyz(k)}";
                if (head == "Size") return $"{Up(Xyz(k))} size";
                break;
            case C3dPrism pr:
            {
                var (u, v, n) = Axes(pr.Plane);
                if (head == "Offset") return $"Offset ({n})";
                if (head == "Height") return $"Height ({n})";
                if (head == "Shear") return $"Shear {(k == 0 ? u : v)}";
                break;
            }
            case C3dSheet sh:
            {
                var (u, v, n) = Axes(sh.Plane);
                if (head == "Offset") return $"Offset ({n})";
                if (head == "Rect.Min") return $"Corner {(k == 0 ? u : v)}";
                if (head == "Rect.Size") return $"{Up(k == 0 ? u : v)} size";
                if (head == "ThicknessUm") return "Thickness";
                break;
            }
            case C3dPolyline pl:
                if (head == "Offset") return $"Offset ({Axes(pl.Plane).N})";
                break;
            case C3dCylinder c:
                if (head == "Base") return $"Base {Xyz(k)}";
                if (head == "Length") return $"Length (along {c.Axis.ToString().ToLowerInvariant()})";
                if (head == "Radius") return "Radius";
                break;
            case C3dWire:
                if (head == "DiameterUm") return "Diameter";
                // 3D editor round 4 — a wire row.
                if (head == "Array.Count") return "Number of wires";
                if (head == "Array.Pitch") return $"Pitch {Xyz(k)}";
                break;
        }
        return path;
    }

    // ── the air box (3D editor round 1) ─────────────────────────────────────────────────────

    /// <summary>The active setup's air box is what is shown: its padding per axis and each face's boundary.</summary>
    [ObservableProperty] private bool _isAirBox;

    /// <summary>3D editor round 3 — the air box is what is shown, with or without a setup: its material is editable either way.</summary>
    [ObservableProperty] private bool _isAirBoxRow;

    /// <summary>3D editor round 3 — the material that fills the air box (Air by default); a choice is one undo entry.</summary>
    [ObservableProperty] private string? _airBoxMaterial;

    public IReadOnlyList<string> AirBoxMaterials => editor.AirBoxMaterialChoices;

    partial void OnAirBoxMaterialChanged(string? value)
    {
        if (_loading || !IsAirBoxRow || value is null) return;
        Error = editor.SetAirBoxMaterial(value) ?? "";
    }
    [ObservableProperty] private string _padXPercent = "";
    [ObservableProperty] private string _padYPercent = "";
    [ObservableProperty] private string _padZPercent = "";

    /// <summary>One row per face: its boundary (editable) and the padding it has now.</summary>
    public ObservableCollection<C3dAirBoxFaceRow> AirBoxFaces { get; } = [];

    public static IReadOnlyList<Em3dBoundaryKind> BoundaryKinds { get; } = Enum.GetValues<Em3dBoundaryKind>();

    private void LoadAirBox()
    {
        IsAirBox = true;
        IsAirBoxRow = true;
        AirBoxMaterial = C3dProblemAssembly.BoxFill(editor.Document);
        OnPropertyChanged(nameof(AirBoxMaterials));
        var setup = editor.ActiveSetup;
        Heading = "Air box";
        if (setup is null || editor.ShownAirBox is not { } box)
        {
            Rows.Add(new C3dPropertyRow("Setup", setup is null
                ? "No setup is active: the box drawn is a new setup's default. Its padding and faces are a setup's — add one in Simulate ▸ Setup Analyses…"
                : "The active setup's air box could not be built around this geometry; Simulate ▸ Run reports why."));
            IsAirBox = false;
            return;
        }
        Rows.Add(new C3dPropertyRow("Setup", editor.ActiveSetupLabel));
        var stated = setup.AirBox;
        string Pct(EmAirBoxFace? a, EmAirBoxFace? b)
            => a?.PaddingPercent is { } p && b?.PaddingPercent == p ? p.ToString("G6", CultureInfo.InvariantCulture) : "";
        PadXPercent = Pct(stated?.XMin, stated?.XMax);
        PadYPercent = Pct(stated?.YMin, stated?.YMax);
        PadZPercent = Pct(stated?.ZMin, stated?.ZMax);
        var f = box.Faces;
        foreach (var (face, kind, s) in new[]
                 {
                     ("xmin", f.XMin, stated?.XMin), ("xmax", f.XMax, stated?.XMax), ("ymin", f.YMin, stated?.YMin),
                     ("ymax", f.YMax, stated?.YMax), ("zmin", f.ZMin, stated?.ZMin), ("zmax", f.ZMax, stated?.ZMax),
                 })
        {
            string pad = editor.AirBoxPaddingText(face) + " " + LayoutUnits.Suffix(editor.Document.DisplayUnit)
                       + (s?.PaddingPercent is { } p ? $" ({p.ToString("G6", CultureInfo.InvariantCulture)} %)"
                          : s?.PaddingUm is not null ? " (stated)" : " (default)");
            AirBoxFaces.Add(new C3dAirBoxFaceRow(face, kind, pad, (fc, k) => Error = editor.SetAirBoxBoundary(fc, k) ?? ""));
        }
        Rows.Add(new C3dPropertyRow("Size", $"{editor.Viewer.FormatLength(box.Max.X - box.Min.X)} × " +
                                            $"{editor.Viewer.FormatLength(box.Max.Y - box.Min.Y)} × {editor.Viewer.FormatLength(box.Max.Z - box.Min.Z)}"));
        Rows.Add(new C3dPropertyRow("Editing", "The air box is the active setup's, not the geometry's: it cannot be deleted or duplicated."));
    }

    /// <summary>An axis's padding percentage — Enter or lost focus: both faces of the axis pad by that share of the
    /// content's extent along it; empty keeps the faces as they are. One undo entry.</summary>
    public void CommitAirBoxPercent(char axis)
    {
        if (!IsAirBox) return;
        string text = axis switch { 'x' => PadXPercent, 'y' => PadYPercent, _ => PadZPercent };
        if (string.IsNullOrWhiteSpace(text)) return;
        Error = editor.SetAirBoxPaddingPercent(axis, text) ?? "";
    }

    /// <summary>A dimension's Enter or lost focus: a number replaces the expression; an expression is bound (and kept even
    /// when it does not resolve — the field then says why). One undo entry.</summary>
    public void CommitField(C3dDimensionField field)
    {
        if (ObjectIndex < 0 || field.Text == field.Loaded) return;
        Error = editor.SetFieldText(ObjectIndex, field.Path, field.Text) ?? "";
    }

    /// <summary>A face's perimeter (metres): the scene's own feature edges around it, which are the solid's edges, not its
    /// triangles'. Null when the face has none (a sweep names no faces).</summary>
    private double? Perimeter(uint id, int face)
    {
        if (editor.Viewer.Scene.FeaturesOf(id) is not { Table: { } t } fr || face >= t.FaceCount) return null;
        double p = 0;
        for (int k = t.FaceEdgeStart[face]; k < t.FaceEdgeStart[face + 1]; k++)
        {
            int e = t.FaceEdges[k];
            var a = fr.Vertex(t.EdgeA[e]);
            var b = fr.Vertex(t.EdgeB[e]);
            p += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) + (b.Z - a.Z) * (b.Z - a.Z));
        }
        return p > 0 ? p : null;
    }

    /// <summary>brief-em3d-47 R-em3d47-4 — Measure's second face: the distance between two parallel faces, or that they are not.</summary>
    private void TwoFaces(Scene3DItem a, Scene3DItem b)
    {
        var viewer = editor.Viewer;
        var scene = viewer.Scene;
        if (scene.Object(a.Object) is not { } oa || scene.Object(b.Object) is not { } ob) { Heading = "2 selected"; return; }
        Heading = "2 faces";
        Rows.Add(new C3dPropertyRow("First", viewer.Name(a)));
        Rows.Add(new C3dPropertyRow("Second", viewer.Name(b)));
        var (_, na) = Scene3DFaces.AreaAndNormal(scene, oa.Id, a.Face);
        var (_, nb) = Scene3DFaces.AreaAndNormal(scene, ob.Id, b.Face);
        if (na is not { } n1 || nb is not { } n2) { Rows.Add(new C3dPropertyRow("Distance", "A curved face has no single plane.")); return; }
        var c = System.Numerics.Vector3.Cross(n1, n2);
        if (c.Length() > 1e-5f) { Rows.Add(new C3dPropertyRow("Distance", "Not parallel.")); return; }
        if (Corner(oa.Id, a.Face) is not { } pa || Corner(ob.Id, b.Face) is not { } pb) return;
        double d = Math.Abs(n1.X * (pb.X - pa.X) + n1.Y * (pb.Y - pa.Y) + n1.Z * (pb.Z - pa.Z));
        Rows.Add(new C3dPropertyRow("Distance", viewer.FormatLength(d)));
        Rows.Add(new C3dPropertyRow("Facing", System.Numerics.Vector3.Dot(n1, n2) < 0 ? "each other" : "the same way"));
    }

    private Point3? Corner(uint id, int face)
        => editor.Viewer.Scene.FeaturesOf(id) is { Table: { } t } fr && face < t.FaceCount && t.FaceVertexStart[face] < t.FaceVertexStart[face + 1]
            ? fr.Vertex(t.FaceVertices[t.FaceVertexStart[face]]) : null;

    /// <summary>brief-em3d-47 R-em3d47-3e — Set Coordinates' Enter or lost focus: three lengths in the display unit (a suffix
    /// may name another), the vertex moved there as one undo entry — or the refusal, and the fields put back.</summary>
    public void CommitVertex()
    {
        if (!IsVertexEditable) return;
        var doc = editor.Document;
        if (!LayoutUnits.TryParse(VertexX, doc.DisplayUnit, doc.DbuPerMicron, out long x) ||
            !LayoutUnits.TryParse(VertexY, doc.DisplayUnit, doc.DbuPerMicron, out long y) ||
            !LayoutUnits.TryParse(VertexZ, doc.DisplayUnit, doc.DbuPerMicron, out long z))
        {
            Error = $"A vertex is three lengths, in {LayoutUnits.Suffix(doc.DisplayUnit)} unless a unit is written.";
            return;
        }
        if (editor.SetVertexCoordinates(new C3dPoint3(x, y, z)) is { } why) { Error = why; return; }
        Error = "";
    }

    // ── read-only dimensions, per kind ──────────────────────────────────────────────────────

    /// <summary>What the editable fields do not already show (3D editor round 4: a corner, a size, a radius were listed
    /// twice — once to edit and once to read).</summary>
    private IEnumerable<C3dPropertyRow> Dimensions(C3dObject obj)
    {
        switch (obj)
        {
            case C3dPrism p:
                yield return new("Plane", p.Plane.ToString());
                yield return new("Outline", $"{p.Outline.Count} points" + (p.Holes.Count > 0 ? $", {p.Holes.Count} hole(s)" : ""));
                break;
            case C3dCylinder c:
                yield return new("Axis", c.Axis.ToString());
                break;
            case C3dSheet s:
                yield return new("Plane", s.Plane.ToString());
                if (s.Rect is null) yield return new("Outline", $"{s.Outline.Count} points" + (s.Holes.Count > 0 ? $", {s.Holes.Count} hole(s)" : ""));
                break;
            case C3dPolyline l:
                yield return new("Plane", l.Plane.ToString());
                yield return new("Points", l.VertexCount + (l.Closed ? ", closed" : "") + (l.Points3 is not null ? ", leaving its plane (stored in 3D)" : ""));
                yield return new("In the problem", "No: a polyline is construction geometry.");
                break;
            case C3dPolyhedron ph:
                yield return new("Vertices", ph.Vertices.Count.ToString(CultureInfo.InvariantCulture));
                yield return new("Faces", ph.Faces.Count.ToString(CultureInfo.InvariantCulture));
                break;
        }
    }

    private static string Num(float v) => (MathF.Abs(v) < 5e-7f ? 0f : v).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>An area in the display unit squared.</summary>
    private string AreaText(double m2)
    {
        var unit = editor.Document.DisplayUnit;
        // One display unit in metres, from the DBU conversion itself: 1 unit = FromDbu⁻¹ …
        long dbuPerUnit = LayoutUnits.ToDbu(1m, unit, editor.Document.DbuPerMicron);
        double metresPerUnit = dbuPerUnit * 1e-6 / editor.Document.DbuPerMicron;
        double v = m2 / (metresPerUnit * metresPerUnit);
        return v.ToString("G6", CultureInfo.InvariantCulture) + " " + LayoutUnits.Suffix(unit) + "²";
    }

    // ── commits: one typed edit, one undo entry, validated ──────────────────────────────────

    /// <summary>The Name field's Enter or lost focus.</summary>
    public void CommitName()
    {
        if (ObjectIndex < 0) return;
        Error = editor.Rename(ObjectIndex, NameText) ?? "";
        if (Error.Length > 0) NameText = editor.Document.Objects[ObjectIndex].Name;
    }

    partial void OnMaterialChanged(string? value)
    {
        if (_loading || ObjectIndex < 0 || value is null) return;
        int i = ObjectIndex;
        if (value == C3dEditorViewModel.NewMaterialItem)
        {
            // A command, not a material: the picker opens on a new row; the field keeps what the object states.
            _loading = true;
            Material = editor.Document.Objects[i].Material;
            _loading = false;
            editor.RequestMaterialPicker([i], startNew: true);
            return;
        }
        editor.ChangeObjects($"Material of {editor.Document.Objects[i].Name}", [i], o => o.Material = value);
    }

    partial void OnRoleChanged(string value)
    {
        if (_loading || ObjectIndex < 0) return;
        Em3dRole? role = Enum.TryParse<Em3dRole>(value, out var r) ? r : null;
        int i = ObjectIndex;
        editor.ChangeObjects($"Role of {editor.Document.Objects[i].Name}", [i], o => o.Role = role);
    }

    partial void OnMirrorXChanged(bool value)
    {
        if (_loading || ObjectIndex < 0) return;
        int i = ObjectIndex;
        editor.ChangeObjects($"Mirror {editor.Document.Objects[i].Name}", [i], o => o.Placement.MirrorX = value);
    }

    /// <summary>The rotations field: <c>z 30, x 90</c> — each an axis and degrees, applied in order.</summary>
    public void CommitRotate()
    {
        if (ObjectIndex < 0) return;
        if (ParseRotations(RotateText) is not { } list)
        {
            Error = "Rotations are written as an axis and degrees, in order: \"z 30, x 90\".";
            return;
        }
        Error = "";
        int i = ObjectIndex;
        editor.ChangeObjects($"Rotate {editor.Document.Objects[i].Name}", [i], o => o.Placement.Rotate = list);
    }

    /// <summary>Parses <c>z 30, x 90</c>; null when it is not that shape. An empty text is no rotation.</summary>
    public static List<C3dRotation>? ParseRotations(string text)
    {
        var list = new List<C3dRotation>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length != 2 || !Enum.TryParse<C3dAxis>(bits[0], ignoreCase: true, out var axis) ||
                !double.TryParse(bits[1].TrimEnd('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out double deg) ||
                !double.IsFinite(deg))
                return null;
            list.Add(new C3dRotation { Axis = axis, Deg = deg });
        }
        return list;
    }
}
