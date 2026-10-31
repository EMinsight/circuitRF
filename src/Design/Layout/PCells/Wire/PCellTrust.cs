namespace CircuitRF.Design.Layout.PCells.Wire;

/// <summary>What this installation has decided about one kit's generator scripts.</summary>
public enum PCellTrustDecision
{
    /// <summary>Nobody has been asked. Nothing runs — see <see cref="PCellWorkerResolver"/>.</summary>
    Unknown,
    Allowed,
    Denied,
}

/// <summary>
/// Which kits' generator scripts this installation has agreed to run.
///
/// <para><b>Recorded PER USER, not inside the workspace — and that is a deliberate departure from the
/// plan's "recorded per workspace" wording, for a reason worth stating plainly.</b> A decision stored
/// in a file that travels with the artifact can be written by whoever sends you the artifact: a
/// workspace arriving with its own scripts already marked trusted would run them on open with no
/// prompt, which defeats the entire mechanism. Consent is a property of the person at the keyboard,
/// so it lives in this installation's own preferences and is never serialized into <c>.cws</c>.</para>
///
/// <para><b>Keyed by the kit's directory, not by its content.</b> Hashing the scripts into the key
/// would re-ask on every save while somebody is authoring a generator — training exactly the reflexive
/// "Allow" this exists to prevent. Moving a kit to a new path asks again, which is the honest answer:
/// it is a different thing on disk.</para>
/// </summary>
public sealed class PCellTrustStore
{
    private readonly Dictionary<string, bool> _decisions;
    private readonly Action<IReadOnlyDictionary<string, bool>>? _persist;

    /// <param name="seed">Decisions already recorded. Keys are normalized on the way in, so a store
    /// loaded from disk answers the same as one just written.</param>
    /// <param name="persist">Where a new decision goes. Null keeps it in memory only — for a headless
    /// caller and for tests, which must not write into the developer's own preferences.</param>
    public PCellTrustStore(
        IEnumerable<KeyValuePair<string, bool>>? seed = null,
        Action<IReadOnlyDictionary<string, bool>>? persist = null)
    {
        _decisions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        _persist = persist;

        if (seed is null) return;
        foreach (var (path, allowed) in seed)
            if (Normalize(path) is { Length: > 0 } key) _decisions[key] = allowed;
    }

    /// <summary>
    /// The decisions this installation has already RECORDED, read-only — what a headless run honours
    /// (brief-generated-cells-2 R-gc2-2). Reads <see cref="UserStateDirectory.PreferencesPath"/> by the
    /// one key the settings store writes, the way <c>RevisionIdentity.FromPreferences</c> reads the
    /// commit identity: the file is per-user state and crosses no firewall; the dialog that edits it
    /// does, and stays in <c>src/Ui</c>.
    ///
    /// <para><b>Nothing is ever written back.</b> A headless run cannot ask, so it can only honour a
    /// decision somebody made at the keyboard, or one granted for that run alone. A missing, truncated
    /// or wrong-shaped file is an absent one — never a throw on the path that resolves a layout.</para>
    /// </summary>
    public static PCellTrustStore FromRecordedPreferences()
    {
        var seed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = UserStateDirectory.PreferencesPath;
            if (File.Exists(path))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(PreferenceKey, out var table)
                    && table.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var row in table.EnumerateObject())
                        if (row.Value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                            seed[row.Name] = row.Value.GetBoolean();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return new PCellTrustStore(seed, persist: null);
    }

    /// <summary>The <c>preferences.json</c> key the application records decisions under.</summary>
    public const string PreferenceKey = "pcell_trust";

    public PCellTrustDecision Decide(string manifestDirectory)
        => _decisions.TryGetValue(Normalize(manifestDirectory), out bool allowed)
            ? allowed ? PCellTrustDecision.Allowed : PCellTrustDecision.Denied
            : PCellTrustDecision.Unknown;

    /// <summary>
    /// Records a decision and writes it. <b>A refusal is recorded too</b> — otherwise every open
    /// re-asks about the same kit, and a prompt that nags is a prompt people learn to dismiss without
    /// reading. Reversing a refusal is what Settings ▸ Security &amp; Permissions is for, one kit at a time.
    /// </summary>
    public void Record(string manifestDirectory, bool allowed)
    {
        string key = Normalize(manifestDirectory);
        if (key.Length == 0) return;

        _decisions[key] = allowed;
        // Failing to persist must not undo having decided: the session's own answer stands, and the
        // only cost is being asked again next time — which errs toward asking, not toward running.
        try { _persist?.Invoke(_decisions); } catch { /* preferences are best-effort by design */ }
    }

    public int Count => _decisions.Count;

    /// <summary>
    /// Replaces every decision with <paramref name="decisions"/> and persists nothing — what an open
    /// workspace does after Settings changed one kit's answer, so the gate its resolver already holds
    /// (this instance's <see cref="Decide"/>) answers from the new record without being rebuilt.
    /// </summary>
    public void ReplaceAll(IEnumerable<KeyValuePair<string, bool>> decisions)
    {
        _decisions.Clear();
        foreach (var (path, allowed) in decisions)
            if (Normalize(path) is { Length: > 0 } key) _decisions[key] = allowed;
    }

    /// <summary>
    /// <paramref name="directory"/> with every symbolic link along it resolved, then
    /// <see cref="Normalize"/>d — the key a gdsfactory kit's consent is recorded under (brief-gdf-2
    /// R-gdf2-2a). An installed kit is routinely reached through a link (an environment's
    /// <c>lib64</c>, a shared tool area), and the link and its target are one kit on disk, so they are
    /// one decision; two environments are two directories and two decisions.
    ///
    /// <para>Not folded into <see cref="Normalize"/> itself: every decision already recorded for a
    /// <c>cni</c> kit is keyed on the path as written, and resolving those now would quietly turn a
    /// recorded answer into a question.</para>
    /// </summary>
    public static string ResolvedDirectory(string directory)
    {
        string full = Normalize(directory);
        try { return full.Length == 0 ? full : Normalize(Resolve(full, hops: 0)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return full;
        }
    }

    /// <summary>
    /// Walks <paramref name="full"/> one component at a time; at a link, starts again from the link's
    /// target plus whatever was left — a target can itself run through a link (macOS's <c>/var</c> is
    /// <c>/private/var</c>), so resolving only the last hop leaves two spellings of one directory.
    /// Bounded, so a link loop costs the unresolved form rather than a hang.
    /// </summary>
    private static string Resolve(string full, int hops)
    {
        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || hops > 40) return full;

        string[] parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        string current = root;
        for (int i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (new DirectoryInfo(current).LinkTarget is not { } target) continue;

            string resolved = Path.GetFullPath(target, Path.GetDirectoryName(current) ?? root);
            string rest = string.Join(Path.DirectorySeparatorChar, parts[(i + 1)..]);
            return Resolve(rest.Length == 0 ? resolved : Path.Combine(resolved, rest), hops + 1);
        }
        return current;
    }

    /// <summary>Absolute, separator-normalized, no trailing separator — so the same directory named
    /// two ways is one entry rather than two.</summary>
    public static string Normalize(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "";
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch { return directory.Trim(); }
    }
}
