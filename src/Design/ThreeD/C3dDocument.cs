// brief-em3d-41 R-em3d41-2/-3 — the .c3d document: a cell's 3D view.
//
// ONE TYPE FAMILY IS BOTH THE WORKING MODEL AND THE FILE. `.clay`'s shapes are the precedent: an
// object here is exactly what the file says and nothing the file does not, so a second DTO tree and
// a mapping between the two would be a copy that could only disagree. The generated reference page
// (`circuitrf reference 3d`) walks these types, which is why every computed member is [JsonIgnore]
// and every point type says, in a [Description], how it is SPELLED rather than what its fields are.
//
// EVERY COORDINATE IS AN INTEGER DBU (owner decision D4, overview §1d). Two solids drawn to touch
// share a face EXACTLY, so Gmsh's fragment makes one surface and the FDTD grid one line — doubles
// would make touching a matter of tolerance. A rotation that is not a multiple of 90° would break
// that if it were baked into coordinates, so it never is: it lives in the object's Placement, and
// the geometry stays integer in the object's own frame.
//
// DIMENSIONS ARE SIZES, NOT SECOND CORNERS (R-em3d41-2-dims). Brief 51 binds a typed width to a
// FIELD; with two corners "the width is w" could only be written as an expression nobody typed.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Design.Layout;
using CircuitRF.Engine.Em3d;
using BondStyle = CircuitRF.WBond.BondStyle;
using WireCrossSection = CircuitRF.WBond.WireCrossSection;

namespace CircuitRF.Design.ThreeD;

// ── Points ────────────────────────────────────────────────────────────────────────────────────

/// <summary>A point in the document's 3D frame, integer DBU. Spelled <c>[x, y, z]</c>.</summary>
[JsonConverter(typeof(C3dPoint3JsonConverter))]
[Description("[x, y, z]")]
public readonly record struct C3dPoint3(long X, long Y, long Z)
{
    public static C3dPoint3 operator +(C3dPoint3 a, C3dPoint3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static C3dPoint3 operator -(C3dPoint3 a, C3dPoint3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
}

/// <summary>A point on a drawing plane, integer DBU. Spelled <c>[u, v]</c>; which world axes u and v
/// are is the plane's (<see cref="C3dPlane"/>).</summary>
[JsonConverter(typeof(C3dPoint2JsonConverter))]
[Description("[u, v]")]
public readonly record struct C3dPoint2(long U, long V);

/// <summary>
/// A drawing plane, named by the two world axes it spans. u is the first, v the second, and the
/// plane's normal — the direction a prism's <c>Height</c> and a plane's <c>Offset</c> are measured
/// along — is the third: XY → (x, y) along +z, YZ → (y, z) along +x, XZ → (x, z) along +y.
/// </summary>
public enum C3dPlane { XY, YZ, XZ }

/// <summary>A world axis.</summary>
public enum C3dAxis { X, Y, Z }

/// <summary>Which view of the placed cell an instance shows (brief 48 swaps it).</summary>
public enum C3dInstanceView { ThreeD, Layout }

// ── Placement ─────────────────────────────────────────────────────────────────────────────────

/// <summary>One rotation of a placement: about a world axis through the object's origin, right-handed,
/// in degrees.</summary>
public sealed class C3dRotation : IC3dBindable
{
    public C3dAxis Axis { get; set; } = C3dAxis.Z;
    public double  Deg  { get; set; }

    /// <summary>brief-em3d-51 — the angle's expression, when it holds one (<see cref="C3dBindings"/>).</summary>
    [JsonIgnore] public Dictionary<string, C3dExpr?[]>? Exprs { get; set; }
}

/// <summary>
/// Where an object or an instance sits (R-em3d41-2a): its own frame is mirrored, then rotated by each
/// entry of <see cref="Rotate"/> in list order, then translated to <see cref="Origin"/> — layout's
/// mirror-then-rotate order, extended by a list. <see cref="MirrorX"/> negates the object's own x, as
/// a layout instance's does. The whole record is omitted from the file when it states nothing.
/// </summary>
public sealed partial class C3dPlacement : IC3dBindable
{
    /// <summary>brief-em3d-51 — the origin's expressions, when it holds any (<see cref="C3dBindings"/>).</summary>
    [JsonIgnore] public Dictionary<string, C3dExpr?[]>? Exprs { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public C3dPoint3 Origin { get; set; }

    public List<C3dRotation> Rotate { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MirrorX { get; set; }

    /// <summary>True when the placement states nothing, which is when the file leaves it out. A stated
    /// 0° rotation is not nothing: it was written, so it is kept.</summary>
    [JsonIgnore]
    public bool IsDefault => Origin == default && Rotate.Count == 0 && !MirrorX && Exprs is null;

    /// <summary>The placement as one rigid transform — see <see cref="C3dTransform"/>.</summary>
    public C3dTransform ToTransform()
    {
        var t = MirrorX ? C3dTransform.MirrorX : C3dTransform.Identity;
        foreach (var r in Rotate) t = t.Then(C3dTransform.Rotation(r.Axis, r.Deg));
        return t.Then(C3dTransform.Translation(Origin));
    }
}

// ── Objects ───────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One object of the document: a primitive, a polyhedron, or a construction polyline. The list order
/// is construction order (R-em3d41-2b): where two solids overlap, the LATER one wins the volume, which
/// is <c>Em3dSolid.Order</c> — except that metal always takes precedence over dielectric, whatever the order
/// (<c>Em3dPrecedence</c>, em-3d.md §6.3a), the one overlap rule both backends honour. Nothing sorts the list.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(C3dBox),        "Box")]
[JsonDerivedType(typeof(C3dPrism),      "Prism")]
[JsonDerivedType(typeof(C3dCylinder),   "Cylinder")]
[JsonDerivedType(typeof(C3dSheet),      "Sheet")]
[JsonDerivedType(typeof(C3dPolyline),   "Polyline")]
[JsonDerivedType(typeof(C3dPolyhedron), "Polyhedron")]
[JsonDerivedType(typeof(C3dWire),       "Wire")]
[JsonDerivedType(typeof(C3dBoolean),    "Boolean")]
[JsonDerivedType(typeof(C3dFillet),     "Fillet")]
[JsonDerivedType(typeof(C3dChamfer),    "Chamfer")]
[JsonDerivedType(typeof(C3dStep),       "Step")]
public abstract class C3dObject : IC3dBindable
{
    /// <summary>brief-em3d-51 — the object's dimension fields that hold an expression (<see cref="C3dBindings"/>).</summary>
    [JsonIgnore] public Dictionary<string, C3dExpr?[]>? Exprs { get; set; }

    /// <summary>Unique in the document, validated as a cell name is; <c>airbox</c> is reserved (the air
    /// box's faces are <c>airbox/xmin</c> …). brief-em3d-64 R-em3d64-1d — an operation's Blank or Target has none: the
    /// operation carries it, so the key is left out of the file when it is empty.</summary>
    [JsonPropertyOrder(-10)]
    public string Name { get; set; } = "";

    /// <summary>A material of the document's technology. A polyline has none.</summary>
    [JsonPropertyOrder(-9)]
    public string? Material { get; set; }

    /// <summary>Overrides the role the material implies. Omitted: the material decides (brief 42).</summary>
    [JsonPropertyOrder(-8)]
    public Em3dRole? Role { get; set; }

    /// <summary>The group the object is in, as a path from the outermost group: <c>pa</c>, <c>pa/match</c>
    /// (<see cref="C3dGroups"/>). Omitted: in none. Organisation only — nothing it says reaches a solver. An operation
    /// carries it for its result; the object it wraps has none.</summary>
    [JsonPropertyOrder(-7)]
    public string? Group { get; set; }

    [JsonPropertyOrder(10)]
    public C3dPlacement Placement { get; set; } = new();

    /// <summary>Document state, like a layer's visibility in a <c>.ctech</c>. The camera is not.</summary>
    [JsonPropertyOrder(11)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Hidden { get; set; }

    /// <summary>Keys this build does not read. Kept, and written back, so a document from a later build
    /// does not lose them by being opened here; <c>check</c> names each one.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }

    /// <summary>The object's faces by NAME (R-em3d41-2c) — never by index, so a boundary attached to a
    /// face survives an edit (em-3d.md §6.4). Empty for a sheet and a polyline, which are not
    /// solids.</summary>
    public abstract IReadOnlyList<string> FaceNames();

    /// <summary>The <c>$type</c> this object is written as.</summary>
    public static string KindOf(C3dObject o) => KindOf(o.GetType());

    /// <summary>The <c>$type</c> spelling of a concrete object type, read off the attributes the reader
    /// itself uses.</summary>
    public static string KindOf(Type t)
    {
        foreach (var d in Kinds) if (d.Type == t) return d.Name;
        return t.Name;
    }

    /// <summary>Every <c>$type</c> this build reads.</summary>
    public static IReadOnlyList<(string Name, Type Type)> Kinds { get; } =
        [.. typeof(C3dObject).GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .Select(a => ((string)a.TypeDiscriminator!, a.DerivedType))];
}

/// <summary>An axis-aligned box in its own frame: a corner and a size (never two corners).</summary>
public sealed class C3dBox : C3dObject
{
    public C3dPoint3 Min  { get; set; }
    public C3dPoint3 Size { get; set; }

    public static readonly string[] FaceNameList = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];

    public override IReadOnlyList<string> FaceNames() => FaceNameList;
}

/// <summary>
/// A polygon on a drawing plane, pulled along the plane's normal by <see cref="Height"/> — negative is
/// allowed, and says which way it was pulled. <see cref="Shear"/> moves the top against the bottom,
/// which is an oblique prism: still a prism, never a polyhedron.
/// </summary>
public sealed class C3dPrism : C3dObject
{
    public C3dPlane Plane  { get; set; }
    public long     Offset { get; set; }
    public List<C3dPoint2>       Outline { get; set; } = [];
    public List<List<C3dPoint2>> Holes   { get; set; } = [];
    public long     Height { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public C3dPoint2 Shear { get; set; }

    /// <summary><c>bottom</c>, <c>top</c>, <c>side&lt;k&gt;</c> for the outline edge from vertex k to
    /// k+1, and <c>hole&lt;h&gt;.side&lt;k&gt;</c> for each hole's.</summary>
    public override IReadOnlyList<string> FaceNames()
    {
        var names = new List<string>(2 + Outline.Count) { "bottom", "top" };
        for (int k = 0; k < Outline.Count; k++) names.Add($"side{k}");
        for (int h = 0; h < Holes.Count; h++)
            for (int k = 0; k < Holes[h].Count; k++) names.Add($"hole{h}.side{k}");
        return names;
    }
}

/// <summary>A right circular cylinder along a world axis from <see cref="Base"/>. Any other axis is a
/// placement's rotation.</summary>
public sealed class C3dCylinder : C3dObject
{
    public C3dPoint3 Base   { get; set; }
    public C3dAxis   Axis   { get; set; } = C3dAxis.Z;
    public long      Length { get; set; }
    public long      Radius { get; set; }

    public static readonly string[] FaceNameList = ["bottom", "top", "side"];

    public override IReadOnlyList<string> FaceNames() => FaceNameList;
}

/// <summary>A rectangle on a drawing plane: a corner and a size.</summary>
public sealed class C3dRect : IC3dBindable
{
    [JsonIgnore] public Dictionary<string, C3dExpr?[]>? Exprs { get; set; }

    public C3dPoint2 Min  { get; set; }
    public C3dPoint2 Size { get; set; }
}

/// <summary>A conductor thin enough to be a surface, on a drawing plane: a <see cref="Rect"/>, or an
/// <see cref="Outline"/> with <see cref="Holes"/> — one or the other.</summary>
public sealed class C3dSheet : C3dObject
{
    public C3dPlane Plane  { get; set; }
    public long     Offset { get; set; }
    public C3dRect? Rect   { get; set; }
    public List<C3dPoint2>       Outline { get; set; } = [];
    public List<List<C3dPoint2>> Holes   { get; set; } = [];

    /// <summary>The metal's real thickness, micrometres — what the solver's surface impedance uses.</summary>
    public double? ThicknessUm { get; set; }

    public override IReadOnlyList<string> FaceNames() => [];
}

/// <summary>Construction geometry (R-em3d41-2e): drawn and snapped to, and the input to Extrude, but
/// NEVER in the solved problem. It has no material.</summary>
/// <para>brief-em3d-45 R-em3d45-3d — a polyline's vertices snap to features off its plane, so one whose points
/// leave the plane is stored in 3D form: <see cref="Points3"/> instead of <see cref="Points"/>, with
/// <see cref="Plane"/>/<see cref="Offset"/> still the plane it was drawn on. <c>Points3</c> is written only then,
/// so a planar polyline's spelling is unchanged.</para>
public sealed class C3dPolyline : C3dObject
{
    public C3dPlane        Plane  { get; set; }
    public long            Offset { get; set; }
    public List<C3dPoint2> Points { get; set; } = [];

    /// <summary>The vertices in 3D, integer DBU, when they do not all lie on the plane; null (and absent from the
    /// file) otherwise. When present it is the polyline and <see cref="Points"/> is empty.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<C3dPoint3>? Points3 { get; set; }

    /// <summary>How many vertices the polyline has, in whichever form it is stored.</summary>
    [JsonIgnore]
    public int VertexCount => Points3?.Count ?? Points.Count;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Closed { get; set; }

    public override IReadOnlyList<string> FaceNames() => [];
}

/// <summary>One planar face of a polyhedron: an outer loop and hole loops, as indices into the
/// polyhedron's vertices, and a NAME that stays with it through every edit.</summary>
public sealed class C3dFace
{
    public string          Name  { get; set; } = "";
    public List<int>       Outer { get; set; } = [];
    public List<List<int>> Holes { get; set; } = [];
}

/// <summary>A closed solid with planar faces — what an edit a primitive cannot express turns an object
/// into (overview §1g).</summary>
public sealed class C3dPolyhedron : C3dObject
{
    public List<C3dPoint3> Vertices { get; set; } = [];
    public List<C3dFace>   Faces    { get; set; } = [];

    public override IReadOnlyList<string> FaceNames() => [.. Faces.Select(f => f.Name)];
}

// ── Bond wires (brief-em3d-50) ────────────────────────────────────────────────────────────────

/// <summary>One end of a <see cref="C3dWire"/>: how it is bonded.</summary>
public sealed class C3dWireEnd
{
    /// <summary>A wedge lays a foot on the pad; a ball sits a flattened ball on it (em-3d.md §6.6).</summary>
    public BondStyle Style { get; set; }

    /// <summary>A wedge's foot length, µm. Omitted: the workspace's assembly rules (<c>.wasm</c>), then about twice
    /// the diameter — and the elaboration says which.</summary>
    public double? FootLengthUm { get; set; }
}

/// <summary>
/// brief-em3d-50 R-em3d50-2 — a bond wire drawn in the 3D view: between two pads that may be in different instances
/// (a die pad inside <c>U1</c>, a package lead in the parent), which a <c>.wBond</c> — attached to one layout — cannot
/// reach across. <see cref="Points"/> are the axis and <b>the only truth about the shape</b> (wbond.md): there is no
/// stored profile. They are WORLD points of this document — a wire has no placement, because it spans things that
/// each have their own — and each end lies on its pad's top surface.
/// </summary>
public sealed class C3dWire : C3dObject
{
    /// <summary>The axis, in order from start to end, integer DBU.</summary>
    public List<C3dPoint3> Points { get; set; } = [];

    /// <summary>The wire's diameter, µm. Omitted: 1 mil (wBond's own default).</summary>
    public double? DiameterUm { get; set; }

    /// <summary>The swept cross-section. Omitted: <c>Hexagon</c>, the flat-bottomed section a foot lands on face to face.</summary>
    public WireCrossSection? Section { get; set; }

    public C3dWireEnd Start { get; set; } = new();
    public C3dWireEnd End   { get; set; } = new();

    /// <summary>3D editor round 4 — a row of identical wires: this one and copies <see cref="C3dWireArray.Pitch"/> apart.
    /// Omitted: one wire.</summary>
    public C3dWireArray? Array { get; set; }

    /// <summary>A wire is a swept solid with no named faces.</summary>
    public override IReadOnlyList<string> FaceNames() => [];
}

/// <summary>
/// 3D editor round 4 — a wire array: <see cref="Count"/> wires, the drawn one first and each next one moved by
/// <see cref="Pitch"/> (a vector, DBU, so a row may run in any direction). Every copy is a wire of its own in the model —
/// its ends looked up on the pads under them and refused by name (<c>w1[2]</c>) like any wire's. One-dimensional on
/// purpose, unlike an instance's <see cref="C3dArray"/>: a bonded row is a number of wires and a pitch.
/// </summary>
public sealed class C3dWireArray : IC3dBindable
{
    [JsonIgnore] public Dictionary<string, C3dExpr?[]>? Exprs { get; set; }

    /// <summary>How many wires, at least 1.</summary>
    public long      Count { get; set; } = 1;
    public C3dPoint3 Pitch { get; set; }

    /// <summary>Element <paramref name="k"/>'s offset from the drawn wire.</summary>
    public C3dPoint3 Offset(long k) => new(k * Pitch.X, k * Pitch.Y, k * Pitch.Z);
}

// ── Kernel operations (brief-em3d-64) ─────────────────────────────────────────────────────────
//
// A SMALL TREE PER OBJECT, NOT A GLOBAL HISTORY (overview §1f). Each of these owns its operands inline and is evaluated
// bottom-up by the geometry worker; the top-level list keeps construction order and precedence exactly as before.
//
// THE WRAPPER TAKES THE NAME (R-em3d64-1d). A Boolean IS its Blank to the rest of the document, a Fillet or Chamfer its
// Target: the object inside carries no Name, so a port or a boundary put on `lid` before anything was subtracted from it
// is still on `lid` afterwards, with nothing rewritten. Tools keep their own names, unique across the whole document.

/// <summary>What a <see cref="C3dBoolean"/> does with its Tools.</summary>
public enum C3dBooleanOp { Subtract, Unite, Intersect }

/// <summary>The operations: a <see cref="C3dBoolean"/>, <see cref="C3dFillet"/> or <see cref="C3dChamfer"/>. <see cref="Enabled"/>
/// false means as if the operation were not there (R-em3d64-4).</summary>
public abstract class C3dOperation : C3dObject
{
    /// <summary>Written only when false. Disabled, the operands elaborate as ordinary objects.</summary>
    [DefaultValue(true)]
    [JsonPropertyOrder(1)]
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// <see cref="Op"/> of one <see cref="Blank"/> and one or more <see cref="Tools"/> (R-em3d64-1a). It takes the Blank's
/// name, material and role; the Blank stores none of its own. The result's faces: the Blank's keep their bare names,
/// a Tool's are <c>&lt;tool&gt;:&lt;face&gt;</c>, and a face the operation split is <c>zmax#1</c>, <c>zmax#2</c> — a
/// reference to <c>zmax</c> covering every piece.
/// </summary>
public sealed class C3dBoolean : C3dOperation
{
    [JsonPropertyOrder(0)]
    public C3dBooleanOp Op { get; set; }

    /// <summary>With <c>Subtract</c>: each Tool also elaborates as its own solid, right after the result, with its own
    /// material — a dielectric fill in a bore stated directly (R-em3d64-3c).</summary>
    [JsonPropertyOrder(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool KeepTools { get; set; }

    [JsonPropertyOrder(3)]
    public C3dObject? Blank { get; set; }

    [JsonPropertyOrder(4)]
    public List<C3dObject> Tools { get; set; } = [];

    /// <summary>The Blank's faces bare, then each Tool's as <c>&lt;tool&gt;:&lt;face&gt;</c> — what the result may have
    /// before split pieces and deleted faces are known, which only the kernel knows.</summary>
    public override IReadOnlyList<string> FaceNames() =>
        [.. Blank?.FaceNames() ?? [], .. Tools.SelectMany(t => t.FaceNames().Select(f => t.Name + ":" + f))];
}

/// <summary>A <see cref="Target"/> with named <see cref="Edges"/> rounded by <see cref="Radius"/>. It takes the target's name.</summary>
public sealed class C3dFillet : C3dOperation
{
    /// <summary>DBU, or an expression; positive.</summary>
    [JsonPropertyOrder(0)]
    public long Radius { get; set; }

    /// <summary>Edges of the target by NAME — the two faces they separate, <c>xmax|zmax</c> (R-em3d64-2b).</summary>
    [JsonPropertyOrder(0)]
    public List<string> Edges { get; set; } = [];

    [JsonPropertyOrder(3)]
    public C3dObject? Target { get; set; }

    /// <summary>The target's faces, and <c>fillet(&lt;edge&gt;)</c> for each listed edge.</summary>
    public override IReadOnlyList<string> FaceNames() =>
        [.. Target?.FaceNames() ?? [], .. Edges.Select(e => $"fillet({e})")];
}

/// <summary>A <see cref="Target"/> with named <see cref="Edges"/> cut by <see cref="Distance"/> (and, when set,
/// <see cref="Distance2"/> along the edge's second face). It takes the target's name.</summary>
public sealed class C3dChamfer : C3dOperation
{
    [JsonPropertyOrder(0)]
    public long Distance { get; set; }

    /// <summary>The distance along the edge's second face; omitted (0) for a symmetric chamfer.</summary>
    [JsonPropertyOrder(0)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Distance2 { get; set; }

    [JsonPropertyOrder(0)]
    public List<string> Edges { get; set; } = [];

    [JsonPropertyOrder(3)]
    public C3dObject? Target { get; set; }

    public override IReadOnlyList<string> FaceNames() =>
        [.. Target?.FaceNames() ?? [], .. Edges.Select(e => $"chamfer({e})")];
}

/// <summary>
/// ONE solid part of a STEP file in the cell's <c>3d/</c> folder (brief 68 owns every field's meaning). The part's
/// assembly transform is applied by the worker from <see cref="Part"/>, never written into the placement, which starts
/// at identity — so an imported part lands exactly where its file puts it. Its faces are <c>face&lt;n&gt;</c>, meaningful
/// for the recorded <see cref="Hash"/>.
/// </summary>
public sealed class C3dStep : C3dObject
{
    /// <summary>Relative to the <c>.c3d</c>, inside the cell's <c>3d/</c> folder.</summary>
    public string File { get; set; } = "";

    /// <summary>The part's occurrence path in the file's assembly, as the worker's STEP reader reports it (<c>1/2</c>).</summary>
    public string Part { get; set; } = "";

    /// <summary><c>sha256:&lt;hex&gt;</c> of the file's bytes.</summary>
    public string Hash { get; set; } = "";

    /// <summary>The file's own length unit, recorded for <c>explain</c>.</summary>
    public string? Unit { get; set; }

    /// <summary>Where the file was imported from, for <i>Reload from Source</i>.</summary>
    public string? SourcePath { get; set; }

    /// <summary>Known only once the kernel has read the part.</summary>
    public override IReadOnlyList<string> FaceNames() => [];
}

// ── Instances ─────────────────────────────────────────────────────────────────────────────────

/// <summary>An array of an instance: <see cref="Counts"/> copies along x, y and z, <see cref="Pitch"/>
/// apart. What it elaborates to is brief 42's.</summary>
public sealed class C3dArray : IC3dBindable
{
    [JsonIgnore] public Dictionary<string, C3dExpr?[]>? Exprs { get; set; }

    /// <summary>[nx, ny, nz], each at least 1.</summary>
    public List<int>  Counts { get; set; } = [1, 1, 1];
    public C3dPoint3  Pitch  { get; set; }
}

/// <summary>
/// A placed cell (R-em3d41-4). <see cref="CellRef"/> is spelled exactly as a <c>.clay</c> instance's —
/// relative to this document's folder, or <c>ws://</c> — so every walker that follows a cell reference
/// follows this one the same way. What it MEANS is brief 42's.
/// </summary>
public sealed class C3dInstance
{
    public string Name    { get; set; } = "";
    public string CellRef { get; set; } = "";

    /// <summary>The group the instance is in, as an object's <see cref="C3dObject.Group"/> is. Omitted: in none.</summary>
    public string? Group { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public C3dInstanceView View { get; set; }

    public C3dPlacement Placement { get; set; } = new();
    public C3dArray?    Array     { get; set; }

    /// <summary>
    /// brief-em3d-51 R-em3d51-2e — overrides of the placed cell's PARAMETERS, each an expression evaluated in THIS
    /// document's scope (override → parent scope, expressions.md §9). A name that is only a VAR of the child is refused,
    /// and a <c>Layout</c> instance takes none: a <c>.clay</c> has no parameters.
    /// </summary>
    public Dictionary<string, C3dExpr>? Params { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

// ── Ports and face boundaries (brief-em3d-49) ─────────────────────────────────────────────────

/// <summary>A wave port's stated voltage path, on the port's own plane: from the negative conductor to the positive one.</summary>
public sealed class C3dVoltagePath
{
    public C3dPoint2 From { get; set; }
    public C3dPoint2 To   { get; set; }
}

/// <summary>
/// brief-em3d-49 R-em3d49-2a — a port, drawn in the parent (overview §1k: a placed cell's ports are never used). A
/// <see cref="Rect"/> on a drawing plane, axis-aligned. A lumped port is a sheet between two conductors; which two, and
/// which way round, is INFERRED from what the rectangle's edges touch unless <see cref="Positive"/> and
/// <see cref="Negative"/> say (both, or neither). A wave port's rectangle is a region of an air-box face of the setup
/// being run. Every setup uses every port.
/// </summary>
public sealed class C3dPort
{
    public int    Number { get; set; }
    public string Name   { get; set; } = "";
    public Em3dPortKind Kind { get; set; }
    public C3dPlane Plane  { get; set; }
    public long     Offset { get; set; }
    public C3dRect  Rect   { get; set; } = new();

    /// <summary>The reference impedance, Ω — a real number or a complex one (<c>50</c>, <c>25+j10</c>), as a .cem's.</summary>
    public string Z0 { get; set; } = "50";

    /// <summary>The positive conductor, by elaborated name (<c>U1/pad3</c> allowed) or an air-box face
    /// (<c>airbox/zmin</c>); null is inferred.</summary>
    public string? Positive { get; set; }

    /// <summary>The negative conductor; null is inferred. Stating one without the other is refused.</summary>
    public string? Negative { get; set; }

    /// <summary>Swaps the inferred (or stated) conductors: a wrong polarity is a 180° error in every transmission term.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Flip { get; set; }

    /// <summary>A wave port's voltage path; null is inferred as brief 23 infers it.</summary>
    public C3dVoltagePath? VoltagePath { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>
/// brief-em3d-49 R-em3d49-4a — a boundary on a NAMED face of a dielectric or air object (overview §1e): it follows the
/// face through every edit, and a fold hands it to every piece. <see cref="Material"/> is a Conductive face's metal.
/// </summary>
public sealed class C3dFaceBoundary
{
    public string Object { get; set; } = "";
    public string Face   { get; set; } = "";
    public Em3dFaceBoundaryKind Kind { get; set; }
    public string? Material { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

// ── VARs (brief-em3d-51) ──────────────────────────────────────────────────────────────────────

/// <summary>
/// brief-em3d-51 R-em3d51-1c — a VAR of the 3D view: a name, an expression and a unit, as a <c>.csch</c> VAR row. The
/// unit scales the expression (<c>10</c> with <c>Mil</c> is ten mil); with none the expression takes the units of the
/// names it references. <see cref="Linked"/> says what happens when the cell has a PARAMETER of the same name.
/// </summary>
public sealed class C3dVariable
{
    public string  Name       { get; set; } = "";
    public string  Expression { get; set; } = "";
    public string? Unit       { get; set; }

    /// <summary>
    /// With a cell parameter of the same name: <b>linked</b> (true, or absent — owner decision D12) takes the PARAMETER's
    /// value, an instance's override else the <c>.ccell</c> default, and this VAR's own expression is used only when the
    /// document has no cell; <b>false</b> keeps this VAR's own expression, which then hides the parameter from every
    /// dimension here (check warns). Ignored when no parameter has the name.
    /// </summary>
    public bool? Linked { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

// ── The document ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A cell's 3D view — the mutable working model and the file (R-em3d41-3a). Framework-free.
/// <see cref="C3dPersistence"/> reads and writes it.
/// </summary>
public sealed class C3dDocument
{
    public int FormatVersion { get; set; } = C3dPersistence.CurrentFormatVersion;

    /// <summary>What one DBU is worth, exactly as a <c>.clay</c>'s: at 1000 one DBU is a nanometre.</summary>
    public int DbuPerMicron { get; set; } = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>A document preference, not geometry: changing it moves nothing (layout-view §1.3).</summary>
    public LayoutUnit DisplayUnit { get; set; } = LayoutUnit.Um;

    /// <summary>The drawing grid. It never re-snaps geometry.</summary>
    public long SnapDbu { get; set; }

    /// <summary>Resolved as a <c>.clay</c>'s is — relative to this file, or null for the workspace's
    /// default. The materials come from here.</summary>
    public string? TechRef { get; set; }

    /// <summary>Always written, even empty: with <see cref="FormatVersion"/> it is how a <c>.c3d</c> is
    /// told from another program's file of the same extension (overview §1c).</summary>
    [JsonRequired]
    public List<C3dObject>   Objects   { get; set; } = [];

    public List<C3dInstance> Instances { get; set; } = [];

    /// <summary>
    /// brief-em3d-51 R-em3d51-1c — the 3D view's VARs, the record a <c>.csch</c> VAR row holds. With the cell's
    /// parameters they are one namespace; a VAR named like a parameter is LINKED to it unless it says otherwise.
    /// </summary>
    public List<C3dVariable> Variables      { get; set; } = [];

    /// <summary>brief-em3d-49 — the document's ports. They belong to the document, not to a setup: every setup uses all
    /// of them.</summary>
    public List<C3dPort> Ports { get; set; } = [];

    /// <summary>brief-em3d-49 — boundaries on named faces of dielectric and air objects.</summary>
    public List<C3dFaceBoundary> FaceBoundaries { get; set; } = [];

    /// <summary>brief-em3d-73 R-em3d73-4a — where heat is put. Read by thermal setups only; no EM lowering sees one.</summary>
    public List<C3dHeatSource> HeatSources { get; set; } = [];

    /// <summary>brief-em3d-73 R-em3d73-4b — where temperature is read.</summary>
    public List<C3dProbe> Probes { get; set; } = [];

    /// <summary>brief-em3d-73 R-em3d73-4c — boxes with a target element size, for every setup that meshes with Gmsh.</summary>
    public List<C3dMeshRegion> MeshRegions { get; set; } = [];

    /// <summary>brief-em3d-73 R-em3d73-4d — per-contact overrides of the technology's thermal interface resistances.</summary>
    public List<C3dContactResistance> ContactResistances { get; set; } = [];

    /// <summary>
    /// 3D editor round 3 — the material that fills the air box where no object is: a technology material's name. Absent
    /// means Air (the common case; the technology's Air, else a built-in one with free space's values) — the user makes the
    /// box a vacuum by naming Vacuum here. It belongs to the document, not to a setup: it is physics, like an object's material.
    /// </summary>
    public string? AirBoxMaterial { get; set; }

    /// <summary>
    /// 3D editor round 5 — the air box is hidden in the editor (its faces and its edges). Document state exactly as an
    /// object's <see cref="C3dObject.Hidden"/> is: saved with the file and undoable, so a reopened document shows what it
    /// was saved showing. Drawing only — a run solves in the box whether or not it is drawn.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AirBoxHidden { get; set; }

    /// <summary>Brief 42's embedded EM setups (the <c>.cem</c> schema minus <c>LayoutRef</c>), each read by the
    /// <c>.cem</c> reader (<see cref="C3dSetups.Read"/>).</summary>
    public List<JsonElement> Setups         { get; set; } = [];

    /// <summary>Keys this build does not read, kept and written back — see <see cref="C3dObject.Unread"/>.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}
