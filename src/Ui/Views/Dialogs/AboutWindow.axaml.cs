using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // One source for the version, and it is the build's own: see CircuitRF.Ui.AppVersion.
        VersionText.Text  = $"Version {AppVersion.Display}";
        PlatformText.Text = AppVersion.Platform;

        // The geometry kernel names ITSELF: the worker is asked for its OCCT version off the UI thread
        // (a process start, ~40 ms), so the dialog opens at once and the line fills in.
        _ = FillGeometryKernelAsync();

        NoticesButton.IsEnabled = File.Exists(GeometryKernelNotice.NoticesPath(AppContext.BaseDirectory));
    }

    private async Task FillGeometryKernelAsync()
    {
        string text = await Task.Run(() => GeometryKernelNotice.ProbeAsync(AppContext.BaseDirectory, TimeSpan.FromSeconds(10)));
        await Dispatcher.UIThread.InvokeAsync(() => GeometryKernelText.Text = text);
    }

    private async void OnAcknowledgmentsClicked(object? sender, RoutedEventArgs e)
    {
        await new AcknowledgmentsWindow().ShowDialog(this);
    }

    // The copy the installer carries beside the executable, opened with whatever reads Markdown here.
    private void OnNoticesClicked(object? sender, RoutedEventArgs e)
    {
        string path = GeometryKernelNotice.NoticesPath(AppContext.BaseDirectory);
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start(new ProcessStartInfo("open", ["-t", path]) { UseShellExecute = false });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo("notepad.exe", [path]) { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("xdg-open", [path]) { UseShellExecute = false });
        }
        catch (Exception) { /* no viewer; the file is beside the application either way */ }
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
        => Close();
}
