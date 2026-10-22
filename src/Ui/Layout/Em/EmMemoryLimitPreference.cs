// brief-emi-10 R-emi10-4 — the user's memory limit for a 3D solver: STORED here, in AppPreferences, and set in Settings ▸
// Solvers. EmSolveCorePreference's shape and reasoning: a property of the MACHINE and not of the design, so never in the
// .cem/.c3d, and handed to the run as an ARGUMENT — the run service lives in CircuitRF.Design and cannot read this file, and
// a headless run says what it means with `em --memory-limit`.

using CircuitRF.Design.Em3d;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>The stored limit, GB. <b>Null means Automatic</b>: this machine's memory.</summary>
public static class EmMemoryLimitPreference
{
    /// <summary>The stored limit, or null for Automatic — and null for a stored value this machine refuses (one written on a
    /// machine with more memory), which is then simply Automatic rather than a limit the run would ignore.</summary>
    public static double? Preferred
    {
        get => AppPreferencesIo.Load().EmMemoryLimitGb is { } gb && Em3dMemoryLimit.Refusal(gb, MachineMemory.PhysicalBytes) is null
            ? gb : null;
    }

    /// <summary>Stores <paramref name="gb"/> (null: Automatic). Null when stored; otherwise the refusal, and nothing is stored —
    /// the sentence <c>em --memory-limit</c> refuses the same value with.</summary>
    public static string? TrySet(double? gb)
    {
        if (gb is { } g && Em3dMemoryLimit.Refusal(g, MachineMemory.PhysicalBytes) is { } refused) return refused;
        AppPreferencesIo.Update(p => p.EmMemoryLimitGb = gb);
        return null;
    }

    /// <summary>The ceiling a run is handed, bytes; null for Automatic.</summary>
    public static long? CeilingBytes => Em3dMemoryLimit.Bytes(Preferred);

    /// <summary>What the box shows greyed when it is empty: this machine's memory in GB ("17.2"), the limit Automatic applies.
    /// The box holds a number beside a GB label, so the placeholder is the number alone.</summary>
    public static string AutomaticText
        => (MachineMemory.PhysicalBytes / Em3dMemoryLimit.BytesPerGb).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
