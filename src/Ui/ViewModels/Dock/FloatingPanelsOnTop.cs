using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// A floating TOOL window is <c>Topmost</c> while one of its own workspace's windows is the active window — the shell,
/// a torn-off document, or another of its floating panels — and an ordinary window otherwise (R-dock-14). The floating
/// Wire Profile then stays above the layout editor it is edited beside, docked or torn off, click for click.
///
/// <para><b>Why not ownership, or a raise.</b> The raise on activation (<c>WorkspaceWindow.RaiseFloatingToolWindows</c>)
/// activates each panel and then the window again, and activating a window brings it to the front, so a click into the
/// layout editor put it back over the panel. Owning the panel by the shell, and by each torn-off document as it was
/// activated, was tried next and kept nothing above anything on the owner's Mac (2026-10-27). A window level is the one
/// ordering the platform keeps by itself.</para>
///
/// <para><b>Why only while the workspace is active.</b> <c>Topmost</c> on its own floats the panel over every other
/// application, and over circuitRF's own dialogs — a prompt is not a window of the workspace, so while one is active the
/// panels are ordinary windows and the prompt is in front. Another workspace window active drops this workspace's panels
/// too: they describe a different design.</para>
///
/// <para><b>"A tool float" is asked with <see cref="CrfHostWindow.FloatsAnyNonToolDocument"/></b>, never
/// <see cref="CrfHostWindow.FloatsAnyDocument"/>: Dock's <c>Tool</c> is an <c>IDocument</c> too, and the broader test
/// skipped the Wire Profile as a document window every time, so this class never acted until it was changed.</para>
///
/// <para>Recomputed after every change of any window's <c>IsActive</c>, once per dispatcher pass: moving focus between
/// two windows deactivates one before it activates the other, and the moment in between (no window active) must not
/// drop the panels.</para>
/// </summary>
internal static class FloatingPanelsOnTop
{
    private static bool _installed;
    private static bool _pending;

    /// <summary>Called once, by the first <see cref="CrfHostWindow"/>: there is nothing to float before one exists.</summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        WindowBase.IsActiveProperty.Changed.AddClassHandler<Window>((_, _) => Schedule());
    }

    /// <summary>Whether a floating panel of <paramref name="panelWorkspace"/> floats while
    /// <paramref name="activeWorkspace"/> owns the active window (null: a dialog, or no circuitRF window at all).</summary>
    internal static bool FloatsWhile(WorkspaceViewModel? activeWorkspace, WorkspaceViewModel? panelWorkspace) =>
        activeWorkspace is not null && (panelWorkspace is null || ReferenceEquals(activeWorkspace, panelWorkspace));

    private static void Schedule()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.UIThread.Post(() => { _pending = false; Apply(); }, DispatcherPriority.Background);
    }

    private static void Apply()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        var active = desktop.Windows.FirstOrDefault(w => w.IsActive);
        var activeWorkspace = active switch
        {
            Views.WorkspaceWindow shell => shell.DataContext as WorkspaceViewModel,
            CrfHostWindow host          => host.OwningWorkspace,
            _                           => null,
        };

        foreach (var panel in desktop.Windows.OfType<CrfHostWindow>())
        {
            if (panel.PlatformImpl is null || !panel.FloatsAnyTool() || panel.FloatsAnyNonToolDocument()) continue;
            bool onTop = FloatsWhile(activeWorkspace, panel.OwningWorkspace);
            if (panel.Topmost != onTop) panel.Topmost = onTop;
        }
    }
}
