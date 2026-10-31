// Finds gdsfactory kits in a Python environment WITHOUT running any of them (brief-gdf-2 R-gdf2-1).
//
// A gdsfactory kit is an ordinary installed Python package whose distribution requires gdsfactory.
// Listing kits reads files only: each `*.dist-info/METADATA` in the environment's site-packages. The
// one process started is the interpreter itself, asked for its version and where its site-packages
// are — running the interpreter, not kit code (overview D2). Nothing here names a kit: the marker is
// gdsfactory's own distribution name, the same kind of format marker `KitPCellLibrary.ApiModule` is.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Layout.PCells.Wire;

/// <summary>One gdsfactory kit found in an environment, or as a source tree on disk.</summary>
/// <param name="Distribution">The distribution's name as its METADATA states it (a source tree: its
/// module name).</param>
/// <param name="Version">The installed version; empty for a source tree that states none.</param>
/// <param name="RequiresPython">The METADATA <c>Requires-Python</c> specifier, or null.</param>
/// <param name="Module">The importable top-level module, e.g. what <c>register_pdk</c> is given.</param>
/// <param name="InstalledDirectory">The module's own directory — what consent, the cache key and repair
/// anchor on.</param>
/// <param name="PythonPathRoot">The directory that must be on <c>PYTHONPATH</c> for
/// <paramref name="Module"/> to import. Null for an installed kit, which the environment already
/// finds.</param>
public sealed record GdsfactoryKit(
    string Distribution,
    string Version,
    string? RequiresPython,
    string Module,
    string InstalledDirectory,
    string? PythonPathRoot = null)
{
    public bool IsSourceTree => PythonPathRoot is not null;

    /// <summary>"name version" — how the kit is named in a list or a prompt.</summary>
    public string Display => string.IsNullOrEmpty(Version) ? Distribution : $"{Distribution} {Version}";
}

/// <summary>What one interpreter said about itself: its version and its package directories.</summary>
public sealed record PythonEnvironment(string Interpreter, int Major, int Minor, int Micro, IReadOnlyList<string> SitePackages)
{
    public string Version => $"{Major}.{Minor}.{Micro}";
}

/// <summary>A kit found but not usable with this interpreter, and why.</summary>
public sealed record GdsfactoryKitRefusal(GdsfactoryKit Kit, string Reason);

/// <summary>Everything one discovery found. <see cref="Refusal"/> is the environment-level answer
/// when nothing can be offered at all; per-kit refusals are in <see cref="Refused"/>.</summary>
public sealed record GdsfactoryDiscovery(
    PythonEnvironment? Environment,
    string? GdsfactoryVersion,
    IReadOnlyList<GdsfactoryKit> Kits,
    IReadOnlyList<GdsfactoryKitRefusal> Refused,
    string? Refusal);

/// <summary>
/// Lists gdsfactory kits by reading files. Framework-free (no Avalonia / Skia).
/// </summary>
public static class GdsfactoryKitDiscovery
{
    /// <summary>The distribution every kit requires, and the marker this keys on.</summary>
    public const string GdsfactoryDistribution = "gdsfactory";

    /// <summary>gdsfactory kits need Python 3.12 or newer (overview D5). The kit's own
    /// <c>Requires-Python</c> may ask for more, and is checked as well.</summary>
    public const int MinimumMajor = 3, MinimumMinor = 12;

    /// <summary>
    /// The probe. Imports nothing beyond <c>sys</c>, <c>sysconfig</c> and <c>os</c>, and runs with
    /// <c>-I -S</c>: <c>-I</c> so no environment variable and no current directory can put kit code on
    /// its path, and <c>-S</c> because <c>-I</c> alone still runs <c>site</c>, which executes every
    /// <c>.pth</c> file in site-packages — an installed distribution's own code, before anyone has
    /// allowed it to run (overview D2).
    ///
    /// <para>The directories are printed in the order the interpreter would search them, and only the
    /// ones it WOULD search: a virtual environment's own, then the user scheme, then the base
    /// interpreter's. A virtual environment leaves the last two off unless its <c>pyvenv.cfg</c>
    /// includes the system site-packages, and listing the user scheme anyway "finds" a kit, or a
    /// gdsfactory, the environment cannot import.</para>
    ///
    /// <para><b>The virtual environment is found here, not read off <c>sys.prefix</c>.</b> Before
    /// Python 3.14 it is <c>site</c> that points <c>sys.prefix</c> at the environment, so under
    /// <c>-S</c> a 3.12 or 3.13 environment reports its BASE interpreter's site-packages. The rule is
    /// <c>site</c>'s own: a <c>pyvenv.cfg</c> beside the executable or one level up, and the
    /// environment is the executable's grandparent.</para>
    /// </summary>
    private const string ProbeScript =
        "import os, sys, sysconfig\n" +
        "print('%d.%d.%d' % sys.version_info[:3])\n" +
        "exe_dir = os.path.dirname(os.path.abspath(sys.executable))\n" +
        "prefix = os.path.dirname(exe_dir)\n" +
        "cfg = next((c for c in (os.path.join(exe_dir, 'pyvenv.cfg'), os.path.join(prefix, 'pyvenv.cfg')) if os.path.isfile(c)), None)\n" +
        "system_site = cfg is None\n" +
        "if cfg:\n" +
        "    try:\n" +
        "        with open(cfg, encoding='utf-8') as f:\n" +
        "            for line in f:\n" +
        "                key, _, value = line.partition('=')\n" +
        "                if key.strip().lower() == 'include-system-site-packages':\n" +
        "                    system_site = value.strip().lower() == 'true'\n" +
        "    except OSError:\n" +
        "        pass\n" +
        "if cfg:\n" +
        "    v = sysconfig.get_paths(vars={'base': prefix, 'platbase': prefix})\n" +
        "    print(v.get('purelib', ''))\n" +
        "    print(v.get('platlib', ''))\n" +
        "if system_site:\n" +
        "    try:\n" +
        "        print(sysconfig.get_path('purelib', sysconfig.get_preferred_scheme('user')))\n" +
        "    except Exception:\n" +
        "        pass\n" +
        "if not cfg:\n" +
        "    p = sysconfig.get_paths()\n" +
        "    print(p.get('purelib', ''))\n" +
        "    print(p.get('platlib', ''))\n" +
        "if cfg and system_site:\n" +
        "    b = sysconfig.get_paths(vars={'base': sys.base_prefix, 'platbase': sys.base_exec_prefix})\n" +
        "    print(b.get('purelib', ''))\n" +
        "    print(b.get('platlib', ''))\n";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Runs <see cref="ProbeScript"/> under <paramref name="interpreter"/> and returns its stdout, or
    /// null when it would not run, failed, or ran past <see cref="ProbeTimeout"/>. The default for every
    /// <c>runProbe</c> seam below.
    /// </summary>
    public static string? RunProbe(string interpreter)
    {
        var parsed = PythonInterpreter.ParseRecord(interpreter);
        if (parsed is null) return null;

        var info = new ProcessStartInfo(parsed.Value.Command);
        foreach (string a in parsed.Value.Arguments) info.ArgumentList.Add(a);
        info.ArgumentList.Add("-I");
        info.ArgumentList.Add("-S");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(ProbeScript);

        try
        {
            return BoundedProcess.Run(info, ProbeTimeout) is { TimedOut: false, ExitCode: 0 } run ? run.Stdout : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The interpreter's version and package directories, or null with a sentence.</summary>
    /// <param name="runProbe">How the probe is run — the counting seam. Null runs it for real.</param>
    public static PythonEnvironment? Probe(string interpreter, out string? refusal, Func<string, string?>? runProbe = null)
    {
        refusal = null;
        string? output = (runProbe ?? RunProbe)(interpreter);
        var lines = (output ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var parts = lines.Length > 0 ? lines[0].Split('.') : [];
        if (parts.Length < 3 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor)
            || !int.TryParse(parts[2], out int micro))
        {
            refusal = $"'{interpreter}' did not answer as a Python interpreter.";
            return null;
        }

        var sites = lines.Skip(1)
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToList();
        return new PythonEnvironment(interpreter, major, minor, micro, sites);
    }

    /// <summary>
    /// Every gdsfactory kit installed for <paramref name="interpreter"/> — the probe, then files only.
    /// </summary>
    public static GdsfactoryDiscovery Discover(string interpreter, Func<string, string?>? runProbe = null)
    {
        var env = Probe(interpreter, out string? notPython, runProbe);
        if (env is null) return new GdsfactoryDiscovery(null, null, [], [], notPython);

        if (!AtLeast(env.Major, env.Minor, MinimumMajor, MinimumMinor))
            return new GdsfactoryDiscovery(env, null, [], [],
                $"'{interpreter}' is Python {env.Version}; gdsfactory kits need Python {MinimumMajor}.{MinimumMinor} " +
                "or newer. circuitRF installs nothing: choose an environment that has a newer Python.");

        var notListed = new List<GdsfactoryKitRefusal>();
        var found = ScanSitePackages(env.SitePackages, notListed);
        string? gfVersion = GdsfactoryVersionIn(env.SitePackages);

        if (found.Count == 0 && notListed.Count == 0)
            return new GdsfactoryDiscovery(env, gfVersion, [], [],
                $"The Python environment at '{interpreter}' holds no gdsfactory kit: no installed distribution " +
                $"requires gdsfactory (looked in {string.Join(", ", env.SitePackages.Select(s => $"'{s}'"))}).");

        if (gfVersion is null)
            return new GdsfactoryDiscovery(env, null, [],
                [.. found.Select(k => new GdsfactoryKitRefusal(k, "gdsfactory is not installed")), .. notListed],
                MissingGdsfactorySentence(interpreter, (found.FirstOrDefault() ?? notListed[0].Kit).Display));

        var kits = new List<GdsfactoryKit>();
        var refused = new List<GdsfactoryKitRefusal>();
        foreach (var kit in found)
        {
            if (Satisfies(kit.RequiresPython, env.Major, env.Minor, env.Micro)) kits.Add(kit);
            else refused.Add(new GdsfactoryKitRefusal(kit,
                $"{kit.Display} needs Python {kit.RequiresPython}; '{interpreter}' is Python {env.Version}."));
        }
        refused.AddRange(notListed);

        return new GdsfactoryDiscovery(env, gfVersion, kits, refused,
            kits.Count == 0 ? string.Join(" ", refused.Select(r => r.Reason)) : null);
    }

    /// <summary>The §1c sentence for a kit whose gdsfactory never followed it into the environment.
    /// The Python side states the same thing in the same shape (<c>gdsfactory_bridge.GDSFACTORY_MISSING</c>).</summary>
    public static string MissingGdsfactorySentence(string interpreter, string? kit)
        => $"gdsfactory is not installed in the Python environment at '{interpreter}'" +
           (kit is null ? "" : $", though {kit} requires it") +
           ". A kit installed with --no-deps leaves its dependencies behind: install gdsfactory into that " +
           "environment. circuitRF installs nothing.";

    // ── files ────────────────────────────────────────────────────────────────

    /// <summary>Every distribution in <paramref name="sitePackages"/> whose METADATA requires gdsfactory.</summary>
    /// <param name="notListed">
    /// Receives each distribution that requires gdsfactory but installs no package directory a kit can be
    /// run from — most often an EDITABLE install, whose code stays in its source tree — with the reason.
    /// Saying so is the point: skipping it in silence told the user the environment held no kit at all.
    /// </param>
    public static IReadOnlyList<GdsfactoryKit> ScanSitePackages(
        IEnumerable<string> sitePackages, List<GdsfactoryKitRefusal>? notListed = null)
    {
        var kits = new List<GdsfactoryKit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string site in sitePackages)
        {
            IEnumerable<string> infos;
            try { infos = Directory.EnumerateDirectories(site, "*.dist-info").Order(StringComparer.Ordinal).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (string info in infos)
            {
                var meta = ReadMetadata(Path.Combine(info, "METADATA"));
                if (meta is null || !RequiresGdsfactory(meta.RequiresDist)) continue;
                // The first site that INSTALLS the distribution answers for it, exactly as the import
                // system would; a site holding only an unusable record of it does not hide a later one.
                if (seen.Contains(NormalizeName(meta.Name))) continue;

                if (TopLevelModule(info, site, out var named) is not { } module)
                {
                    if (notListed is not null && !notListed.Any(r => NormalizeName(r.Kit.Distribution) == NormalizeName(meta.Name)))
                        notListed.Add(NotInstalledHere(meta, info, named));
                    continue;
                }
                seen.Add(NormalizeName(meta.Name));
                notListed?.RemoveAll(r => NormalizeName(r.Kit.Distribution) == NormalizeName(meta.Name));
                kits.Add(new GdsfactoryKit(meta.Name, meta.Version, meta.RequiresPython, module,
                                           Path.Combine(site, module)));
            }
        }

        return kits;
    }

    /// <summary>
    /// The refusal for a distribution that requires gdsfactory but has no package directory in
    /// site-packages. An editable install (PEP 610's <c>direct_url.json</c> says so) names where its code
    /// really is, so the sentence can say which folder to import instead.
    /// </summary>
    private static GdsfactoryKitRefusal NotInstalledHere(Metadata meta, string distInfo, string? named)
    {
        string? source = EditableSource(distInfo);
        string module = named ?? NormalizeName(meta.Name).Replace('-', '_');
        var kit = new GdsfactoryKit(meta.Name, meta.Version, meta.RequiresPython, module, source ?? "");
        string reason = source is not null
            ? $"{kit.Display} is an editable install: its code is in '{source}', not in this environment's " +
              "site-packages, so it cannot be offered from here. Import that folder instead (File ▸ Import PDK ▸ " +
              "Choose folder…, or circuitrf import pdk <folder>)."
            : $"{kit.Display} installs no package directory circuitRF can run it from (a single-file module or a " +
              "namespace package), so it cannot be offered from here. Import its source tree as a folder instead " +
              "(File ▸ Import PDK ▸ Choose folder…, or circuitrf import pdk <folder>).";
        return new GdsfactoryKitRefusal(kit, reason);
    }

    /// <summary>The local directory an editable install's <c>direct_url.json</c> points at, or null.</summary>
    private static string? EditableSource(string distInfo)
    {
        string path = Path.Combine(distInfo, "direct_url.json");
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("dir_info", out var dirInfo) || dirInfo.ValueKind != System.Text.Json.JsonValueKind.Object
                || !dirInfo.TryGetProperty("editable", out var editable) || editable.ValueKind != System.Text.Json.JsonValueKind.True
                || !root.TryGetProperty("url", out var url) || url.GetString() is not { } u
                || !Uri.TryCreate(u, UriKind.Absolute, out var uri) || !uri.IsFile)
                return null;
            return uri.LocalPath;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                    or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Where <paramref name="module"/> (dotted) is installed among <paramref name="sitePackages"/>, searched
    /// in their order — the package directory the interpreter would import, by files alone — or null.
    /// </summary>
    public static string? PackageDirectoryIn(IEnumerable<string> sitePackages, string module)
    {
        string relative = module.Replace('.', Path.DirectorySeparatorChar);
        return sitePackages.Select(s => Path.Combine(s, relative))
                           .FirstOrDefault(d => File.Exists(Path.Combine(d, "__init__.py")));
    }

    /// <summary>gdsfactory's own installed version, read from its METADATA, or null when it is absent.</summary>
    public static string? GdsfactoryVersionIn(IEnumerable<string> sitePackages)
    {
        foreach (string site in sitePackages)
        {
            IEnumerable<string> infos;
            try { infos = Directory.EnumerateDirectories(site, "*.dist-info").Order(StringComparer.Ordinal).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (string info in infos)
                if (ReadMetadata(Path.Combine(info, "METADATA")) is { } meta
                    && NormalizeName(meta.Name) == GdsfactoryDistribution)
                    return meta.Version;
        }
        return null;
    }

    /// <summary>
    /// The version installed beside <paramref name="kitDirectory"/> now — the dist-info in the same
    /// site-packages whose top-level module is that directory. Null for a source tree, or when nothing
    /// says. What the per-open version NOTE compares against (R-gdf2-2d).
    /// </summary>
    public static string? InstalledVersion(string kitDirectory) => InstalledMetadata(kitDirectory)?.Version;

    /// <summary>The installed kit's <c>Requires-Python</c>, read the same way; null when it states none.</summary>
    public static string? InstalledRequiresPython(string kitDirectory) => InstalledMetadata(kitDirectory)?.RequiresPython;

    private static Metadata? InstalledMetadata(string kitDirectory)
    {
        string? site = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(kitDirectory));
        string module = Path.GetFileName(Path.TrimEndingDirectorySeparator(kitDirectory));
        if (site is null || !Directory.Exists(site)) return null;

        try
        {
            foreach (string info in Directory.EnumerateDirectories(site, "*.dist-info").Order(StringComparer.Ordinal))
                if (TopLevelModule(info, site) is { } top && string.Equals(top, module, StringComparison.Ordinal)
                    && ReadMetadata(Path.Combine(info, "METADATA")) is { } meta)
                    return meta;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// A gdsfactory kit SOURCE TREE inside <paramref name="root"/> (R-gdf2-1b): a package whose
    /// <c>__init__.py</c> imports gdsfactory and constructs <c>Pdk(</c>. Only the head of each file is
    /// read — the <c>KitPCellLibrary</c> peek rule — and nothing is run. The shallowest wins, ties by
    /// path, so the answer is the same on every machine.
    /// </summary>
    public static GdsfactoryKit? FindSourceTree(string root, int maxDepth = KitPCellLibrary.DefaultMaxDepth)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((Path.GetFullPath(root), 0));
        int visited = 0;
        while (queue.Count > 0 && visited++ < 4000)
        {
            var (dir, depth) = queue.Dequeue();
            if (IsPdkPackage(dir)) return SourceTreeKit(dir);
            if (depth >= maxDepth) continue;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (string sub in subs.Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(sub);
                if (name.Length == 0 || name[0] == '.' || SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                queue.Enqueue((sub, depth + 1));
            }
        }
        return null;
    }

    private static readonly string[] SkipDirs =
        ["__pycache__", "node_modules", ".venv", "venv", "site-packages", "build", "dist", "docs", "tests"];

    private static readonly Regex ImportsGdsfactory =
        new(@"^\s*(import\s+gdsfactory\b|from\s+gdsfactory[\s.])", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static bool IsPdkPackage(string dir)
    {
        string init = Path.Combine(dir, "__init__.py");
        if (!File.Exists(init)) return false;
        string head = Peek(init);
        return ImportsGdsfactory.IsMatch(head) && head.Contains("Pdk(", StringComparison.Ordinal);
    }

    private static GdsfactoryKit SourceTreeKit(string dir)
    {
        // Dotted name from the package levels above, exactly as KitPCellLibrary computes it.
        var parts = new List<string> { Path.GetFileName(dir) };
        string top = dir;
        while (Path.GetDirectoryName(top) is { Length: > 0 } parent && File.Exists(Path.Combine(parent, "__init__.py")))
        {
            parts.Insert(0, Path.GetFileName(parent));
            top = parent;
        }
        string pathRoot = Path.GetDirectoryName(top) ?? top;
        string module = string.Join('.', parts);

        // A source tree states its Python floor in pyproject.toml when it states one at all.
        string? requires = null;
        foreach (string candidate in new[] { Path.Combine(pathRoot, "pyproject.toml"), Path.Combine(dir, "pyproject.toml") })
            if (File.Exists(candidate)
                && Regex.Match(Peek(candidate, 16384), @"^\s*requires-python\s*=\s*[""']([^""']+)[""']", RegexOptions.Multiline) is { Success: true } m)
            { requires = m.Groups[1].Value; break; }

        return new GdsfactoryKit(module, "", requires, module, dir, pathRoot);
    }

    private static string Peek(string path, int bytes = 4096)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[bytes];
            int read = stream.Read(buffer, 0, buffer.Length);
            return read <= 0 ? "" : Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    // ── METADATA ─────────────────────────────────────────────────────────────

    private sealed record Metadata(string Name, string Version, string? RequiresPython, IReadOnlyList<string> RequiresDist);

    /// <summary>The header block of a core-metadata file. Read to the first blank line; the body after
    /// it is the long description and says nothing this needs.</summary>
    private static Metadata? ReadMetadata(string path)
    {
        if (!File.Exists(path)) return null;
        string? name = null, version = null, requiresPython = null;
        var requiresDist = new List<string>();
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0) break;
                int colon = line.IndexOf(':');
                if (colon <= 0 || char.IsWhiteSpace(line[0])) continue;
                string key = line[..colon], value = line[(colon + 1)..].Trim();
                switch (key.ToLowerInvariant())
                {
                    case "name":            name ??= value; break;
                    case "version":         version ??= value; break;
                    case "requires-python": requiresPython ??= value; break;
                    case "requires-dist":   requiresDist.Add(value); break;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        return name is null ? null : new Metadata(name, version ?? "", requiresPython, requiresDist);
    }

    /// <summary>True when a requirement names gdsfactory unconditionally — a requirement only under an
    /// extra is an optional integration, not a kit.</summary>
    private static bool RequiresGdsfactory(IEnumerable<string> requiresDist)
    {
        foreach (string req in requiresDist)
        {
            var m = Regex.Match(req, @"^\s*([A-Za-z0-9][A-Za-z0-9._-]*)");
            if (!m.Success || NormalizeName(m.Groups[1].Value) != GdsfactoryDistribution) continue;
            int semi = req.IndexOf(';');
            if (semi >= 0 && req[(semi + 1)..].Contains("extra", StringComparison.Ordinal)) continue;
            return true;
        }
        return false;
    }

    /// <summary>Distribution names compare case- and separator-insensitively (the packaging rule).</summary>
    private static string NormalizeName(string name)
        => Regex.Replace(name.Trim().ToLowerInvariant(), "[-_.]+", "-");

    /// <summary>
    /// The importable top-level package a distribution installed: <c>top_level.txt</c> when it is there,
    /// else the first directory <c>RECORD</c> lists. Only a name that exists as a package directory in
    /// <paramref name="site"/> is accepted — consent keys on that directory.
    /// </summary>
    private static string? TopLevelModule(string distInfo, string site) => TopLevelModule(distInfo, site, out _);

    /// <param name="named">The first name the record states, whether or not it exists as a package here.</param>
    private static string? TopLevelModule(string distInfo, string site, out string? named)
    {
        named = null;
        var candidates = new List<string>();
        try
        {
            string top = Path.Combine(distInfo, "top_level.txt");
            if (File.Exists(top))
                candidates.AddRange(File.ReadLines(top).Select(l => l.Trim()).Where(l => l.Length > 0));

            string record = Path.Combine(distInfo, "RECORD");
            if (candidates.Count == 0 && File.Exists(record))
                foreach (string line in File.ReadLines(record))
                {
                    string entry = line.Split(',')[0];
                    int slash = entry.IndexOfAny(['/', '\\']);
                    if (slash <= 0) continue;
                    string first = entry[..slash];
                    if (first is ".." or "__pycache__" || first.EndsWith(".dist-info", StringComparison.Ordinal)
                        || first.EndsWith(".data", StringComparison.Ordinal)) continue;
                    if (!candidates.Contains(first)) candidates.Add(first);
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        named = candidates.FirstOrDefault(c => !c.StartsWith("__editable__", StringComparison.Ordinal)
                                               && !c.StartsWith('_') && !c.EndsWith(".pth", StringComparison.Ordinal));
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(site, c, "__init__.py")));
    }

    // ── Requires-Python ──────────────────────────────────────────────────────

    /// <summary>
    /// Whether a version satisfies a <c>Requires-Python</c> specifier. The comparison clauses kits
    /// write (<c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c>, <c>==</c> with or without <c>.*</c>,
    /// <c>!=</c>, <c>~=</c>); a clause this cannot read is not held against the interpreter — refusing
    /// on a misreading would lock out an environment that works.
    /// </summary>
    public static bool Satisfies(string? specifier, int major, int minor, int micro)
    {
        if (string.IsNullOrWhiteSpace(specifier)) return true;
        int[] have = [major, minor, micro];

        foreach (string raw in specifier.Split(','))
        {
            var m = Regex.Match(raw.Trim(), @"^(~=|===|==|!=|<=|>=|<|>)\s*([0-9]+(?:\.[0-9]+)*)(\.\*)?$");
            if (!m.Success) continue;
            string op = m.Groups[1].Value;
            int[] want = [.. m.Groups[2].Value.Split('.').Select(int.Parse)];
            bool wildcard = m.Groups[3].Success;
            int cmp = Compare(have, want);

            bool ok = op switch
            {
                ">=" => cmp >= 0,
                ">"  => cmp > 0,
                "<=" => cmp <= 0,
                "<"  => cmp < 0,
                "==" or "===" => wildcard ? PrefixEquals(have, want) : cmp == 0,
                "!=" => wildcard ? !PrefixEquals(have, want) : cmp != 0,
                "~=" => cmp >= 0 && PrefixEquals(have, want[..Math.Max(1, want.Length - 1)]),
                _    => true,
            };
            if (!ok) return false;
        }
        return true;
    }

    private static int Compare(int[] have, int[] want)
    {
        for (int i = 0; i < Math.Max(have.Length, want.Length); i++)
        {
            int a = i < have.Length ? have[i] : 0, b = i < want.Length ? want[i] : 0;
            if (a != b) return a.CompareTo(b);
        }
        return 0;
    }

    private static bool PrefixEquals(int[] have, int[] prefix)
        => prefix.Select((p, i) => i < have.Length && have[i] == p).All(x => x);

    private static bool AtLeast(int major, int minor, int wantMajor, int wantMinor)
        => major > wantMajor || (major == wantMajor && minor >= wantMinor);

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
