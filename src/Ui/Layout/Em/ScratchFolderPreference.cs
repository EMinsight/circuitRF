// Settings ▸ Solvers ▸ Scratch folder — STORED here, in AppPreferences, per machine, and installed into
// CircuitRF.Design.ScratchFolder, which every consumer reads. See that type for why it is process-wide.

using CircuitRF.Design;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout.Em;

public static class ScratchFolderPreference
{
    /// <summary>Stores <paramref name="folder"/> (null: the system temp folder) and makes it the one this process
    /// uses from the next run on.</summary>
    public static void Set(string? folder)
    {
        string? value = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        AppPreferencesIo.Update(p => p.ScratchFolder = value);
        ScratchFolder.Chosen = value;
    }

    /// <summary>The system temp folder, which is what an unset scratch folder means.</summary>
    public static string SystemDefault => System.IO.Path.GetTempPath();
}
