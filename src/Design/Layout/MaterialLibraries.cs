using System.Globalization;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout;

/// <summary>
/// Where a technology's library references land and how each is read — one loader per place a
/// technology can come from (brief-em3d-53 §1c): a file on disk resolves its references against the
/// <c>.ctech</c>'s own directory; a shipped technology against the resources beside it.
/// </summary>
/// <param name="Locate">A stored reference → the library's identity (an absolute path, or a shipped
/// library's <c>shipped:</c> name).</param>
/// <param name="Read">That identity → its records. Throws for a library that cannot be read.</param>
/// <param name="ReadInterfaces">brief-em3d-73 — that identity → its thermal interfaces (none for null, or for a loader
/// that does not say).</param>
public sealed record MaterialLibraryLoader(
    Func<string, string> Locate,
    Func<string, IReadOnlyList<TechMaterial>> Read,
    Func<string, IReadOnlyList<TechThermalInterface>?>? ReadInterfaces = null);

/// <summary>One thing wrong with a technology's libraries — its <c>check</c> rule id and sentence.</summary>
public sealed record MaterialLibraryProblem(string Id, string Message);

/// <summary>
/// A technology whose libraries cannot be resolved (brief-em3d-53 §1d): one that cannot be read, or a
/// name two sources define differently. Thrown by the loader so the technology <b>fails to resolve</b>
/// — never an empty list, never a shadowed value — and turned by <see cref="TechnologyResolver"/> into
/// the same non-fatal diagnostic a corrupt <c>.ctech</c> gets.
/// </summary>
public sealed class MaterialLibraryException(string ctechLabel, IReadOnlyList<MaterialLibraryProblem> problems)
    : IOException(string.Join(" ", problems.Select(p => p.Message)))
{
    /// <summary>The technology whose libraries failed.</summary>
    public string Technology { get; } = ctechLabel;

    /// <summary>Every problem, not just the first — a user fixing a conflict wants the whole list.</summary>
    public IReadOnlyList<MaterialLibraryProblem> Problems { get; } = problems;
}

/// <summary>
/// brief-em3d-53 — material libraries: the loaders, the one resolution rule (§3), the check ids, and
/// the shipped generic library (§8a).
/// </summary>
public static class MaterialLibraries
{
    // ── check rule ids ────────────────────────────────────────────────────────
    public const string ConflictId          = "material.conflict";
    public const string LibraryMissingId    = "material.library-unreadable";
    public const string LibraryAbsoluteId   = "material.library-absolute";
    public const string ReservedCharacterId = "material.reserved-character";
    /// <summary>brief-em3d-73 R-em3d73-3a — a material pair two sources give different interface resistances.</summary>
    public const string InterfaceConflictId = "material.interface-conflict";

    /// <summary>The shipped library's file name — the same in the assembly and in a workspace's
    /// <c>tech/</c> folder, so a shipped technology's reference to it is true in both places.</summary>
    public const string GenericFileName = "generic-materials.cmat";

    /// <summary>How a shipped library is named in a message and in <see cref="LibraryMaterial.SourcePath"/>.</summary>
    public const string ShippedPrefix = "shipped:";

    private const string ShippedResourcePrefix = "CircuitRF.Design.resources.technologies.";

    // ── Loaders ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The loader for a <c>.ctech</c> on disk. <paramref name="live"/> answers a library that is open
    /// in an editor with unsaved changes (the cache's live override, R-em3d53-1f), or null to read the
    /// file.
    /// </summary>
    public static MaterialLibraryLoader Disk(string ctechPath, Func<string, IReadOnlyList<TechMaterial>?>? live = null)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(ctechPath))!;
        return new MaterialLibraryLoader(
            reference => Core.RefPath.Resolve(dir, reference),
            path =>
            {
                if (live?.Invoke(path) is { } open) return open;
                if (Directory.Exists(path)) throw new IOException("it is a directory, not a .cmat file.");
                if (!File.Exists(path)) throw new FileNotFoundException("the file does not exist.");
                return MaterialLibraryPersistence.LoadFromFile(path);
            },
            path => File.Exists(path) ? MaterialLibraryPersistence.LoadInterfacesFromFile(path) : null);
    }

    /// <summary>The loader for a technology shipped inside the assembly: a reference resolves against
    /// the resources beside it, exactly as the same reference resolves beside the workspace copy.</summary>
    public static MaterialLibraryLoader Shipped { get; } = new(
        reference => ShippedPrefix + Path.GetFileName(Core.RefPath.ToNative(reference)),
        name => MaterialLibraryPersistence.Deserialize(ShippedRawJson(name[ShippedPrefix.Length..])),
        name => MaterialLibraryPersistence.DeserializeInterfaces(ShippedRawJson(name[ShippedPrefix.Length..])));

    // ── The shipped generic library (R-em3d53-8) ──────────────────────────────

    /// <summary>The shipped generic library's bytes, exactly as authored — what New Workspace and
    /// <i>Add Generic Materials</i> copy.</summary>
    public static string GenericRawJson() => ShippedRawJson(GenericFileName);

    /// <summary>A shipped library's authored bytes, by file name. Throws naming it when none ships.</summary>
    public static string ShippedRawJson(string fileName)
    {
        using var stream = typeof(MaterialLibraries).Assembly.GetManifestResourceStream(ShippedResourcePrefix + fileName)
            ?? throw new FileNotFoundException($"circuitRF ships no material library named '{fileName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The shipped generic library's records.</summary>
    public static IReadOnlyList<TechMaterial> LoadGeneric() => MaterialLibraryPersistence.Deserialize(GenericRawJson());

    /// <summary>
    /// <i>Add Generic Materials</i>' file half (R-em3d53-8d): copies the shipped library beside
    /// <paramref name="ctechPath"/> and returns the reference to add. An identical file already there is
    /// reused; a <b>different</b> file of that name is a refusal naming it (throws
    /// <see cref="IOException"/>) — it is the user's file and is never overwritten.
    /// </summary>
    public static string CopyGenericBeside(string ctechPath)
    {
        string target = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ctechPath))!, GenericFileName);
        string json = GenericRawJson();
        if (File.Exists(target))
        {
            if (File.ReadAllText(target) != json)
                throw new IOException(
                    $"'{target}' already exists and is not circuitRF's generic library. Rename or move it, or add it " +
                    "with Add Library… if it is the library you want.");
        }
        else File.WriteAllText(target, json);
        return GenericFileName;
    }

    // ── References ────────────────────────────────────────────────────────────

    /// <summary>The stored reference from the technology at <paramref name="ctechPath"/> to
    /// <paramref name="libraryPath"/>: relative to the <c>.ctech</c>'s directory, <c>/</c>-separated.</summary>
    public static string RelativeReference(string ctechPath, string libraryPath)
        => Core.RefPath.ToStored(Path.GetRelativePath(
            Path.GetDirectoryName(Path.GetFullPath(ctechPath))!, Path.GetFullPath(libraryPath)));

    /// <summary>The absolute path a technology's reference lands on.</summary>
    public static string ResolvePath(string ctechPath, string reference)
        => Core.RefPath.Resolve(Path.GetDirectoryName(Path.GetFullPath(ctechPath))!, reference);

    /// <summary>
    /// The <c>.ctech</c> files under <paramref name="root"/> that name <paramref name="libraryPath"/> — the
    /// walk R-em3d53-1e adds (a library's document header, <c>explain</c> on a <c>.cmat</c>, <c>find</c>).
    /// Reads each file's own references only, so a technology that refuses to load is still counted;
    /// an unreadable file is skipped. Sorted by path.
    /// </summary>
    public static IReadOnlyList<string> TechnologiesNaming(string libraryPath, string root)
    {
        string lib = Path.GetFullPath(libraryPath);
        var list = new List<string>();
        foreach (string ctech in TechnologyFiles(root))
            if (ReferencesOf(ctech).Any(p => string.Equals(p, lib, StringComparison.OrdinalIgnoreCase)))
                list.Add(ctech);
        return list;
    }

    /// <summary>The absolute paths a <c>.ctech</c>'s own library references land on — read from the
    /// file as written, with nothing resolved. Empty for a file that cannot be read.</summary>
    public static IReadOnlyList<string> ReferencesOf(string ctechPath)
    {
        try
        {
            var raw = TechPersistence.DeserializeUnresolved(GzipTextFile.ReadAllTextAutoGzip(ctechPath));
            return [.. (raw.MaterialLibraries ?? []).Select(r => ResolvePath(ctechPath, r))];
        }
        catch { return []; }
    }

    /// <summary>Every <c>.ctech</c> under <paramref name="root"/>, sorted — a symlinked directory is not
    /// followed, and an unreadable one is skipped.</summary>
    public static IReadOnlyList<string> TechnologyFiles(string root)
    {
        var list = new List<string>();
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            list.AddRange(Directory.EnumerateFiles(root, "*.ctech", options).Select(Path.GetFullPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    // ── Resolution (§2, §3) ───────────────────────────────────────────────────

    /// <summary>
    /// Fills <see cref="Technology.LibraryMaterials"/> and <see cref="Technology.ResolvedLibraryPaths"/>
    /// from the libraries <paramref name="tech"/> names. Throws <see cref="MaterialLibraryException"/>
    /// listing every library that cannot be read and every name two sources define differently; on a
    /// throw the technology is left with no library materials.
    /// </summary>
    /// <param name="ctechLabel">How the technology is named in a sentence — its path.</param>
    public static void Resolve(Technology tech, MaterialLibraryLoader loader, string ctechLabel)
    {
        tech.LibraryMaterials = [];
        tech.ResolvedLibraryPaths = [];
        tech.LibraryThermalInterfaces = [];
        if (tech.MaterialLibraries is not { Count: > 0 } refs) return;

        var problems = new List<MaterialLibraryProblem>();
        var sources = new List<(string Source, IReadOnlyList<TechMaterial> Materials)>();
        var interfaceSources = new List<(string Source, IReadOnlyList<TechThermalInterface> Interfaces)>();
        var paths = new List<string>();
        foreach (string reference in refs)
        {
            string path;
            try { path = loader.Locate(reference); }
            catch (Exception ex)
            {
                problems.Add(new(LibraryMissingId,
                    $"Material library '{reference}', named by technology '{ctechLabel}', cannot be located: {ex.Message}"));
                continue;
            }
            if (paths.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            paths.Add(path);
            try
            {
                sources.Add((path, loader.Read(path)));
                if (loader.ReadInterfaces?.Invoke(path) is { Count: > 0 } found) interfaceSources.Add((path, found));
            }
            catch (Exception ex)
            {
                problems.Add(new(LibraryMissingId,
                    $"Material library '{Display(path)}', named by technology '{ctechLabel}', cannot be read: {ex.Message}"));
            }
        }

        problems.AddRange(Conflicts(ctechLabel, tech.Materials, sources));
        problems.AddRange(InterfaceConflicts(ctechLabel, tech.ThermalInterfaces ?? [], interfaceSources));
        if (problems.Count > 0) throw new MaterialLibraryException(ctechLabel, problems);

        var list = new List<LibraryMaterial>();
        foreach (var (source, materials) in sources)
            foreach (var m in materials) list.Add(new LibraryMaterial(m, source));
        tech.LibraryMaterials = list;
        tech.ResolvedLibraryPaths = paths;
        tech.LibraryThermalInterfaces = [.. interfaceSources.SelectMany(s => s.Interfaces.Select(i => new LibraryThermalInterface(i, s.Source)))];
    }

    /// <summary>
    /// brief-em3d-73 R-em3d73-3a — a material pair two sources of ONE technology give different resistances: the
    /// materials' own rule (§3), applied to the unordered pair. Equal values merge. A pair stated twice inside one file is
    /// <see cref="MaterialValidation.ValidateInterfaces"/>' business, so only each source's first record of a pair takes part.
    /// </summary>
    public static IEnumerable<MaterialLibraryProblem> InterfaceConflicts(
        string ctechLabel, IReadOnlyList<TechThermalInterface> own,
        IReadOnlyList<(string Source, IReadOnlyList<TechThermalInterface> Interfaces)> libraries)
    {
        var all = new List<(string Source, TechThermalInterface I)>();
        foreach (var i in own.GroupBy(i => i.PairKey).Select(g => g.First())) all.Add(($"technology '{ctechLabel}'", i));
        foreach (var (source, list) in libraries)
            foreach (var i in list.GroupBy(i => i.PairKey).Select(g => g.First())) all.Add(($"library '{Display(source)}'", i));

        foreach (var group in all.GroupBy(x => x.I.PairKey, StringComparer.Ordinal))
        {
            var entries = group.ToList();
            if (entries.Count < 2 || entries.All(e => e.I.ResistanceM2KW == entries[0].I.ResistanceM2KW)) continue;
            var first = entries[0].I;
            yield return new MaterialLibraryProblem(InterfaceConflictId,
                $"The thermal interface between '{first.MaterialA}' and '{first.MaterialB}' is given different resistances by " +
                string.Join(" and by ", entries.Select(e => $"{e.Source} ({e.I.ResistanceM2KW.ToString("G6", CultureInfo.InvariantCulture)} m²·K/W)")) +
                ". A technology must have one answer for a pair: make the values equal, or remove one.");
        }
    }

    /// <summary>
    /// §3 — a name two sources of ONE technology define with different values. A name defined twice
    /// inside one file is <c>tech.material.duplicate</c>'s business, not this rule's, so only each
    /// source's first record of a name takes part.
    /// </summary>
    public static IEnumerable<MaterialLibraryProblem> Conflicts(
        string ctechLabel, IReadOnlyList<TechMaterial> own,
        IReadOnlyList<(string Source, IReadOnlyList<TechMaterial> Materials)> libraries)
    {
        var all = new List<(string Source, TechMaterial M)>();
        foreach (var m in FirstOfEachName(own)) all.Add(($"technology '{ctechLabel}'", m));
        foreach (var (source, materials) in libraries)
            foreach (var m in FirstOfEachName(materials)) all.Add(($"library '{Display(source)}'", m));

        foreach (var group in all.GroupBy(x => x.M.Name ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var entries = group.ToList();
            if (entries.Count < 2) continue;
            var first = entries[0];
            if (entries.Skip(1).All(e => SameValues(first.M, e.M))) continue;
            yield return new MaterialLibraryProblem(ConflictId,
                $"Material '{group.Key}' is defined differently by " +
                string.Join(" and by ", entries.Select(e => $"{e.Source} ({Describe(e.M)})")) +
                ". A technology must have one answer for a name: make the values equal, or rename one.");
        }

        static IEnumerable<TechMaterial> FirstOfEachName(IReadOnlyList<TechMaterial> list)
            => list.GroupBy(m => m.Name ?? "", StringComparer.OrdinalIgnoreCase).Select(g => g.First());
    }

    /// <summary>
    /// Whether two records are the same material to a solver — the comparison the 3D elaborator makes
    /// between technologies (overview §1j): εr, the εr tensor, tanδ, μr and σ, each as the solver
    /// receives it (an unstated εr is 1, tanδ 0, μr 1, σ 0), plus α₂₀, which decides σ at every other
    /// temperature. Display fields — <see cref="TechMaterial.Source"/>, <see cref="TechMaterial.Color"/> and
    /// <see cref="TechMaterial.Appearance"/> (brief-em3d-105) — take no part.
    ///
    /// <para>brief-em3d-73 — the thermal values (k, the two tables, the k tensor, density, specific heat) take part <b>only when both
    /// records state them</b>: a technology's own copy of a metal written before the library gained its thermal values
    /// says nothing about them, and that silence must not turn every such workspace into a refusal to load. Two records
    /// that each state a different k are two answers, and refuse.</para>
    /// </summary>
    public static bool SameValues(TechMaterial a, TechMaterial b)
        => ElectricalSame(a, b)
        && BothOrNone(a.ThermalK, b.ThermalK) && BothOrNone(a.DensityKgM3, b.DensityKgM3) && BothOrNone(a.SpecificHeat, b.SpecificHeat)
        && TablesAgree(a.SigmaVsTemp, b.SigmaVsTemp) && TablesAgree(a.ThermalKVsTemp, b.ThermalKVsTemp)
        && (a.ThermalKTensor is null || b.ThermalKTensor is null || a.ThermalKTensor.SequenceEqual(b.ThermalKTensor));

    private static bool BothOrNone(double? x, double? y) => x is null || y is null || x == y;

    private static bool TablesAgree(List<TechTemperaturePoint>? x, List<TechTemperaturePoint>? y)
        => x is not { Count: > 0 } || y is not { Count: > 0 }
        || x.Count == y.Count && x.Zip(y).All(p => p.First.TempC == p.Second.TempC && p.First.Value == p.Second.Value);

    private static bool ElectricalSame(TechMaterial a, TechMaterial b)
        => (a.Epsr ?? 1) == (b.Epsr ?? 1)
        && (a.TanD ?? 0) == (b.TanD ?? 0)
        && (a.Mur ?? 1) == (b.Mur ?? 1)
        && (a.Sigma20 ?? 0) == (b.Sigma20 ?? 0)
        && (a.Sigma20 is null || a.Alpha20 == b.Alpha20)
        && (a.EpsrTensor is { Length: 3 } ? a.EpsrTensor : []).SequenceEqual(b.EpsrTensor is { Length: 3 } ? b.EpsrTensor : []);

    /// <summary>A record's electrical values, as a conflict sentence states them.</summary>
    public static string Describe(TechMaterial m)
    {
        var parts = new List<string>();
        if (m.Epsr is { } e) parts.Add($"εr {Num(e)}");
        if (m.EpsrTensor is { Length: 3 } t) parts.Add($"εr tensor {Num(t[0])}/{Num(t[1])}/{Num(t[2])}");
        if (m.TanD is { } d) parts.Add($"tanδ {Num(d)}");
        if (m.Mur is { } u) parts.Add($"μr {Num(u)}");
        if (m.Sigma20 is { } s) parts.Add($"σ₂₀ {Num(s)} S/m");
        if (m.Alpha20 is { } a) parts.Add($"α₂₀ {Num(a)} 1/K");
        if (m.ThermalK is { } k) parts.Add($"k {Num(k)} W/(m·K)");
        if (m.ThermalKVsTemp is { Count: > 0 } kt) parts.Add($"a {kt.Count}-point k(T) table");
        if (m.ThermalKTensor is { Length: 3 } ktt) parts.Add($"k tensor {Num(ktt[0])}/{Num(ktt[1])}/{Num(ktt[2])} W/(m·K)");
        if (m.SigmaVsTemp is { Count: > 0 } st) parts.Add($"a {st.Count}-point σ(T) table");
        return parts.Count == 0 ? "states nothing" : string.Join(", ", parts);

        static string Num(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    }

    /// <summary>A library identity as a sentence names it: a path, or the shipped library's name.</summary>
    public static string Display(string sourcePath)
        => sourcePath.StartsWith(ShippedPrefix, StringComparison.Ordinal)
            ? $"{sourcePath[ShippedPrefix.Length..]} (shipped with circuitRF)"
            : sourcePath;
}
