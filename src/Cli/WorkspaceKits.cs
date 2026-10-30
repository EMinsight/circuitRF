using CircuitRF.Core.Devices.External;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Cli;

/// <summary>
/// The kits a schematic's workspace references, loaded before it is extracted — what the application
/// does on a workspace open, so a part placed from an imported kit resolves headlessly too
/// (brief-gdf-1). Until this, nothing outside the GUI ever populated <see cref="PdkKitRegistry"/>, and a
/// schematic holding one kit part could not be netlisted or run from the command line at all.
///
/// <para><b>It owns no loading.</b> <see cref="PdkWorkspaceImport.LoadReferenced"/> reads and registers
/// each kit through the function the application's own open uses, and writes nothing — a run leaves the
/// workspace as it found it. Loaded once per workspace per process; <see cref="Forget"/> drops that
/// memory when an import in the same process changed what is referenced.</para>
/// </summary>
internal static class WorkspaceKits
{
    private static readonly Dictionary<string, IExternalProviderResolver?> _loaded =
        new(StringComparer.Ordinal);
    private static readonly Lock _gate = new();

    /// <summary>Makes the kits of <paramref name="documentPath"/>'s workspace resolvable, saying on stderr
    /// which could not be loaded. Silent for a document in no workspace, or one referencing no kit.</summary>
    public static void EnsureLoadedFor(string documentPath)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(documentPath));
        string? root = WorkspaceRootFinder.WorkspaceDirOf(dir) ?? CliEntry.HostWorkspace;
        if (root is null) return;
        root = Path.GetFullPath(root);

        lock (_gate)
        {
            if (_loaded.ContainsKey(root)) return;

            var result = PdkWorkspaceImport.LoadReferenced(root);
            foreach (string problem in result.Problems)
            {
                var d = CliDiagnostics.KitNotLoaded(problem);
                Console.Error.WriteLine($"warning: {d.Render()}");
                JsonRun.Note(d);
            }

            // A resolver rather than providers, as the application registers: no worker starts until
            // a design actually asks for one of the kit's devices.
            IExternalProviderResolver? resolver = null;
            if (result.Manifests.Count > 0)
            {
                resolver = new DeviceWorkerProviderResolver(result.Manifests);
                ExternalDeviceRegistry.AddResolver(resolver);
            }
            _loaded[root] = resolver;
        }
    }

    /// <summary>Forgets <paramref name="workspaceRoot"/>'s loaded kits, so the next extraction reads them
    /// again — after an import in the same process changed what the workspace references.</summary>
    public static void Forget(string workspaceRoot)
    {
        lock (_gate)
        {
            string root = Path.GetFullPath(workspaceRoot);
            if (_loaded.Remove(root, out var resolver)) ExternalDeviceRegistry.RemoveResolver(resolver);
        }
    }
}
