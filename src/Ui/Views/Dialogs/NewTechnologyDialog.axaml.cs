using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What a new technology is copied from. <see cref="Catalog"/> is any technology the catalog offers —
/// shipped or user-installed (<c>TechnologyCatalog.All</c>), so a stackup circuitRF ships is offered here as it is by
/// New Workspace; <see cref="File"/> is a <c>.ctech</c> the user picks.</summary>
public enum NewTechnologyStarter { Pcb, Mmic, Empty, Catalog, File }

/// <summary>
/// Result returned by <see cref="NewTechnologyDialog"/> on OK. Name is the validated technology
/// name (also used as the .ctech file stem — technology names, like cell names, are filesystem
/// path components, so <see cref="NameValidator"/> is sufficient without a separate slug step).
/// <paramref name="CatalogId"/> is set for <see cref="NewTechnologyStarter.Catalog"/>,
/// <paramref name="FilePath"/> for <see cref="NewTechnologyStarter.File"/>.
/// </summary>
public sealed record NewTechnologyResult(string Name, NewTechnologyStarter Starter, bool SetAsDefault,
                                         string? CatalogId = null, string? FilePath = null);

/// <summary>
/// Custom "New Technology…" modal. Mirrors <see cref="NewWorkspaceDialog"/>'s return-or-null
/// ShowDialog contract and live-validation idiom, simplified: a technology always saves into the
/// current workspace's <c>tech/</c> folder, so there is no parent-location picker.
/// </summary>
public partial class NewTechnologyDialog : Window
{
    private readonly string? _techDir;

    /// <summary>One row of the starting-point list.</summary>
    private sealed record StarterItem(string Label, NewTechnologyStarter Kind, string? CatalogId = null)
    {
        public override string ToString() => Label;
    }

    private static readonly StarterItem FromFileItem = new("From a .ctech file…", NewTechnologyStarter.File);
    private StarterItem? _lastPicked;
    private string? _pickedFile;
    private bool _picking;

    public NewTechnologyDialog()
    {
        InitializeComponent();
        // The three built-in starters first, then every technology the catalog holds — shipped and the user's own —
        // by its own name, then a file. The catalog is what New Workspace offers, so a stackup circuitRF ships is
        // never missing here.
        var items = new List<StarterItem>
        {
            new("PCB (2-layer) starter", NewTechnologyStarter.Pcb),
            new("MMIC starter", NewTechnologyStarter.Mmic),
            new("Empty", NewTechnologyStarter.Empty),
        };
        foreach (var e in TechnologyCatalog.All)
            items.Add(new(e.IsUserInstalled ? $"{e.Name} (yours)" : e.Name, NewTechnologyStarter.Catalog, e.Id));
        items.Add(FromFileItem);
        StarterCombo.ItemsSource = items;
        StarterCombo.SelectedIndex = 0;
    }

    /// <param name="techDir">Absolute path of the workspace's tech/ folder (may not exist yet).</param>
    /// <param name="suggestedName">Initial name shown in the field, pre-selected.</param>
    public NewTechnologyDialog(string techDir, string suggestedName) : this()
    {
        _techDir = techDir;
        NameBox.Text = suggestedName;
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        UpdateView();
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e) => UpdateView();

    private void OnOkClick(object? sender, RoutedEventArgs e) => TryCommit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            TryCommit();
            e.Handled = true;
        }
    }

    private void TryCommit()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (NameValidator.Validate(name) is not null) return;
        if (_techDir is not null && File.Exists(Path.Combine(_techDir, $"{name}.ctech"))) return;

        if (StarterCombo.SelectedItem is not StarterItem item) return;
        if (item.Kind == NewTechnologyStarter.File && _pickedFile is null) return;

        Close(new NewTechnologyResult(name, item.Kind, SetAsDefaultCheck.IsChecked == true,
                                      item.CatalogId, item.Kind == NewTechnologyStarter.File ? _pickedFile : null));
    }

    /// <summary>Choosing the file row opens the picker at once; cancelling it puts the previous row back, so the
    /// list never rests on a file row with no file.</summary>
    private async void OnStarterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_picking || StarterCombo is null || StarterCombo.SelectedItem is not StarterItem item) return;
        if (item.Kind != NewTechnologyStarter.File) { _lastPicked = item; UpdateView(); return; }

        _picking = true;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Start From a Technology",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("circuitRF Technology") { Patterns = ["*.ctech"] }],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                _pickedFile = path;
                _lastPicked = item;
            }
            else
                StarterCombo.SelectedItem = _lastPicked ?? (StarterCombo.ItemsSource as IList<StarterItem>)![0];
        }
        finally { _picking = false; }
        UpdateView();
    }

    private void UpdateView()
    {
        var name      = NameBox.Text?.Trim() ?? "";
        var nameError = NameValidator.Validate(name);

        string? message = nameError;
        if (message is null && _techDir is not null && name.Length > 0
            && File.Exists(Path.Combine(_techDir, $"{name}.ctech")))
        {
            message = $"A technology named '{name}' already exists.";
        }

        ValidationMessage.Text      = message;
        ValidationMessage.IsVisible = message is not null;

        bool fileRow = StarterCombo?.SelectedItem is StarterItem { Kind: NewTechnologyStarter.File };
        if (StarterFileLabel is not null)
        {
            StarterFileLabel.Text      = fileRow && _pickedFile is not null ? _pickedFile : "";
            StarterFileLabel.IsVisible = fileRow && _pickedFile is not null;
        }

        OkButton.IsEnabled = name.Length > 0 && message is null && (!fileRow || _pickedFile is not null);

        if (name.Length > 0 && message is null)
        {
            PreviewLabel.Text      = $"Will create: tech/{name}.ctech";
            PreviewLabel.IsVisible = true;
        }
        else
        {
            PreviewLabel.IsVisible = false;
        }
    }
}
