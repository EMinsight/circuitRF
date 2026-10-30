// brief-dfr31-5 R-dfr31-5-3 — after a lost GPU context a .c3d opened on "Building the 3D view…" and stayed there until
// the app was restarted. Whatever a first scene's build ends with, the building state ends with it, and the failure is
// said once in Messages.

using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

public class Viewer3DFirstSceneTests
{
    [Fact]
    public void AFirstSceneBuildThatLosesTheGpuContext_EndsBuilding_AndSaysSoInMessages()
    {
        var messages = new List<(string Text, bool Warning)>();
        using var failed = new ManualResetEventSlim(false);
        using var vm = new Viewer3DViewModel("lost.c3d", "lost.c3d", () => null,
            (_, _, _) => throw new Avalonia.Platform.PlatformGraphicsContextLostException(),
            () => new RecordingBackend(), () => null, a => a());
        vm.PostMessage = (text, warning) => messages.Add((text, warning));
        vm.Source.SceneReady += _ => failed.Set();

        vm.Regenerate();
        Assert.True(failed.Wait(TimeSpan.FromSeconds(10)));
        SpinWait.SpinUntil(() => !vm.IsRegenerating, TimeSpan.FromSeconds(5));

        Assert.False(vm.IsBuildingFirstScene);
        var (text, warning) = Assert.Single(messages);
        Assert.True(warning);
        Assert.StartsWith("The 3D view could not build its scene:", text);
    }
}
