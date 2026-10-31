// The "Save fields" check box beside each solver's "Save fields at (GHz)" box: unticked writes SaveFieldsGHz: [] (no fields),
// ticked restores the list the box held, and a file or a typed `none` that saves nothing shows it unticked.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Ui.Layout.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class SaveFieldsCheckBoxTests
{
    [Fact]
    public void Unticking_SavesNone_TickingRestoresTheList_AndNoneUnticks()
    {
        var vm = new EmSetupEditorViewModel("x.cem", new EmSetup { Palace = new CemPalace { SaveFieldsGHz = [2, 6] } });
        Assert.True(vm.PalaceSaveFields);

        vm.PalaceSaveFields = false;
        Assert.Empty(vm.Working.Palace!.SaveFieldsGHz!);
        Assert.Equal("none", vm.PalaceSaveFieldsText);

        vm.PalaceSaveFields = true;
        Assert.Equal([2.0, 6.0], vm.Working.Palace!.SaveFieldsGHz!);

        vm.EmergeSaveFieldsText = "none";
        vm.CommitEmergeField("Emerge.SaveFieldsGHz");
        Assert.False(vm.EmergeSaveFields);

        var loaded = new EmSetupEditorViewModel("y.cem", new EmSetup { OpenEms = new CemOpenEms { SaveFieldsGHz = [] } });
        Assert.False(loaded.OpenEmsSaveFields);
    }
}
