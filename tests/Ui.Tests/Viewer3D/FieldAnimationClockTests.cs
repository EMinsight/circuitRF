using System;
using System.Collections.Generic;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

/// <summary>
/// brief-idle-power — a playing field is paced by the frame clock of the pane showing it, and by nothing while no pane shows
/// it: its tab in the background or its document closed. (A minimised or covered window's clock is Avalonia's to stop: it
/// stops rendering, and so stops calling back.) Counted, never timed.
/// </summary>
public sealed class FieldAnimationClockTests
{
    private static Viewer3DViewModel NewViewer()
        => new("model.c3d", "model", () => null, (_, _, _) => throw new InvalidOperationException(),
               () => new RecordingBackend(), () => null, a => a());

    [Fact]
    public void AShownView_TicksOnItsFrameClock_AndAHiddenOneHasNothingRunning()
    {
        using var vm = NewViewer();
        var pending = new Queue<Action>();
        vm.ShowIn(pending.Enqueue);

        vm.FieldPlaying = true;
        Assert.True(vm.AnimationRunning);
        for (int frame = 0; frame < 3; frame++)
        {
            Assert.Single(pending);                               // one callback asked for per frame, never more
            pending.Dequeue()();
        }

        vm.ShowIn(null);                                          // the tab went to the background
        Assert.False(vm.AnimationRunning);
        pending.Dequeue()();                                      // the frame already asked for arrives, and asks for none
        Assert.Empty(pending);

        vm.ShowIn(pending.Enqueue);                               // shown again, still playing: it resumes
        Assert.True(vm.AnimationRunning);
        Assert.Single(pending);

        vm.FieldPlaying = false;
        Assert.False(vm.AnimationRunning);
        pending.Dequeue()();
        Assert.Empty(pending);
    }
}

/// <summary>brief-idle-power — a legend's colour bar is made once per colour map, not once per render.</summary>
public sealed class LegendBrushTests
{
    [Fact]
    public void TheColourBar_IsTheSameInstanceOnEveryRender()
    {
        foreach (var map in CircuitRF.Render.Scene3D.ColorMap3D.All)
            Assert.Same(Viewer3DOverlay.LegendBar(map), Viewer3DOverlay.LegendBar(map));
        Assert.NotSame(Viewer3DOverlay.LegendBar(CircuitRF.Render.Scene3D.ColorMap3D.Viridis),
                       Viewer3DOverlay.LegendBar(CircuitRF.Render.Scene3D.ColorMap3D.Inferno));
    }
}
