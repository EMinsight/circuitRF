using System.IO;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Views.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Simulate ▸ Setup Analyses… for a 3D view: its embedded EM setups as analysis cards. Open via
/// <c>WorkspaceViewModel.SetupAnalysesCommand</c> (or the 3D editor's tune button). Holds no state of its own —
/// every action is one of <see cref="C3dEditorViewModel"/>'s setup edits, so undo and the dirty mark are the document's.
/// </summary>
public partial class C3dSetupAnalysesDialog : Window
{
    private C3dEditorViewModel? Vm => DataContext as C3dEditorViewModel;

    public C3dSetupAnalysesDialog() => InitializeComponent();

    public C3dSetupAnalysesDialog(C3dEditorViewModel vm) : this()
    {
        DataContext = vm;
        Title = $"Setup Analyses — {Path.GetFileName(vm.TopFilePath)}";
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // ── the card's own actions: select the card first, then the view model's edit on the selection ─────────────

    private C3dSetupItem? Select(object? sender)
    {
        if (Vm is not { } vm || (sender as Control)?.DataContext is not C3dSetupItem item) return null;
        vm.SelectedSetupItem = item;
        return item;
    }

    private void OnActiveClick(object? sender, RoutedEventArgs e)
    {
        if (Select(sender) is { } item) Vm!.SetActiveSetup(item.Name);
    }

    private void OnMenuMakeActive(object? sender, RoutedEventArgs e) => OnActiveClick(sender, e);

    private void OnMenuRun(object? sender, RoutedEventArgs e)
    {
        if (Select(sender) is { } item && Vm is { RunRequested: { } run } vm) _ = run(vm, item.IsExternal ? null : item.Name);
    }

    private void OnMenuDuplicate(object? sender, RoutedEventArgs e)
    {
        if (Select(sender) is not null) Vm!.DuplicateSetup();
    }

    private void OnMenuRemove(object? sender, RoutedEventArgs e)
    {
        if (Select(sender) is not null) Vm!.RemoveSetup();
    }

    private void OnMenuRename(object? sender, RoutedEventArgs e)
    {
        if (Select(sender) is not null) OnRenameClick(sender, e);
    }

    private void OnMenuEdit(object? sender, RoutedEventArgs e)
    {
        if (Select(sender) is not null) OpenEditor();
    }

    private void OnEditClick(object? sender, RoutedEventArgs e) => OpenEditor();

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only a card: a double-click on the list's empty space edits nothing.
        if ((e.Source as Control)?.DataContext is C3dSetupItem) OpenEditor();
    }

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { SelectedSetupItem: { IsExternal: false } item } vm) return;
        var text = await new InputNameDialog("Rename Setup", "Setup name:", item.Name).ShowDialog<string?>(this);
        if (text is null) return;
        ShowError(vm.RenameSetup(text));
    }

    private void ShowError(string? why)
    {
        ErrorText.Text = why;
        ErrorText.IsVisible = why is not null;
    }

    /// <summary>
    /// The selected setup in the <c>.cem</c> panel, in its own modal window: the view model's
    /// <see cref="C3dEditorViewModel.SetupEditor"/>, bound, so an undo that rebuilds it is followed.
    /// </summary>
    private void OpenEditor()
    {
        if (Vm is not { SelectedSetupItem: { } item } vm || vm.SetupEditor is null) return;
        var view = new EmSetupEditorView();
        view.Bind(DataContextProperty, new Binding(nameof(C3dEditorViewModel.SetupEditor)) { Source = vm });
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, Padding = new(20, 6), Margin = new(12, 8),
                                 HorizontalAlignment = HorizontalAlignment.Right };
        var body = new DockPanel();
        DockPanel.SetDock(close, Avalonia.Controls.Dock.Bottom);
        body.Children.Add(close);
        body.Children.Add(view);
        var window = new Window
        {
            Title = $"Setup — {item.Name}" + (item.IsExternal ? " (read-only)" : ""),
            Width = 520, Height = 680,
            CanResize = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = body,
        };
        close.Click += (_, _) => window.Close();
        _ = window.ShowDialog(this);
    }
}
