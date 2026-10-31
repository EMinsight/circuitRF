using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// One recorded answer about one kit's artwork scripts, as Settings lists it.
/// </summary>
public sealed partial class PCellTrustRow : ObservableObject
{
    private readonly Action<PCellTrustRow>? _changed;

    public PCellTrustRow(string path, bool allowed, string? interpreter, bool onDisk, Action<PCellTrustRow>? changed)
    {
        Path = path;
        _allowed = allowed;
        Interpreter = interpreter;
        _changed = changed;

        // The folder name is what the consent prompt called it, and a gdsfactory kit's installed
        // directory is named after its module, so either kind reads the way it was asked about.
        Name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } leaf ? leaf : path;
        Detail = onDisk ? path : $"Not found on this machine: {path}";
    }

    /// <summary>The directory the answer is keyed on, exactly as recorded.</summary>
    public string Path { get; }

    public string Name { get; }

    /// <summary>The directory, or a sentence saying it has gone — an answer about a kit that was
    /// uninstalled or moved is still listed, so it can be cleared.</summary>
    public string Detail { get; }

    /// <summary>The interpreter a gdsfactory kit runs in on this machine, or null.</summary>
    public string? Interpreter { get; }

    [ObservableProperty] private bool _allowed;

    partial void OnAllowedChanged(bool value) => _changed?.Invoke(this);
}

/// <summary>
/// Settings ▸ Security &amp; Permissions ▸ Generated Artwork: every kit this installation has been
/// asked about, each allowed, refused or forgotten on its own (see the .axaml's header comment).
///
/// <para>Every change is written at once and applied to every open workspace at once — the settings
/// dialog has no OK button, and a refusal that only took effect on the next open would leave a kit
/// running after the user had every reason to believe it had stopped.</para>
/// </summary>
public partial class PCellTrustSettingsView : UserControl
{
    /// <summary>
    /// The answers the user guide's Security figure lists instead of this machine's own, which would
    /// put the generating author's directories into a committed picture. Set only by the docs fixture;
    /// with it set, nothing is written and no workspace is told.
    /// </summary>
    internal static IReadOnlyList<(string Path, bool Allowed, string? Interpreter)>? RowsForCapture;

    public PCellTrustSettingsView()
    {
        InitializeComponent();
        Load();
    }

    public void Load()
    {
        List<PCellTrustRow> rows;
        if (RowsForCapture is { } sample)
        {
            rows = [.. sample.Select(r => new PCellTrustRow(r.Path, r.Allowed, r.Interpreter, onDisk: true, changed: null))];
        }
        else
        {
            var interpreters = Theming.AppPreferencesIo.Load().GdsfactoryInterpreters;
            rows = [.. PCellTrustPreferences.Load().Select(kv =>
            {
                string? leaf = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(kv.Key));
                // Interpreters are recorded per MODULE, and only a gdsfactory kit has one; a cni kit's
                // folder that happened to share a module's name must not borrow it, hence the package test.
                string? interpreter = leaf is { Length: > 0 }
                                      && File.Exists(System.IO.Path.Combine(kv.Key, "__init__.py"))
                                      && interpreters?.GetValueOrDefault(leaf) is { } chosen ? chosen : null;
                return new PCellTrustRow(kv.Key, kv.Value, interpreter, Directory.Exists(kv.Key), OnRowChanged);
            })];
        }

        rows.Sort((a, b) =>
        {
            int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        _rows = rows;
        ClearAllButton.IsEnabled = rows.Count > 0;
        FilterBox.IsEnabled = rows.Count > 0;
        ShowRows();
    }

    private List<PCellTrustRow> _rows = [];

    /// <summary>The rows the filter matches — by name or by directory, ignoring case — and the line
    /// under the list that says how many there are and how many are refused.</summary>
    private void ShowRows()
    {
        string filter = FilterBox.Text?.Trim() ?? "";
        var shown = filter.Length == 0
            ? _rows
            : [.. _rows.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || r.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))];

        KitRows.ItemsSource = shown;
        KitScroller.IsVisible = shown.Count > 0;
        EmptyText.IsVisible = _rows.Count == 0;
        NoMatchText.IsVisible = _rows.Count > 0 && shown.Count == 0;
        UpdateCount(shown.Count);
    }

    private void UpdateCount(int shown)
    {
        int refused = _rows.Count(r => !r.Allowed);
        string all = _rows.Count == 1 ? "1 kit" : $"{_rows.Count} kits";
        CountText.Text = _rows.Count == 0 ? ""
            : (shown == _rows.Count ? all : $"{shown} of {all} shown")
              + (refused == 0 ? "" : $" · {refused} not allowed");
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e) => ShowRows();

    private void OnRowChanged(PCellTrustRow row)
    {
        PCellTrustPreferences.Set(row.Path, row.Allowed);
        // The count only — re-assigning the rows would rebuild the list and throw away the scroll
        // position of someone working down a long one.
        UpdateCount((KitRows.ItemsSource as ICollection<PCellTrustRow>)?.Count ?? _rows.Count);
        TellTheOpenWorkspaces();
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (RowsForCapture is not null || (sender as Control)?.Tag is not PCellTrustRow row) return;
        PCellTrustPreferences.Forget(row.Path);
        Load();
        TellTheOpenWorkspaces();
    }

    private void OnClearAllClick(object? sender, RoutedEventArgs e)
    {
        if (RowsForCapture is not null) return;
        PCellTrustPreferences.ForgetAll();
        Load();
        TellTheOpenWorkspaces();
    }

    private static void TellTheOpenWorkspaces()
    {
        // Nothing here may fail Settings: the answer is already written, and the cost of a workspace
        // that missed it is only that it applies on the next open.
        try { WorkspaceViewModel.ApplyPCellTrustEverywhere(); } catch { }
    }
}
