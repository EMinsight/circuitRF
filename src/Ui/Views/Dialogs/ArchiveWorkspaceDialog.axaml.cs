using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CircuitRF.Ui.Archive;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// "Prompt the user in a dialog if they want to copy any of the Referenced Kits into the workspace"
/// (owner, 2026-08-15) — plus the referenced files and the results, each with its size so the choice
/// can be made on the numbers.
///
/// <para>The dialog decides nothing itself: it presents <see cref="WorkspaceArchivePlan"/> and writes
/// the ticks back onto it. What is offered, what each default is, and what is skipped outright all
/// live in <see cref="WorkspaceArchiveScanner"/>, where they are testable without a window.</para>
/// </summary>
public partial class ArchiveWorkspaceDialog : Window
{
    private readonly WorkspaceArchivePlan? _plan;
    private readonly IMessageSink? _messages;
    private ObservableCollection<ArchiveTreeNode> _roots = [];
    private bool _historyPrepared;

    public ArchiveWorkspaceDialog() => InitializeComponent();

    public ArchiveWorkspaceDialog(WorkspaceArchivePlan plan, IMessageSink? messages = null) : this()
    {
        _plan     = plan;
        _messages = messages;

        HeaderText.Text = $"Archive “{Path.GetFileName(plan.WorkspaceDir.TrimEnd(Path.DirectorySeparatorChar))}”";

        var roots = _roots = BuildRoots(plan);
        Tree.ItemsSource = roots;

        // RC-8 R-rc8-2. Off, and set from the plan rather than from anything remembered: nothing
        // persists this choice, so every archive is decided on its own.
        HistoryPanel.IsVisible       = plan.HistoryAvailable;
        IncludeHistoryCheck.IsChecked = plan.IncludeHistory;
        HistoryDetailText.Text        = HistoryInvitation;

        UpdateTotal();

        // The tick state lives on the plan's options, which the tree writes through to — so polling
        // the plan on a slow tick keeps the total honest without every node raising.
        //
        // Started from Opened, not from here: at construction the window is not visible yet, so a
        // timer whose keep-running answer is `IsVisible` would stop on its very first tick.
        Opened += (_, _) =>
        {
            var alive = true;
            Closed += (_, _) => alive = false;
            DispatcherTimer.Run(() => { UpdateTotal(); return alive; }, TimeSpan.FromMilliseconds(250));
        };

        MeasureKitsInBackground(roots, plan);
    }

    /// <summary>The branches, in the order the owner described them.</summary>
    private static ObservableCollection<ArchiveTreeNode> BuildRoots(WorkspaceArchivePlan plan)
    {
        var roots = new ObservableCollection<ArchiveTreeNode>();

        if (plan.Kits.Any())
            roots.Add(ArchiveTreeNode.Group(
                $"Referenced Kits ({plan.Kits.Count()})",
                () => plan.Kits.Select(ArchiveTreeNode.Leaf)));

        // MW2 R-mw2-16 — cells this workspace instances out of another one. Its own branch rather
        // than a row among the kits: a kit is the vendor's content and starts unticked, while these
        // are the user's own design and an archive without them opens showing placeholders.
        if (plan.ReferencedWorkspaces.Any())
            roots.Add(ArchiveTreeNode.Group(
                $"Referenced Workspaces ({plan.ReferencedWorkspaces.Count()})",
                () => plan.ReferencedWorkspaces.Select(ArchiveTreeNode.Leaf)));

        if (plan.ExternalFiles.Any())
            roots.Add(ArchiveTreeNode.Group(
                $"Referenced Files ({plan.ExternalFiles.Count()})",
                () => plan.ExternalFiles.Select(ArchiveTreeNode.Leaf)));

        if (plan.Results.Any())
            roots.Add(ArchiveTreeNode.Group(
                $"Results ({plan.Results.Count()})",
                () => WorkspaceArchiveScanner.ResultGroupOrder
                        .Where(g => plan.Results.Any(r => r.Group == g))
                        .Select(g => ArchiveTreeNode.Group(
                            $"{g} ({plan.Results.Count(r => r.Group == g)})",
                            () => plan.Results.Where(r => r.Group == g).Select(ArchiveTreeNode.Leaf)))));

        // One row, not a branch: the cells are content-hashed folder names nobody would recognise,
        // so the only choice worth offering is whether the artwork travels at all.
        if (plan.GeneratedCells is { } generated)
            roots.Add(ArchiveTreeNode.Leaf(generated));

        // Opening on the branches already reflects the defaults, so the user sees what they are
        // agreeing to rather than three closed headings.
        foreach (var root in roots) root.IsExpanded = true;

        return roots;
    }

    /// <summary>
    /// Fills in each kit folder's size behind the dialog. A vendor kit is routinely tens of thousands
    /// of files, so measuring one on the UI thread would hold the window shut for seconds — and the
    /// number is exactly what the user needs in order to decide, so it cannot simply be omitted.
    /// </summary>
    private static void MeasureKitsInBackground(IEnumerable<ArchiveTreeNode> roots, WorkspaceArchivePlan plan)
    {
        var pending = plan.Kits.Where(k => k.SizeBytes < 0 && k.IsDirectory).ToList();
        if (pending.Count == 0) return;

        _ = Task.Run(() =>
        {
            foreach (var kit in pending)
            {
                long bytes;
                bool complete;
                try { bytes = WorkspaceArchiveScanner.MeasureDirectory(kit.SourcePath, out complete); }
                catch { continue; }

                kit.SizeBytes = bytes;
                var text = WorkspaceArchivePlan.FormatSize(bytes) + (complete ? "" : "+");

                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var node in roots.SelectMany(r => r.SelfAndDescendants()))
                        if (ReferenceEquals(node.Option, kit)) node.SizeText = text;
                });
            }
        });
    }

    private void UpdateTotal()
    {
        if (_plan is null) return;

        var ticked = _plan.Options.Count(o => o.Selected);
        TotalText.Text =
            $"Approximate uncompressed size: {WorkspaceArchivePlan.FormatSize(_plan.SelectedBytes)}  " +
            $"({WorkspaceArchivePlan.FormatSize(_plan.AlwaysIncludedBytes)} of workspace files, {ticked} item(s) ticked)";

        var warning = _plan.GeneratedCellsWarning;
        GeneratedCellsWarningText.Text      = warning ?? "";
        GeneratedCellsWarningText.IsVisible = warning is not null;
    }

    /// <summary>
    /// Ticks or unticks everything optional.
    ///
    /// <para>The plan's options are set FIRST and the tree second. A group that has never been
    /// expanded is standing in for rows it has not built yet — writing through the node alone would
    /// reach only the rows on screen, which is the shape this dialog's laziness makes easy to get
    /// wrong.</para>
    ///
    /// <para><b>"Everything optional" deliberately does not include the history</b> (RC-8 R-rc8-2).
    /// The tree's rows are about bulk and provenance and are all recoverable choices; the history is
    /// the one decision in this dialog that cannot be undone once the archive has been sent, so a
    /// convenience button must not be able to make it.</para>
    /// </summary>
    private void SetAll(bool included)
    {
        if (_plan is null) return;

        foreach (var option in _plan.Options) option.Selected = included;
        foreach (var root in _roots) root.IsChecked = included;

        UpdateTotal();
    }

    // ── The history (RC-8) ────────────────────────────────────────────────────

    /// <summary>
    /// What the row says before it is ticked. <b>It describes the CONTENT, not the mechanism</b>
    /// (R-rc8-6): a label reading "include git history" fails on two counts — it is git vocabulary,
    /// and it does not tell the sender what they are about to send.
    ///
    /// <para><b>And it does not read as a warning against its own feature</b> (R-rc8-3). Handing a
    /// design to a partner WITH its history is a far better handover than a snapshot, and internally
    /// it is usually what is wanted. The point of the default is only that it must be chosen, not
    /// inherited.</para>
    /// </summary>
    private const string HistoryInvitation =
        "Every earlier version of every file this workspace has kept, and every restore point — "
      + "the recipient can go back to any of them. Tick it to see what that adds.";

    /// <summary>
    /// Ticking it computes the answer; unticking it puts the invitation back.
    ///
    /// <para><b>The work is done once and behind the dialog.</b> Preparing means PACKING the
    /// repository (R-rc8-9), which is seconds of I/O on a large history — so it runs off the UI
    /// thread, reports itself in the Messages panel, and says so here in the meantime. A warning that
    /// arrives after the user has clicked through is not a warning, so the Archive button is held
    /// until the figures are in.</para>
    /// </summary>
    private void OnIncludeHistoryChanged(object? sender, RoutedEventArgs e)
    {
        if (_plan is null) return;

        bool ticked = IncludeHistoryCheck.IsChecked == true;
        _plan.IncludeHistory = ticked;

        if (!ticked)
        {
            HistoryDetailText.Text = HistoryInvitation;
            UpdateTotal();
            return;
        }

        if (_historyPrepared)
        {
            ShowHistorySummary();
            UpdateTotal();
            return;
        }

        HistoryDetailText.Text = "Working out what that adds…";
        ArchiveButton.IsEnabled = false;

        var plan     = _plan;
        var messages = _messages;

        // The dialog can be cancelled while this is in flight. The pack itself is safe to abandon and
        // leaves a correct repository either way; what must not happen is the continuation writing the
        // figures into a window that is no longer on screen.
        bool open = true;
        Closed += (_, _) => open = false;

        _ = Task.Run(() => ArchiveHistoryPreparation.Prepare(plan, messages))
                .ContinueWith(_ => Dispatcher.UIThread.Post(() =>
                {
                    if (!open) return;

                    _historyPrepared        = true;
                    ArchiveButton.IsEnabled = true;
                    HistoryPanel.IsVisible  = plan.HistoryAvailable;
                    ShowHistorySummary();
                    UpdateTotal();
                }), TaskScheduler.Default);
    }

    /// <summary>
    /// The computed sentences (R-rc8-7). <b>The deleted-file names are the item that does the real
    /// work</b> (R-rc8-8) — someone about to leak a file will almost always recognise it by name, and
    /// nobody recognises "the repository contains historical objects".
    /// </summary>
    private void ShowHistorySummary()
        => HistoryDetailText.Text = _plan?.History is { } summary
            ? string.Join(Environment.NewLine, summary.Describe())
            : HistoryInvitation;

    private void OnIncludeAllClick(object? sender, RoutedEventArgs e)  => SetAll(true);
    private void OnIncludeNoneClick(object? sender, RoutedEventArgs e) => SetAll(false);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
    private void OnArchiveClick(object? sender, RoutedEventArgs e) => Close(true);
}
