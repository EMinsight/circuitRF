using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf explain --pdk &lt;kit&gt; [&lt;workspace&gt;] [--python &lt;interp&gt;]</c> — which
/// interpreter a gdsfactory kit resolves to, and whether that answer came from <c>--python</c> or from
/// this user's recorded preference (brief-gdf-2 R-gdf2-5c).
///
/// <para>Read-only on <c>explain</c>'s terms, and it runs no kit code: the declaration is read from the
/// workspace's manifest, the version comparison from files, and the one process started is the
/// interpreter answering its own version — the same probe discovery uses. <c>&lt;kit&gt;</c> is the
/// module name or the declaration's folder name.</para>
/// </summary>
internal static class ExplainPdk
{
    public static int Run(string[] args)
    {
        string? name = null, path = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pdk" && i + 1 < args.Length) { name = args[++i]; continue; }
            if (args[i].StartsWith('-')) { JsonRun.Report(CliDiagnostics.ExplainUnknownOption(args[i])); return Usage(); }
            if (path is not null) { JsonRun.Report(CliDiagnostics.ExplainMultiplePaths()); return Usage(); }
            path = args[i];
        }
        if (name is null) return Usage();

        // The workspace is optional: without one, the question is still answerable for a module name.
        PCellKit? kit = null;
        string full = Path.GetFullPath(path ?? Directory.GetCurrentDirectory());
        if (File.Exists(full)) full = Path.GetDirectoryName(full)!;
        if (Directory.Exists(full) && WorkspaceRootFinder.WorkspaceDirOf(full) is { } wsRoot)
        {
            JsonRun.InputPath = wsRoot;
            // A resolver that is never asked for a generator starts nothing: listing its kits is a
            // manifest scan.
            using var resolver = new PCellWorkerResolver(wsRoot, trust: _ => PCellTrustDecision.Unknown,
                                                         moduleInterpreter: GeneratedCells.InterpreterFor);
            kit = resolver.Kits.FirstOrDefault(k => k.IsGdsfactory && (k.Module == name || k.Name == name));
        }

        string module = kit?.Module ?? name;
        if (kit is not null)
        {
            Console.WriteLine($"kit: {module} ({kit.Directory})");
            JsonRun.Note(CliDiagnostics.ExplainPdkNote($"declared in '{kit.Directory}'"));
            if (kit.Location is { } location)
            {
                Console.WriteLine($"location: {location}");
                string? installed = GdsfactoryKitDiscovery.InstalledVersion(location);
                string versions = $"version recorded: {kit.ModuleVersion ?? "(none)"}; installed: {installed ?? "(not stated)"}";
                Console.WriteLine(versions);
                JsonRun.Note(CliDiagnostics.ExplainPdkNote(versions));
            }
        }

        string? flag = GeneratedCells.PythonOverride;
        string? recorded = GdsfactoryInterpreterPreferences.Recorded(module);
        if ((flag ?? recorded) is not { } interpreter)
            return JsonRun.Fail(CliDiagnostics.ExplainPdkNoInterpreter(GdsfactoryInterpreterPreferences.NoInterpreterSentence(module)));

        string source = flag is not null
            ? $"from {GeneratedCells.PythonFlag}" + (recorded is null ? "" : $"; this user's preference is '{recorded}'")
            : "from this user's preference";
        Console.WriteLine($"interpreter: {interpreter} ({source})");
        JsonRun.Note(CliDiagnostics.ExplainPdkInterpreter(module, interpreter, source));

        var env = GdsfactoryKitImport.CheckEnvironment(interpreter, module, out string? gfVersion, out string? refusal);
        if (env is null) return JsonRun.Fail(CliDiagnostics.ExplainPdkNoInterpreter(refusal!));
        string facts = $"Python {env.Version}, gdsfactory {gfVersion}";
        Console.WriteLine(facts);
        JsonRun.Note(CliDiagnostics.ExplainPdkNote(facts));
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: circuitrf explain --pdk <kit> [<workspace>] [--python <interp>]");
        return 1;
    }
}
