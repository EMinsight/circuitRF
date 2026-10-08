using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// Esc in a text field cancels the edit and leaves the field: the box gets back the text it had when it
/// took focus, THEN the host takes focus — so a lost-focus commit finds nothing changed and writes
/// nothing. Attached once per host view; every <see cref="TextBox"/> inside it is covered.
///
/// <para>Listens on the TUNNEL with handled events too: a window that binds Esc to a command marks the
/// key handled before the focused control sees it. A view that gives Esc its own meaning in one box
/// (a rename to cancel) handles the key on its own tunnel first; the text put back here is the text
/// that meaning wants as well.</para>
///
/// <para>A box in one of the host's flyouts gets its text back and nothing more: the key is left
/// unhandled so the flyout closes as Esc always closes it, and a flyout that commits on close then
/// commits the text the field had before the edit.</para>
/// </summary>
public static class EscapeCancelsEdit
{
    private static readonly AttachedProperty<string?> FocusTextProperty =
        AvaloniaProperty.RegisterAttached<TextBox, string?>("FocusText", typeof(EscapeCancelsEdit));

    public static void Attach(Control host)
    {
        host.Focusable = true;
        host.AddHandler(InputElement.GotFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox tb) tb.SetValue(FocusTextProperty, tb.Text);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        host.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || e.Source is not TextBox { IsFocused: true } box) return;
            box.Text = box.GetValue(FocusTextProperty);
            if (!host.IsVisualAncestorOf(box)) return;   // in a flyout: let Esc close it
            host.Focus();
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
}
