using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>brief-tuneopt-4 R-to4-2: the panel follows the focused schematic; switching stops the session.</summary>
public sealed class TuningPanelFollowsFocusTests
{
    private sealed class Sink : ITuneResultSink
    {
        public int Dropped;
        public void Publish(DataSet data) { }
        public void Commit(DataSet data, IReadOnlyDictionary<string, string> values) { }
        public void Drop() => Dropped++;
        public void Snapshot() { }
        public void ClearSnapshot() { }
    }

    internal static string WorkspaceTuningSource([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md"))) dir = Path.GetDirectoryName(dir);
        return File.ReadAllText(Path.Combine(dir!, "src/Ui/ViewModels/WorkspaceViewModel.Tuning.cs"));
    }

    [Fact]
    public void Schematic_Populates_AnythingElse_Clears()
    {
        var f = new TuningPanelFixture();
        f.Panel.SetTuned("R1.R", true);
        Assert.True(f.Panel.HasSchematic);
        Assert.Equal("tb.csch", f.Panel.HeaderLabel);
        Assert.Single(f.Panel.Rows);

        f.Panel.SetActiveSchematic(null, null);         // a layout, a 3D view, the welcome tab
        Assert.False(f.Panel.HasSchematic);
        Assert.Empty(f.Panel.Rows);

        // The routing: a schematic sets its TOP frame, anything else clears — except a Data Display while
        // a session runs, which is where its results are being watched.
        string src = WorkspaceTuningSource();
        Assert.Contains("panel.SetActiveSchematic(sd.NavFrames[0].Session", src);
        Assert.Contains("case DataDisplayDocument when panel.IsRunning:", src);
        Assert.Contains("panel.SetActiveSchematic(null, null);", src);
    }

    [Fact]
    public void SwitchingSchematic_StopsTheSession()
    {
        var f = new TuningPanelFixture();
        f.Panel.SetTuned("R1.R", true);
        var sink = new Sink();
        f.Panel.CreateSession = (_, _) => new TuneSession((_, _, _) =>
            new CircuitRF.Design.Circuit.RunResult(CircuitRF.Design.Circuit.RunStatus.Success, "ok",
                grouped: TuningDisplayFixture.Results(1)), sink, a => a());
        f.Panel.StartCommand.Execute(null);
        var session = f.Panel.Session;
        Assert.True(f.Panel.IsRunning);

        f.Panel.SetActiveSchematic(new SchematicViewModel(new CircuitRF.Design.Schematic.SchematicEditModel()), "other.csch");

        Assert.False(f.Panel.IsRunning);
        Assert.Equal(1, sink.Dropped);                  // Stop drops the published result
        session!.Request(new Dictionary<string, string> { ["R1.R"] = "70 Ohm" });
        Assert.Equal(1, session.Evaluations);           // a stopped session evaluates nothing more
    }
}
