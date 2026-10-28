using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// Which separators of a menu show: never two together, never first, never last. A menu whose items hide per kind
/// cannot get this from per-separator bindings — each one only knows its own condition, so a separator whose group
/// is hidden for this kind sits against the next one (owner rule: two separators in a row are not allowed).
///
/// <para>A separator's own condition rides its <c>Tag</c> (a bound bool; no Tag is "always"), not its
/// <c>IsVisible</c>, because <see cref="Apply"/> SETS <c>IsVisible</c> and a set value would replace a binding there
/// for good — the menu belongs to a recycled row whose DataContext changes.</para>
/// </summary>
public static class MenuSeparators
{
    /// <summary>
    /// For each entry, whether it is a separator that shows. A separator shows when its condition holds, a shown item
    /// precedes it with no shown separator between, and a shown item follows it before the next separator that could
    /// show; of several candidates in one gap, the first is kept.
    /// </summary>
    public static bool[] Shown(IReadOnlyList<(bool IsSeparator, bool Visible)> entries)
    {
        var shown = new bool[entries.Count];
        bool itemSince = false;   // a shown item since the start or the last shown separator
        int pending = -1;         // a separator waiting for a shown item after it
        for (int i = 0; i < entries.Count; i++)
        {
            var (isSeparator, visible) = entries[i];
            if (!visible) continue;
            if (isSeparator)
            {
                if (itemSince && pending < 0) pending = i;
                continue;
            }
            if (pending >= 0) { shown[pending] = true; pending = -1; itemSince = false; }
            itemSince = true;
        }
        return shown;
    }

    private static readonly ConditionalWeakTable<ContextMenu, object> Tracked = new();

    /// <summary>
    /// Keeps <paramref name="menu"/>'s separators decided: now, and again whenever an item's <c>IsVisible</c> or a separator's
    /// <c>Tag</c> changes. Idempotent; call it from the menu's <c>Opening</c>.
    ///
    /// <para><b>Opening alone is not enough.</b> The FIRST time a row's menu opens, its items' bindings have not resolved
    /// yet — every item still reads visible and every separator's condition unset — so every separator was kept, and the
    /// bindings then hid the items between them: three separators in a row, gone on the next open (owner report,
    /// 2026-10-28). Re-deciding as each binding lands is what makes the first open come out the same as the second.</para>
    /// </summary>
    public static void Track(ContextMenu menu)
    {
        if (!Tracked.TryGetValue(menu, out _))
        {
            Tracked.Add(menu, new object());
            bool applying = false;
            void Changed(object? sender, AvaloniaPropertyChangedEventArgs e)
            {
                if (applying) return;
                if (e.Property != Visual.IsVisibleProperty && !(sender is Separator && e.Property == Control.TagProperty)) return;
                applying = true;
                try { Apply(menu.Items); }
                finally { applying = false; }
            }
            foreach (var item in menu.Items)
                if (item is Control c && c is not Separator) c.PropertyChanged += Changed;
                else if (item is Separator s) s.PropertyChanged += (o, e) => { if (e.Property == Control.TagProperty) Changed(o, e); };
            // And once the popup is up, when every binding has had its DataContext: whatever changed on the way.
            menu.Opened += (_, _) => Apply(menu.Items);
            menu.DataContextChanged += (_, _) => Apply(menu.Items);
        }
        Apply(menu.Items);
    }

    /// <summary>Applies <see cref="Shown"/> to a menu's own items as their bindings stand now. Every
    /// <see cref="Separator"/> in it is decided here.</summary>
    public static void Apply(IEnumerable<object?> items)
    {
        var list    = new List<object?>(items);
        var entries = new (bool, bool)[list.Count];
        for (int i = 0; i < list.Count; i++)
            entries[i] = list[i] switch
            {
                Separator s => (true, s.Tag is not false),
                Control c   => (false, c.IsVisible),
                _           => (false, false),
            };
        var shown = Shown(entries);
        for (int i = 0; i < list.Count; i++)
            if (list[i] is Separator s) s.IsVisible = shown[i];
    }
}
