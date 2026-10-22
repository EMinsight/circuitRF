// brief-emi-9 R-emi9-3 — where an EMerge run factorises, Automatic, CPU or GPU: STORED here, in AppPreferences, and shown in
// Settings ▸ Solvers on EMerge's row. EmPalaceDevicePreference's shape and reasoning: a property of the MACHINE, never in the
// .cem/.c3d, and handed to the run as an ARGUMENT — `em --device` says the same headlessly.

using CircuitRF.Design.Em3d;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>The stored device. Absent, or anything but <c>cpu</c> or <c>gpu</c>, is <see cref="EmergeDevice.Auto"/>.</summary>
public static class EmEmergeDevicePreference
{
    /// <summary>Test seam, as <see cref="EmPalaceDevicePreference"/>'s.</summary>
    internal static EmergeDevice TestOverrideStore;
    internal static bool         TestOverrideActive;

    /// <summary>What a stored value means: <c>cpu</c> or <c>gpu</c>, anything else (absent included) Automatic.</summary>
    public static EmergeDevice FromStored(string? stored) => EmergeDevices.Parse(stored) ?? EmergeDevice.Auto;

    /// <summary>What is stored for <paramref name="device"/>: <c>cpu</c> or <c>gpu</c>, or nothing at all for Automatic.</summary>
    public static string? ToStored(EmergeDevice device) => device == EmergeDevice.Auto ? null : device.Id();

    public static EmergeDevice Preferred
    {
        get => TestOverrideActive ? TestOverrideStore : FromStored(AppPreferencesIo.Load().EmEmergeDevice);
        set
        {
            if (TestOverrideActive) TestOverrideStore = value;
            else AppPreferencesIo.Update(p => p.EmEmergeDevice = ToStored(value));
        }
    }
}
