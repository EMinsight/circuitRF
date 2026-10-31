using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Layout.PCells.Wire;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// File ▸ Import PDK's Python-package source, steps 1 and 2 (brief-gdf-2 R-gdf2-5a): pick an
/// interpreter — those found on <c>PATH</c>, plus "Choose…" — then pick from the gdsfactory kits
/// discovery finds in its environment. Discovery reads files only; the one process it starts is the
/// interpreter answering its version.
///
/// <para>The same window in its INTERPRETER-ONLY form is the once-per-user question a workspace open
/// asks when a declared gdsfactory kit has no environment chosen on this machine (R-gdf2-2b).</para>
/// </summary>
public sealed class GdsfactoryKitDialog : Window
{
    /// <summary>What the user picked: the interpreter, and the kit in its environment.</summary>
    public sealed record Picked(string Interpreter, GdsfactoryKit Kit);

    private sealed record KitRow(GdsfactoryKit Kit, string? Refusal)
    {
        public string Title => Kit.Display;
        public string Detail => Refusal ?? $"{Kit.InstalledDirectory}" +
                                (Kit.RequiresPython is { } r ? $" — Python {r}" : "");
        public bool IsUsable => Refusal is null;
    }

    private readonly string? _module;
    private readonly ComboBox _interpreters = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 300 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, MinHeight = 34, Opacity = 0.85 };
    private readonly ListBox _kits = new() { Height = 170 };
    private readonly Button _continue = new() { Content = "Continue", Width = 100, IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly List<string> _choices = [];
    private GdsfactoryDiscovery? _discovery;
    private object? _result;

    /// <param name="module">Null for the kit picker; a module name for the interpreter-only question.</param>
    public GdsfactoryKitDialog(IReadOnlyList<string> candidates, string? module = null, string? preselect = null)
    {
        _module = module;
        Title = module is null ? "Import PDK — Python package (gdsfactory)" : "Python environment for a gdsfactory kit";
        Width = 580;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _choices.AddRange(candidates.Distinct(StringComparer.Ordinal));
        if (preselect is { Length: > 0 } && !_choices.Contains(preselect)) _choices.Insert(0, preselect);
        _interpreters.ItemsSource = _choices.ToList();

        var choose = new Button { Content = "Choose…", Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        choose.Click += async (_, _) => await ChooseInterpreterAsync();

        var cancel = new Button { Content = "Cancel", Width = 100, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close();
        _continue.Click += (_, _) => Accept();

        _kits.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<KitRow>((row, _) =>
            row is null ? new TextBlock() : new StackPanel
            {
                Margin = new Thickness(0, 2),
                Opacity = row.IsUsable ? 1.0 : 0.55,
                Children =
                {
                    new TextBlock { Text = row.Title, FontSize = 12, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = row.Detail, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
                },
            });
        _kits.SelectionChanged += (_, _) => _continue.IsEnabled = _kits.SelectedItem is KitRow { IsUsable: true };
        _interpreters.SelectionChanged += async (_, _) => await RediscoverAsync();

        var body = new StackPanel { Margin = new Thickness(20, 16), Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = module is null ? "Which Python environment has the kit installed?"
                                  : $"Which Python environment runs '{module}' on this machine?",
            FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = module is null
                ? "circuitRF lists the gdsfactory kits installed there by reading their package records — nothing " +
                  "of a kit runs until you allow it. circuitRF installs nothing."
                : "The choice is remembered for you on this machine and is never written into the workspace.",
            FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap,
        });

        var pickRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        pickRow.Children.Add(_interpreters);
        Grid.SetColumn(choose, 1);
        pickRow.Children.Add(choose);
        body.Children.Add(pickRow);
        body.Children.Add(_status);

        if (module is null)
        {
            body.Children.Add(new TextBlock { Text = "gdsfactory kits in this environment", FontSize = 12, FontWeight = FontWeight.SemiBold });
            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = _kits };
            frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("CrfTileBorderBrush"));
            body.Children.Add(frame);
        }

        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { cancel, _continue },
        });
        Content = body;

        if (_choices.Count > 0) _interpreters.SelectedIndex = 0;
        else _status.Text = "No Python interpreter was found on PATH. Choose… names one — a virtual environment's own python, for instance.";
    }

    /// <summary>
    /// The interpreters offered before the user chooses one: what <c>PATH</c> and the usual install
    /// locations hold, each probed by running it, plus every interpreter this user already chose for
    /// some other kit. Slow enough (a process per candidate) to be called off the UI thread.
    /// </summary>
    public static IReadOnlyList<string> FindCandidates(IEnumerable<string>? alsoOffer = null)
    {
        var found = new List<string>(alsoOffer ?? []);
        foreach (var (command, arguments, how) in PythonInterpreterDiscovery.Candidates())
            if (PythonInterpreterDiscovery.TryProbe(command, arguments, how, out var interpreter, out _) && interpreter is not null)
                found.Add(interpreter.ToRecord());
        return [.. found.Distinct(StringComparer.Ordinal)];
    }

    public static async Task<Picked?> PickKitAsync(Window owner, IReadOnlyList<string> candidates, string? preselect = null)
    {
        var dlg = new GdsfactoryKitDialog(candidates, module: null, preselect);
        await dlg.ShowDialog(owner);
        return dlg._result as Picked;
    }

    public static async Task<string?> PickInterpreterAsync(Window owner, string module, IReadOnlyList<string> candidates)
    {
        var dlg = new GdsfactoryKitDialog(candidates, module);
        await dlg.ShowDialog(owner);
        return dlg._result as string;
    }

    private async Task ChooseInterpreterAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a Python interpreter",
            AllowMultiple = false,
        });
        if (files.Count == 0 || files[0].Path.LocalPath is not { Length: > 0 } path) return;
        if (!_choices.Contains(path)) _choices.Insert(0, path);
        _interpreters.ItemsSource = _choices.ToList();
        _interpreters.SelectedItem = path;
    }

    /// <summary>Which <see cref="RediscoverAsync"/> is current: a later selection makes an earlier,
    /// still-running probe's answer stale, and a stale answer must not overwrite the current one.</summary>
    private int _generation;

    /// <summary>
    /// Reads the chosen environment. OFF the UI thread: it starts the interpreter (a first start after an
    /// install compiles bytecode for seconds, and a wedged interpreter waits out the probe's timeout), and
    /// a dialog that freezes while it does looks like a crash.
    /// </summary>
    private async Task RediscoverAsync()
    {
        int generation = ++_generation;
        _continue.IsEnabled = false;
        _kits.ItemsSource = null;
        if (_interpreters.SelectedItem is not string interpreter) return;
        _status.Text = $"Reading the Python environment at '{interpreter}'…";

        if (_module is not null)
        {
            string module = _module;
            var (env, gf, refusal) = await Task.Run(() =>
            {
                var e = GdsfactoryKitImport.CheckEnvironment(interpreter, module, out string? g, out string? r);
                return (e, g, r);
            });
            if (generation != _generation) return;
            _status.Text = env is null ? refusal : $"Python {env.Version}, gdsfactory {gf}.";
            _continue.IsEnabled = env is not null;
            return;
        }

        var discovery = await Task.Run(() => GdsfactoryKitDiscovery.Discover(interpreter));
        if (generation != _generation) return;
        _discovery = discovery;
        var rows = _discovery.Kits.Select(k => new KitRow(k, null))
            .Concat(_discovery.Refused.Select(r => new KitRow(r.Kit, r.Reason)))
            .ToList();
        _kits.ItemsSource = rows;
        _status.Text = _discovery.Refusal ?? (_discovery.Environment is { } e
            ? $"Python {e.Version}, gdsfactory {_discovery.GdsfactoryVersion}: {rows.Count(r => r.IsUsable)} kit(s) found."
            : "");
        if (rows.FindIndex(r => r.IsUsable) is >= 0 and var first) _kits.SelectedIndex = first;
    }

    private void Accept()
    {
        if (_interpreters.SelectedItem is not string interpreter) return;
        if (_module is not null) { _result = interpreter; Close(); return; }
        if (_kits.SelectedItem is KitRow { IsUsable: true } row) { _result = new Picked(interpreter, row.Kit); Close(); }
    }
}
