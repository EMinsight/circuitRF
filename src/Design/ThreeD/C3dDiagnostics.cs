using CircuitRF.Diagnostics;

namespace CircuitRF.Design.ThreeD;

/// <summary>
/// What the <c>.c3d</c> reader refuses and what its validator finds, as coded diagnostics
/// (<c>EmDiagnostics</c> is the pattern). Returned, never posted: <c>check</c> writes them to stderr and
/// the editor, when it exists, to the Messages window.
/// </summary>
public static class C3dDiagnostics
{
    // ── Refusals: the file cannot be read at all ─────────────────────────────────────────────────

    public static Diagnostic Unreadable(string reason) => Diagnostic.Create(
        "c3d.read.unreadable", DiagnosticSeverity.Error,
        "The 3D view could not be read: {reason}", ("reason", reason));

    public static Diagnostic NotAnObject() => new(
        "c3d.read.not-an-object", DiagnosticSeverity.Error,
        "The 3D view is not a JSON object, so it is not a circuitRF 3D view.");

    public static Diagnostic NewerFormat(int version, int current) => Diagnostic.Create(
        "c3d.read.newer-format", DiagnosticSeverity.Error,
        "This 3D view is format version {version}, newer than this build reads ({current}). Update the application.",
        ("version", version), ("current", current));

    /// <summary>R-em3d41-2d: an unknown <c>$type</c> is named, never a crash.</summary>
    public static Diagnostic UnknownKinds(string which, string known) => Diagnostic.Create(
        "c3d.read.unknown-kind", DiagnosticSeverity.Error,
        "This build cannot read {which}. It reads: {known}. The document was probably written by a later build.",
        ("which", which), ("known", known));

    public static Diagnostic MissingKind(int index) => Diagnostic.Create(
        "c3d.read.missing-kind", DiagnosticSeverity.Error,
        "Object {index} has no \"$type\", so there is no telling what kind of object it is.",
        ("index", index));

    /// <summary>R-em3d41-2d / brief-em3d-51: a bare string where a number belongs. A dimension's expression is an object
    /// carrying its unit; a point list holds numbers only.</summary>
    public static Diagnostic ExpressionNotYet(string text) => Diagnostic.Create(
        "c3d.read.expression", DiagnosticSeverity.Error,
        "\"{text}\" is written where a number belongs. Point lists (Outline, Holes, Points, Vertices) hold numbers only; " +
        "a named dimension may hold an expression, written {example} with its unit.",
        ("text", text), ("example", Example(text)));

    /// <summary>brief-em3d-51 R-em3d51-1a — an expression written as a bare string in a dimension field.</summary>
    public static Diagnostic ExpressionAsString(string text) => Diagnostic.Create(
        "c3d.read.expression", DiagnosticSeverity.Error,
        "\"{text}\" is written as a bare string. An expression in a dimension is an object that carries the unit it was " +
        "typed in — {example} — so that changing the display unit never changes what it means.",
        ("text", text), ("example", Example(text)));

    /// <summary>brief-em3d-51 — an expression object with a key it does not have, or no text.</summary>
    public static Diagnostic ExpressionShape(string key) => Diagnostic.Create(
        "c3d.read.expression-shape", DiagnosticSeverity.Error,
        "An expression in a dimension is written {example}; '{key}' is not one of its keys, or its Expr is empty.",
        ("key", key), ("example", Example("…")));

    private static string Example(string text) => "{ \"Expr\": \"" + text + "\", \"Unit\": \"Mil\" }";

    public static Diagnostic NumberExpected(string found) => Diagnostic.Create(
        "c3d.read.number-expected", DiagnosticSeverity.Error,
        "A number was expected and a {found} was found.", ("found", found));

    public static Diagnostic IntegerExpected(string text) => Diagnostic.Create(
        "c3d.read.integer-expected", DiagnosticSeverity.Error,
        "{text} is not an integer. Every coordinate and size in a 3D view is an integer number of DBU.",
        ("text", text));

    public static Diagnostic ArrayExpected(string shape) => Diagnostic.Create(
        "c3d.read.array-expected", DiagnosticSeverity.Error,
        "A list spelled {shape} was expected here.", ("shape", shape));

    public static Diagnostic WrongArity(string shape, int expected, int found) => Diagnostic.Create(
        "c3d.read.wrong-arity", DiagnosticSeverity.Error,
        "A point is spelled {shape}: {expected} integers, and this one has {found}.",
        ("shape", shape), ("expected", expected), ("found", found));

    // ── Findings: the file reads, and something in it is wrong (R-em3d41-2f) ───────────────────

    public static Diagnostic InvalidName(string kind, string name, string reason) => Diagnostic.Create(
        "c3d.name.invalid", DiagnosticSeverity.Error,
        "The {kind} name '{name}' is not usable: {reason}", ("kind", kind), ("name", name), ("reason", reason));

    public static Diagnostic ReservedName(string name) => Diagnostic.Create(
        "c3d.name.reserved", DiagnosticSeverity.Error,
        "'{name}' is reserved: the air box's faces are named airbox/xmin, airbox/zmax and so on.",
        ("name", name));

    public static Diagnostic DuplicateName(string name, int count) => Diagnostic.Create(
        "c3d.name.duplicate", DiagnosticSeverity.Error,
        "The name '{name}' is used by {count} objects. A port or a boundary attaches to an object by name, " +
        "so every name must be unique in the document.",
        ("name", name), ("count", count));

    public static Diagnostic NoMaterial(string name) => Diagnostic.Create(
        "c3d.material.missing", DiagnosticSeverity.Error,
        "'{name}' names no material, so nothing says what it is made of.", ("name", name));

    /// <summary>A warning, because materials resolve late (brief 42): the technology may not be the
    /// one the document will be elaborated against.</summary>
    public static Diagnostic UnknownMaterial(string name, string material) => Diagnostic.Create(
        "c3d.material.unknown", DiagnosticSeverity.Warning,
        "'{name}' is made of '{material}', which the technology does not define.",
        ("name", name), ("material", material));

    public static Diagnostic TooFewPoints(string name, string loop, int distinct) => Diagnostic.Create(
        "c3d.outline.too-few-points", DiagnosticSeverity.Error,
        "The {loop} of '{name}' has {distinct} distinct points; it needs at least three.",
        ("name", name), ("loop", loop), ("distinct", distinct));

    public static Diagnostic ZeroVolume(string name, string kind, string why) => Diagnostic.Create(
        "c3d.solid.zero-volume", DiagnosticSeverity.Error,
        "The {kind} '{name}' has no volume: {why}.", ("name", name), ("kind", kind), ("why", why));

    public static Diagnostic SheetShape(string name, string why) => Diagnostic.Create(
        "c3d.sheet.shape", DiagnosticSeverity.Error,
        "The sheet '{name}' {why}.", ("name", name), ("why", why));

    public static Diagnostic PolylineTooShort(string name, int count) => Diagnostic.Create(
        "c3d.polyline.too-short", DiagnosticSeverity.Error,
        "The polyline '{name}' has {count} point(s); it needs at least two.", ("name", name), ("count", count));

    /// <summary>brief-em3d-50 — a wire that cannot be a wire, whatever it lands on.</summary>
    public static Diagnostic WireShape(string name, string why) => Diagnostic.Create(
        "c3d.wire.shape", DiagnosticSeverity.Error,
        "The wire '{name}' {why}.", ("name", name), ("why", why));

    /// <summary>brief-em3d-50 R-em3d50-2 — a wire's points are world points: it has no placement.</summary>
    public static Diagnostic WirePlacement(string name) => Diagnostic.Create(
        "c3d.wire.placement", DiagnosticSeverity.Error,
        "The wire '{name}' states a Placement. A wire has none: its Points are where it is, because it spans things that " +
        "each have their own placement. Write the points where the wire is, and leave Placement out.", ("name", name));

    public static Diagnostic FaceIndex(string name, string face, int index, int vertexCount) => Diagnostic.Create(
        "c3d.polyhedron.bad-index", DiagnosticSeverity.Error,
        "Face '{face}' of '{name}' names vertex {index}, and the polyhedron has {vertexCount} vertices.",
        ("name", name), ("face", face), ("index", index), ("vertexCount", vertexCount));

    public static Diagnostic FaceTooSmall(string name, string face) => Diagnostic.Create(
        "c3d.polyhedron.face-too-small", DiagnosticSeverity.Error,
        "Face '{face}' of '{name}' has a loop of fewer than three vertices.", ("name", name), ("face", face));

    public static Diagnostic FaceUnnamed(string name, int index) => Diagnostic.Create(
        "c3d.polyhedron.face-unnamed", DiagnosticSeverity.Error,
        "Face {index} of '{name}' has no name. Faces are named, never indexed, so a boundary survives an edit.",
        ("name", name), ("index", index));

    public static Diagnostic FaceNameDuplicate(string name, string face) => Diagnostic.Create(
        "c3d.polyhedron.face-duplicate", DiagnosticSeverity.Error,
        "'{name}' has more than one face named '{face}'.", ("name", name), ("face", face));

    /// <summary>R-em3d41-2f: a failure names the edge's two vertices.</summary>
    public static Diagnostic NotClosed(string name, string edges) => Diagnostic.Create(
        "c3d.polyhedron.not-closed", DiagnosticSeverity.Error,
        "The polyhedron '{name}' is not closed. Every edge must be used by exactly two faces, in opposite " +
        "directions, and these are not: {edges}.",
        ("name", name), ("edges", edges));

    public static Diagnostic NotPlanar(string name, string face, double deviation) => Diagnostic.Create(
        "c3d.polyhedron.not-planar", DiagnosticSeverity.Error,
        "Face '{face}' of '{name}' is not planar: a vertex lies {deviation} DBU off the face's plane, and " +
        "the limit is 1 DBU.",
        ("name", name), ("face", face), ("deviation", deviation));

    public static Diagnostic FaceNoArea(string name, string face) => Diagnostic.Create(
        "c3d.polyhedron.face-no-area", DiagnosticSeverity.Error,
        "Face '{face}' of '{name}' has no area: its vertices are in a line.", ("name", name), ("face", face));

    public static Diagnostic InstanceNoCell(string name) => Diagnostic.Create(
        "c3d.instance.no-cell", DiagnosticSeverity.Error,
        "The instance '{name}' names no cell.", ("name", name));

    public static Diagnostic ArrayCounts(string name) => Diagnostic.Create(
        "c3d.instance.array-counts", DiagnosticSeverity.Error,
        "The array on '{name}' must give three counts, [nx, ny, nz], each at least 1.", ("name", name));

    public static Diagnostic UnreadKey(string owner, string key) => Diagnostic.Create(
        "c3d.key.unread", DiagnosticSeverity.Warning,
        "{owner} has a key '{key}' that is not part of the format. It is kept, and nothing reads it.",
        ("owner", owner), ("key", key));
}
