using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Core;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Close all / other / left / right tabs ask ONCE, and close prompts never stack (GitHub #6).
//
//  The defect: CircuitRfDockFactory.CloseDockable was async void, and Dock's bulk closes loop over it
//  without awaiting — so every dirty tab opened its own modal Save dialog on the same owner at the
//  same time. The dialogs disabled one another and nothing could be clicked; on macOS the whole
//  window server froze until a forced restart.
//
//  The prompts here are TaskCompletionSources the test answers, standing in for the modal dialogs:
//  "pending" is a dialog on screen. With no SynchronizationContext the factory's continuation runs
//  inline on SetResult, so every assertion after an answer sees the finished close.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class BulkTabCloseTests
{
    private static (CircuitRfDockFactory f, List<StubDocument> docs) FactoryWith(int count)
    {
        var f = new CircuitRfDockFactory();
        var root = f.CreateLayout();
        f.InitLayout(root);
        f.RemoveWelcomeStub();
        var docs = Enumerable.Range(0, count)
            .Select(i => new StubDocument($"doc{i}", StubDocument.StubKind.Schematic))
            .ToList();
        foreach (var d in docs) f.OpenDocument(d);
        return (f, docs);
    }

    private static bool IsOpen(IDockable d) =>
        d.Owner is IDock { VisibleDockables: { } tabs } && tabs.Contains(d);

    private sealed class InlineContinuations : IDisposable
    {
        private readonly SynchronizationContext? _saved = SynchronizationContext.Current;
        public InlineContinuations() => SynchronizationContext.SetSynchronizationContext(null);
        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_saved);
    }

    [Fact]
    public void CloseAll_AsksOnce_ClosesNothingUntilAnswered_ThenOnlyWhatTheAnswerAllows()
    {
        using var _ = new InlineContinuations();
        var (f, docs) = FactoryWith(3);
        var calls  = new List<IReadOnlyList<IDockable>>();
        var answer = new TaskCompletionSource<IReadOnlyCollection<IDockable>>();
        f.CloseDockablesConfirm = t => { calls.Add(t); return answer.Task; };
        f.CloseDockableConfirm  = _ => throw new Xunit.Sdk.XunitException("bulk close must not prompt per tab");

        f.CloseAllDockables(docs[1]);

        Assert.Equal(docs, Assert.Single(calls));
        Assert.True(f.IsClosePromptOpen);
        Assert.All(docs, d => Assert.True(IsOpen(d)));

        // Save All with a backed-out picker on doc1: the saved and clean tabs close, doc1 stays.
        answer.SetResult([docs[0], docs[2]]);

        Assert.False(f.IsClosePromptOpen);
        Assert.False(IsOpen(docs[0]));
        Assert.True(IsOpen(docs[1]));
        Assert.False(IsOpen(docs[2]));
    }

    [Fact]
    public void AFailingBulkPrompt_LeavesTheTabsOpen_AndReleasesTheGuard()
    {
        using var _ = new InlineContinuations();
        var (f, docs) = FactoryWith(2);
        f.CloseDockablesConfirm = _ => Task.FromException<IReadOnlyCollection<IDockable>>(new InvalidOperationException());

        f.CloseAllDockables(docs[0]);

        Assert.All(docs, d => Assert.True(IsOpen(d)));
        Assert.False(f.IsClosePromptOpen);
    }

    // Dock's own CloseWindow (a floating window's close box) still loops over CloseDockable. Each
    // request waits for the prompt before it rather than stacking a second dialog — and none is
    // dropped, or every tab after the first dirty one would be left in a closed window.
    [Fact]
    public void LoopedSingleCloses_PromptOneAtATime_AndNoneIsDropped()
    {
        using var _ = new InlineContinuations();
        var (f, docs) = FactoryWith(3);
        var pending = new List<TaskCompletionSource<bool>>();
        f.CloseDockableConfirm = _ =>
        {
            var tcs = new TaskCompletionSource<bool>();
            pending.Add(tcs);
            return tcs.Task;
        };

        foreach (var d in docs) f.CloseDockable(d);
        Assert.Single(pending);                 // one dialog on screen, not three

        pending[0].SetResult(true);
        Assert.Equal(2, pending.Count);
        pending[1].SetResult(false);            // Cancel on doc1 stops only doc1
        Assert.Equal(3, pending.Count);
        pending[2].SetResult(true);

        Assert.False(IsOpen(docs[0]));
        Assert.True(IsOpen(docs[1]));
        Assert.False(IsOpen(docs[2]));
        Assert.False(f.IsClosePromptOpen);
    }

    [Fact]
    public void ACloseRequestedDuringABulkPrompt_WaitsForIt_AndSkipsATabTheBulkClosed()
    {
        using var _ = new InlineContinuations();
        var (f, docs) = FactoryWith(2);
        var answer = new TaskCompletionSource<IReadOnlyCollection<IDockable>>();
        var single = new List<IDockable>();
        f.CloseDockablesConfirm = _ => answer.Task;
        f.CloseDockableConfirm  = d => { single.Add(d); return Task.FromResult(true); };

        f.CloseAllDockables(docs[0]);
        f.CloseDockable(docs[0]);               // the tab's ×, or Cmd+W, while the dialog is up
        f.CloseDockable(docs[1]);
        Assert.Empty(single);

        answer.SetResult([docs[0]]);            // the bulk prompt closed doc0 only

        Assert.Equal([docs[1]], single);        // doc0 was already gone; doc1 asked after
        Assert.All(docs, d => Assert.False(IsOpen(d)));
    }

    [Fact]
    public void ATabRemovedWhileItsPromptIsOpen_IsNotClosedAgain()
    {
        using var _ = new InlineContinuations();
        var (f, docs) = FactoryWith(2);
        var answer = new TaskCompletionSource<bool>();
        int closed = 0;
        f.DockableClosed += (_, _) => closed++;
        f.CloseDockableConfirm = _ => answer.Task;

        f.CloseDockable(docs[0]);
        f.ForceCloseDockable(docs[0]);          // removed some other way while the dialog was up
        answer.SetResult(true);

        Assert.Equal(1, closed);
        Assert.True(IsOpen(docs[1]));
    }

    [Theory]
    [InlineData("All",   new[] { 0, 1, 2, 3 })]
    [InlineData("Other", new[] { 0, 2, 3 })]
    [InlineData("Left",  new[] { 0 })]
    [InlineData("Right", new[] { 2, 3 })]
    public void BulkTargets_MatchTheMenuWording(string which, int[] expected)
    {
        var (_, docs) = FactoryWith(4);

        var targets = CircuitRfDockFactory.BulkTargets(docs[1], Enum.Parse<CircuitRfDockFactory.BulkClose>(which));

        Assert.Equal(expected.Select(i => docs[i]), targets);
    }

    [Fact]
    public void BulkMessage_NamesEachDocument_UpToACap()
    {
        var names = Enumerable.Range(1, 25).Select(i => $"cell{i}").ToList();

        var msg = WorkspaceViewModel.BulkCloseMessage(names);

        Assert.StartsWith("25 documents have unsaved changes:", msg);
        Assert.Contains("cell10", msg);
        Assert.DoesNotContain("cell11", msg);
        Assert.Contains("…and 15 more", msg);
    }
}
