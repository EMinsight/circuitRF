namespace CircuitRF.Design.Layout.PCells.Wire;

/// <summary>Asked for a generator nobody has registered. See <see cref="PCellRegistry"/>.</summary>
public interface IPCellGeneratorResolver
{
    /// <summary>
    /// Produce a generator for <paramref name="generatorId"/>, or null for "no opinion" — which is
    /// the ordinary answer and must not be an error: several resolvers may be registered and all but
    /// one of them will not know any given id.
    /// </summary>
    PCellGenerator? Resolve(string generatorId);

    /// <summary>Every id this resolver can currently answer for, for listing without asking by
    /// name. May start a generator to find out.</summary>
    IReadOnlyCollection<string> KnownGeneratorIds { get; }

    /// <summary>Where this resolver looked, for a message when it found nothing.</summary>
    string Describe();

    /// <summary>
    /// <paramref name="generatorId"/>'s own declared parameter defaults, or null for an id this
    /// resolver does not own. What makes a cell PLACEABLE without the caller already knowing its
    /// parameters — see <see cref="PCellWireParameterDecl.Default"/>.
    /// </summary>
    IReadOnlyDictionary<string, PCellValue>? DeclaredDefaults(string generatorId) => null;

    /// <summary>
    /// Everything <paramref name="generatorId"/> declares about its parameters — kinds, defaults,
    /// labels, enumerations, bounds — or null for an id this resolver does not own.
    ///
    /// <para>A superset of <see cref="DeclaredDefaults"/>, which stays because it answers the
    /// narrower question ("what do I place this with") that every caller but the parameter editor
    /// asks, and answering it through this would make placement depend on display metadata.</para>
    /// </summary>
    IReadOnlyList<PCellParameterInfo>? DeclaredParameters(string generatorId) => null;

    /// <summary>
    /// A string that changes whenever <paramref name="generatorId"/>'s own definition changes, folded
    /// into the key that names a generated cell. Null for an id this resolver does not own.
    ///
    /// <para>Without it, editing a generator leaves every cell it already produced on disk and in
    /// use — the exact failure the built-ins' hand-maintained version number exists to prevent, with
    /// a larger blast radius because a script is edited far more often than a built-in.</para>
    /// </summary>
    string? ContentKeyFor(string generatorId);
}

/// <summary>A kit declared under the workspace: where its manifest is, and the script that would run.</summary>
public sealed record PCellKit(string Directory, string EntryScript)
{
    /// <summary>What to call it in a prompt — the kit's own folder name.</summary>
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory));

    /// <summary>
    /// What this kit's consent is recorded under. The manifest's folder for a <c>cni</c> kit, exactly
    /// as always; for a gdsfactory kit, the kit's INSTALLED directory with links resolved
    /// (<see cref="PCellTrustStore.ResolvedDirectory"/>) — it is that directory's code that runs, and
    /// a second workspace declaring the same installed kit is the same question (brief-gdf-2 R-gdf2-2a).
    ///
    /// <para>That holds only while the workspace adds no code of its own. A declaration whose entry
    /// script is not the one circuitRF generates, or whose <c>pythonPath</c> reaches anywhere but the
    /// kit's own source-tree root, runs workspace code under the kit's name — so it is keyed on its
    /// declaration folder, like any other kit, and a workspace that arrived from someone else is asked
    /// about rather than run on the strength of permission given to the kit.</para>
    /// </summary>
    public string TrustKey { get; init; } = Directory;

    /// <summary>The gdsfactory kit's module, or null for every other kit. Set means the consent prompt
    /// shows the package rows below.</summary>
    public string? Module { get; init; }

    /// <summary>The kit's version as the consent prompt states it: what is INSTALLED now, because that
    /// is what would run — the recorded one when nothing installed says.</summary>
    public string? ModuleVersion { get; init; }

    /// <summary>The version the workspace recorded when it declared the kit.</summary>
    public string? RecordedVersion { get; init; }

    /// <summary>Where the kit's code is installed.</summary>
    public string? Location { get; init; }

    /// <summary>The interpreter that would run it, when one is chosen.</summary>
    public string? Interpreter { get; init; }

    public bool IsGdsfactory => Module is not null;
}

/// <summary>
/// Finds generator scripts declared under a workspace and starts one the first time a design
/// actually asks for one of its cells.
///
/// <para><b>Resolvers are registered at workspace open; providers are not — so opening a workspace
/// starts no processes.</b> A workspace may reference several kits and a given design typically uses
/// cells from none of them. This is the same arrangement <c>DeviceWorkerProviderResolver</c> already
/// has, and for the same reason: the alternative is an interpreter per kit spawned on every open.</para>
/// </summary>
public sealed class PCellWorkerResolver : IPCellGeneratorResolver, IDisposable
{
    private readonly string _rootDirectory;
    private readonly Func<PCellGeneratorManifest, string, PythonInterpreter?> _findInterpreter;
    private readonly Action<string>? _report;
    private readonly Func<string, PCellTrustDecision>? _trust;
    private readonly Func<string, string?> _moduleInterpreter;
    private readonly Action<string>? _note;
    private readonly Lock _gate = new();

    private readonly List<(string Directory, PCellGeneratorManifest Manifest)> _manifests = [];
    private readonly List<PCellWorkerProvider> _providers = [];
    private Dictionary<string, PCellWorkerProvider>? _byGeneratorId;
    private bool _disposed;

    /// <param name="rootDirectory">Workspace root — searched itself and one level down, matching the
    /// device manifest's own search shape. Deliberately shallow: the further out it goes, the less
    /// the territory has to do with the kit, and it would eventually match by accident.</param>
    /// <param name="findInterpreter">How to turn a manifest into an interpreter to run. A seam, so
    /// that real discovery (a later phase) replaces the default without touching this class.</param>
    /// <param name="report">Where a problem with a manifest goes. Null discards — a headless caller
    /// has nowhere to put it, and a resolver must not be the thing that fails an open.</param>
    /// <param name="trust">
    /// Whether this installation has agreed to run a given kit's scripts. <b>Anything other than
    /// <see cref="PCellTrustDecision.Allowed"/> means the kit is never started</b> — including
    /// <c>Unknown</c>, so a kit nobody has been asked about does not run while the asking happens.
    ///
    /// <para>Null means the CALLER HAS NO POLICY and everything declared runs. That default exists for
    /// headless callers (tests, a future CLI) which have no user to ask and no place to record an
    /// answer; the application always supplies a real policy, and
    /// <c>PCellTrustGateTests.ResetPCellGenerators_AlwaysPassesATrustPolicy</c> pins that it does, so
    /// a future construction site cannot quietly disable consent by omitting it.</para>
    /// </param>
    /// <param name="moduleInterpreter">
    /// For a gdsfactory kit: which interpreter this user chose for a module name (brief-gdf-2
    /// R-gdf2-2b). Null reads the choices this installation has RECORDED
    /// (<see cref="GdsfactoryInterpreterPreferences"/>), read-only — the application passes its own
    /// preferences, a headless run may put its <c>--python</c> first.
    /// </param>
    /// <param name="note">Where a NOTE goes — something worth saying that is not a problem, such as a
    /// gdsfactory kit whose installed version changed since it was declared. Null sends it to
    /// <paramref name="report"/>.</param>
    /// <param name="runProbe">How a gdsfactory kit's interpreter is asked for its version and package
    /// directories — the seam <see cref="GdsfactoryKitDiscovery.Probe"/> takes. Null runs it for real.</param>
    public PCellWorkerResolver(
        string rootDirectory,
        Func<PCellGeneratorManifest, string, PythonInterpreter?>? findInterpreter = null,
        Action<string>? report = null,
        Func<string, PCellTrustDecision>? trust = null,
        Func<string, string?>? moduleInterpreter = null,
        Action<string>? note = null,
        Func<string, string?>? runProbe = null)
    {
        _rootDirectory = rootDirectory;
        _findInterpreter = findInterpreter ?? DefaultInterpreter;
        _report = report;
        _trust = trust;
        _moduleInterpreter = moduleInterpreter ?? GdsfactoryInterpreterPreferences.Recorded;
        _note = note ?? report;
        _runProbe = runProbe;
        Rescan();
    }

    private readonly Func<string, string?>? _runProbe;

    /// <summary>
    /// What this workspace recorded last time, replayed rather than re-derived. Set before the first
    /// lookup; see <see cref="Recorded"/>'s own note for why replaying matters.
    /// </summary>
    /// <remarks>
    /// Probing candidates costs a process launch each, and the answer does not change between
    /// sessions — the same reasoning that made kit settings recorded rather than re-derived, where
    /// the measured difference was 0.5 ms against 199.8 ms.
    /// </remarks>
    public string? Recorded { get; set; }

    /// <summary>Raised once, with the interpreter actually chosen, so the caller can record it and
    /// say so. Not raised when discovery failed — there is nothing to record.</summary>
    public event Action<PythonInterpreter>? InterpreterChosen;

    /// <summary>
    /// Full discovery: the kit's own declaration, then this workspace's recorded choice, then PATH,
    /// the platform launcher and the usual install locations — each probed by RUNNING it, so a
    /// broken shim is rejected rather than launched.
    /// </summary>
    public PythonInterpreter? DefaultInterpreter(PCellGeneratorManifest manifest, string manifestDirectory)
    {
        // A gdsfactory kit is run by the environment its user chose for it, and by nothing found on
        // the way — a guessed interpreter without the kit's dependencies fails far from here.
        if (manifest.IsGdsfactory) return GdsfactoryInterpreter(manifest, manifestDirectory);

        // Relative resolves against the manifest, like every other path it carries — a kit shipping
        // its own environment names it relative to itself.
        string? declared = manifest.Interpreter is { Length: > 0 } d
            ? (Path.IsPathRooted(d) ? d : Path.GetFullPath(Path.Combine(manifestDirectory, d)))
            : null;

        var found = PythonInterpreterDiscovery.Find(declared, Recorded, out var rejected);

        if (found is null)
        {
            _report?.Invoke(PythonInterpreterDiscovery.DescribeFailure(rejected));
            return null;
        }

        // Reported ONCE, and only when it was not already the recorded choice: an automatically-made
        // decision should be visible the first time it is made, and noise on every open otherwise.
        if (!string.Equals(found.ToRecord(), Recorded, StringComparison.Ordinal))
        {
            _report?.Invoke($"Using Python {found.Version} for generated artwork " +
                            $"({found.HowFound}: {found.Command}).");
            InterpreterChosen?.Invoke(found);
        }

        return found;
    }

    /// <summary>
    /// The interpreter this user chose for a gdsfactory kit's module, checked: it runs, it is new
    /// enough for gdsfactory and for the kit, and gdsfactory is installed in it. Each refusal is the
    /// sentence discovery itself uses. The gdsfactory version it reports is kept for the cache key.
    /// </summary>
    private PythonInterpreter? GdsfactoryInterpreter(PCellGeneratorManifest manifest, string manifestDirectory)
    {
        string module = manifest.Module!;
        if (_moduleInterpreter(module) is not { Length: > 0 } chosen)
        {
            _report?.Invoke(GdsfactoryInterpreterPreferences.NoInterpreterSentence(module));
            return null;
        }

        var env = GdsfactoryKitDiscovery.Probe(chosen, out string? notPython, _runProbe);
        if (env is null)
        {
            _report?.Invoke($"The gdsfactory kit '{module}' cannot run: {notPython}");
            return null;
        }

        string? kitRoot = manifest.ResolveKitRoot(manifestDirectory);
        string? requires = kitRoot is null ? null : GdsfactoryKitDiscovery.InstalledRequiresPython(kitRoot);
        if (!GdsfactoryKitDiscovery.Satisfies($">={GdsfactoryKitDiscovery.MinimumMajor}.{GdsfactoryKitDiscovery.MinimumMinor}", env.Major, env.Minor, env.Micro)
            || !GdsfactoryKitDiscovery.Satisfies(requires, env.Major, env.Minor, env.Micro))
        {
            _report?.Invoke($"The gdsfactory kit '{module}' cannot run: '{chosen}' is Python {env.Version}, and " +
                            $"the kit needs {requires ?? $">={GdsfactoryKitDiscovery.MinimumMajor}.{GdsfactoryKitDiscovery.MinimumMinor}"}.");
            return null;
        }

        // The environment chosen must hold THE kit this declaration names — the installed directory
        // permission was given for, and the one the cache key hashed. The choice is per user and by
        // module NAME, so nothing else ties the two together: a different environment's copy of the
        // module is different code, which nobody has allowed, and running it on the strength of the
        // other copy's permission is a consent bypass (overview D2: "a different environment asks
        // again"). A source tree is found through PYTHONPATH, ahead of site-packages, so it is its
        // own answer.
        if (kitRoot is not null && manifest.ResolvePythonPath(manifestDirectory).Count == 0)
        {
            string? here = GdsfactoryKitDiscovery.PackageDirectoryIn(env.SitePackages, module);
            if (here is null || !string.Equals(PCellTrustStore.ResolvedDirectory(here), PCellTrustStore.ResolvedDirectory(kitRoot),
                                               StringComparison.OrdinalIgnoreCase))
            {
                _report?.Invoke($"The gdsfactory kit '{module}' cannot run in '{chosen}': " +
                                (here is null ? $"that environment has no '{module}' installed" : $"that environment's '{module}' is at '{here}'") +
                                $", and this workspace declares the one at '{kitRoot}', which is what permission was given for. " +
                                "Import the kit again from the environment that should run it (File ▸ Import PDK), " +
                                "or choose the environment it is installed in.");
                return null;
            }
        }

        if (GdsfactoryKitDiscovery.GdsfactoryVersionIn(env.SitePackages) is not { } gfVersion)
        {
            _report?.Invoke(GdsfactoryKitDiscovery.MissingGdsfactorySentence(chosen, module));
            return null;
        }

        _gdsfactoryVersionByDir[manifestDirectory] = gfVersion;
        var parsed = PythonInterpreter.ParseRecord(chosen)!.Value;
        // -P keeps the entry script's own folder — workspace content — off the front of sys.path, so
        // nothing lying beside the script can shadow the kit, gdsfactory or circuitRF's own package. The
        // probe above proved the interpreter is at least 3.12, which knows the flag.
        return new PythonInterpreter(parsed.Command, [.. parsed.Arguments, "-P"], env.Version, "chosen for this kit on this machine");
    }

    /// <summary>gdsfactory's version per gdsfactory-kit manifest directory, as its interpreter reported it.</summary>
    private readonly Dictionary<string, string> _gdsfactoryVersionByDir = new(StringComparer.Ordinal);

    /// <summary>
    /// Re-reads everything from disk: the manifests, and — the part authoring iteration depends on —
    /// the CONTENT HASHES. Ends whatever was already running first. Reading a manifest is cheap and
    /// starts nothing.
    ///
    /// <para><b>Clearing the content keys is what makes an edited script take effect.</b> They are
    /// computed once and cached (a hash per kit per session), so without this a script edited
    /// mid-session keeps its old key, resolves to the cell the previous version wrote, and the edit
    /// appears to do nothing. <see cref="StopProviders"/> deliberately does the opposite and KEEPS
    /// them — it is called when permission changes, where the scripts have not moved and a
    /// session-scoped fallback key must stay stable for the session.</para>
    /// </summary>
    public void Rescan()
    {
        StopProviders();

        lock (_gate)
        {
            _contentKeys.Clear();
            _manifests.Clear();

            foreach (string dir in CandidateDirectories())
            {
                var manifest = PCellGeneratorManifest.TryRead(dir, out var problem);
                if (problem is not null) _report?.Invoke(problem);
                if (manifest is not null) _manifests.Add((dir, manifest));
            }
        }

        // One NOTE per gdsfactory kit per open when what is installed is not what was declared — a
        // kit upgraded under a workspace is ordinary, and still worth knowing (R-gdf2-2d). Files only.
        // Once per resolver, not per Rescan: an import reloads the generators, and the same note again
        // on every reload is noise that teaches people to stop reading notes.
        foreach (var (dir, manifest) in _manifests.ToList())
            if (manifest.IsGdsfactory && manifest.ModuleVersion is { Length: > 0 } recorded
                && manifest.ResolveKitRoot(dir) is { } kitRoot
                && GdsfactoryKitDiscovery.InstalledVersion(kitRoot) is { Length: > 0 } installed
                && !string.Equals(installed, recorded, StringComparison.Ordinal)
                && _versionNoted.Add($"{dir}\n{recorded}\n{installed}"))
                _note?.Invoke($"The gdsfactory kit '{manifest.Module}' was {recorded} when this workspace declared " +
                              $"it and is {installed} now; its cells are generated by the installed version.");
    }

    /// <summary>
    /// Ends every started interpreter and forgets what they offered, so the next lookup re-decides
    /// from scratch. Called when the trust decision changes — a kit that was refused a moment ago is
    /// now allowed, and the "we already worked out what is available" answer is stale.
    ///
    /// <para>Disposing is what actually ends the processes; clearing the map alone would start a
    /// SECOND interpreter per kit on the next lookup and leave the first running with nothing to talk
    /// to. The per-directory content keys are deliberately kept: a session-scoped fallback key must
    /// stay stable for the session, or the same cells would regenerate twice.</para>
    /// </summary>
    public void StopProviders()
    {
        List<PCellWorkerProvider> providers;
        lock (_gate)
        {
            providers = [.. _providers];
            _providers.Clear();
            _byGeneratorId = null;
            _contentKeyByGeneratorId.Clear();
            _kitNameByGeneratorId.Clear();
            _kitDirectoryByGeneratorId.Clear();
        }

        foreach (var provider in providers)
            try { provider.Dispose(); } catch { /* teardown must not fail the caller */ }
    }

    /// <summary>Every kit declared under the root, from the manifest scan alone — nothing has been
    /// started to produce this. What the consent prompt names.</summary>
    public IReadOnlyList<PCellKit> Kits
    {
        get
        {
            lock (_gate)
                return [.. _manifests.Select(m => KitOf(m.Directory, m.Manifest))];
        }
    }

    /// <summary>One declared kit as the consent prompt and the trust policy see it.</summary>
    private PCellKit KitOf(string dir, PCellGeneratorManifest manifest)
    {
        var kit = new PCellKit(dir, manifest.ResolveEntry(dir));
        if (!manifest.IsGdsfactory) return kit;

        string? location = manifest.ResolveKitRoot(dir);
        return kit with
        {
            TrustKey        = TrustKeyOf(dir, manifest),
            Module          = manifest.Module,
            ModuleVersion   = (location is null ? null : GdsfactoryKitDiscovery.InstalledVersion(location)) ?? manifest.ModuleVersion,
            RecordedVersion = manifest.ModuleVersion,
            Location        = location,
            Interpreter     = _moduleInterpreter(manifest.Module!),
        };
    }

    /// <summary>What a declared kit's consent is recorded under — <see cref="PCellKit.TrustKey"/>, without
    /// the rest of <see cref="KitOf"/> (the installed version is a directory scan the gate does not need).</summary>
    private static string TrustKeyOf(string dir, PCellGeneratorManifest manifest)
        => manifest.IsGdsfactory && manifest.ResolveKitRoot(dir) is { } location && AddsNoCodeOfItsOwn(dir, manifest, location)
            ? PCellTrustStore.ResolvedDirectory(location)
            : dir;

    /// <summary>
    /// Whether a gdsfactory kit's declaration runs nothing but the kit (see <see cref="PCellKit.TrustKey"/>):
    /// the entry script is circuitRF's own, and <c>pythonPath</c> is empty (an installed kit) or exactly the
    /// root its source tree imports from. With <c>-P</c> on the interpreter
    /// (<see cref="GdsfactoryInterpreter"/>) the entry script's folder is not on the path, so those are the
    /// only two ways a workspace can put code of its own into the run.
    /// </summary>
    private static bool AddsNoCodeOfItsOwn(string dir, PCellGeneratorManifest manifest, string location)
    {
        if (!KitPCellLibrary.IsGeneratedGdsfactoryEntry(manifest.ResolveEntry(dir), manifest.Module!)) return false;

        var path = manifest.ResolvePythonPath(dir);
        if (path.Count == 0) return true;
        if (path.Count > 1) return false;
        string expected = Path.Combine(path[0], manifest.Module!.Replace('.', Path.DirectorySeparatorChar));
        return string.Equals(PCellTrustStore.Normalize(expected), PCellTrustStore.Normalize(location), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Version notes already given by this resolver — see <see cref="Rescan"/>.</summary>
    private readonly HashSet<string> _versionNoted = new(StringComparer.Ordinal);

    /// <summary>The hint a gdsfactory kit's real parameters carry: the number is the kit's own, passed
    /// through exactly (overview D3), so the editor states whose units it is in.</summary>
    public const string GdsfactoryRealHint = "kit units (µm for dimensions)";

    private IEnumerable<string> CandidateDirectories()
    {
        if (string.IsNullOrWhiteSpace(_rootDirectory) || !Directory.Exists(_rootDirectory)) yield break;

        yield return _rootDirectory;
        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(_rootDirectory); }
        catch { yield break; }

        foreach (string child in children)
        {
            // A generated-cells folder is circuitRF's own cache, not a kit.
            if (string.Equals(Path.GetFileName(child), GeneratedCellStore.ReservedFolderName,
                              StringComparison.OrdinalIgnoreCase)) continue;

            // …and the archive's own kits folder is a container OF kits, not a kit — the one place
            // where the deliberate one-level shallowness would hide something. An unarchived
            // workspace puts each included kit at `kits/<kit>/`, two levels down; leaving that
            // unscanned would mean its generator is never found, the recipient is never asked for
            // permission, and every cell it draws stays a placeholder with nothing said. Still one
            // named folder rather than a general depth increase.
            if (string.Equals(Path.GetFileName(child), CircuitRF.Design.Workspace.WorkspaceFolders.Kits,
                              StringComparison.OrdinalIgnoreCase))
            {
                IEnumerable<string> kits;
                try { kits = Directory.EnumerateDirectories(child); }
                catch { continue; }
                foreach (string kit in kits) yield return kit;
                continue;
            }

            yield return child;
        }
    }

    /// <summary>
    /// Content key per manifest directory, computed once. A directory whose declared sources could
    /// not be hashed gets a PER-SESSION key instead, so its cells regenerate this session rather
    /// than being reused on the strength of a hash nobody could compute.
    /// </summary>
    private readonly Dictionary<string, string> _contentKeys = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, PCellValue>? DeclaredDefaults(string generatorId)
    {
        foreach (var provider in EnsureStarted().Values.Distinct())
            if (provider.DeclaredDefaults(generatorId) is { } defaults) return defaults;
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<PCellParameterInfo>? DeclaredParameters(string generatorId)
    {
        foreach (var provider in EnsureStarted().Values.Distinct())
            if (provider.DeclaredParameters(generatorId) is { } declared)
                return IsGdsfactoryGenerator(generatorId)
                    ? [.. declared.Select(p => p.Kind == PCellValueKind.Real && p.Dimension == PCellDimension.None
                                               ? p with { Hint = GdsfactoryRealHint } : p)]
                    : declared;
        return null;
    }

    /// <summary>Whether <paramref name="generatorId"/> came from a gdsfactory kit's manifest.</summary>
    public bool IsGdsfactoryGenerator(string generatorId)
    {
        lock (_gate)
        {
            EnsureStartedLocked();
            return _kitDirectoryByGeneratorId.TryGetValue(generatorId, out var dir)
                && _manifests.Any(m => m.Directory == dir && m.Manifest.IsGdsfactory);
        }
    }

    public string? ContentKeyFor(string generatorId)
    {
        lock (_gate)
        {
            EnsureStartedLocked();
            return _contentKeyByGeneratorId.GetValueOrDefault(generatorId);
        }
    }

    private readonly Dictionary<string, string> _contentKeyByGeneratorId = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _kitNameByGeneratorId = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _kitDirectoryByGeneratorId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which kit each generator came from, so a kit's cells can be listed under that kit's own name
    /// exactly as its schematic parts already are.
    ///
    /// <para>Starts every declared kit, for the same reason <see cref="KnownGeneratorIds"/> does: a
    /// script's own <c>describe</c> is the only source of its generator list.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> KitNameByGeneratorId
    {
        get
        {
            lock (_gate)
            {
                EnsureStartedLocked();
                return new Dictionary<string, string>(_kitNameByGeneratorId, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Which kit FOLDER each generator came from — the manifest's own directory, which is where a
    /// kit's schematic symbol for that cell would be (see <c>PCellKitSchematicParts</c>).
    ///
    /// <para>Kept beside <see cref="KitNameByGeneratorId"/> rather than derived from it: a kit's
    /// name is its folder's name, so two kits in two workspaces can share one and the name would
    /// then resolve to whichever folder was asked about last.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> KitDirectoryByGeneratorId
    {
        get
        {
            lock (_gate)
            {
                EnsureStartedLocked();
                return new Dictionary<string, string>(_kitDirectoryByGeneratorId, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// The interpreter's path for one kit: circuitRF's OWN Python package first, then whatever the
    /// manifest names.
    ///
    /// <para>circuitRF supplies its own package rather than making a kit say where it is — a manifest
    /// cannot know where circuitRF was installed, and an absolute path written into a shipped kit
    /// makes that kit machine-specific. It goes FIRST because the package and this build are
    /// versioned together (the wire version is a constant in both), so a kit shipping an older copy
    /// of its own must not shadow the one that matches. See <see cref="PCellPythonPackage"/>.</para>
    /// </summary>
    private static IReadOnlyList<string> PythonPathFor(string dir, PCellGeneratorManifest manifest)
    {
        var declared = manifest.ResolvePythonPath(dir);
        if (PCellPythonPackage.RootDirectory is not { } own) return declared;

        var path = new List<string>(declared.Count + 1) { own };
        path.AddRange(declared);
        return path;
    }

    private string ContentKeyOf(string manifestDirectory, PCellGeneratorManifest manifest)
    {
        // A gdsfactory upgrade can change a kit's geometry with the kit itself untouched, so the version
        // the interpreter reports is part of what built the cell (R-gdf2-2c). Appended on every call
        // rather than cached with the rest: StopProviders keeps the cached keys on purpose, and an
        // interpreter re-probed after it may report a different gdsfactory.
        string gfSuffix = _gdsfactoryVersionByDir.TryGetValue(manifestDirectory, out var gf) ? $"+gf{gf}" : "";
        return BaseContentKeyOf(manifestDirectory, manifest) + gfSuffix;
    }

    private string BaseContentKeyOf(string manifestDirectory, PCellGeneratorManifest manifest)
    {
        if (_contentKeys.TryGetValue(manifestDirectory, out var cached)) return cached;

        string key = PCellGeneratorContentHash.Compute(manifestDirectory, manifest, out var problem);
        if (problem is not null)
        {
            _report?.Invoke(problem);
            // Unique to this resolver instance, so nothing generated by a previous session is
            // reused and nothing generated now is trusted next session. Regenerating is a cost;
            // reusing a cell built from source we cannot prove unchanged is a wrong answer.
            key = "session-" + Guid.NewGuid().ToString("N")[..12];
        }

        // circuitRF's own PCell package is part of what built the cell, not just the kit's scripts —
        // see PCellPythonPackage.ContentKey. Appended only when there is one, so a build that cannot
        // locate its own package produces exactly the keys it always did.
        if (PCellPythonPackage.ContentKey is { Length: > 0 } host) key = $"{key}+{host}";

        _contentKeys[manifestDirectory] = key;
        return key;
    }

    public IReadOnlyCollection<string> KnownGeneratorIds => EnsureStarted().Keys;

    public PCellGenerator? Resolve(string generatorId)
        => EnsureStarted().TryGetValue(generatorId, out var provider)
           && provider.TryGetGenerator(generatorId, out var generator)
            ? generator
            : null;

    public string Describe()
    {
        lock (_gate)
            return _manifests.Count == 0
                ? $"No PCell generator manifest ({PCellGeneratorManifest.FileName}) under '{_rootDirectory}'."
                : $"{_manifests.Count} PCell generator manifest(s) under '{_rootDirectory}': " +
                  string.Join(", ", _manifests.Select(m => m.Directory));
    }

    /// <summary>
    /// Starts every declared script and asks each what it offers.
    ///
    /// <para>A script that will not start, or will not describe itself, is REPORTED AND SKIPPED
    /// rather than allowed to fail the lookup: one broken kit must not make the others' cells
    /// unresolvable. The user still finds out, because the reason is reported.</para>
    /// </summary>
    private Dictionary<string, PCellWorkerProvider> EnsureStarted()
    {
        lock (_gate) return EnsureStartedLocked();
    }

    private Dictionary<string, PCellWorkerProvider> EnsureStartedLocked()
    {
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_byGeneratorId is not null) return _byGeneratorId;

            var map = new Dictionary<string, PCellWorkerProvider>(StringComparer.Ordinal);
            var fromGdsfactory = new HashSet<string>(StringComparer.Ordinal);

            // gdsfactory kits first (owner, 2026-10-30): a kit imported both as its own tree and as its
            // gdsfactory package offers many cells under one name in both, and the package's is the one wanted.
            // OrderBy is stable, so kits of one kind keep their order among themselves.
            foreach (var (dir, manifest) in _manifests.OrderBy(m => m.Manifest.IsGdsfactory ? 0 : 1))
            {
                // CONSENT IS CHECKED BEFORE ANYTHING IS LAUNCHED, and this — not the prompt — is what
                // actually enforces it: a prompt that never appeared (headless, a dialog that failed,
                // a workspace switched away mid-question) leaves the decision Unknown, and Unknown
                // does not run. Degrade, never deny: the kit's cells draw as the existing Not Found
                // placeholder and every other kit, every built-in and the design itself are untouched.
                string trustKey = TrustKeyOf(dir, manifest);
                if (_trust is not null && _trust(trustKey) is var decision && decision != PCellTrustDecision.Allowed)
                {
                    _report?.Invoke(decision == PCellTrustDecision.Denied
                        ? $"The generated artwork in '{trustKey}' is not allowed to run on this machine, so its " +
                          "cells draw as placeholders. Settings ▸ Security & Permissions changes the answer."
                        : $"The generated artwork in '{trustKey}' has not been allowed to run yet, so its cells " +
                          "draw as placeholders.");
                    continue;
                }

                string entry = manifest.ResolveEntry(dir);
                if (!File.Exists(entry))
                {
                    _report?.Invoke($"'{Path.Combine(dir, PCellGeneratorManifest.FileName)}' names an " +
                                    $"entry script that is not there: '{entry}'.");
                    continue;
                }

                var interpreter = _findInterpreter(manifest, dir);
                if (interpreter is null)
                {
                    // DEGRADE, NEVER DENY. Every cell this kit generates draws as the existing Not
                    // Found placeholder and the design still opens — the same rule a missing kit, a
                    // missing layout and a foreign document already follow. The user's design is
                    // their data; an interpreter is circuitRF's problem.
                    _report?.Invoke($"The PCell generators in '{dir}' have no interpreter to run in, " +
                                    "so their cells will draw as placeholders.");
                    continue;
                }

                PCellWorkerProvider provider;
                try
                {
                    provider = new PCellWorkerProvider(ProcessPCellWorkerTransport.Start(
                        interpreter.Command, entry, PythonPathFor(dir, manifest), interpreter.Arguments));

                    string contentKey = ContentKeyOf(dir, manifest);
                    string kitName = new PCellKit(dir, entry).Name;
                    foreach (string id in provider.GeneratorIds)
                        if (map.TryAdd(id, provider))
                        {
                            _contentKeyByGeneratorId[id] = contentKey;
                            // Which KIT a cell came from. Recorded here because this is the one place
                            // both facts are in hand at once — a kit's ids are only known once its
                            // script has described itself, and PCellKit deliberately carries no
                            // generator list so that listing kits (for the trust prompt) starts
                            // nothing.
                            _kitNameByGeneratorId[id] = kitName;
                            // …and WHERE that kit is, which is the only thing that can answer
                            // "does this kit ship a schematic symbol for this cell". The name
                            // cannot: two workspaces may hold kits of one name, and a name is not
                            // a path.
                            _kitDirectoryByGeneratorId[id] = dir;
                            if (manifest.IsGdsfactory) fromGdsfactory.Add(id);
                        }
                        else
                            // Two kits offering one id: a gdsfactory kit's wins over any other kind (the
                            // order above); between two of one kind the first found does. Either way the
                            // collision is named, never resolved in silence.
                            _report?.Invoke($"Two PCell generators are both called '{id}'; the one in " +
                                            $"'{map[id].Origin}' is used" +
                                            (fromGdsfactory.Contains(id) && !manifest.IsGdsfactory ? " (a gdsfactory kit's cell is preferred)" : "") +
                                            $" and '{provider.Origin}' is not.");
                }
                catch (PCellWireException ex)
                {
                    _report?.Invoke(ex.Message);
                    continue;
                }

                _providers.Add(provider);
            }

            return _byGeneratorId = map;
        }
    }

    public void Dispose()
    {
        List<PCellWorkerProvider> providers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            providers = [.. _providers];
            _providers.Clear();
            _byGeneratorId = null;
        }

        foreach (var provider in providers) provider.Dispose();
    }
}
