// brief-em3d-48 — hierarchy in a .c3d, headless: what the 3D editor's Place, Swap View, Flatten, Group into Cell and
// New 3D View from Layout DO, with no display. The editor (src/Ui/ThreeD) only asks, confirms and pushes one undo
// entry; every rule lives here, so `check`, a test and the GUI cannot disagree about one.
//
// CYCLES ARE BY (CELL, VIEW), NOT BY CELL (R-em3d48-6a, §7). A 3D view may hold its own cell's LAYOUT — the views
// differ, and a layout never contains a 3D view — but never its own cell's 3D view, directly or through other 3D
// views. The check walks the child's 3D instances at the pick, before anything is written; the elaborator's own
// cycle refusal stays as the backstop for a file edited by hand.
//
// FLATTEN (R-em3d48-5, L3c's rules carried to 3D). One level of a 3D child is EXACT: each child object keeps its
// primitive and its own frame, and only its placement is composed with the instance's (C3dPlacement.Then, the
// canonical form brief 46 wrote); the child's own instances come up a level the same way. A LAYOUT child has no
// objects to keep — it becomes its ELABORATED solids (prisms, sheets, cylinders, and wires as polyhedra), which is
// why the editor asks first and states the count. A flattened object's material must mean the same in this
// document's technology as it did in its own: a material the technology lacks, or holds with other values, is a
// refusal naming it, never a silent swap of conductivity.
//
// GROUP INTO CELL moves the chosen objects into a new cell's 3D view and puts one instance of it where they were.
// The geometry does not move (R-L3c-5): every grouped placement is moved by −anchor and the instance sits AT the
// anchor, which is an integer translation, so it is exact. The folder is created through CellFolder.CreateCellFolder
// and is never deleted by an undo (R-L3c-6).

using System.Globalization;
using System.Text.Json;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>What flattening produced: the objects to append, the instances that take the flattened one's place, and
/// why not.</summary>
public sealed record C3dFlattenResult(IReadOnlyList<C3dObject> Objects, IReadOnlyList<C3dInstance> Instances,
                                      string? Refusal, IReadOnlyList<string> Notes)
{
    public static C3dFlattenResult Refuse(string why) => new([], [], why, []);
}

/// <summary>A New 3D View from Layout: the document, and the ports that could not be carried across (with why).</summary>
public sealed record C3dFromLayout(C3dDocument? Document, string? Refusal, IReadOnlyList<string> SetupsCopied,
                                   IReadOnlyList<string> PortsLeftOut);

public static class C3dHierarchy
{
    /// <summary>The prefix of a new instance's name: <c>U1</c>, <c>U2</c> …</summary>
    public const string InstancePrefix = "U";

    // ── names (R-em3d48-1c) ──────────────────────────────────────────────────────────────────

    /// <summary>Why <paramref name="name"/> cannot name an instance of <paramref name="doc"/> (other than
    /// <paramref name="self"/>), or null. An instance's contents are named <c>U1/…</c> and an array element's
    /// <c>U1[i,j,k]/…</c>, so neither separator may appear in it; <c>airbox</c> is reserved.</summary>
    public static string? ValidateInstanceName(C3dDocument doc, string name, C3dInstance? self = null)
    {
        name = name.Trim();
        if (name.Length == 0) return "A name cannot be empty.";
        if (string.Equals(name, C3dValidation.ReservedName, StringComparison.OrdinalIgnoreCase))
            return "'airbox' is reserved: the air box's faces are named after it.";
        if (name.IndexOfAny(['/', '[', ']']) >= 0)
            return "A name cannot hold '/', '[' or ']': an instance's contents are named '<instance>/<object>' and an array element's '<instance>[i,j,k]'.";
        if (doc.Objects.Any(o => o.Name == name) || doc.Instances.Any(i => !ReferenceEquals(i, self) && i.Name == name))
            return $"'{name}' is already the name of something in this 3D view.";
        return null;
    }

    /// <summary>The smallest <c>U&lt;n&gt;</c> nothing in <paramref name="doc"/> is called.</summary>
    public static string NextInstanceName(C3dDocument doc)
    {
        var used = new HashSet<string>(doc.Objects.Select(o => o.Name).Concat(doc.Instances.Select(i => i.Name)), StringComparer.Ordinal);
        for (int n = 1; ; n++)
            if (!used.Contains(InstancePrefix + n)) return InstancePrefix + n;
    }

    // ── views and cycles (R-em3d48-6a) ───────────────────────────────────────────────────────

    /// <summary>The cell folder a <c>.c3d</c> belongs to: the folder above its <c>3d/</c>, or null for a loose file.</summary>
    public static string? CellDirOf(string c3dPath)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(c3dPath));
        return dir is not null && string.Equals(Path.GetFileName(dir), CellFolder.SubFolderName(ViewType.ThreeD), StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dir) : null;
    }

    private static ViewType TypeOf(C3dInstanceView v) => v == C3dInstanceView.Layout ? ViewType.Layout : ViewType.ThreeD;

    /// <summary>The primary file of <paramref name="view"/> in <paramref name="cellDir"/>, or null.</summary>
    public static string? ViewFile(string cellDir, C3dInstanceView view)
    {
        var type = TypeOf(view);
        var primary = CellFolder.ResolvePrimary(cellDir, type);
        return primary.ResolvedName is { } f ? Path.Combine(CellFolder.SubFolderPath(cellDir, type), f) : null;
    }

    /// <summary>Why <paramref name="view"/> of <paramref name="cellDir"/> cannot be placed, or null: the view is missing.</summary>
    public static string? MissingView(string cellDir, C3dInstanceView view)
        => ViewFile(cellDir, view) is null
            ? $"Cell '{Path.GetFileName(cellDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}' has no {CellFolder.ViewNoun(TypeOf(view))}."
            : null;

    /// <summary>
    /// R-em3d48-6a — why placing <paramref name="view"/> of <paramref name="cellDir"/> in the document at
    /// <paramref name="parentPath"/> would make the 3D view contain itself, with the path; null when it would not.
    /// A layout never contains a 3D view, so placing one never cycles.
    /// </summary>
    public static string? CycleRefusal(string parentPath, string cellDir, C3dInstanceView view)
    {
        if (view == C3dInstanceView.Layout) return null;
        string parentFile = Norm(parentPath);
        string? parentCell = CellDirOf(parentPath) is { } pc ? Norm(pc) : null;
        string childCell = Norm(cellDir);
        string Name(string dir) => Path.GetFileName(dir);
        string parentName = parentCell is { } p ? Name(p) : Path.GetFileNameWithoutExtension(parentPath);
        if (parentCell is not null && Same(childCell, parentCell))
            return $"The 3D view of '{parentName}' cannot hold its own 3D view ({parentName} → {parentName}): a cell cannot contain itself. " +
                   "Its layout can be placed here — the views differ.";

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { childCell };
        var path = new List<string> { parentName, Name(childCell) };
        return Walk(childCell);

        string? Walk(string cell)
        {
            if (ViewFile(cell, C3dInstanceView.ThreeD) is not { } file) return null;
            if (Same(Norm(file), parentFile)) return Refusal();
            C3dDocument doc;
            try { doc = C3dPersistence.LoadFromFile(file); }
            catch { return null; }
            string baseDir = Path.GetDirectoryName(file)!;
            foreach (var inst in doc.Instances.Where(i => i.View == C3dInstanceView.ThreeD))
            {
                if (ExternalCellRef.ResolveCellDir(inst.CellRef, baseDir) is not { } d) continue;
                string next = Norm(d);
                if ((parentCell is not null && Same(next, parentCell)) ||
                    (ViewFile(next, C3dInstanceView.ThreeD) is { } nf && Same(Norm(nf), parentFile)))
                {
                    path.Add(parentName);
                    return Refusal();
                }
                if (!visited.Add(next)) continue;
                path.Add(Name(next));
                if (Walk(next) is { } found) return found;
                path.RemoveAt(path.Count - 1);
            }
            return null;
        }

        string Refusal() => $"Placing the 3D view of '{Name(childCell)}' here would make this 3D view contain itself: " +
                            $"{string.Join(" → ", path)}. A cell cannot contain itself.";
    }

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ── swap view (R-em3d48-2) ───────────────────────────────────────────────────────────────

    /// <summary>The other view of an instance's cell.</summary>
    public static C3dInstanceView Other(C3dInstanceView v) => v == C3dInstanceView.Layout ? C3dInstanceView.ThreeD : C3dInstanceView.Layout;

    /// <summary>Why <paramref name="inst"/> of the document at <paramref name="docPath"/> cannot swap to its other view, or
    /// null: its cell does not resolve, the other view is missing, or the swap would make a cycle.</summary>
    public static string? SwapRefusal(string docPath, C3dInstance inst)
    {
        var to = Other(inst.View);
        if (ExternalCellRef.ResolveCellDir(inst.CellRef, Path.GetDirectoryName(Path.GetFullPath(docPath))) is not { } cellDir || !Directory.Exists(cellDir))
            return $"'{inst.Name}' places '{inst.CellRef}', which resolves to nothing.";
        return MissingView(cellDir, to) is { } why ? why + " Swap View needs both." : CycleRefusal(docPath, cellDir, to);
    }

    /// <summary>
    /// Every name the document's ports and face boundaries give to something inside <paramref name="instance"/>
    /// (<c>U1/…</c>, <c>U1[i,j,k]/…</c>) — read from the records as they are stored, whatever their later schema,
    /// because any string value naming an instance's object is exactly what a swap can strand.
    /// </summary>
    public static IReadOnlyList<string> NamesInside(C3dDocument doc, string instance)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        void Visit(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Visit(p.Value); break;
                case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Visit(x); break;
                case JsonValueKind.String:
                    string s = e.GetString() ?? "";
                    if (s.StartsWith(instance + "/", StringComparison.Ordinal) || s.StartsWith(instance + "[", StringComparison.Ordinal)) found.Add(s);
                    break;
            }
        }
        foreach (var p in doc.Ports) Visit(JsonSerializer.SerializeToElement(p));
        foreach (var b in doc.FaceBoundaries) Visit(JsonSerializer.SerializeToElement(b));
        return [.. found];
    }

    /// <summary>Which of <paramref name="names"/> name no object of <paramref name="elaboration"/>. A name may carry a
    /// face after the object (<c>U1/pad/zmax</c>); it resolves when some prefix of it is an object.</summary>
    public static IReadOnlyList<string> Unresolved(IEnumerable<string> names, C3dElaboration elaboration)
    {
        var missing = new List<string>();
        foreach (string n in names)
        {
            bool ok = false;
            for (int cut = n.Length; cut > 0 && !ok; cut = n.LastIndexOf('/', cut - 1))
                ok = elaboration.Provenance.ContainsKey(n[..cut]);
            if (!ok) missing.Add(n);
        }
        return missing;
    }

    // ── flatten (R-em3d48-5) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// One level of <paramref name="doc"/>'s instance at <paramref name="index"/>. A 3D child's objects and instances
    /// come up with their placements composed; a layout child becomes the solids <paramref name="elaboration"/> (this
    /// document's own, current) holds for it. <paramref name="tech"/> is this document's technology, which every
    /// flattened material must mean the same in; <paramref name="tempC"/> the temperature they are compared at.
    /// </summary>
    public static C3dFlattenResult FlattenOne(C3dDocument doc, string docPath, int index, C3dElaboration elaboration,
                                              Technology? tech, TechnologyCache? cache = null, string? workspaceCws = null,
                                              double tempC = EmSetup.DefaultOperatingTempC)
    {
        var inst = doc.Instances[index];
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(docPath))!;
        if (ExternalCellRef.ResolveCellDir(inst.CellRef, baseDir) is not { } cellDir || !Directory.Exists(cellDir))
            return C3dFlattenResult.Refuse($"'{inst.Name}' places '{inst.CellRef}', which resolves to nothing: there is nothing to flatten.");
        if (ViewFile(cellDir, inst.View) is not { } file)
            return C3dFlattenResult.Refuse(MissingView(cellDir, inst.View)!);
        var used = new HashSet<string>(doc.Objects.Select(o => o.Name).Concat(doc.Instances.Where(i => i != inst).Select(i => i.Name)),
                                       StringComparer.Ordinal);
        var flat = inst.View == C3dInstanceView.ThreeD
            ? FlattenThreeD(doc, inst, file, baseDir, used, tech, cache, workspaceCws, tempC)
            : FlattenLayout(doc, inst, elaboration, used, tech);
        // 3D editor groups — what the instance held lands in the instance's group; the child's own groups were the child's.
        foreach (var o in flat.Objects) o.Group = inst.Group;
        foreach (var i in flat.Instances) i.Group = inst.Group;
        // brief-em3d-92 — and what the instance's transparency multiplied onto each part is written onto the part itself, so
        // flattening does not change how anything is drawn (C3dTransparency.Compose; a part at its default takes the instance's).
        if (inst.Transparency is not null)
        {
            foreach (var o in flat.Objects.Where(o => o is not C3dPolyline)) o.Transparency = C3dTransparency.Compose(inst.Transparency, o.Transparency);
            foreach (var i in flat.Instances) i.Transparency = C3dTransparency.Compose(inst.Transparency, i.Transparency);
        }
        // brief-em3d-93 — an instance that is not modelled flattens to parts that are not, so the solve is unchanged.
        if (!inst.Model)
        {
            foreach (var o in flat.Objects.Where(o => o is not C3dPolyline)) o.Model = false;
            foreach (var i in flat.Instances) i.Model = false;
        }
        return flat;
    }

    /// <summary>Each element of an instance: its suffix for names (empty for a plain instance) and its placement in the
    /// parent — the instance's own, moved by the pitch in the parent's frame (the elaborator's rule).</summary>
    public static IEnumerable<(string Suffix, C3dPlacement Placement)> Elements(C3dInstance inst)
    {
        var counts = inst.Array?.Counts ?? [1, 1, 1];
        bool array = inst.Array is not null && counts.Count == 3 && counts.Any(n => n > 1);
        if (!array) { yield return ("", inst.Placement.Clone()); yield break; }
        var pitch = inst.Array!.Pitch;
        for (int k = 0; k < counts[2]; k++)
            for (int j = 0; j < counts[1]; j++)
                for (int i = 0; i < counts[0]; i++)
                    yield return ($"_{i}_{j}_{k}", inst.Placement.Translated(new C3dPoint3(i * pitch.X, j * pitch.Y, k * pitch.Z)));
    }

    private static C3dFlattenResult FlattenThreeD(C3dDocument doc, C3dInstance inst, string file, string baseDir, HashSet<string> used,
                                                  Technology? tech, TechnologyCache? cache, string? workspaceCws, double tempC)
    {
        C3dDocument child;
        try { child = C3dPersistence.LoadFromFile(file); }
        catch (Exception e) { return C3dFlattenResult.Refuse($"'{file}' cannot be read: {e.Message}"); }
        if (child.DbuPerMicron != doc.DbuPerMicron && doc.DbuPerMicron % child.DbuPerMicron != 0)
            return C3dFlattenResult.Refuse(
                $"'{inst.Name}' is drawn at {child.DbuPerMicron} DBU per µm and this 3D view at {doc.DbuPerMicron}: its coordinates do not " +
                "land on this view's grid, so flattening would move them. Keep the instance.");
        long scale = doc.DbuPerMicron / child.DbuPerMicron;

        // The child's materials resolve in ITS technology; each must mean the same here.
        var (childTech, _) = TechnologyResolver.ResolveForDocument(child.TechRef, file, workspaceCws, cache ?? new TechnologyCache());
        var materials = child.Objects.Select(o => o.Material).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (MaterialMismatch(materials.Select(m => (m, C3dProblemAssembly.ObjectMaterial(childTech.Tech, m) is { } tm ? C3dElaborator.MaterialValues(tm, tempC) : null)),
                             tech, tempC, inst.Name) is { } why)
            return C3dFlattenResult.Refuse(why);

        var objects = new List<C3dObject>();
        var instances = new List<C3dInstance>();
        var notes = new List<string>();
        string childDir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        foreach (var (suffix, placement) in Elements(inst))
        {
            var outer = placement.ToTransform();
            foreach (var o in child.Objects)
            {
                var copy = C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(o));
                if (scale != 1) Scale(copy, scale);
                copy.Placement = copy.Placement.Then(outer, out bool exact);
                // brief-em3d-50 — a wire has no placement: the instance's lands on its points.
                if (!exact || !C3dWires.BakePlacement(copy)) return C3dFlattenResult.Refuse(OffGrid(inst, o.Name));
                copy.Name = Unique($"{inst.Name}{suffix}_{o.Name}", used);
                objects.Add(copy);
            }
            foreach (var ci in child.Instances)
            {
                var copy = C3dPersistence.DeserializeInstance(C3dPersistence.SerializeInstance(ci));
                if (!ExternalCellRef.IsExternalRef(ci.CellRef) && ExternalCellRef.ResolveCellDir(ci.CellRef, childDir) is { } abs)
                    copy.CellRef = ExternalCellRef.MakeCellRef(baseDir, abs);
                var scaled = copy.Placement.Clone();
                if (scale != 1) scaled.Origin = Mul(scaled.Origin, scale);
                copy.Placement = scaled.Then(outer, out bool exact);
                if (!exact) return C3dFlattenResult.Refuse(OffGrid(inst, ci.Name));
                if (copy.Array is { } arr)
                {
                    var p = Mul(arr.Pitch, scale);
                    var (x, y, z) = (outer with { Tx = 0, Ty = 0, Tz = 0 }).Apply(p);
                    if (!Whole(x) || !Whole(y) || !Whole(z)) return C3dFlattenResult.Refuse(OffGrid(inst, ci.Name));
                    copy.Array = new C3dArray { Counts = [.. arr.Counts], Pitch = new C3dPoint3((long)x, (long)y, (long)z) };
                    if (!outer.IsIntegral || outer.M00 != 1 || outer.M11 != 1 || outer.M22 != 1)
                        notes.Add($"'{copy.Name}' is an array under a rotation or mirror: its pitch was turned with it.");
                }
                copy.Name = Unique($"{inst.Name}{suffix}_{ci.Name}", used);
                instances.Add(copy);
            }
        }
        if (child.Ports.Count > 0 || child.FaceBoundaries.Count > 0 || child.Setups.Count > 0)
            notes.Add("The child's ports, face boundaries and setups are not carried up: only the parent says where a signal enters (overview §1k).");
        return new C3dFlattenResult(objects, instances, null, notes);
    }

    private static string OffGrid(C3dInstance inst, string what)
        => $"Flattening '{inst.Name}' would put '{what}' off this view's DBU grid (the instance is turned by an angle that is not a " +
           "quarter turn): its geometry would move. Keep the instance.";

    private static C3dFlattenResult FlattenLayout(C3dDocument doc, C3dInstance inst, C3dElaboration e, HashSet<string> used, Technology? tech)
    {
        bool Mine(string path) => path == inst.Name || path.StartsWith(inst.Name + "[", StringComparison.Ordinal) ||
                                  path.StartsWith(inst.Name + "/", StringComparison.Ordinal);
        var solids = e.Solids.Where(s => e.Provenance.TryGetValue(s.Name, out var p) && Mine(p.InstancePath)).ToList();
        var sheets = e.Sheets.Where(s => e.Provenance.TryGetValue(s.Name, out var p) && Mine(p.InstancePath)).ToList();
        if (solids.Count + sheets.Count == 0)
            return C3dFlattenResult.Refuse($"'{inst.Name}' elaborates to nothing here ({string.Join(" ", e.Refusals.Where(r => r.Contains(inst.Name, StringComparison.Ordinal)))}).".Replace(" ().", "."));

        // A layout's solids are made of its STACKUP's entries (named for the entry, valued from it); an object here names a
        // material of this view's technology. Each entry becomes the technology's material of the same name, else the one
        // with equal values — never one that merely shares a name.
        var byName = e.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var rename = new Dictionary<string, string>(StringComparer.Ordinal);
        var unmatched = new List<string>();
        foreach (string n in solids.Select(s => s.Material).Concat(sheets.Select(s => s.Material)).Distinct(StringComparer.Ordinal))
        {
            var values = byName.GetValueOrDefault(n);
            string? match = C3dProblemAssembly.ObjectMaterial(tech, n) is { } same && (values is null || SameValues(C3dElaborator.MaterialValues(same, EmSetup.DefaultOperatingTempC), values))
                ? same.Name
                : values is null ? null
                : tech?.ResolvedMaterials.FirstOrDefault(m => SameValues(C3dElaborator.MaterialValues(m, EmSetup.DefaultOperatingTempC), values))?.Name;
            if (match is null) unmatched.Add(values is null ? $"'{n}'" : $"'{n}' ({Describe(values)})");
            else rename[n] = match;
        }
        if (unmatched.Count > 0)
            return C3dFlattenResult.Refuse(
                $"Flattening '{inst.Name}' would give its objects materials this view's technology does not have: {string.Join(", ", unmatched)} — " +
                "neither by name nor with equal values. Add them to the technology, or keep the instance: a silent swap would change what the solver sees.");

        double per = C3dLowering.Metres(1, doc.DbuPerMicron);
        var ordered = solids.Select(s => (s.Order, (object)s)).Concat(sheets.Select(s => (s.Order, (object)s))).OrderBy(t => t.Order);
        var objects = new List<C3dObject>();
        int rounded = 0, tessellated = 0;
        foreach (var (_, item) in ordered)
        {
            C3dObject? made = item switch
            {
                Em3dSolid s => FromSolid(s, per, ref rounded, ref tessellated),
                Em3dSheet sh => FromSheet(sh, per, ref rounded),
                _ => null,
            };
            if (made is null) return C3dFlattenResult.Refuse($"'{NameOf(item)}' of '{inst.Name}' has a shape a 3D view's objects cannot hold.");
            made.Name = Unique(Flat(NameOf(item)), used);
            made.Material = rename[made.Material!];
            objects.Add(made);
        }
        var notes = new List<string>();
        if (tessellated > 0) notes.Add($"{tessellated} curved solid(s) (bond wires, balls) became polyhedra: their facets are the viewer's tessellation.");
        if (rounded > 0) notes.Add($"{rounded} coordinate(s) were not whole DBU and were rounded to the nearest.");
        return new C3dFlattenResult(objects, [], null, notes);

        static string NameOf(object o) => o is Em3dSolid s ? s.Name : ((Em3dSheet)o).Name;
        static string Flat(string n) => n.Replace("/", "_").Replace("[", "_").Replace("]", "").Replace(",", "_");
    }

    private static bool SameValues(Em3dMaterial a, Em3dMaterial b)
        => a.Epsr == b.Epsr && a.TanD == b.TanD && a.Mur == b.Mur && Math.Abs(a.SigmaSm - b.SigmaSm) <= 1e-9 * Math.Abs(b.SigmaSm)
           && (a.EpsrTensor ?? []).SequenceEqual(b.EpsrTensor ?? []);

    private static string Describe(Em3dMaterial m) => m.SigmaSm > 0
        ? string.Create(CultureInfo.InvariantCulture, $"σ {m.SigmaSm:G6} S/m")
        : string.Create(CultureInfo.InvariantCulture, $"εr {m.Epsr:G6}, tanδ {m.TanD:G6}");

    /// <summary>Why the flattened materials would not mean the same in <paramref name="tech"/>, or null.
    /// <paramref name="values"/>: each name with the values it has where it came from (null: unknown there).</summary>
    private static string? MaterialMismatch(IEnumerable<(string Name, Em3dMaterial? Values)> values, Technology? tech, double? tempC, string inst)
    {
        var missing = new List<string>();
        var differ = new List<string>();
        foreach (var (name, v) in values)
        {
            if (C3dProblemAssembly.ObjectMaterial(tech, name) is not { } here) { missing.Add(name); continue; }
            if (v is null) continue;
            var mine = C3dElaborator.MaterialValues(here, tempC ?? EmSetup.DefaultOperatingTempC);
            if (!(mine.Epsr == v.Epsr && mine.TanD == v.TanD && mine.Mur == v.Mur && Math.Abs(mine.SigmaSm - v.SigmaSm) <= 1e-9 * Math.Abs(v.SigmaSm)
                  && (mine.EpsrTensor ?? []).SequenceEqual(v.EpsrTensor ?? [])))
                differ.Add(name);
        }
        if (missing.Count == 0 && differ.Count == 0) return null;
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add($"this view's technology does not define {string.Join(", ", missing.Select(m => $"'{m}'"))}");
        if (differ.Count > 0) parts.Add($"it defines {string.Join(", ", differ.Select(m => $"'{m}'"))} with other values");
        return $"Flattening '{inst}' would give its objects materials that do not mean the same here: {string.Join("; ", parts)}. " +
               "Add or align them in the technology, or keep the instance — a silent swap would change their conductivity.";
    }

    private static C3dObject? FromSolid(Em3dSolid s, double per, ref int rounded, ref int tessellated)
    {
        int r = 0;
        long D(double m)
        {
            double d = m / per, q = Math.Round(d, MidpointRounding.AwayFromZero);
            if (Math.Abs(d - q) > 1e-6) r++;
            return (long)q;
        }
        C3dPoint3 P(Point3 p) => new(D(p.X), D(p.Y), D(p.Z));
        C3dObject? made;
        switch (s.Primitive)
        {
            case Em3dBox b:
                var lo = P(b.Min);
                made = new C3dBox { Min = lo, Size = P(b.Max) - lo };
                break;
            case Em3dExtrudedPolygon x:
                made = new C3dPrism
                {
                    Plane = C3dPlane.XY, Offset = D(x.ZBottom), Height = D(x.ZTop) - D(x.ZBottom),
                    Outline = [.. x.Outline.Select(q => new C3dPoint2(D(q.X), D(q.Y)))],
                    Holes = [.. x.Holes.Select(h => h.Select(q => new C3dPoint2(D(q.X), D(q.Y))).ToList())],
                };
                break;
            case Em3dCylinder c when AxisOf(c) is { } axis:
                var a = P(c.AxisStart);
                var z = P(c.AxisEnd);
                long len = DrawingGet(z, axis) - DrawingGet(a, axis);
                made = new C3dCylinder { Base = len >= 0 ? a : z, Axis = axis, Length = Math.Abs(len), Radius = D(c.Radius) };
                break;
            case Em3dPolyhedron ph:
                made = new C3dPolyhedron
                {
                    Vertices = [.. ph.Vertices.Select(P)],
                    Faces = [.. ph.Faces.Select((f, i) => new C3dFace
                    {
                        Name = f.Name is { Length: > 0 } n ? n : $"face{i}",
                        Outer = [.. f.Outer], Holes = [.. f.Holes.Select(h => h.ToList())],
                    })],
                };
                break;
            default:
                // A wire, a ball, a slanted cylinder: the viewer's own tessellation, as triangles.
                var mesh = Em3dTessellation.Of(s);
                made = new C3dPolyhedron
                {
                    Vertices = [.. mesh.Vertices.Select(P)],
                    Faces = [.. mesh.Triangles.Select((t, i) => new C3dFace { Name = $"t{i}", Outer = [t.A, t.B, t.C] })],
                };
                tessellated++;
                r = 0;                           // a tessellation's vertices are never on the grid; that is what the note says
                break;
        }
        rounded += r;
        made.Material = s.Material;
        made.Role = s.Role;
        return made;

        static C3dAxis? AxisOf(Em3dCylinder c)
        {
            double dx = c.AxisEnd.X - c.AxisStart.X, dy = c.AxisEnd.Y - c.AxisStart.Y, dz = c.AxisEnd.Z - c.AxisStart.Z;
            return dy == 0 && dz == 0 ? C3dAxis.X : dx == 0 && dz == 0 ? C3dAxis.Y : dx == 0 && dy == 0 ? C3dAxis.Z : null;
        }
        static long DrawingGet(C3dPoint3 p, C3dAxis axis) => axis switch { C3dAxis.X => p.X, C3dAxis.Y => p.Y, _ => p.Z };
    }

    private static C3dSheet? FromSheet(Em3dSheet sh, double per, ref int rounded)
    {
        int r = 0;
        long D(double m)
        {
            double d = m / per, q = Math.Round(d, MidpointRounding.AwayFromZero);
            if (Math.Abs(d - q) > 1e-6) r++;
            return (long)q;
        }
        C3dPlane plane;
        double offset;
        Func<Point2, C3dPoint2> uv;
        if (sh.Frame is not { } f)
        {
            plane = C3dPlane.XY; offset = sh.Z;
            uv = q => new C3dPoint2(D(q.X), D(q.Y));
        }
        else
        {
            // A frame whose u and v are world axes is one of the drawing planes; its origin's in-plane part shifts u and v.
            var world = new[] { f.World(0, 0, sh.Z), f.World(1, 0, sh.Z), f.World(0, 1, sh.Z) };
            var du = new Point3(world[1].X - world[0].X, world[1].Y - world[0].Y, world[1].Z - world[0].Z);
            var dv = new Point3(world[2].X - world[0].X, world[2].Y - world[0].Y, world[2].Z - world[0].Z);
            if (Unit(du, 0) && Unit(dv, 1)) { plane = C3dPlane.XY; offset = world[0].Z; uv = q => new C3dPoint2(D(world[0].X + q.X), D(world[0].Y + q.Y)); }
            else if (Unit(du, 1) && Unit(dv, 2)) { plane = C3dPlane.YZ; offset = world[0].X; uv = q => new C3dPoint2(D(world[0].Y + q.X), D(world[0].Z + q.Y)); }
            else if (Unit(du, 0) && Unit(dv, 2)) { plane = C3dPlane.XZ; offset = world[0].Y; uv = q => new C3dPoint2(D(world[0].X + q.X), D(world[0].Z + q.Y)); }
            else return null;
        }
        var made = new C3dSheet
        {
            Plane = plane, Offset = D(offset),
            Outline = [.. sh.Outline.Select(uv)],
            Holes = [.. sh.Holes.Select(h => h.Select(uv).ToList())],
            ThicknessUm = sh.ThicknessM > 0 ? sh.ThicknessM * 1e6 : null,
            Material = sh.Material,
        };
        rounded += r;
        return made;

        static bool Unit(Point3 d, int axis) => axis switch
        {
            0 => Math.Abs(d.X - 1) < 1e-12 && Math.Abs(d.Y) < 1e-12 && Math.Abs(d.Z) < 1e-12,
            1 => Math.Abs(d.Y - 1) < 1e-12 && Math.Abs(d.X) < 1e-12 && Math.Abs(d.Z) < 1e-12,
            _ => Math.Abs(d.Z - 1) < 1e-12 && Math.Abs(d.X) < 1e-12 && Math.Abs(d.Y) < 1e-12,
        };
    }

    /// <summary>A name not in <paramref name="used"/> (the name itself, else with a number), added to it.</summary>
    private static string Unique(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        for (int n = 2; ; n++)
            if (used.Add(name + "_" + n)) return name + "_" + n;
    }

    private static C3dPoint3 Mul(C3dPoint3 p, long k) => new(p.X * k, p.Y * k, p.Z * k);
    private static C3dPoint2 Mul(C3dPoint2 p, long k) => new(p.U * k, p.V * k);
    private static bool Whole(double v) => Math.Abs(v - Math.Round(v)) <= 1e-6;

    /// <summary>An object's own-frame geometry multiplied by <paramref name="k"/> (a finer DBU); its origin too.</summary>
    private static void Scale(C3dObject o, long k)
    {
        o.Placement.Origin = Mul(o.Placement.Origin, k);
        switch (o)
        {
            case C3dBox b: b.Min = Mul(b.Min, k); b.Size = Mul(b.Size, k); break;
            case C3dPrism p:
                p.Offset *= k; p.Height *= k; p.Shear = Mul(p.Shear, k);
                p.Outline = [.. p.Outline.Select(q => Mul(q, k))];
                p.Holes = [.. p.Holes.Select(h => h.Select(q => Mul(q, k)).ToList())];
                break;
            case C3dCylinder c: c.Base = Mul(c.Base, k); c.Length *= k; c.Radius *= k; break;
            case C3dSheet s:
                s.Offset *= k;
                if (s.Rect is { } r) s.Rect = new C3dRect { Min = Mul(r.Min, k), Size = Mul(r.Size, k) };
                s.Outline = [.. s.Outline.Select(q => Mul(q, k))];
                s.Holes = [.. s.Holes.Select(h => h.Select(q => Mul(q, k)).ToList())];
                break;
            case C3dPolyline l:
                l.Offset *= k;
                l.Points = [.. l.Points.Select(q => Mul(q, k))];
                if (l.Points3 is { } p3) l.Points3 = [.. p3.Select(q => Mul(q, k))];
                break;
            case C3dPolyhedron ph: ph.Vertices = [.. ph.Vertices.Select(q => Mul(q, k))]; break;
            case C3dWire w:
                w.Points = [.. w.Points.Select(q => Mul(q, k))];
                if (w.Array is { } wa) wa.Pitch = Mul(wa.Pitch, k);
                break;
        }
    }

    /// <summary>
    /// R-em3d48-5 / R-L3c-4 — every level of <paramref name="doc"/> flattened, on a COPY: one level at a time until no
    /// instance is left. Returns the flat copy (or why not) so the caller can state the count before anything changes.
    /// <paramref name="elaborate"/> elaborates a working copy (the editor's elaborator, whose child cache makes it cheap).
    /// </summary>
    public static (C3dDocument? Flat, string? Refusal, IReadOnlyList<string> Notes) FlattenAll(
        C3dDocument doc, string docPath, Func<C3dDocument, C3dElaboration> elaborate, Technology? tech,
        TechnologyCache? cache = null, string? workspaceCws = null, int maxLevels = 64)
    {
        var work = C3dPersistence.Deserialize(C3dPersistence.Serialize(doc));
        var notes = new List<string>();
        for (int level = 0; work.Instances.Count > 0; level++)
        {
            if (level == maxLevels) return (null, $"The hierarchy is deeper than {maxLevels} levels.", notes);
            var e = elaborate(work);
            var next = new List<C3dInstance>();
            var added = new List<C3dObject>();
            for (int i = 0; i < work.Instances.Count; i++)
            {
                var r = FlattenOne(work, docPath, i, e, tech, cache, workspaceCws);
                if (r.Refusal is { } why) return (null, why, notes);
                added.AddRange(r.Objects);
                next.AddRange(r.Instances);
                foreach (string n in r.Notes) if (!notes.Contains(n)) notes.Add(n);
                // Later instances' new names must not collide with what this one just added.
                work.Objects.AddRange(r.Objects);
            }
            work.Instances = next;
        }
        return (work, null, notes);
    }

    // ── group into cell (R-em3d48-5) ─────────────────────────────────────────────────────────

    /// <summary>
    /// Moves the objects at <paramref name="objectIndices"/> and the instances at <paramref name="instanceIndices"/> of
    /// <paramref name="doc"/> into a new cell <paramref name="cellName"/> under <paramref name="parentDir"/> — its
    /// <c>3d/</c> holding them, every placement moved by −<paramref name="anchor"/> — and returns the instance that
    /// replaces them (at the anchor, so nothing moves) with the new files, or why not. <paramref name="doc"/> itself is
    /// not changed: the caller makes that one undo entry.
    /// </summary>
    public static (C3dInstance? Instance, string? CellDir, string? C3dPath, string? Refusal) GroupIntoCell(
        C3dDocument doc, string docPath, IReadOnlyList<int> objectIndices, IReadOnlyList<int> instanceIndices,
        string cellName, string parentDir, C3dPoint3 anchor)
    {
        if (objectIndices.Count + instanceIndices.Count == 0) return (null, null, null, "Select what to group first.");
        if (NameValidator.Validate(cellName) is { } bad) return (null, null, null, $"'{cellName}' cannot name a cell: {bad}");
        string cellDir = Path.Combine(parentDir, cellName);
        if (Directory.Exists(cellDir) || File.Exists(cellDir)) return (null, null, null, $"'{cellDir}' already exists.");

        string baseDir = Path.GetDirectoryName(Path.GetFullPath(docPath))!;
        string newPath = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.ThreeD), cellName + C3dPersistence.Extension);
        string newDir = Path.GetDirectoryName(newPath)!;
        var minus = new C3dPoint3(-anchor.X, -anchor.Y, -anchor.Z);
        // 3D editor groups — the new instance takes the group everything grouped shared, and the child keeps only the
        // groups below it: moving a group's contents into a cell leaves the cell where the group was.
        var paths = objectIndices.Select(i => doc.Objects[i].Group).Concat(instanceIndices.Select(i => doc.Instances[i].Group)).ToList();
        var shared = C3dGroups.Segments(paths[0]).ToList();
        foreach (var p in paths.Skip(1))
        {
            var segs = C3dGroups.Segments(p);
            int k = 0;
            while (k < shared.Count && k < segs.Length && shared[k] == segs[k]) k++;
            shared.RemoveRange(k, shared.Count - k);
        }
        string? Below(string? path) => C3dGroups.Join(C3dGroups.Segments(path).Skip(shared.Count));
        var child = new C3dDocument
        {
            DbuPerMicron = doc.DbuPerMicron,
            DisplayUnit = doc.DisplayUnit,
            SnapDbu = doc.SnapDbu,
            TechRef = doc.TechRef is { Length: > 0 } tr && !Path.IsPathRooted(tr)
                ? Path.GetRelativePath(newDir, Path.GetFullPath(Path.Combine(baseDir, tr))).Replace('\\', '/')
                : doc.TechRef,
        };
        foreach (int i in objectIndices.Order())
        {
            var o = C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(doc.Objects[i]));
            o.Placement = o.Placement.Translated(minus);
            o.Group = Below(o.Group);
            C3dWires.BakePlacement(o);
            child.Objects.Add(o);
        }
        foreach (int i in instanceIndices.Order())
        {
            var inst = C3dPersistence.DeserializeInstance(C3dPersistence.SerializeInstance(doc.Instances[i]));
            if (!ExternalCellRef.IsExternalRef(inst.CellRef) && ExternalCellRef.ResolveCellDir(inst.CellRef, baseDir) is { } abs)
                inst.CellRef = Path.GetRelativePath(newDir, abs).Replace('\\', '/');
            inst.Placement = inst.Placement.Translated(minus);
            inst.Group = Below(inst.Group);
            child.Instances.Add(inst);
        }
        try
        {
            CellFolder.CreateCellFolder(parentDir, cellName);
            CellCreate.WriteThreeDView(cellDir, cellName, child);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null, null, $"The cell could not be created: {e.Message}");
        }
        var placed = new C3dInstance
        {
            Name = NextInstanceName(doc),
            CellRef = ExternalCellRef.MakeCellRef(baseDir, cellDir),
            Group = C3dGroups.Join(shared),
            Placement = new C3dPlacement { Origin = anchor },
        };
        return (placed, cellDir, newPath, null);
    }

    // ── New 3D View from Layout (R-em3d48-7) ─────────────────────────────────────────────────

    /// <summary>
    /// A cell's first <c>.c3d</c> from its own layout: one instance <c>U1</c> of it at the origin, the layout's display
    /// unit, DBU and snap, the layout's technology as <c>TechRef</c> (relative to <paramref name="c3dPath"/>), and a
    /// copy of every 3D <c>.cem</c> naming the layout as an embedded setup — its ports translated to parent-level ports
    /// where each is one axis-aligned rectangle, else left out and listed. Writes nothing.
    /// </summary>
    public static C3dFromLayout NewFromLayout(string cellDir, string c3dPath, string? workspaceCws, TechnologyCache? cache = null)
    {
        cache ??= new TechnologyCache();
        if (ViewFile(cellDir, C3dInstanceView.Layout) is not { } clay)
            return new(null, MissingView(cellDir, C3dInstanceView.Layout), [], []);
        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(clay); }
        catch (Exception e) { return new(null, $"'{clay}' cannot be read: {e.Message}", [], []); }

        string c3dDir = Path.GetDirectoryName(Path.GetFullPath(c3dPath))!;
        var (tech, _) = TechnologyResolver.ResolveForDocument(view.TechRef, clay, workspaceCws, cache);
        var doc = new C3dDocument
        {
            DbuPerMicron = view.DbuPerMicron,
            DisplayUnit = view.DisplayUnit,
            SnapDbu = view.SnapDbu,
            TechRef = tech.ResolvedPath is { } tp ? Path.GetRelativePath(c3dDir, tp).Replace('\\', '/') : null,
            Instances = [new C3dInstance { Name = InstancePrefix + "1", CellRef = ExternalCellRef.MakeCellRef(c3dDir, cellDir), View = C3dInstanceView.Layout }],
        };

        // Every .cem naming this layout: the workspace's own finder when there is a workspace, else the cell's em/ folder.
        IEnumerable<string> cems = workspaceCws is { } cws && File.Exists(cws)
            ? EmSetupResolver.FindSetupsForLayout(cws, clay)
            : Directory.Exists(Path.Combine(cellDir, "em"))
                ? Directory.EnumerateFiles(Path.Combine(cellDir, "em"), "*" + EmSetupPersistence.Extension)
                    .Where(c => { try { var s = EmSetupPersistence.LoadFromFile(c); return EmSetupResolver.ResolveLayoutPath(c, s.LayoutRef, null) is { } p && Same(Norm(p), Norm(clay)); } catch { return false; } })
                : [];
        var copied = new List<string>();
        var leftOut = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ports = new Dictionary<int, C3dPort>();
        foreach (string cem in cems.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            EmSetup setup;
            try { setup = EmSetupPersistence.LoadFromFile(cem); }
            catch { continue; }
            if (!setup.Is3D) continue;
            if (setup.Name.Length == 0) setup.Name = Path.GetFileNameWithoutExtension(cem);
            string baseName = setup.Name;
            for (int n = 2; !names.Add(setup.Name); n++) setup.Name = $"{baseName} {n}";
            doc.Setups.Add(EmSetupPersistence.ToEmbedded(setup));
            copied.Add(setup.Name);
            TranslatePorts(setup, cem, workspaceCws, cache, view, ports, leftOut);
        }
        doc.Ports = [.. ports.OrderBy(p => p.Key).Select(p => p.Value)];
        return new C3dFromLayout(doc, null, copied, leftOut);
    }

    /// <summary>The setup's generated ports as brief 49's parent-level records (one per number, the first setup's wins),
    /// in the new view's frame: the layout sits at the origin with its stack's bottom at z = 0.</summary>
    private static void TranslatePorts(EmSetup setup, string cem, string? workspaceCws, TechnologyCache cache, LayoutView view,
                                       Dictionary<int, C3dPort> ports, List<string> leftOut)
    {
        // brief-em3d-49 — a layout's ports are its port labels, whatever kind the setup states for them: Ports3D only
        // names a wave port, so gating on it left every all-lumped setup's ports behind.
        var resolved = EmSetupResolver.Resolve(cem, setup.LayoutRef, workspaceCws, cache);
        if (resolved.Source is not { Technology: { } tech } source) return;
        var g = Em3dGenerator.Generate(setup, source, tech);
        if (g.Problem is not { } problem)
        {
            leftOut.Add($"{setup.Name}: its ports ({g.Refusal})");
            return;
        }
        var solids = Em3dLayoutSolids.From(source, tech, null, new Em3dLayoutSolidsOptions(null, setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC, Instance: true));
        double dz = -solids.StackBottomM;
        double per = C3dLowering.Metres(1, view.DbuPerMicron);
        long D(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);
        foreach (var p in problem.Ports)
        {
            if (ports.ContainsKey(p.Number)) continue;
            var (a, b) = (p.Min, p.Max);
            int flat = a.X == b.X ? 0 : a.Y == b.Y ? 1 : a.Z == b.Z ? 2 : -1;
            if (p.Annulus is not null || flat < 0)
            {
                leftOut.Add($"{setup.Name}: port {p.Number} ({p.Name}) — " + (p.Annulus is not null ? "a coaxial annulus is not one rectangle" : "it is not flat"));
                continue;
            }
            var (plane, offset, u0, v0, u1, v1) = flat switch
            {
                0 => (C3dPlane.YZ, a.X, a.Y, a.Z + dz, b.Y, b.Z + dz),
                1 => (C3dPlane.XZ, a.Y, a.X, a.Z + dz, b.X, b.Z + dz),
                _ => (C3dPlane.XY, a.Z + dz, a.X, a.Y, b.X, b.Y),
            };
            ports[p.Number] = new C3dPort
            {
                Number = p.Number,
                Name = $"P{p.Number}",
                Kind = p.Kind,
                Plane = plane,
                Offset = D(offset),
                Rect = new C3dRect { Min = new C3dPoint2(D(u0), D(v0)), Size = new C3dPoint2(D(u1) - D(u0), D(v1) - D(v0)) },
                Z0 = C3dPorts.FormatZ0(p.Z0),
            };
        }
    }
}
