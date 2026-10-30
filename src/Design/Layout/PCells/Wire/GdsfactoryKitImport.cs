using System.Diagnostics;
using System.Text.Json;

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

        var kits = GdsfactoryKitDiscovery.ScanSitePackages(env.SitePackages);
        var kit = kits.FirstOrDefault(k => k.Module == module);
        if (kit is null)
        {
            string relative = module.Replace('.', Path.DirectorySeparatorChar);
            string? dir = env.SitePackages
                .Select(s => Path.Combine(s, relative))
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "__init__.py")));
            if (dir is null)
            {
                refusal = $"No package '{module}' is installed for '{interpreter}'" +
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
        problem = null;
        var parsed = PythonInterpreter.ParseRecord(interpreter);
        if (parsed is null) { problem = $"'{interpreter}' is not an interpreter."; return null; }
        if (PCellPythonPackage.RootDirectory is not { } own)
        {
            problem = "circuitRF's own Python package was not found beside the application.";
            return null;
        }

        var info = new ProcessStartInfo(parsed.Value.Command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (string a in parsed.Value.Arguments) info.ArgumentList.Add(a);
        info.ArgumentList.Add("-m");
        info.ArgumentList.Add("gdsfactory_bridge.report");
        info.ArgumentList.Add(kit.Module);
        info.Environment["PYTHONPYCACHEPREFIX"] = UserStateDirectory.SubDir("pcell-cache");
        var path = new List<string> { own };
        if (kit.PythonPathRoot is { } root) path.Add(root);
        string existing = Environment.GetEnvironmentVariable("PYTHONPATH") ?? "";
        info.Environment["PYTHONPATH"] = string.Join(Path.PathSeparator, existing.Length > 0 ? [.. path, existing] : path);

        try
        {
            using var process = Process.Start(info);
            if (process is null) { problem = $"'{interpreter}' could not be started."; return null; }
            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync();
            string stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)ReportTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                problem = $"describing '{kit.Module}' took longer than {ReportTimeout.TotalSeconds:0} s.";
                return null;
            }

            string line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .LastOrDefault(l => l.StartsWith('{')) ?? "";
            if (GdsfactoryImportReport.Parse(line) is { } report) return report.WithBuiltInsShadowing();

            string tail = stderr.Result.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
            problem = $"describing '{kit.Module}' gave no report" + (tail.Length > 0 ? $": {tail}" : ".");
            return null;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return null;
        }
    }
}
