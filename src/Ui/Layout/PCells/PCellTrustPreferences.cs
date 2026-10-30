using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout.PCells;

// The per-USER half of PCellTrustStore, which stayed here when the store itself crossed the UI
// firewall (brief-generated-cells-2 R-gc2-1): a preference is an ARGUMENT below the firewall
// (src/Design/CLAUDE.md), and AppPreferencesIo — the settings dialog's store — is src/Ui's business.

/// <summary>The store the application uses: this installation's own preferences.</summary>
public static class PCellTrustStores
{
    public static PCellTrustStore UserLocal()
        => new(PCellTrustPreferences.Load(), PCellTrustPreferences.Save);
}

/// <summary>The preferences half of <see cref="PCellTrustStore"/>, kept separate so the store itself
/// has no dependency on where decisions are kept and stays trivially testable.</summary>
public static class PCellTrustPreferences
{
    public static IReadOnlyDictionary<string, bool> Load()
        => AppPreferencesIo.Load().PCellTrust ?? new Dictionary<string, bool>();

    public static void Save(IReadOnlyDictionary<string, bool> decisions)
        => AppPreferencesIo.Update(p => p.PCellTrust =
            decisions.Count == 0 ? null : new Dictionary<string, bool>(decisions));

    /// <summary>Drops every recorded decision, so circuitRF asks about each kit again. The one way
    /// back from a refusal — reachable from Settings.</summary>
    public static void Forget() => AppPreferencesIo.Update(p => p.PCellTrust = null);

    public static int RememberedCount() => AppPreferencesIo.Load().PCellTrust?.Count ?? 0;
}

/// <summary>The per-user gdsfactory interpreter choice (brief-gdf-2 R-gdf2-2b): module name → the
/// interpreter that runs it on this machine. The read half for headless callers is
/// <see cref="GdsfactoryInterpreterPreferences"/> in <c>src/Design</c>; this is the half that writes.</summary>
public static class GdsfactoryInterpreterChoices
{
    public static string? For(string module)
        => AppPreferencesIo.Load().GdsfactoryInterpreters?.GetValueOrDefault(module);

    public static void Record(string module, string interpreter)
        => AppPreferencesIo.Update(p =>
        {
            p.GdsfactoryInterpreters ??= new Dictionary<string, string>(StringComparer.Ordinal);
            p.GdsfactoryInterpreters[module] = interpreter;
        });
}
