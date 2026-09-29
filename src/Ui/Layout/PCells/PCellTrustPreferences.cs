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
