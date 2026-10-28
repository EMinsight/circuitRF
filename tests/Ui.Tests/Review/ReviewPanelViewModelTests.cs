// The Review panel (brief-review-6 §2): a threshold edit writes only that threshold, Reset removes it, Accept writes
// the right kind of acceptance for a review finding and for an impedance finding, selecting a finding zooms the
// canvas to its marker, and a cancelled run shows the finished checks and marks the rest cancelled.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Review;
using CircuitRF.Engine;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Review;

namespace CircuitRF.Ui.Tests.Review;

public class ReviewPanelViewModelTests
{
    private static readonly LayerKey Top = new(1, 0);

    private static long Um(double um) => (long)Math.Round(um * LayoutUnits.DefaultDbuPerMicron);

    private static (LayoutEditorViewModel Vm, ReviewPanelViewModel Panel) Open(LayoutView? view = null)
    {
        var vm = new LayoutEditorViewModel(view ?? new LayoutView { DisplayUnit = LayoutUnit.Mm });
        var panel = new ReviewPanelViewModel();
        panel.SetEditor(vm);
        return (vm, panel);
    }

    private static ReviewFinding Finding(string check, double x, string? key = null)
    {
        var box = new Bbox(Um(x), 0, Um(x + 300), Um(300));
        return new ReviewFinding(check, Top, ReviewSeverity.Warning, $"{check} at {x}", "0.3 mm",
                                 [[box.MinX, box.MinY, box.MaxX, box.MinY, box.MaxX, box.MaxY, box.MinX, box.MaxY]], box,
                                 key ?? ReviewFinding.KeyOf(check, Top, box)) { LayerName = "Top" };
    }

    private static ReviewCheckRow Ran(string id, int n) => new(id, LayoutReview.FindCheck(id)!.Title, ReviewCheckState.Ran, null, n, 0);

    [Fact]
    public void EditingAThreshold_WritesOnlyThatThreshold_AndResetRemovesIt()
    {
        var (vm, panel) = Open();
        var check = panel.Checks.Single(c => c.Def.Id == "edge-copper");

        check.Thresholds[0].Text = "0.5";   // the layout's display unit, mm

        Assert.True(vm.IsDirty);
        var saved = Assert.Single(vm.Model.Review!.Thresholds!);
        Assert.Equal("edge-copper.pullback", saved.Key);
        Assert.Equal(Um(500), saved.Value);
        Assert.True(check.IsModified);

        check.ResetCommand.Execute(null);

        Assert.Null(vm.Model.Review);
        Assert.False(check.IsModified);
        Assert.Equal("0.25", check.Thresholds[0].Text);
    }

    [Fact]
    public void TheMmicSwitch_ListsOnlyTheChecksOfADie_AndIsSavedOnTheLayout()
    {
        var (vm, panel) = Open();
        Assert.True(panel.IsPcb);

        panel.IsMmic = true;

        Assert.Equal(["ground-pin-via", "coupled-lines", "probe-pads", "impedance"], panel.Checks.Select(c => c.Def.Id));
        Assert.Equal(ReviewTarget.Mmic, vm.Model.Review?.Target);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void Accept_WritesAReviewAcceptance_ForAReviewFinding()
    {
        var (vm, panel) = Open();
        var f = Finding("via-in-pad", 0);
        vm.ReviewResult = new LayoutReviewResult([Ran("via-in-pad", 1)], [f], "t", [], false);

        var row = panel.Rows.OfType<ReviewFindingResultRow>().Single();
        panel.SetSelectedRows([row]);
        panel.BeginAcceptCommand.Execute(null);
        panel.AcceptReason = "Filled and capped";
        panel.ConfirmAcceptCommand.Execute(null);

        var a = Assert.Single(vm.Model.ReviewAcceptances);
        Assert.Equal(f.Key, a.Key);
        Assert.Empty(vm.Model.ImpedanceAcceptances);
        // Moved to ACCEPTED with no re-run.
        Assert.True(panel.Rows.OfType<ReviewFindingResultRow>().Single().IsAccepted);
    }

    [Fact]
    public void Accept_WritesImpedanceAcceptances_ForAnImpedanceFinding()
    {
        var (vm, panel) = Open();
        var issue = new TraceIssue(TraceIssueKind.ReturnBroken, IssueSeverity.Fail, 0, 0, Um(200), 0, "broken");
        var trace = new TraceRun
        {
            Id = "T1", Layer = Top, LayerName = "Top", Verdict = TraceVerdict.Fail, Issues = [issue],
            WidthMin = Um(100), WidthMax = Um(100), Z0Min = 48, Z0Max = 52,
            StartX = Um(-1000), StartY = 0, EndX = Um(1000), EndY = 0,
        };
        var report = new TraceImpedanceReport
        {
            TargetOhms = 50, TolerancePercent = 10, WarningPercent = 20,
            Layers = [new TraceLayerResult { Layer = Top, Name = "Top", Traces = [trace] }],
        };
        string key = TraceImpedanceAcceptance.TraceKeyOf("Top", trace.StartX, trace.StartY, trace.EndX, trace.EndY, report.DbuPerMicron);
        vm.ReviewResult = new LayoutReviewResult([Ran("impedance", 1)], [Finding("impedance", 0, key)], "t", [], false) { Impedance = report };

        var row = panel.Rows.OfType<ReviewFindingResultRow>().Single();
        panel.SetSelectedRows([row]);
        panel.BeginAcceptCommand.Execute(null);
        panel.AcceptReason = "Return is through the shield can";
        panel.ConfirmAcceptCommand.Execute(null);

        var a = Assert.Single(vm.Model.ImpedanceAcceptances);
        Assert.Equal(TraceImpedanceAcceptance.KeyOf(trace, issue, report.DbuPerMicron), a.Key);
        Assert.Empty(vm.Model.ReviewAcceptances);
        Assert.True(panel.Rows.OfType<ReviewFindingResultRow>().Single().IsAccepted);
    }

    [Fact]
    public void SelectingAFinding_RequestsAZoomToItsMarker()
    {
        var (vm, panel) = Open();
        var f = Finding("via-in-pad", 5000);
        vm.ReviewResult = new LayoutReviewResult([Ran("via-in-pad", 1)], [f], "t", [], false);
        Bbox? zoomed = null;
        vm.ZoomToRegionRequested += b => zoomed = b;

        panel.SelectedRow = panel.Rows.OfType<ReviewFindingResultRow>().Single();

        Assert.Equal(vm.ReviewZoomRegion(f), zoomed);
        Assert.True(zoomed!.Value.MinX < f.Marker.MinX && zoomed.Value.MaxX > f.Marker.MaxX);   // with margin
        Assert.Equal(f.Key, vm.SelectedReviewFinding?.Key);
    }

    [Fact]
    public async Task ACancelledRun_ShowsTheFinishedChecks_AndMarksTheRestCancelled()
    {
        var view = new LayoutView { DisplayUnit = LayoutUnit.Mm };
        view.Shapes.Add(new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Um(1000), Y2 = Um(1000) });
        var tech = new Technology
        {
            Name = "review",
            Layers = [new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" }],
            Stackup = new Stackup
            {
                Layers = [new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = Um(35), DrawingLayers = [Top] }],
            },
        };
        var (vm, panel) = Open(view);
        vm.Technology = tech;

        // Cancelled as the second check starts: the first finished, every later one did not.
        using var cts = new CancellationTokenSource();
        var control = new RunControl { Token = cts.Token, Progress = new Cancelling(cts) };
        await vm.RunReviewAsync(control);

        var checks = panel.Rows.OfType<ReviewCheckResultRow>().ToList();
        Assert.Equal(LayoutReview.ChecksFor(ReviewTarget.Pcb).Count, checks.Count);
        Assert.False(checks[0].IsCancelled);
        Assert.True(checks[^1].IsCancelled);
        Assert.Equal("cancelled", checks[^1].CountText);
        Assert.True(vm.ReviewResult!.Cancelled);
    }

    private sealed class Cancelling(CancellationTokenSource cts) : IProgress<RunProgress>
    {
        public void Report(RunProgress value)
        {
            if (value.Stage.Contains("(2 of", StringComparison.Ordinal)) cts.Cancel();
        }
    }
}
