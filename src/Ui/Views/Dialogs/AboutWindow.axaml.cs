using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // One source for the version, and it is the build's own: see CircuitRF.Ui.AppVersion.
        VersionText.Text  = $"Version {AppVersion.Display}";
        PlatformText.Text = AppVersion.Platform;

        // The kernel names ITSELF (brief-em3d-62 R-em3d62-6d): the line is the capability the one client
        // established — the version the worker REPORTED in its handshake (brief-em3d-63) — asked off the UI
        // thread when nothing has asked yet, so the dialog opens at once and the line fills in.
        GeometryKernelText.Text = GeometryKernel.AboutNotice(GeometryKernel.Shared.Known);
        if (GeometryKernel.Shared.Known is null) _ = FillGeometryKernelAsync();

        NoticesButton.IsEnabled = File.Exists(NoticesPath);
    }

    /// <summary>The notices file every installer carries beside the executable (the .csproj copies it).</summary>
    private static string NoticesPath => Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");

    private async Task FillGeometryKernelAsync()
    {
        var cap = await GeometryKernel.Shared.ProbeAsync();
        await Dispatcher.UIThread.InvokeAsync(() => GeometryKernelText.Text = GeometryKernel.AboutNotice(cap));
    }

    private async void OnAcknowledgmentsClicked(object? sender, RoutedEventArgs e)
    {
        await new AcknowledgmentsWindow().ShowDialog(this);
    }

    // The copy the installer carries beside the executable, opened with whatever reads Markdown here.
    private void OnNoticesClicked(object? sender, RoutedEventArgs e)
    {
        string path = NoticesPath;
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
