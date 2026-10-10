// brief-em3d-129 — Split into Solids: a Step object with no Solid whose part holds several solids becomes what brief 128's
// import would have made of it — one Step object per solid, gathered in a group named after the old object, every reference
// to the old object's faces moved to the piece that owns the face.
//
//   Plan      the object → its pieces, their group, and where each face reference goes — or every reason it cannot be split
//   Apply     a plan → the old object replaced AT ITS INDEX by the pieces in Solid order, and every reference re-pointed
//   Findings  check's word on such an object: a WARNING when its solids' colours differ, a NOTE otherwise (R-em3d129-1a)
//
// NOTHING MOVES. Each piece copies the old object's placement, which composes on top of the file's own location exactly as
// it did for the whole; the k-th solid is built where the file puts it (brief 127). The pieces name the same File and Hash,
// so no file is written.
//
// REFERENCES MOVE BY GEOMETRY, NEVER BY INDEX (overview rule 5). A face of the whole is the one face of one piece that
// coincides with it (StepImport.SameFace, in the file's own frame). Zero or several is a refusal naming the reference, and so
// is a reference to the WHOLE object — a heat source over it, a port's conductor, a static terminal — which no single piece
// can stand for. The walker is StepImport.References, shared with Reload from Source.

using CircuitRF.Design.Cells;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using System.Text.Json;

namespace CircuitRF.Design.ThreeD.Step;

/// <summary>One reference a split moves: <paramref name="What"/>, from the old object's face to a piece's.</summary>
public sealed record StepSplitMove(string What, string From, string To)
{
    public override string ToString() => $"{What}: {From} is {To}.";
}

/// <summary>What <see cref="StepSplit.Plan(C3dDocument, string, string, GeometryKernel, RunControl?)"/> decided.</summary>
/// <param name="Object">The object split, by name.</param>
/// <param name="Index">Its index in the document's object list: where the pieces go.</param>
/// <param name="Group">The group path the pieces gather in: the old object's group plus one level named after it.</param>
/// <param name="Pieces">One Step object per closed solid, in Solid order.</param>
/// <param name="Dropped">The part's solids that are not closed, each with why: left out, and only after asking.</param>
/// <param name="Refusals">Every reason the split cannot be made; empty when it can.</param>
public sealed record StepSplitPlan(string Object, int Index, string Group, IReadOnlyList<C3dStep> Pieces,
                                   IReadOnlyList<string> Dropped, IReadOnlyList<string> Refusals)
{
    /// <summary>Old face number → (index into <see cref="Pieces"/>, that piece's face number).</summary>
    public IReadOnlyDictionary<int, (int Piece, int Face)> Map { get; init; } = new Dictionary<int, (int, int)>();

    /// <summary>Every face reference the split moves, in words.</summary>
    public IReadOnlyList<StepSplitMove> Moves { get; init; } = [];

    public bool Refused => Refusals.Count > 0;
}

public static class StepSplit
{
    /// <summary>The top-level object named <paramref name="objectName"/> (or the operand of that name), planned as
    /// <see cref="Plan(C3dDocument, string, C3dStep, GeometryKernel, RunControl?)"/> plans it.</summary>
    public static StepSplitPlan Plan(C3dDocument doc, string c3dPath, string objectName, GeometryKernel kernel, RunControl? control = null)
    {
        var step = doc.Objects.SelectMany(C3dOperands.SelfAndDescendants).OfType<C3dStep>()
                              .FirstOrDefault(s => string.Equals(s.Name, objectName, StringComparison.Ordinal));
        return step is null
            ? new StepSplitPlan(objectName, -1, "", [], [], [$"No Step object is named '{objectName}'."])
            : Plan(doc, c3dPath, step, kernel, control);
    }

    /// <summary>
    /// R-em3d129-2a — what splitting <paramref name="step"/> would do: its pieces, their group and where every face reference
    /// lands — or every reason it cannot (R-em3d129-2b/-2c), gathered rather than stopping at the first. Reads the copied file
    /// and the faces of the whole and of each piece; writes nothing.
    /// </summary>
    /// <exception cref="GeometryKernelException">The kernel refused, crashed, or is absent.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing changed.</exception>
    public static StepSplitPlan Plan(C3dDocument doc, string c3dPath, C3dStep step, GeometryKernel kernel, RunControl? control = null)
    {
        int index = doc.Objects.IndexOf(step);
        string name = step.Name;
        StepSplitPlan Refuse(params string[] why) => new(name, index, "", [], [], why);

        if (index < 0)
            return OperandOf(doc, step) is { } op ? Refuse(OperandRefusal(step, op.Op, op.Role)) : Refuse($"'{name}' is not in this document.");
        if (step.Solid is { } k)
            return Refuse($"'{name}' is already one solid (solid {k}) of part {step.Part} of '{step.File}'; there is nothing to split.");

        string dir = Path.GetDirectoryName(Path.GetFullPath(c3dPath))!;
        string file = Path.Combine(dir, step.File);
        if (!File.Exists(file)) return Refuse($"'{name}' names '{step.File}', which is not in this 3D view's folder.");
        byte[] bytes = File.ReadAllBytes(file);
        if (!string.Equals(StepImport.HashOf(bytes), step.Hash, StringComparison.OrdinalIgnoreCase))
            return Refuse($"'{step.File}' has changed since '{name}' was read from it, so its face numbers no longer mean what the " +
                          "references on it assumed. Reload from Source first.");
        var part = kernel.ImportStep(bytes, control).Parts.FirstOrDefault(p => p.Path == step.Part);
        if (part is null) return Refuse($"'{name}' is part {step.Part} of '{step.File}', which the file does not have.");
        if (part.Solids.Count < 2)
            return Refuse($"'{name}' is part {step.Part} of '{step.File}', which is one solid; there is nothing to split.");

        var refusals = new List<string>();
        var closed = part.Solids.Where(s => s.Closed).ToList();
        var dropped = part.Solids.Where(s => !s.Closed).Select(s => $"solid {s.Index} ({s.Why})").ToList();
        if (closed.Count == 0) return Refuse($"No solid of '{name}' is a closed solid, so there is nothing to split it into: {string.Join("; ", dropped)}.");

        // The group: the old object's path plus one level named after it — free once the object is gone, unless a group has it.
        string group = C3dGroups.Join([.. C3dGroups.Segments(step.Group), name])!;
        if (C3dGroups.NameRefusal(doc, name) is { } badGroup)
            refusals.Add($"The pieces gather in a group named after '{name}': {badGroup}");

        // Overview D5: the solid's own name when the file gives one that is legal and unused, else <object>_<k>.
        var used = StepImport.UsedNames(doc);
        used.Remove(name);
        var pieces = new List<C3dStep>();
        foreach (var s in closed)
        {
            string own = s.Name.Trim();
            string pieceName = own.Length > 0 && NameValidator.Validate(own) is null && !used.Contains(own) ? own : $"{name}_{s.Index}";
            if (!used.Add(pieceName))
                refusals.Add($"'{pieceName}' (solid {s.Index} of '{name}') is already a name in this document.");
            var piece = (C3dStep)C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(step));
            piece.Name = pieceName;
            piece.Solid = s.Index;
            piece.Group = group;
            piece.FaceImages = null;     // each image goes to the piece that owns its face, with the other references
            pieces.Add(piece);
        }

        refusals.AddRange(ObjectReferences(doc, name).Select(what =>
            $"{what} names '{name}' as a whole, and no one piece can stand for it; remove it, split, then add it back on the piece it means."));

        // R-em3d129-2a — every face reference, to the one piece face that coincides with it.
        var refs = StepImport.References(doc, step);
        var map = new Dictionary<int, (int, int)>();
        var moves = new List<StepSplitMove>();
        if (refs.Count > 0)
        {
            double tolUm = Math.Max(1.0 / Math.Max(doc.DbuPerMicron, 1), 1e-6);
            var whole = kernel.Faces(StepImport.StepTree(step, file, step.Hash, null, doc.DbuPerMicron));
            var faces = closed.Select(s => kernel.Faces(StepImport.StepTree(step, file, step.Hash, s.Index, doc.DbuPerMicron))).ToList();
            foreach (int n in refs.Select(r => r.Face).Distinct().Order())
            {
                string what = refs.First(r => r.Face == n).What;
                if (n < 1 || n > whole.Count)
                {
                    refusals.Add($"{what} is on {name}/face{n}, which '{name}' does not have.");
                    continue;
                }
                var hits = faces.SelectMany((pf, p) => pf.Select((f, m) => (p, m, f))).Where(h => StepImport.SameFace(whole[n - 1], h.f, tolUm)).ToList();
                if (hits.Count == 1)
                {
                    map[n] = (hits[0].p, hits[0].m + 1);
                    continue;
                }
                refusals.Add(hits.Count == 0
                    ? $"{what} is on {name}/face{n}, which coincides with no face of any piece."
                    : $"{what} is on {name}/face{n}, which coincides with {hits.Count} faces of the pieces; circuitRF does not choose one.");
            }
            foreach (var r in refs.Where(r => map.ContainsKey(r.Face)))
            {
                var (p, m) = map[r.Face];
                moves.Add(new StepSplitMove(r.What, $"{name}/face{r.Face}", $"{pieces[p].Name}/face{m}"));
            }
        }
        return new StepSplitPlan(name, index, group, pieces, dropped, refusals) { Map = map, Moves = moves };
    }

    /// <summary>
    /// R-em3d129-2d — the split: the old object replaced, at its index, by the pieces in Solid order, so construction order is
    /// kept, and every reference re-pointed. The one function the GUI and any headless path call.
    /// </summary>
    /// <param name="dropNonClosed">The user was asked, and agreed, to leave out the solids that are not closed.</param>
    /// <exception cref="StepImportException">The plan was refused, needs that answer, or no longer fits the document.</exception>
    public static IReadOnlyList<C3dStep> Apply(StepSplitPlan plan, C3dDocument doc, bool dropNonClosed = false)
    {
        if (plan.Refused) throw new StepImportException(StepDiagnostics.Split(string.Join(" ", plan.Refusals)));
        if (plan.Dropped.Count > 0 && !dropNonClosed)
            throw new StepImportException(StepDiagnostics.Split(
                $"Splitting '{plan.Object}' leaves out {string.Join("; ", plan.Dropped)}, which is not a closed solid; that has to be agreed to first."));
        if (plan.Index < 0 || plan.Index >= doc.Objects.Count || doc.Objects[plan.Index] is not C3dStep { Solid: null } old || old.Name != plan.Object)
            throw new StepImportException(StepDiagnostics.Split($"The document changed since '{plan.Object}''s split was planned: split it again."));
        if (StepImport.References(doc, old).FirstOrDefault(r => !plan.Map.ContainsKey(r.Face)) is { } stale)
            throw new StepImportException(StepDiagnostics.Split($"{stale.What} is on {plan.Object}/face{stale.Face}, which the plan did not place: split it again."));

        StepImport.Repoint(doc, old, n => plan.Map.TryGetValue(n, out var t) ? (plan.Pieces[t.Piece], t.Face) : null);
        doc.Objects.RemoveAt(plan.Index);
        doc.Objects.InsertRange(plan.Index, plan.Pieces);
        return plan.Pieces;
    }

    // ── check and explain (R-em3d129-1) ─────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d129-1a — one finding per Step object with no Solid whose part has more than one solid: a WARNING when its
    /// solids' colours differ (they are probably different materials), a NOTE otherwise. A product of one solid has none.
    /// <paramref name="built"/> is the elaboration's solid count by object name, when known: an object built as one solid
    /// asks the kernel nothing more. Each file is read once.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Findings(C3dDocument doc, string c3dPath, GeometryKernel kernel,
                                                     IReadOnlyDictionary<string, int>? built = null)
    {
        var found = new List<Diagnostic>();
        var reads = new Dictionary<string, GeometryKernelImport?>(StringComparer.Ordinal);
        foreach (var top in doc.Objects)
            foreach (var step in C3dOperands.SelfAndDescendants(top).OfType<C3dStep>())
            {
                if (step.Solid is not null || built?.TryGetValue(step.Name, out int n) == true && n < 2) continue;
                if (PartOf(step, c3dPath, kernel, reads) is not { Solids.Count: > 1 } part) continue;
                string label = step.Name.Length > 0 ? step.Name : top.Name;
                bool differ = part.Solids.Select(ColourWord).Distinct(StringComparer.Ordinal).Count() > 1;
                found.Add(C3dDiagnostics.StepManySolids(label, part.Solids.Count, step.File, step.Part, differ));
            }
        return found;
    }

    /// <summary>R-em3d129-1b — the part <paramref name="step"/> names, as the kernel reads its copied file — or null when the
    /// file is not there or holds no such part.</summary>
    public static GeometryKernelImportPart? PartOf(C3dStep step, string c3dPath, GeometryKernel kernel)
        => PartOf(step, c3dPath, kernel, new Dictionary<string, GeometryKernelImport?>(StringComparer.Ordinal));

    private static GeometryKernelImportPart? PartOf(C3dStep step, string c3dPath, GeometryKernel kernel, Dictionary<string, GeometryKernelImport?> reads)
    {
        string file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(c3dPath))!, step.File));
        if (!reads.TryGetValue(file, out var read))
            reads[file] = read = File.Exists(file) ? kernel.ImportStep(File.ReadAllBytes(file)) : null;
        return read?.Parts.FirstOrDefault(p => p.Path == step.Part);
    }

    /// <summary>A solid's colour in words: <c>#rrggbb</c>, <c>mixed</c> when its faces disagree, <c>none</c> when it has none.</summary>
    public static string ColourWord(GeometryKernelImportSolid s)
        => s.Colour is { Length: 3 } c ? StepImport.Hex(c) : s.Mixed ? "mixed" : "none";

    // ── references to the whole object (R-em3d129-2c) ───────────────────────────────────────

    /// <summary>Everything that names the object <paramref name="name"/> itself rather than one of its faces: a port's
    /// conductor, reference or terminal, a heat source or probe over its volume, a contact resistance, and an embedded static
    /// setup's terminal.</summary>
    internal static List<string> ObjectReferences(C3dDocument doc, string name)
    {
        bool Is(string? s) => string.Equals(s, name, StringComparison.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var p in doc.Ports)
        {
            if (Is(p.Positive)) list.Add($"Port '{p.Name}''s Positive");
            if (Is(p.Negative)) list.Add($"Port '{p.Name}''s Negative");
            if (Is(p.Reference)) list.Add($"Port '{p.Name}''s Reference");
            foreach (var t in p.Terminals ?? []) if (Is(t.Conductor)) list.Add($"Terminal '{t.Name}' of port '{p.Name}'");
        }
        foreach (var h in doc.HeatSources) if (Is(h.Solid)) list.Add($"The heat source '{h.Name}'");
        foreach (var p in doc.Probes) if (Is(p.Solid)) list.Add($"The probe '{p.Name}'");
        foreach (var c in doc.ContactResistances)
            if (c.Between.Any(Is)) list.Add($"The contact resistance between {string.Join(" and ", c.Between.Select(b => $"'{b}'"))}");
        foreach (var setup in doc.Setups)
        {
            if (setup.ValueKind != JsonValueKind.Object) continue;
            string setupName = Get(setup, "Name") is { ValueKind: JsonValueKind.String } sn ? sn.GetString()! : "";
            if (Get(setup, "Terminals3D") is not { ValueKind: JsonValueKind.Array } terminals) continue;
            foreach (var t in terminals.EnumerateArray())
                if (t.ValueKind == JsonValueKind.Object && Get(t, "Objects") is { ValueKind: JsonValueKind.Array } objects
                    && objects.EnumerateArray().Any(o => o.ValueKind == JsonValueKind.String && Is(o.GetString())))
                    list.Add($"Terminal '{(Get(t, "Name") is { ValueKind: JsonValueKind.String } tn ? tn.GetString() : "")}' of setup '{setupName}'");
        }
        return list;
    }

    private static JsonElement? Get(JsonElement o, string key)
    {
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    // ── an operand (R-em3d129-2b) ───────────────────────────────────────────────────────────

    /// <summary>The operation <paramref name="step"/> is a direct operand of, and its role there — or null when it is no
    /// operand.</summary>
    private static (C3dObject Op, string Role)? OperandOf(C3dDocument doc, C3dStep step)
    {
        foreach (var top in doc.Objects)
            foreach (var o in C3dOperands.SelfAndDescendants(top))
                foreach (var (prefix, child) in C3dOperands.Of(o))
                    if (ReferenceEquals(child, step))
                        return (o.Name.Length > 0 ? o : top, prefix.StartsWith("Blank", StringComparison.Ordinal) ? "Blank"
                                                           : prefix.StartsWith("Tools", StringComparison.Ordinal) ? "Tool" : "Target");
        return null;
    }

    /// <summary>An operation's operand has no group and no tree row of its own, so there is nothing to split it into.</summary>
    private static string OperandRefusal(C3dStep step, C3dObject op, string role)
    {
        string label = step.Name.Length > 0 ? $"'{step.Name}'" : $"Part {step.Part} of '{step.File}'";
        return $"{label} is the {role} of '{op.Name}'; split it before it is combined" +
               (op is C3dBoolean ? $" (Dissolve Boolean makes it a top-level object again)." : ".");
    }
}
