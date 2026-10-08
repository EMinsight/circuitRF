using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>brief-tuneopt-3 R-to3-6: publications during a redraw coalesce to one more redraw, for the newest.</summary>
public sealed class FrameCoalescingTests
{
    [Fact]
    public async Task FivePublishesDuringOneRedraw_OneFurtherRedrawFourSkips()
    {
        var dir = TuningDisplayFixture.TempDir();
        try
        {
            var path = TuningDisplayFixture.WriteResults(dir, level: 0);
            Action? endFrame = null;
            var lib = new DataSourceLibraryViewModel { FrameScheduler = a => endFrame = a };
            await lib.LoadFileAsync(path);

            lib.Publish(path, TuningDisplayFixture.Results(1));        // starts drawing
            for (int i = 2; i <= 6; i++)
                lib.Publish(path, TuningDisplayFixture.Results(i));    // arrive while it draws
            Assert.Equal((1L, 4L), (lib.PublishedRedraws, lib.SkippedFrames));

            var first = endFrame!; endFrame = null;
            first();                                                   // frame done → the newest draws
            endFrame!();
            Assert.Equal((2L, 4L), (lib.PublishedRedraws, lib.SkippedFrames));
            Assert.Equal(6, lib.Entries.Single().Data!["Gain"].RealValues[0]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>The optimizer's finish publishes its best point and writes the file in one breath. When that
    /// last frame arrives mid-draw it is the one the file now holds, so keeping the publication as the file
    /// must SHOW it — it used to be dropped, leaving the display on an earlier, worse point.</summary>
    [Fact]
    public async Task KeepAsFile_WhileTheLastFrameWaits_ShowsThatFrame()
    {
        var dir = TuningDisplayFixture.TempDir();
        try
        {
            var path = TuningDisplayFixture.WriteResults(dir, level: 0);
            var lib = new DataSourceLibraryViewModel { FrameScheduler = _ => { } };   // the first frame never ends
            await lib.LoadFileAsync(path);

            lib.Publish(path, TuningDisplayFixture.Results(1));
            lib.Publish(path, TuningDisplayFixture.Results(7));        // waits behind frame 1
            lib.KeepPublishedAsFile(path);

            Assert.Equal(7, lib.Entries.Single().Data!["Gain"].RealValues[0]);
            Assert.False(lib.IsPublished(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
