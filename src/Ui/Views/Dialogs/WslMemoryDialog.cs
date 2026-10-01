using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Wsl;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// brief-em3d-97 R-em3d97-2 — <i>Linux subsystem memory</i>: the VM's memory now, the value proposed, the one
/// line of <c>.wslconfig</c> that changes, and what a restart stops. Three buttons: <b>Save and restart the
/// subsystem</b> (no second question — this dialog IS the question, so it names what stops and the button says
/// "restart"), <b>Save only</b>, and <b>Cancel</b>. Everything it shows and does is
/// <see cref="WslMemoryDialogModel"/>'s; this file only lays it out.
/// </summary>
public static class WslMemoryDialog
{
    /// <summary>
    /// Reads the subsystem (off the UI thread — it can start the VM), shows the dialog, and posts what the chosen
    /// button did to <paramref name="messages"/>. Does nothing where there is no <c>wsl.exe</c>.
    /// </summary>
    /// <param name="preferredDistribution">The distribution to ask about the VM when none is running: Palace's.</param>
    /// <returns>True when the file was written.</returns>
    public static async Task<bool> OpenAsync(Window? owner, IMessageSink? messages, string? preferredDistribution = null)
    {
        if (SolverDiscovery.Palace.Subsystem is not { Available: true } wsl) return false;
        owner ??= App.DialogOwner();
        if (owner is null) return false;
        string? refusal = null;
        var model = await Task.Run(() => WslMemoryDialogModel.Load(wsl, preferredDistribution, WslPalace.WslConfigPath(),
                                                                   MachineMemory.PhysicalBytes, out refusal));
        if (model is null)
        {
            messages?.Warning(refusal ?? "The Linux subsystem could not be read.");
            return false;
        }
        var choice = await ShowAsync(owner, model);
        if (choice is not { } restart) return false;
        var outcome = await Task.Run(() => restart ? model.SaveAndRestart() : model.SaveOnly());
        if (messages is not null) WslMemoryDialogModel.Report(messages, outcome, model.ConfigPath);
        if (outcome.Written) Changed?.Invoke();
        return outcome.Written;
    }

    /// <summary>Raised after a write, so a Settings line showing the figure reads it again.</summary>
    public static event Action? Changed;

    /// <summary>The dialog itself: null for Cancel, true for Save and restart, false for Save only.</summary>
    public static async Task<bool?> ShowAsync(Window owner, WslMemoryDialogModel model)
    {
        bool? result = null;
        var dialog = new Window
        {
            Title                 = "Linux Subsystem Memory",
            Width                 = 560,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
        };
        dialog.Content = Build(model, r => { result = r; dialog.Close(); }, () => dialog.Close());
        ModalPromptFront.Attach(dialog);
        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>The dialog's content, separate from the window so it can be rendered on its own.</summary>
    public static Control Build(WslMemoryDialogModel model, Action<bool> chose, Action cancel)
    {
        static SelectableTextBlock Text(string s, double opacity = 0.85, FontWeight weight = FontWeight.Normal) => new()
        {
            Text = s, TextWrapping = TextWrapping.Wrap, Opacity = opacity, FontWeight = weight,
        };
        static SelectableTextBlock Heading(string s) => new()
        {
            Text = s, FontWeight = FontWeight.SemiBold, Margin = new Avalonia.Thickness(0, 6, 0, 0),
        };

        var body = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 6 };

        body.Children.Add(Heading("Now"));
        body.Children.Add(Text(model.SubsystemLine));
        body.Children.Add(Text(model.FileLine));
        body.Children.Add(Text(model.HostLine));

        body.Children.Add(Heading("Proposed"));
        var box = new NumericUpDown
        {
            Minimum = WslMemory.MinimumGb, Maximum = model.MaximumGb, Increment = 1, Value = model.Proposed,
            FormatString = "0", Width = 140, ParsingNumberStyle = System.Globalization.NumberStyles.Integer,
        };
        var boxRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        boxRow.Children.Add(box);
        boxRow.Children.Add(new TextBlock { Text = $"GB  (from {WslMemory.MinimumGb} to {model.MaximumGb})", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 });
        body.Children.Add(boxRow);
        var valueRefusal = Text("", 1);
        valueRefusal.Foreground = Brushes.IndianRed;
        body.Children.Add(valueRefusal);

        body.Children.Add(Heading("The change"));
        var before = Text("", 0.85);
        var after = Text("", 0.85);
        before.FontFamily = after.FontFamily = new FontFamily("Menlo, Consolas, monospace");
        body.Children.Add(before);
        body.Children.Add(after);
        var repeat = Text("", 0.75);
        body.Children.Add(repeat);
        var link = new Button
        {
            Content = model.ConfigPath, Padding = new Avalonia.Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Foreground = Brushes.SteelBlue,
        };
        ToolTip.SetTip(link, "Show the file");
        link.Click += (_, _) => FileReveal.Reveal(File.Exists(model.ConfigPath) ? model.ConfigPath : Path.GetDirectoryName(model.ConfigPath));
        body.Children.Add(link);

        body.Children.Add(Heading("What restarting stops"));
        body.Children.Add(Text(WslMemoryDialogModel.RestartSentence));
        body.Children.Add(Text(model.RunningLine));
        body.Children.Add(Text(WslMemoryDialogModel.SaveOnlyNote, 0.7));
        if (model.RestartBlocked is { } blocked)
        {
            var b = Text(blocked, 1);
            b.Foreground = Brushes.DarkOrange;
            body.Children.Add(b);
        }

        var restart = new Button { Content = "Save and restart the subsystem", IsDefault = true };
        var save = new Button { Content = "Save only" };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true };
        restart.Click += (_, _) => chose(true);
        save.Click += (_, _) => chose(false);
        cancelButton.Click += (_, _) => cancel();
        if (model.RestartBlocked is { } why) ToolTip.SetTip(restart, why);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(restart);
        buttons.Children.Add(save);
        buttons.Children.Add(cancelButton);
        body.Children.Add(buttons);

        void Refresh()
        {
            model.Proposed = (int)Math.Round(box.Value ?? model.Proposed);
            valueRefusal.Text = model.ValueRefusal ?? "";
            valueRefusal.IsVisible = model.ValueRefusal is not null;
            before.Text = "Before:  " + model.BeforeLine;
            after.Text  = "After:   " + model.AfterLine;
            repeat.Text = model.RepeatNote ?? "";
            repeat.IsVisible = model.RepeatNote is not null;
            restart.IsEnabled = model.CanRestart;
            save.IsEnabled = model.CanSave;
        }
        box.ValueChanged += (_, _) => Refresh();
        Refresh();
        return body;
    }

    /// <summary>Opens the dialog from a Messages row or the 150 % confirmation, on the UI thread.</summary>
    public static Task OpenFromRow(IMessageSink? messages, string? preferredDistribution = null)
        => Dispatcher.UIThread.InvokeAsync(() => OpenAsync(null, messages, preferredDistribution));
}
