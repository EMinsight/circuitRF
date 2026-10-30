namespace CircuitRF.Design.Layout.PCells.Wire;

/// <summary>
/// Which Python environment runs each gdsfactory kit on THIS machine — a per-user choice keyed by the
/// kit's module name, never written into a workspace (brief-gdf-2 R-gdf2-2b, overview D4). A
/// colleague who opens the workspace is asked for theirs and is never handed a path from someone
/// else's machine.
///
/// <para>Recorded by the application (<c>AppPreferences.GdsfactoryInterpreters</c>, the settings
/// store, which stays in <c>src/Ui</c>); read here, read-only, the way
/// <see cref="PCellTrustStore.FromRecordedPreferences"/> reads the trust decisions — the file is
/// per-user state and crosses no firewall. A headless run honours what the user chose and never
/// writes.</para>
/// </summary>
public static class GdsfactoryInterpreterPreferences
{
    /// <summary>The <c>preferences.json</c> key the choices are recorded under.</summary>
    public const string PreferenceKey = "gdsfactory_interpreters";

    /// <summary>Module name → interpreter, as recorded. A missing, truncated or wrong-shaped file is an
    /// empty table — never a throw on the path that resolves a layout.</summary>
    public static IReadOnlyDictionary<string, string> FromRecordedPreferences()
        => FromPreferencesFile(UserStateDirectory.PreferencesPath);

    /// <summary>The table as <paramref name="path"/> records it — the seam the tests read a file through.</summary>
    internal static IReadOnlyDictionary<string, string> FromPreferencesFile(string path)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return table;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty(PreferenceKey, out var rows)
                && rows.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var row in rows.EnumerateObject())
                    if (row.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        && row.Value.GetString() is { Length: > 0 } interpreter)
                        table[row.Name] = interpreter;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return table;
    }

    /// <summary>The recorded interpreter for <paramref name="module"/>, or null.</summary>
    public static string? Recorded(string module)
        => FromRecordedPreferences().TryGetValue(module, out var interpreter) ? interpreter : null;

    /// <summary>
    /// What a run with no interpreter for <paramref name="module"/> says. Names <c>--python</c> because
    /// that is the one thing a headless caller can do about it.
    /// </summary>
    public static string NoInterpreterSentence(string module)
        => $"No Python environment has been chosen on this machine for the gdsfactory kit '{module}', so " +
           "its cells draw as placeholders. circuitRF asks once when the kit is opened in the application; " +
           "a headless run names one with --python <interpreter>.";
}
