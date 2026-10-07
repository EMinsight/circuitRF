using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Interchange.Gdstk;
using CircuitRF.Ui.Theming;

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

    /// <summary>The OASIS writer's options as the dialog closed (the route's defaults, or what
    /// <paramref name="oasis"/> opened on, until the user changes them). Null on a GDSII route.</summary>
    public OasisWriteOptions? OasisOptions { get; private set; }

    public GdsiiExportFidelityDialog(GdsiiExport.ExportPlan plan, StreamRoute route, OasisWriteOptions? oasis = null) : this()
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

        // R-oas-4b: what an OASIS file cannot hold, counted from the elements the write will send, and
        // the writer's options. Both appear on the OASIS route only.
        var oasisLosses = route == StreamRoute.OasisGdstk ? OasisLosses.Of(plan).Messages() : [];
        OasisLossLine.Text = string.Join("\n", oasisLosses.Select(m => $"• {m}"));
        OasisLossLine.IsVisible = oasisLosses.Count > 0;
        if (route == StreamRoute.OasisGdstk)
        {
            var o = oasis ?? OasisWriteOptions.Default;
            OasisOptions = o;
            OasisOptionsPanel.IsVisible = true;
            OasisCompression.Value = o.CompressionLevel;
            OasisValidationBox.SelectedIndex = (int)o.Validation;
            OasisDetectShapes.IsChecked = o.DetectRectanglesAndTrapezoids;
            OasisStandardProperties.IsChecked = o.StandardProperties;
        }

        NoChangesLine.IsVisible = plan.HasNothingToReport && oasisLosses.Count == 0;

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
    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (OasisOptions is not null)
            OasisOptions = new OasisWriteOptions(
                Math.Clamp((int)(OasisCompression.Value ?? 6), 0, 9),
                OasisDetectShapes.IsChecked ?? true,
                OasisValidationBox.SelectedIndex is >= 0 and <= 2 ? (OasisValidation)OasisValidationBox.SelectedIndex : OasisValidation.Crc32,
                OasisStandardProperties.IsChecked ?? false);
        Close(true);
    }

    /// <summary>The OASIS options this user last exported with (<see cref="OasisWriteOptions.Default"/>
    /// for any they never set).</summary>
    public static OasisWriteOptions RememberedOasisOptions()
    {
        var p = AppPreferencesIo.Load();
        var d = OasisWriteOptions.Default;
        return new OasisWriteOptions(
            p.OasisCompressionLevel is int level and >= 0 and <= 9 ? level : d.CompressionLevel,
            p.OasisDetectRectanglesAndTrapezoids ?? d.DetectRectanglesAndTrapezoids,
            p.OasisValidation switch
            {
                "none" => OasisValidation.None,
                "checksum32" => OasisValidation.Checksum32,
                "crc32" => OasisValidation.Crc32,
                _ => d.Validation,
            },
            p.OasisStandardProperties ?? d.StandardProperties);
    }

    /// <summary>Remembers <paramref name="o"/> for this user's next OASIS export.</summary>
    public static void RememberOasisOptions(OasisWriteOptions o) => AppPreferencesIo.Update(p =>
    {
        p.OasisCompressionLevel = o.CompressionLevel;
        p.OasisDetectRectanglesAndTrapezoids = o.DetectRectanglesAndTrapezoids;
        p.OasisValidation = o.Validation switch
        {
            OasisValidation.None => "none",
            OasisValidation.Checksum32 => "checksum32",
            _ => "crc32",
        };
        p.OasisStandardProperties = o.StandardProperties;
    });
}
