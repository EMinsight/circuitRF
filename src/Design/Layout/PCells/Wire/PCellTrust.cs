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
    /// reading. Reversing a refusal is what the Settings "forget" action is for.
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
