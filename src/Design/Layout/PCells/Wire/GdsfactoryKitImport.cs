using System.Diagnostics;
using System.Text.Json;
using CircuitRF.Design.Layout.TechImport;

namespace CircuitRF.Design.Layout.PCells.Wire;

/// <summary>
/// What a gdsfactory kit offers, as <c>gdsfactory_bridge.report</c> states it — the import report's
/// source (brief-gdf-2 R-gdf2-5b). Produced by running the kit, so only ever after consent.
/// </summary>
public sealed class GdsfactoryImportReport
{
    public string Module { get; set; } = "";
    public string GdsfactoryVersion { get; set; } = "";

    /// <summary>Set when the kit could not be described at all — nothing else is.</summary>
    public string? Problem { get; set; }

    public List<string> Offered { get; set; } = [];

    /// <summary>Reason → [cell, detail] pairs, grouped as the report shows them.</summary>
    public Dictionary<string, List<List<string>>> NotOffered { get; set; } = [];

    /// <summary>Cell → the parameters it keeps at their defaults (None, sequences, callables, specs).</summary>
    public Dictionary<string, List<string>> ParametersNotOffered { get; set; } = [];

    /// <summary>Cell → how many <c>placement</c> ports its default build has. Alignment marks, not pins.</summary>
    public Dictionary<string, int> PlacementPorts { get; set; } = [];

    /// <summary>Cell → its declared model (the <c>model</c> default, else <c>info["model"]</c>).</summary>
    public Dictionary<string, string> Models { get; set; } = [];

    /// <summary>Cell → every parameter it declares, which is what the palette's pairing reads.</summary>
    public Dictionary<string, List<string>> Parameters { get; set; } = [];

    /// <summary>Cell → the kit's own <c>.csym</c> for it, found by file stem (brief-gdf-4 R-gdf4-1). Wins over a glyph.</summary>
    public Dictionary<string, string> Symbols { get; set; } = [];

    /// <summary>Cell → the kit's own SVG glyph for it, found by file stem, when it ships no <c>.csym</c> of that stem.</summary>
    public Dictionary<string, string> Glyphs { get; set; } = [];

    /// <summary>Glyph cell → its ports at the default build, <c>placement</c> excluded: what the pins are placed from.</summary>
    public Dictionary<string, List<GdsfactoryPort>> Ports { get; set; } = [];

    public List<string> Problems { get; set; } = [];

    public int CellsNotOffered => NotOffered.Values.Sum(v => v.Count);
    public int ParametersNotOfferedCount => ParametersNotOffered.Values.Sum(v => v.Count);
    public int PlacementPortCount => PlacementPorts.Values.Sum();

    /// <summary>The tally line both the dialog and the CLI lead with.</summary>
    public string Tally()
        => Problem is { } p
            ? p
            : $"{Offered.Count} cell(s) offered, {CellsNotOffered} not offered; {ParametersNotOfferedCount} " +
              $"parameter(s) not offered; {PlacementPortCount} placement port(s) not made pins.";

    /// <summary>The reason a cell named like one of circuitRF's own generators is not offered.</summary>
    public const string ShadowedReason = "named like a circuitRF built-in, which takes precedence";

    /// <summary>
    /// Moves every offered cell whose name a circuitRF BUILT-IN generator already answers to into
    /// "not offered". Built-ins are resolved first, case-insensitively, so a kit can never shadow
    /// them — and the kit's cell of that name is therefore unreachable. Measured: gdsfactory's own
    /// generic kit has a <c>spiral</c> and a <c>via</c>, and placing either drew circuitRF's.
    /// Saying so is the report's job; renaming somebody else's cell is not.
    /// </summary>
    internal GdsfactoryImportReport WithBuiltInsShadowing()
    {
        var shadowed = Offered.Where(PCellRegistry.IsBuiltIn).ToList();
        if (shadowed.Count == 0) return this;
        Offered.RemoveAll(shadowed.Contains);
        NotOffered[ShadowedReason] = [.. shadowed.Select(c => new List<string> { c, $"circuitRF's own '{c}' is drawn instead" })];
        return this;
    }

    internal static GdsfactoryImportReport? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GdsfactoryImportReport>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>One port of a gdsfactory cell as the report states it: µm, and the outward orientation in degrees.</summary>
public sealed class GdsfactoryPort
{
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Orientation { get; set; }
}

/// <summary>
/// The one import a gdsfactory kit gets, whichever surface asks for it — File ▸ Import PDK's Python
/// package source and <c>circuitrf import pdk --module</c> both call this, so neither owns a second
/// copy (the rule <c>src/Design/CLAUDE.md</c> states for every "make an artifact" operation).
/// </summary>
public static class GdsfactoryKitImport
{
    /// <summary>How long the report may take. The first import after installing a kit spends up to
    /// ~26 s compiling bytecode (measured), and describing builds every cell once.</summary>
    public static TimeSpan ReportTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// <paramref name="module"/> as a kit in <paramref name="interpreter"/>'s environment, by files
    /// alone: a distribution discovery lists, or — for a module inside a package, such as the generic
    /// kit gdsfactory ships — the package directory that module names. Refuses with the discovery
    /// sentences: not Python, too old, no gdsfactory, below the kit's own floor, no such module.
    /// </summary>
    public static GdsfactoryKit? ResolveModule(
        string interpreter, string module, out string? refusal, Func<string, string?>? runProbe = null)
    {
        var env = CheckEnvironment(interpreter, module, out string? gfVersion, out refusal, runProbe);
        if (env is null) return null;

        var notListed = new List<GdsfactoryKitRefusal>();
        var kits = GdsfactoryKitDiscovery.ScanSitePackages(env.SitePackages, notListed);
        var kit = kits.FirstOrDefault(k => k.Module == module);
        if (kit is null)
        {
            string? dir = GdsfactoryKitDiscovery.PackageDirectoryIn(env.SitePackages, module);
            if (dir is null)
            {
                // An editable install of exactly this module is named, with the folder to import instead.
                refusal = notListed.FirstOrDefault(r => r.Kit.Module == module)?.Reason
                          ?? $"No package '{module}' is installed for '{interpreter}'" +
                             (kits.Count > 0 ? $"; its gdsfactory kits are {string.Join(", ", kits.Select(k => k.Module))}." : ".");
                return null;
            }

            // The version is the installed distribution's that owns the top-level package.
            string top = module.Split('.')[0];
            string? site = env.SitePackages.FirstOrDefault(s => dir.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            string? version = top == GdsfactoryKitDiscovery.GdsfactoryDistribution
                ? gfVersion
                : site is null ? null : GdsfactoryKitDiscovery.InstalledVersion(Path.Combine(site, top));
            kit = new GdsfactoryKit(module, version ?? "", null, module, dir);
        }

        return Fits(kit, env, interpreter, out refusal) ? kit : null;
    }

    /// <summary>
    /// The checks every gdsfactory kit's interpreter must pass before anything is declared: it is
    /// Python, it is new enough, and gdsfactory is installed in it. Null with the discovery sentence
    /// otherwise. <paramref name="kitName"/> names the kit in the missing-gdsfactory sentence.
    /// </summary>
    public static PythonEnvironment? CheckEnvironment(
        string interpreter, string kitName, out string? gdsfactoryVersion, out string? refusal,
        Func<string, string?>? runProbe = null)
    {
        gdsfactoryVersion = null;
        var env = GdsfactoryKitDiscovery.Probe(interpreter, out refusal, runProbe);
        if (env is null) return null;

        if (!GdsfactoryKitDiscovery.Satisfies(
                $">={GdsfactoryKitDiscovery.MinimumMajor}.{GdsfactoryKitDiscovery.MinimumMinor}", env.Major, env.Minor, env.Micro))
        {
            refusal = $"'{interpreter}' is Python {env.Version}; gdsfactory kits need Python " +
                      $"{GdsfactoryKitDiscovery.MinimumMajor}.{GdsfactoryKitDiscovery.MinimumMinor} or newer. " +
                      "circuitRF installs nothing: choose an environment that has a newer Python.";
            return null;
        }

        gdsfactoryVersion = GdsfactoryKitDiscovery.GdsfactoryVersionIn(env.SitePackages);
        if (gdsfactoryVersion is null)
        {
            refusal = GdsfactoryKitDiscovery.MissingGdsfactorySentence(interpreter, kitName);
            return null;
        }
        return env;
    }

    /// <summary>A kit's own Python floor, against an environment that passed <see cref="CheckEnvironment"/>.</summary>
    public static bool Fits(GdsfactoryKit kit, PythonEnvironment env, string interpreter, out string? refusal)
    {
        refusal = GdsfactoryKitDiscovery.Satisfies(kit.RequiresPython, env.Major, env.Minor, env.Micro)
            ? null
            : $"{kit.Display} needs Python {kit.RequiresPython}; '{interpreter}' is Python {env.Version}.";
        return refusal is null;
    }

    /// <summary>Writes the workspace declaration — see <see cref="KitPCellLibrary.EnsureDeclaredGdsfactory"/>.</summary>
    public static string? Declare(string workspaceRoot, GdsfactoryKit kit, out string? problem, out bool created)
        => KitPCellLibrary.EnsureDeclaredGdsfactory(workspaceRoot, kit, out problem, out created);

    /// <summary>
    /// Runs <c>python -m gdsfactory_bridge.report</c> for <paramref name="kit"/> — <b>which runs the
    /// kit's code</b>, so a caller asks only after consent. Null with a problem when it would not run
    /// or answered with something that is not a report.
    /// </summary>
    public static GdsfactoryImportReport? ReadReport(string interpreter, GdsfactoryKit kit, out string? problem)
    {
        if (RunBridge(interpreter, kit, "gdsfactory_bridge.report", out problem) is not { } run) return null;
        if (GdsfactoryImportReport.Parse(run.Line) is { } report) return report.WithBuiltInsShadowing();
        problem = $"describing '{kit.Module}' gave no report" + (run.StderrTail.Length > 0 ? $": {run.StderrTail}" : ".");
        return null;
    }

    /// <summary>
    /// Runs <c>python -m gdsfactory_bridge.technology</c> for <paramref name="kit"/> (brief-gdf-3 R-gdf3-1a):
    /// its layers, layer views, stack and material cards. <b>It runs the kit's code</b>, through the same
    /// interpreter, path and consent as <see cref="ReadReport"/>.
    ///
    /// <para>The layer-properties file the kit's views are written to lives in a directory circuitRF makes
    /// and removes itself, whatever the run did — handed over as <see cref="LayerViewsDirectoryVariable"/>.
    /// A directory named in the process's OUTPUT was once deleted recursively, which is a delete of
    /// whatever a stray line of that output named; and a run that printed no document leaked it.</para>
    /// </summary>
    public static GdsfactoryStackDocument? ReadTechnology(
        string interpreter, GdsfactoryKit kit, out ProcessLayerTable? layerTable, out string? problem)
    {
        layerTable = null;
        string views = Path.Combine(Path.GetTempPath(), "crf-gdf-views-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Directory.CreateDirectory(views);
            if (RunBridge(interpreter, kit, "gdsfactory_bridge.technology", out problem,
                          new Dictionary<string, string> { [LayerViewsDirectoryVariable] = views }) is not { } run)
                return null;
            if (GdsfactoryStackDocument.Parse(run.Line) is not { } doc)
            {
                problem = $"reading '{kit.Module}''s technology gave no answer" + (run.StderrTail.Length > 0 ? $": {run.StderrTail}" : ".");
                return null;
            }

            // Only a file inside the directory handed over is read; anything else the output names is not.
            if (doc.LayerViews is { Length: > 0 } named && !IsInside(named, views)) doc.LayerViews = null;
            layerTable = doc.LayerTable();
            doc.LayerViews = null;
            return doc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return null;
        }
        finally
        {
            try { Directory.Delete(views, recursive: true); }
            catch { /* a temporary file left behind is not worth failing an import over */ }
        }
    }

    /// <summary>The environment variable <c>gdsfactory_bridge.technology</c> writes the kit's layer views
    /// into — a directory circuitRF created for this one run.</summary>
    public const string LayerViewsDirectoryVariable = "CRF_GDF_VIEWS_DIR";

    private static bool IsInside(string path, string directory)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
            return full.StartsWith(dir, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    private readonly record struct BridgeRun(string Line, string StderrTail);

    /// <summary>
    /// One <c>python -P -m &lt;bridgeModule&gt; &lt;module&gt;</c>, with circuitRF's own package and the kit's
    /// root on the path: the last stdout line that starts a JSON object, and the last stderr line.
    ///
    /// <para><b><c>-P</c></b>, because <c>-m</c> otherwise puts the CURRENT DIRECTORY first on
    /// <c>sys.path</c>: run from a kit's own repository, the working copy there would be described
    /// instead of the installed kit the user allowed, and any <c>json.py</c> lying in that directory
    /// would be imported. <c>-I</c> is not used — it also drops the <c>PYTHONPATH</c> this relies on.
    /// Every interpreter that gets here passed <see cref="CheckEnvironment"/>, so it is at least 3.12 and
    /// knows the flag.</para>
    /// </summary>
    private static BridgeRun? RunBridge(
        string interpreter, GdsfactoryKit kit, string bridgeModule, out string? problem,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        problem = null;
        var parsed = PythonInterpreter.ParseRecord(interpreter);
        if (parsed is null) { problem = $"'{interpreter}' is not an interpreter."; return null; }
        if (PCellPythonPackage.RootDirectory is not { } own)
        {
            problem = "circuitRF's own Python package was not found beside the application.";
            return null;
        }

        var info = new ProcessStartInfo(parsed.Value.Command);
        foreach (string a in parsed.Value.Arguments) info.ArgumentList.Add(a);
        info.ArgumentList.Add("-P");
        info.ArgumentList.Add("-m");
        info.ArgumentList.Add(bridgeModule);
        info.ArgumentList.Add(kit.Module);
        info.Environment["PYTHONPYCACHEPREFIX"] = UserStateDirectory.SubDir("pcell-cache");
        foreach (var (key, value) in environment ?? new Dictionary<string, string>()) info.Environment[key] = value;
        var path = new List<string> { own };
        if (kit.PythonPathRoot is { } root) path.Add(root);
        string existing = Environment.GetEnvironmentVariable("PYTHONPATH") ?? "";
        info.Environment["PYTHONPATH"] = string.Join(Path.PathSeparator, existing.Length > 0 ? [.. path, existing] : path);

        try
        {
            if (BoundedProcess.Run(info, ReportTimeout) is not { } run)
            {
                problem = $"'{interpreter}' could not be started.";
                return null;
            }
            if (run.TimedOut)
            {
                problem = $"describing '{kit.Module}' took longer than {ReportTimeout.TotalSeconds:0} s, so it was stopped.";
                return null;
            }

            string line = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .LastOrDefault(l => l.StartsWith('{')) ?? "";
            string tail = run.Stderr.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
            return new BridgeRun(line, tail);
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return null;
        }
    }
}
