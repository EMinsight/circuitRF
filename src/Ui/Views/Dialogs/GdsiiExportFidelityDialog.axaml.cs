using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// R-L4a-3: "The export dialog states what will change before writing" — curve-flatten count, hole
/// keyhole count, and skipped-bitmap count, computed by <see cref="GdsiiExport.Analyze"/> via the same
/// write path the real export uses (a dry run), so this can never disagree with what actually happens.
/// A coordinate overflow (gate 8) lists every offending shape by name and disables Export outright —
/// never a warning the user can click through.
///
/// <para>One dialog for every stream export route (brief-oasis-gdstk.md §7a), generalised by the route
/// rather than copied: its title and sentences name it ("Export GDSII (gdstk)"), and its counts are the
/// plan's on every route, because both writers start from the same lowering. The one difference is an
/// unresolved instance reference — a warning on circuitRF's own writer, which writes a dangling name,
/// and a block on gdstk's, which cannot.</para>
/// </summary>
public partial class GdsiiExportFidelityDialog : Window
{
    public GdsiiExportFidelityDialog() => InitializeComponent();

    public GdsiiExportFidelityDialog(GdsiiExport.ExportPlan plan) : this(plan, StreamRoute.Gdsii) { }

    public GdsiiExportFidelityDialog(GdsiiExport.ExportPlan plan, StreamRoute route) : this()
    {
        string format = route.FormatName();
        Title = $"Export {route.DisplayName()}";

        CurveLine.Text = $"• {plan.CurvedShapesFlattened} curved shape(s) will flatten to polygons.";
        CurveLine.IsVisible = plan.CurvedShapesFlattened > 0;

        HoleLine.Text = $"• {plan.HolesKeyholed} shape(s) with holes will be keyholed into a single contour.";
        HoleLine.IsVisible = plan.HolesKeyholed > 0;

        BitmapLine.Text = $"• {plan.BitmapsSkipped} bitmap(s) will be skipped (never exported to {format}).";
        BitmapLine.IsVisible = plan.BitmapsSkipped > 0;

        // R-via-9: a via with no landing layer configured exports its barrel only.
        ViaPadSkippedLine.Text = $"• {plan.ViaPadsSkipped} via(s) have no landing layer set — pad not exported.";
        ViaPadSkippedLine.IsVisible = plan.ViaPadsSkipped > 0;

        // D7 (brief-gdsii-native-fixes.md): an imported layer with no GDSII number gets a free one. The
        // sentence says "GDSII" on the OASIS route too: OASIS numbers layers exactly as GDSII does.
        LayersRenumberedLine.Text = string.Join("\n", plan.LayerRenumberings.Select(r => $"• {r}"));
        LayersRenumberedLine.IsVisible = plan.LayerRenumberings.Count > 0;

        // R-via-10: GDSII carries no drill table — never a manufacturable PCB deliverable.
        ViaFabricationNoteLine.IsVisible = plan.HasVias;

        NoChangesLine.IsVisible = plan.HasNothingToReport;

        var blocking = route.BlockingReferences(plan);
        if (plan.UnresolvedInstanceReferences.Count > 0)
        {
            UnresolvedHeader.Text = blocking.Count > 0
                ? $"Export blocked — the following instances reference a cell that could not be resolved, and {route.DisplayName()} cannot write a reference to a cell the file does not hold:"
                : $"Warning — the following instances reference a cell that could not be resolved and will export as a dangling reference (shown as an unresolved placeholder by {format} viewers):";
            if (blocking.Count > 0) ExportButton.IsEnabled = false;
            UnresolvedHeader.IsVisible = true;
            UnresolvedScroll.IsVisible = true;
            UnresolvedList.ItemsSource = plan.UnresolvedInstanceReferences;
        }

        if (!plan.CanWrite)
        {
            OverflowHeader.Text = $"Export blocked — the following values do not fit {format}:";
            OverflowHeader.IsVisible = true;
            OverflowScroll.IsVisible = true;
            OverflowList.ItemsSource = plan.CoordinateOverflowOffenders;
            ExportButton.IsEnabled = false;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
    private void OnExportClick(object? sender, RoutedEventArgs e) => Close(true);
}
