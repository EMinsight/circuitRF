using System.IO;
using CircuitRF.Design;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

/// <summary>
/// Settings ▸ Solvers ▸ Scratch folder (owner request, 2026-10-28). Asked through <see cref="ScratchFolder.RefusalFor"/>
/// so the process's own folder is never changed under a run another test has going.
/// </summary>
public class ScratchFolderTests
{
    /// <summary>A chosen folder that is not there — an external drive not connected — is a refusal naming it, never a quiet
    /// return to the startup disk; unset and an existing folder are both fine.</summary>
    [Fact]
    public void AMissingChosenFolder_IsRefused_ByName()
    {
        string missing = Path.Combine(Path.GetTempPath(), "crf-no-such-scratch-" + System.Guid.NewGuid().ToString("N"));
        Assert.Contains(missing, ScratchFolder.RefusalFor(missing));
        Assert.Null(ScratchFolder.RefusalFor(null));
        Assert.Null(ScratchFolder.RefusalFor(Path.GetTempPath()));
    }

    /// <summary>The command line reads the folder the Settings dialog wrote, under the dialog's own key; a file that names
    /// none, or cannot be read as JSON, is no setting (the system temp folder) rather than an error.</summary>
    [Fact]
    public void TheCommandLine_ReadsTheSettingsDialogsKey()
    {
        string file = Path.Combine(Path.GetTempPath(), "crf-prefs-" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, "{\"" + ScratchFolder.PreferenceKey + "\": \"/Volumes/SSD/scratch\", \"em_max_cores\": 8}");
            Assert.Equal("/Volumes/SSD/scratch", ScratchFolder.FromPreferences(file));
            File.WriteAllText(file, "{\"em_max_cores\": 8}");
            Assert.Null(ScratchFolder.FromPreferences(file));
            File.WriteAllText(file, "{ truncated");
            Assert.Null(ScratchFolder.FromPreferences(file));
        }
        finally { File.Delete(file); }
    }
}
