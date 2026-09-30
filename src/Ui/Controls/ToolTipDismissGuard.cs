using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// A tooltip never survives the click that dismisses it, nor its window losing activation.
///
/// <para><b>The failure.</b> A button's tip is up (or its delay is running) when the button is clicked, and the
/// click opens a dialog. The dialog takes the pointer with the tip still assigned to the button, so the tip stays
/// on screen behind the dialog, and moving the pointer opens and closes it over and over. The same happens when a
/// window loses activation some other way (a keyboard shortcut opening a dialog, another window clicked) while a tip
/// is showing. It was fixed once for one toolbar button in the 3D editor, and reported again for the 3D Properties
/// inspector's material Edit… button — so it is fixed here, once, for every control.</para>
///
/// <para><b>What it does.</b> A click on any <see cref="Button"/> (and so any toggle, split or dropdown button) closes
/// the tip that would show for it — the button's own, or the nearest ancestor's when the button has none — and holds
/// that control's tooltip service off until the pointer has left it with its window active again. A window's
/// deactivation does the same to the tip showing in it. Nothing is changed for a control whose service was already
/// off, and a control that leaves the tree is restored, so the guard can never leave a tip permanently disabled.</para>
///
/// <para>Installed as CLASS handlers, like <see cref="HiddenComboBoxInputGuard"/>, so it covers controls in panels
/// and dialogs that do not exist yet.</para>
/// </summary>
internal static class ToolTipDismissGuard
{
    private static bool _installed;

    /// <summary>The control whose tip opened last, while it is open — what a window deactivation closes.</summary>
    private static WeakReference<Control>? _open;

    /// <summary>The windows already watched for deactivation.</summary>
    private static readonly ConditionalWeakTable<WindowBase, object> Watched = new();

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;

        Button.ClickEvent.AddClassHandler<Button>((button, _) => Dismiss(button),
                                                  RoutingStrategies.Bubble, handledEventsToo: true);

        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((control, args) =>
        {
            _open = new WeakReference<Control>(control);
            if (TopLevel.GetTopLevel(control) is WindowBase window && !Watched.TryGetValue(window, out _))
            {
                Watched.Add(window, new object());
                window.Deactivated += (_, _) =>
                {
                    if (_open?.TryGetTarget(out var c) == true && ToolTip.GetIsOpen(c) && TopLevel.GetTopLevel(c) == window)
                        Suppress(c);
                };
            }
        });

        ToolTip.ToolTipClosingEvent.AddClassHandler(typeof(Control), (sender, args) =>
        {
            if (_open?.TryGetTarget(out var c) == true && ReferenceEquals(c, sender)) _open = null;
        });
    }

    /// <summary>Closes and holds off the tip a click on <paramref name="clicked"/> would leave behind.</summary>
    internal static void Dismiss(Control clicked)
    {
        for (Visual? v = clicked; v is not null; v = v.GetVisualParent())
            if (v is Control c && ToolTip.GetTip(c) is not null)
            {
                Suppress(c);
                return;
            }
    }

    private static void Suppress(Control control)
    {
        ToolTip.SetIsOpen(control, false);
        if (!ToolTip.GetServiceEnabled(control)) return;   // off already, by design or by an earlier click
        ToolTip.SetServiceEnabled(control, false);

        var window = TopLevel.GetTopLevel(control) as WindowBase;
        bool pointerLeft = !control.IsPointerOver;

        void TryRestore()
        {
            if (!pointerLeft || window is { IsActive: false }) return;
            Restore();
        }
        void OnExited(object? s, PointerEventArgs e) { pointerLeft = true; TryRestore(); }
        void OnActivated(object? s, EventArgs e) { if (!control.IsPointerOver) pointerLeft = true; TryRestore(); }
        void OnDetached(object? s, VisualTreeAttachmentEventArgs e) => Restore();
        void Restore()
        {
            control.PointerExited -= OnExited;
            control.DetachedFromVisualTree -= OnDetached;
            if (window is not null) window.Activated -= OnActivated;
            ToolTip.SetServiceEnabled(control, true);
        }

        control.PointerExited += OnExited;
        control.DetachedFromVisualTree += OnDetached;
        if (window is not null) window.Activated += OnActivated;
    }
}
