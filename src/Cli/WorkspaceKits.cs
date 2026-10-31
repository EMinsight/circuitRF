using CircuitRF.Core.Devices.External;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Cli;

/// <summary>
/// The kits a document's workspace references, made resolvable for a headless run — loaded ONE AT A
/// TIME, the first time the run asks for a part or a device of one (brief-gdf-6 R-gdf6-1). Until
/// brief-gdf-1 nothing outside the GUI ever populated <see cref="PdkKitRegistry"/>, and a schematic
/// holding one kit part could not be netlisted or run from the command line at all; until brief-gdf-6
/// every run loaded EVERY referenced kit before it started, which on a large kit was seconds added to
/// every <c>sparam</c>, <c>netlist</c> or <c>check</c> of a design that used none of its parts.
///
/// <para><b>It cannot simply be skipped, and it is not decided here.</b> A top schematic with no kit part
/// can reach kit parts through its child cells, and only the extraction finds out. So this installs a
/// LOADER on <see cref="PdkKitRegistry"/>, which every reader of a kit part already asks, and a resolver
/// on <see cref="ExternalDeviceRegistry"/>, which a netlist's <c>Provider=</c> asks; each loads the one kit
/// it was asked about, once per process. A kit's device-worker resolver is registered by the same load,
/// and a kit that could not be loaded is said at the moment a run needed it — a run using no kit says
/// nothing about kits.</para>
///
/// <para><b>It owns no loading.</b> <see cref="PdkWorkspaceImport.LoadReferencedKit"/> reads and registers
/// the kit through the function the application's own open uses, and writes nothing — a run leaves the
/// workspace as it found it.</para>
///
/// <para><b>Headless only.</b> The application still loads every kit at workspace open: its palette
/// needs every kit's parts whether a design uses them or not.</para>
/// </summary>
internal static class WorkspaceKits
{
    private static readonly Dictionary<string, KitLoader> _loaders = new(StringComparer.Ordinal);
    private static readonly Lock _gate = new();

    /// <summary>
    /// Called with (workspace, kit) each time one kit is actually loaded — a counting seam for the test
    /// that a run loads exactly the kits its design reaches. Null in production.
    /// </summary>
    internal static Action<string, string>? KitLoaded { get; set; }

    /// <summary>Makes the kits of <paramref name="documentPath"/>'s workspace loadable on demand. Loads
    /// nothing itself. Silent for a document in no workspace.</summary>
    public static void EnsureLoadedFor(string documentPath)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(documentPath));
        string? root = WorkspaceRootFinder.WorkspaceDirOf(dir) ?? CliEntry.HostWorkspace;
        if (root is null) return;
        root = Path.GetFullPath(root);

        lock (_gate)
        {
            if (_loaders.ContainsKey(root)) return;

            var loader = new KitLoader(root);
            _loaders[root] = loader;
            PdkKitRegistry.SetLoader(root, loader.Load);
            // A resolver rather than providers, as the application registers: no worker starts until
            // a design actually asks for one of the kit's devices.
            ExternalDeviceRegistry.AddResolver(loader);
        }
    }

    /// <summary>Forgets <paramref name="workspaceRoot"/>'s kits, so the next run reads them again — after
    /// an import in the same process changed what the workspace references.</summary>
    public static void Forget(string workspaceRoot)
    {
        string root = Path.GetFullPath(workspaceRoot);
        lock (_gate)
        {
            if (!_loaders.Remove(root, out var loader)) return;
            PdkKitRegistry.SetLoader(root, null);
            PdkKitRegistry.ClearWorkspace(root);
            ExternalDeviceRegistry.RemoveResolver(loader);
        }
    }

    /// <summary>
    /// One workspace's kits, loaded as they are asked for: by <see cref="PdkKitRegistry"/> for a part, and
    /// as an <see cref="IExternalProviderResolver"/> for a netlist's provider. The devices of every kit
    /// loaded so far resolve through one <see cref="DeviceWorkerProviderResolver"/> over their manifests,
    /// the shape the application registers.
    /// </summary>
    private sealed class KitLoader(string root) : IExternalProviderResolver
    {
        private readonly List<(string Kit, DeviceWorkerManifest Manifest)> _manifests = [];
        private DeviceWorkerProviderResolver? _devices;

        /// <summary>The loader <see cref="PdkKitRegistry"/> calls, at most once per kit.</summary>
        public bool Load(string kit)
        {
            if (PdkWorkspaceImport.LoadReferencedKit(root, kit) is not { } result) return false;   // not one of ours
            KitLoaded?.Invoke(root, kit);

            foreach (string problem in result.Problems)
            {
                var d = CliDiagnostics.KitNotLoaded(problem);
                Console.Error.WriteLine($"warning: {d.Render()}");
                JsonRun.Note(d);
            }

            lock (_manifests)
            {
                _manifests.AddRange(result.Manifests);
                _devices = null;
            }
            return result.Loaded.Count > 0;
        }

        public string Describe
        {
            get { lock (_manifests) return Devices().Describe; }
        }

        public IExternalDeviceProvider? Resolve(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            // Asking is what loads it: the registry calls Load once for this kit, then answers from memory.
            PdkKitRegistry.HasKit(root, DeviceWorkerProviderResolver.SplitOverride(name).Kit);
            DeviceWorkerProviderResolver devices;
            lock (_manifests)
            {
                if (_manifests.Count == 0) return null;
                devices = Devices();
            }
            return devices.Resolve(name);
        }

        private DeviceWorkerProviderResolver Devices() => _devices ??= new DeviceWorkerProviderResolver(_manifests);
    }
}
