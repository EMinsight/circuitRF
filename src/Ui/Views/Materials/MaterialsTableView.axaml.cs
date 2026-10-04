using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Materials;

/// <summary>brief-em3d-53 R-em3d53-4 — the Materials editor. Its only code is Enter-commits-the-field, putting the caret in a
/// new material's name, and (brief-em3d-94) scrolling the selected row into view; every rule is the view model's.</summary>
public partial class MaterialsTableView : UserControl
{
    private MaterialsTableViewModel? _vm;

    public MaterialsTableView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.NameFocusRequested -= FocusName;
                _vm.PropertyChanged -= OnTablePropertyChanged;
            }
            _vm = DataContext as MaterialsTableViewModel;
            if (_vm is not null)
            {
                _vm.NameFocusRequested += FocusName;
                _vm.PropertyChanged += OnTablePropertyChanged;
            }
            RevealSelected();
        };
        // A table opened on a row (Edit Material…, Open Library) was selected before this view existed: reveal it once attached.
        AttachedToVisualTree += (_, _) => RevealSelected();
    }

    private void OnTablePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MaterialsTableViewModel.SelectedRow)) return;
        KeepAppearanceInPlace();
        RevealSelected();
    }

    /// <summary>
    /// Clicking from material to material with the Appearance card on screen leaves the card where it was (owner-reported: it
    /// moved by about ten pixels each time). Everything around it varies by material — the "Used by" line, a read-only note, an
    /// anisotropic tensor's rows, wrapped hints above it, the Source text below it — and at the bottom of the scroll a shorter
    /// material also lowers the maximum offset, so the clamp moved everything. The card's top is read NOW, while the layout is
    /// still the old material's (a selection change lays nothing out synchronously); after the new one lays out, the scroll moves
    /// by however far the card did, with a spacer at the foot giving it the room a shorter material would take away. A card
    /// that is not on screen anchors nothing: the scroll then behaves as it always did.
    /// </summary>
    private void KeepAppearanceInPlace()
    {
        if (_vm?.SelectedRow is not { } row || !DetailScroll.IsVisible || AppearanceTop() is not { } before) return;
        if (before + AppearanceCard.Bounds.Height < 0 || before > DetailScroll.Viewport.Height) return;   // not on screen
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_vm?.SelectedRow, row)) return;
            AnchorSpacer.IsVisible = false;
            DetailScroll.UpdateLayout();
            if (AppearanceTop() is not { } after) return;
            double target = DetailScroll.Offset.Y + (after - before);
            double max = DetailScroll.Extent.Height - DetailScroll.Viewport.Height;
            if (target > max)
            {
                AnchorSpacer.Height = target - max;
                AnchorSpacer.IsVisible = true;
                DetailScroll.UpdateLayout();
            }
            DetailScroll.Offset = new Vector(DetailScroll.Offset.X, Math.Max(0, target));
            DetailScroll.UpdateLayout();
        // ABOVE Render, so it runs before the next frame is drawn: at Background (or Loaded, which Avalonia runs "after layout
        // and render") one frame showed the new material at the OLD offset, a visible flash (owner-reported). By the time a
        // Normal post runs, the selection's bindings have applied (they update synchronously on PropertyChanged), and the
        // UpdateLayout calls lay the new material out here rather than in the render pass.
        }, DispatcherPriority.Normal);
    }

    /// <summary>The Appearance card's top in the detail viewport, or null while it is not laid out.</summary>
    private double? AppearanceTop() => AppearanceCard.TranslatePoint(new Point(0, 0), DetailScroll)?.Y;

    /// <summary>
    /// brief-em3d-94 R-em3d94-2 — the selected row scrolled into view (ListBox.ScrollIntoView), whenever the selection is set,
    /// from code included: selecting is not enough, since a long table leaves the row off-screen. Deferred until the list is
    /// attached and has a size — an editor just opened, or a tab not yet shown, has neither — and then to Background priority,
    /// after the layout pass that realises the rows, so a layout following the scroll does not leave the list at the top.
    /// </summary>
    private void RevealSelected()
    {
        if (_vm?.SelectedRow is not { } row || !this.IsAttachedToVisualTree()) return;   // AttachedToVisualTree calls again
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_vm?.SelectedRow, row)) return;
            if (RowList.Bounds.Height <= 0)
            {
                void Once(object? s, EventArgs e) { RowList.LayoutUpdated -= Once; RevealSelected(); }
                RowList.LayoutUpdated += Once;
                return;
            }
            RowList.ScrollIntoView(row);
        }, DispatcherPriority.Background);
    }

    /// <summary>A new or duplicated material's name, selected to be typed over — after the form has bound to it.</summary>
    private void FocusName()
        => Dispatcher.UIThread.Post(() =>
        {
            if (!NameBox.IsReadOnly && NameBox.Focus()) NameBox.SelectAll();
        }, DispatcherPriority.Background);

    /// <summary>Enter commits a field as leaving it does (the bindings update on LostFocus); Escape puts the
    /// shown value back.</summary>
    private void OnCellKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter)
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateTarget();
            e.Handled = true;
        }
    }
}
