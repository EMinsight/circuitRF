using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>Result of the Change Technology picker (docs/sonnet-briefs/brief-L1g-technology-retarget.md
/// §4). <see cref="AbsoluteTechPath"/> null means "(Workspace default)" — writes
/// <c>LayoutView.TechRef = null</c> and re-resolves through L0c's resolution order; a non-null path
/// (a <c>tech/</c> entry or a browsed file) becomes an explicit <c>TechRef</c>.</summary>
public sealed record ChangeTechnologyResult(string? AbsoluteTechPath, bool AdoptUnits, string? CatalogId = null);

/// <summary>
/// "Change Technology…" picker — the entry point for Gap 1 (docs/sonnet-briefs/brief-L1g-technology-retarget.md
/// §0: "there is no UI to change a layout's technology"). Offers <b>(Workspace default)</b> as an
/// explicit, always-selectable option (§4 — L0c's convention, not something only reachable by never
/// having chosen), every <c>.ctech</c> <b>anywhere in the workspace</b>, and <b>Browse…</b> for one
/// outside it. The unit-adoption checkbox defaults OFF — silently overwriting a user's working unit
/// mid-retarget would be exactly the kind of helpfulness that erodes trust (§4 point 3).
///
/// <para><b>The list was the workspace's <c>tech/</c> folder alone</b> until 2026-09-04 (owner report:
/// the picker does not offer all the workspace's technologies). <c>tech/</c> is where the workspace
/// TEMPLATE puts one and where a new one is written, but nothing stops a technology living beside the
/// cell it belongs to, arriving inside an imported cell folder, or being unpacked from an archive —
/// and a technology the user can see in the Project Tree but not choose here reads as the picker being
/// broken. Browse… could reach them, which is precisely the tell: the file was in the workspace all
/// along and the dialog was sending the user out to the filesystem to find it.</para>
/// </summary>
public partial class ChangeTechnologyDialog : Window
{
    public ChangeTechnologyDialog() => InitializeComponent();

    public ChangeTechnologyDialog(LayoutEditorViewModel vm) : this()
    {
        CurrentText.Text = $"Current: {vm.TechSummaryText}{CurrentFileSuffix(vm)}";

        var items = new List<ListBoxItem>
        {
            Row("(Workspace default)", null,
                "Whatever this workspace's default technology resolves to — no explicit reference is "
                + "written into the layout."),
        };

        var choices = WorkspaceTechnologyChoices.Enumerate(vm.WorkspaceRootDir, vm.WorkspaceTechDir);
        foreach (var choice in choices)
            items.Add(Row(choice.Label, choice.AbsolutePath, choice.AbsolutePath));

        // The dialog OPENS ON WHAT THE LAYOUT IS USING — see WorkspaceTechnologyChoices.IndexOfCurrent
        // for why that is not "(Workspace default)" for every layout, and for what confirming that
        // wrong reading used to do to an imported board's explicit TechRef. Row 0 is the workspace
        // default, so a choice at index i is item i + 1.
        int selected = 0;
        int match = WorkspaceTechnologyChoices.IndexOfCurrent(choices, vm.Model.TechRef, vm.ResolvedTechPath);
        if (match >= 0)
        {
            selected = match + 1;
        }
        else if (vm.Model.TechRef is { Length: > 0 } && vm.ResolvedTechPath is { Length: > 0 } outside)
        {
            // An explicit reference to a technology that lives outside this workspace — reachable
            // only through Browse…, so it is in no enumerated row. Offered as its own row rather
            // than left unrepresented, because the alternative is pre-selecting something the
            // layout is not using.
            items.Add(Row($"{Path.GetFileNameWithoutExtension(outside)}  —  outside this workspace",
                          outside, outside));
            selected = items.Count - 1;
        }

        ChoiceList.ItemsSource = items;
        ChoiceList.SelectedIndex = selected;
        ChoiceList.ScrollIntoView(items[selected]);

        WireButtons();
    }

    /// <summary>
    /// 3D editor bugs round 2 — the same picker for a 3D design that resolves NO technology, so a material has nowhere to
    /// come from: every <c>.ctech</c> in the workspace, then the catalog's technologies (a copy goes into the workspace's
    /// <c>tech/</c>, as New Workspace makes one — <see cref="ChangeTechnologyResult.CatalogId"/>), then Browse…. There is
    /// no "(Workspace default)" row, since that default is what resolved nothing, and no unit adoption (a 3D design's unit
    /// is its own).
    /// </summary>
    public ChangeTechnologyDialog(string intro, string? workspaceRootDir, IReadOnlyList<(string Label, string Id)> catalog) : this()
    {
        Title = "Choose a Technology";
        CurrentText.Text = intro;
        CurrentText.Opacity = 1;
        AdoptUnitsCheck.IsVisible = false;
        OkButton.Content = "Use";
        var items = new List<ListBoxItem>();
        string? techDir = workspaceRootDir is null ? null : Path.Combine(workspaceRootDir, "tech");
        foreach (var choice in WorkspaceTechnologyChoices.Enumerate(workspaceRootDir, techDir))
            items.Add(Row(choice.Label, choice.AbsolutePath, choice.AbsolutePath));
        foreach (var (label, id) in catalog)
        {
            var row = Row($"{label}  —  built in, copied into tech/", null, workspaceRootDir is null
                ? "Built into circuitRF. This design belongs to no workspace, so there is no tech/ folder to copy it into: use Browse…."
                : $"Built into circuitRF: a copy is written to {Path.Combine("tech", id + ".ctech")} and used from there.");
            row.Tag = CatalogTag + id;
            row.IsEnabled = workspaceRootDir is not null;
            items.Add(row);
        }
        ChoiceList.ItemsSource = items;
        ChoiceList.SelectedIndex = items.FindIndex(i => i.IsEnabled);
        WireButtons();
    }

    private const string CatalogTag = "catalog:";

    private void WireButtons()
    {
        BrowseButton.Click += async (_, _) => await OnBrowseAsync();
        OkButton.Click      += (_, _) => { if (ChoiceList.SelectedItem is not null) Close(BuildResult()); };
        CancelButton.Click  += (_, _) => Close(null);
    }

    private async Task OnBrowseAsync()
    {
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a Technology",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("circuitRF Technology") { Patterns = ["*.ctech"] },
                new FilePickerFileType("All Files")             { Patterns = ["*.*"] },
            ],
        });
        if (result.Count == 0) return;

        Close(new ChangeTechnologyResult(result[0].Path.LocalPath, AdoptUnitsCheck.IsChecked == true));
    }

    /// <summary>
    /// One row: its label, the file it stands for (in <c>Tag</c>), and <b>its full path as a
    /// tooltip</b>.
    ///
    /// <para>The tooltip is owner-requested (2026-09-09) and it answers the question no label can. A
    /// technology's row is named after the technology's own internal <c>Name</c>, which two files can
    /// share — a Save As from the technology editor produces exactly that — and while
    /// <see cref="WorkspaceTechnologyChoices"/> appends the workspace-relative path to a duplicated
    /// name, "which file is this actually" is a question a user can have about ANY row, including the
    /// unique ones. Hovering answers it without lengthening every label.</para>
    ///
    /// <para>Built as <c>ListBoxItem</c>s rather than data items with a template because a tooltip is
    /// per-container: an <c>ItemTemplate</c> would have to bind <c>ToolTip.Tip</c> through the item's
    /// own type, and this dialog carries no data context to bind against.</para>
    /// </summary>
    private static ListBoxItem Row(string label, string? absolutePath, string? tooltip)
    {
        var row = new ListBoxItem { Content = label, Tag = absolutePath };
        if (tooltip is { Length: > 0 }) ToolTip.SetTip(row, tooltip);
        return row;
    }

    /// <summary>
    /// The FILE behind the current technology, appended to the "Current:" line, workspace-relative
    /// where it can be — the name alone does not say which of several <c>.ctech</c> files a layout
    /// resolved to, and two files can legitimately carry the same internal name. Empty when nothing
    /// resolved (the line already says so) or when the path cannot be made relative.
    /// </summary>
    private static string CurrentFileSuffix(LayoutEditorViewModel vm)
    {
        if (vm.ResolvedTechPath is not { Length: > 0 } path) return "";
        try
        {
            string label = vm.WorkspaceRootDir is { Length: > 0 } root
                ? Path.GetRelativePath(root, path).Replace('\\', '/')
                : Path.GetFileName(path);
            return $"  ·  {label}";
        }
        catch { return $"  ·  {Path.GetFileName(path)}"; }
    }

    private ChangeTechnologyResult BuildResult()
    {
        string? tag = (ChoiceList.SelectedItem as ListBoxItem)?.Tag as string;
        return tag is not null && tag.StartsWith(CatalogTag, StringComparison.Ordinal)
            ? new(null, false, tag[CatalogTag.Length..])
            : new(tag, AdoptUnitsCheck.IsChecked == true);
    }
}
