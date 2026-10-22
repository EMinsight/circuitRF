using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Settings ▸ Solvers (brief-em3d-6 R-em3d6-4a): one row per program a 3D run can need, each saying what
/// <see cref="SolverDiscovery"/> found. The same discovery a run calls at its top, so the row and the
/// run never disagree.
///
/// <para><b>Blank is the default and means "search".</b> It is never seeded with a discovered path — a
/// seeded value would freeze today's answer into <c>preferences.json</c> and stop tracking a program
/// the user later installs or upgrades, the rule <see cref="VerilogACompilerSettingsView"/> states for
/// the same reason.</para>
/// </summary>
public partial class Em3dSolverSettingsView : UserControl
{
    /// <summary>The populate guard — opening and closing Settings without touching anything must write
    /// nothing. Same reason as <see cref="VerilogACompilerSettingsView"/>'s.</summary>
    private bool _loading;

    public Em3dSolverSettingsView()
    {
        InitializeComponent();
        Load();
        // Loaded fires when the tab is first SHOWN, not when the dialog is built: asking three programs
        // their version is not a cost every opening of Settings should pay.
        Loaded += (_, _) => { RefreshAll(); RefreshLocations(); RefreshKernel(); };
        KernelTitle.Text = GeometryKernel.SettingsTitle;
        // brief-em3d-24 — an install that ends (however it ends) changes what discovery finds.
        CircuitRF.Ui.Views.Dialogs.SolverInstallRunner.Finished += OnInstallFinished;
        CircuitRF.Ui.Views.Dialogs.SolverRemovalRunner.Finished += OnRemovalFinished;
        WslMemoryDialog.Changed += OnSubsystemMemoryChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            // brief-emi-6 R-emi6-2 — an open EM Setup panel re-reads which solvers are installed when this page closes.
            _ = CircuitRF.Ui.Layout.Em.Em3dSolverInstallState.RefreshAsync();
            WslMemoryDialog.Changed -= OnSubsystemMemoryChanged;
            CircuitRF.Ui.Views.Dialogs.SolverInstallRunner.Finished -= OnInstallFinished;
            CircuitRF.Ui.Views.Dialogs.SolverRemovalRunner.Finished -= OnRemovalFinished;
        };
    }

    private void OnInstallFinished(SolverTool tool) => Dispatcher.UIThread.Post(RefreshAll);

    // ── brief-em3d-63 R-em3d63-3d — the geometry kernel's row ────────────────────────────────────

    /// <summary>Shows what the background probe found — probing now if nothing has asked yet — and the disk cache's size.</summary>
    private void RefreshKernel()
    {
        ShowKernel(GeometryKernel.Shared.Known);
        if (GeometryKernel.Shared.Known is null) _ = ProbeKernelAsync();
    }

    private async Task ProbeKernelAsync()
    {
        KernelCheck.IsEnabled = false;
        try
        {
            var cap = await GeometryKernel.Shared.ProbeAsync();
            ShowKernel(cap);
        }
        finally { KernelCheck.IsEnabled = true; }
    }

    private void ShowKernel(GeometryKernelCapability? cap)
    {
        KernelStatus.Text = GeometryKernel.SettingsStatus(cap);
        KernelPath.Text = cap?.WorkerPath ?? "";
        KernelPath.IsVisible = cap?.WorkerPath is not null;
        _ = Task.Run(() => GeometryKernel.Shared.DiskCacheBytes()).ContinueWith(t =>
            Dispatcher.UIThread.Post(() => KernelCache.Text = $"Built shapes kept on disk: {SolverUninstaller.Size(t.Result)}"),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void OnKernelCheckAgain(object? sender, RoutedEventArgs e) => _ = ProbeKernelAsync();

    private void OnKernelClearCache(object? sender, RoutedEventArgs e)
    {
        GeometryKernel.Shared.ClearDiskCache();
        ShowKernel(GeometryKernel.Shared.Known);
    }

    private void OnRemovalFinished() => Dispatcher.UIThread.Post(RefreshAll);

    public void Load()
    {
        _loading = true;
        try
        {
            var p = AppPreferencesIo.Load();
            PalaceBox.Text  = p.Em3dPalacePath  ?? "";
            GmshBox.Text    = p.Em3dGmshPath    ?? "";
            OpenEmsBox.Text = p.Em3dOpenEmsPath ?? "";
            EmergeBox.Text  = p.Em3dEmergePath  ?? "";
            MpiBox.Text     = p.Em3dMpiLauncherPath ?? "";
            string device = CircuitRF.Ui.Layout.Em.EmEmergeDevicePreference.Preferred.Id();
            EmergeDeviceBox.SelectedItem = EmergeDeviceBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == device);
            MemoryLimitBox.PlaceholderText = CircuitRF.Ui.Layout.Em.EmMemoryLimitPreference.AutomaticText;
            MemoryLimitBox.Text = CircuitRF.Ui.Layout.Em.EmMemoryLimitPreference.Preferred is { } gb
                ? gb.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "";
        }
        finally { _loading = false; }
    }

    private (SolverDiscovery Discovery, TextBlock Status, Button Install, StackPanel Removals)[] Rows =>
    [
        (SolverDiscovery.Palace,  PalaceStatus,  PalaceInstall,  PalaceRemovals),
        (SolverDiscovery.Gmsh,    GmshStatus,    GmshInstall,    GmshRemovals),
        (SolverDiscovery.OpenEms, OpenEmsStatus, OpenEmsInstall, OpenEmsRemovals),
        (SolverDiscovery.Emerge,  EmergeStatus,  EmergeInstall,  EmergeRemovals),
    ];

    private void RefreshAll()
    {
        foreach (var (discovery, status, install, _) in Rows) Refresh(discovery, status, install);
        RefreshMpi();
        RefreshRemovals();
    }

    /// <summary>
    /// brief-em3d-25 — one <i>Uninstall …</i> per home circuitRF installed, newest first, each with its size
    /// measured now; a home other than the newest is labelled as the older version it is (R-em3d25-3). A
    /// program circuitRF did not install has no button here, whatever its row says.
    /// </summary>
    private void RefreshRemovals()
    {
        _ = Task.Run(() =>
        {
            // PlanAll measures every home once, and sees an interrupted removal's leftovers, which only
            // Remove all offers to finish.
            var plan  = new SolverUninstaller().PlanAll();
            var homes = Enum.GetValues<SolverTool>().ToDictionary(t => t, t => plan.Homes.Where(h => h.Record.Tool == t)
                .Select(h => (h.Record, h.Bytes, h.Missing)).ToList());
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (discovery, _, _, panel) in Rows)
                {
                    panel.Children.Clear();
                    var list = homes[discovery.Tool];
                    for (int i = 0; i < list.Count; i++)
                    {
                        var (record, bytes, missing) = list[i];
                        // brief-em3d-26 — a home inside a Linux subsystem distribution says so; one that has
                        // vanished there is offered as the clean-up of circuitRF's record, not as an uninstall.
                        string where = record.Distribution is { } d ? $" in '{d}'" : "";
                        string label = missing
                            ? $"Clean up {discovery.Name} {record.Version}{where} (missing)…"
                            : i == 0
                            ? $"Uninstall {discovery.Name} {record.Version}{where} ({SolverUninstaller.Size(bytes)})…"
                            : $"Remove older {discovery.Name} {record.Version}{where} ({SolverUninstaller.Size(bytes)})…";
                        var button = new Button
                        {
                            Content = label, FontSize = 11, Padding = new Avalonia.Thickness(8, 3),
                            Margin = new Avalonia.Thickness(0, i == 0 ? 6 : 0, 0, 0),
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        };
                        ToolTip.SetTip(button, missing
                            ? $"circuitRF installed this at {record.Home} in the Linux subsystem distribution '{record.Distribution}', and it is no longer there. This removes circuitRF's record of it."
                            : $"Installed by circuitRF at {record.Home}{(record.Distribution is { } dist ? $" in the Linux subsystem distribution '{dist}'" : "")}. Shows the space it frees before anything is removed; your documents are not touched.");
                        var tool = discovery.Tool;
                        string version = record.Version;
                        button.Click += async (_, _) =>
                        {
                            button.IsEnabled = false;
                            await CircuitRF.Ui.Views.Dialogs.SolverRemovalRunner.RemoveAsync(
                                u => u.PlanOne(tool, version), TopLevel.GetTopLevel(this) as Window, messages: null,
                                $"Uninstall {discovery.Name} {version}");
                            button.IsEnabled = true;
                        };
                        panel.Children.Add(button);
                    }
                }
                RemoveAll.IsVisible = plan.CanProceed;
                RemoveAllNote.IsVisible = plan.CanProceed && !OperatingSystem.IsWindows();
            });
        });
    }

    /// <summary>brief-em3d-25 R-em3d25-2 — every circuitRF-installed solver, after one confirmation.</summary>
    private async void OnRemoveAll(object? sender, RoutedEventArgs e)
    {
        RemoveAll.IsEnabled = false;
        try
        {
            await CircuitRF.Ui.Views.Dialogs.SolverRemovalRunner.RemoveAsync(
                u => u.PlanAll(), TopLevel.GetTopLevel(this) as Window, messages: null, "Remove all 3D solvers");
        }
        finally { RemoveAll.IsEnabled = true; }
    }

    private (SolverDiscovery, TextBlock, Button, StackPanel) Row(SolverTool tool) => Rows.First(r => r.Discovery.Tool == tool);

    /// <summary>The MPI row: which mpirun a Palace run would use, found against the Palace the
    /// Palace row finds — the Spack route needs to know which Palace it is asking about.</summary>
    private void RefreshMpi()
    {
        MpiStatus.Text = "Checking…";
        _ = Task.Run(() =>
        {
            string text;
            try
            {
                var found = SolverDiscovery.Palace.Find(out _);
                if (found is { Distribution: { } distro } && SolverDiscovery.Palace.Subsystem is { } wsl)
                {
                    // brief-em3d-26 R-em3d26-2b — the MPI beside a subsystem Palace, found inside it.
                    var session = new CircuitRF.Design.Em3d.Wsl.WslSession(wsl, distro);
                    var (path, how) = session.Home(out string? why) is { } home
                        ? CircuitRF.Design.Em3d.Wsl.WslPalace.FindMpiLauncher(session, home, found.Path)
                        : (null, why ?? "the distribution did not answer");
                    text = path is not null
                        ? $"{path} in the Linux subsystem distribution '{distro}' ({how}). The path above is not used for it."
                        : $"Not found: {how}. Palace will run on one core.";
                }
                else
                {
                    var launcher = PalaceRun.FindMpiLauncher(found?.Path ?? "");
                    text = launcher.Path is { } path
                        ? $"{path} ({launcher.How})."
                        : $"Not found: {launcher.How}. Palace will run on one core.";
                }
            }
            catch (Exception ex) { text = ex.Message; }
            Dispatcher.UIThread.Post(() => ShowStatus(MpiStatus, text));
        });
    }

    /// <summary>
    /// brief-em3d-26 R-em3d26-1d — the location row: Automatic, Native, and one entry per Linux subsystem
    /// distribution, listed off the UI thread (listing starts no distribution). Windows only; elsewhere the
    /// row stays hidden, because there is only one location.
    /// </summary>
    private void RefreshLocations()
    {
        if (SolverDiscovery.Palace.Subsystem is not { } wsl) return;
        _ = Task.Run(() =>
        {
            var state = CircuitRF.Design.Em3d.Wsl.WslDistributions.Read(wsl);
            Dispatcher.UIThread.Post(() =>
            {
                _loading = true;
                try
                {
                    var saved = PalaceLocation.Parse(AppPreferencesIo.Load().Em3dPalaceLocation);
                    var items = new List<ComboBoxItem>
                    {
                        new() { Content = "Automatic", Tag = PalaceLocation.Automatic },
                        new() { Content = "Native (this computer only)", Tag = PalaceLocation.Native },
                    };
                    foreach (var d in state.Distributions)
                        items.Add(new() { Content = $"Linux subsystem: {d.Name}{(d.Version < 2 ? " (WSL 1 — not usable)" : "")}",
                                          Tag = PalaceLocation.Subsystem(d.Name) });
                    if (saved.Kind == PalaceLocationKind.Subsystem && !state.Distributions.Any(d =>
                            string.Equals(d.Name, saved.Distribution, StringComparison.OrdinalIgnoreCase)))
                        items.Add(new() { Content = $"Linux subsystem: {saved.Distribution} (not installed)", Tag = saved });
                    PalaceLocationBox.ItemsSource = items;
                    PalaceLocationBox.SelectedItem = items.FirstOrDefault(i => i.Tag is PalaceLocation l && l.Kind == saved.Kind &&
                        string.Equals(l.Distribution, saved.Distribution, StringComparison.OrdinalIgnoreCase)) ?? items[0];
                    PalaceLocationRow.IsVisible = true;
                    if (!state.Ready) ToolTip.SetTip(PalaceLocationRow, state.Refusal);
                }
                finally { _loading = false; }
                RefreshSubsystemMemory();
            });
        });
    }

    // ── brief-em3d-97 R-em3d97-3 — the subsystem's memory ─────────────────────────────────────

    private string? _subsystemDistribution;

    private void OnSubsystemMemoryChanged() => Dispatcher.UIThread.Post(RefreshSubsystemMemory);

    /// <summary>
    /// "Linux subsystem: 3.8 GB of 7.6 GB", read live (<c>free -b</c> in the distribution) every time it is shown —
    /// a stored figure would be a second copy of <c>.wslconfig</c>'s. Shown only when Palace's location is a
    /// distribution, or Automatic finds Palace in one.
    /// </summary>
    private void RefreshSubsystemMemory()
    {
        if (SolverDiscovery.Palace.Subsystem is not { Available: true } wsl) return;
        var location = PalaceLocation.Parse(AppPreferencesIo.Load().Em3dPalaceLocation);
        _ = Task.Run(() =>
        {
            string? distro = location.Kind switch
            {
                PalaceLocationKind.Subsystem => location.Distribution,
                PalaceLocationKind.Native    => null,
                _                            => SolverDiscovery.Palace.Find(out _)?.Distribution,
            };
            long? bytes = distro is null ? null : new CircuitRF.Design.Em3d.Wsl.WslSession(wsl, distro).MemoryBytes();
            string line = CircuitRF.Ui.Layout.Em.WslMemoryDialogModel.SettingsLine(bytes, MachineMemory.PhysicalBytes);
            Dispatcher.UIThread.Post(() =>
            {
                _subsystemDistribution = distro;
                SubsystemMemoryRow.IsVisible = distro is not null;
                SubsystemMemoryText.Text = line;
            });
        });
    }

    private async void OnChangeSubsystemMemory(object? sender, RoutedEventArgs e)
    {
        var messages = (App.LastActiveWorkspace?.DataContext as ViewModels.WorkspaceViewModel)?.Messages;
        await WslMemoryDialog.OpenAsync(TopLevel.GetTopLevel(this) as Window, messages, _subsystemDistribution);
    }

    private void OnLocationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || PalaceLocationBox.SelectedItem is not ComboBoxItem { Tag: PalaceLocation location }) return;
        AppPreferencesIo.Update(p => p.Em3dPalaceLocation = location.ToSetting());
        Refresh(SolverDiscovery.Palace, PalaceStatus, PalaceInstall);
        RefreshMpi();
        RefreshSubsystemMemory();
    }

    /// <summary>Runs discovery for one row off the UI thread and writes its answer back —
    /// <see cref="SolverStatus.Of"/>, which <c>circuitrf solver list</c> prints too.</summary>
    private void Refresh(SolverDiscovery discovery, TextBlock status, Button install)
    {
        status.Text = "Checking…";
        install.IsVisible = false;
        bool emerge = discovery.Tool == SolverTool.Emerge;
        if (emerge) { EmergeAccelerate.IsVisible = false; EmergeEnableGpu.IsVisible = false; }
        _ = Task.Run(() =>
        {
            string text;
            bool offer = false;
            RecipePrerequisite? accelerate = null;
            (SolverRecipe, InstallRecord, RecipePrerequisite)? enableGpu = null;
            bool found = false;
            try
            {
                // brief-emi-3 R-emi3-3 — Settings being shown is one of the two points circuitRF finishes EMerge's faster
                // sparse solver once what it needs has appeared. Never asks: the install it belongs to was agreed to.
                if (emerge) EmergeAccelerator.FinishIfDue(discovery.Find(out _));
                // brief-em3d-136 R-em3d136-4b — the GPU line only for a user who chose GPU: a CPU user pays no GPU solve.
                var row = SolverStatus.Of(discovery.Tool, discovery,
                                          gpu: CircuitRF.Ui.Layout.Em.EmPalaceDevicePreference.Preferred == PalaceDevice.Gpu);
                text  = row.GpuLine is { } gpuLine ? row.Summary + Environment.NewLine + gpuLine : row.Summary;
                offer = row.OfferInstall;
                found = row.Found is not null;
                if (emerge) accelerate = EmergeAccelerator.InstallerFor(row.Found);
                if (emerge) enableGpu = EmergeAccelerator.GpuEnableFor(row.Found);
            }
            catch (Exception ex) { text = ex.Message; }
            Dispatcher.UIThread.Post(() =>
            {
                ShowStatus(status, text);
                if (emerge) ShowAccelerate(accelerate);
                if (emerge)
                {
                    EmergeEnableGpu.Tag = enableGpu;
                    EmergeEnableGpu.IsVisible = enableGpu is not null && accelerate is null;
                    EmergeEnableGpu.IsEnabled = !CircuitRF.Ui.Views.Dialogs.SolverInstallRunner.IsRunning(SolverTool.Emerge);
                    // Owner request, 2026-10-22: Device chooses where EMerge solves, so with no EMerge there is nothing
                    // to choose. The stored preference is kept, and applies again once EMerge is found.
                    EmergeDeviceBox.IsEnabled = found;
                    ToolTip.SetTip(EmergeDeviceBox, found ? DeviceTip : NoEmergeDeviceTip);
                }
                bool running = CircuitRF.Ui.Views.Dialogs.SolverInstallRunner.IsRunning(discovery.Tool);
                install.IsVisible = offer || running;
                install.IsEnabled = !running;
                install.Content   = running ? $"Installing {discovery.Name}… (see Messages)" : $"Install {discovery.Name}…";
            });
        });
    }

    /// <summary>
    /// A status line, with Spack's path padding folded to one "…". A Spack install tree pads every
    /// prefix with a chain of <c>__spack_path_placeholder__</c> directories (<c>padded_length</c>),
    /// which printed in full wrapped a single path over ten rows; the chain carries nothing a reader
    /// can use, and the full text stays on the tooltip for anyone who needs to copy it.
    /// </summary>
    private static void ShowStatus(TextBlock status, string text)
    {
        if (AnonymizeHomeForCapture) text = AnonymizeHome(text);
        status.Text = FoldSpackPadding(text);
        status.Tag  = text;   // the unfolded line, which Copy puts on the clipboard
        ToolTip.SetTip(status, status.Text == text ? null : text);
    }

    /// <summary>
    /// The docs seam (brief-em3d-52): the user guide's picture of this tab is captured on whatever machine runs DocGen,
    /// and its status lines are that machine's real discovery — paths under the author's home directory, which must
    /// never reach the repository. Set only by the figure fixture; a user always sees their own paths.
    /// </summary>
    internal static bool AnonymizeHomeForCapture { get; set; }

    /// <summary>The home directory written as <c>~</c>, keeping the path's shape.</summary>
    internal static string AnonymizeHome(string text)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 1 ? text.Replace(home, "~", StringComparison.Ordinal) : text;
    }

    /// <summary>Right-click ▸ Copy on a status line: the selection when there is one, otherwise the
    /// whole line UNFOLDED — the folded "…" is not a path anyone can paste into a terminal.</summary>
    private void OnCopyStatusClick(object? sender, RoutedEventArgs e)
    {
        if (OwningStatus(sender) is not { } status) return;
        string selected = status.SelectedText;
        CopyToClipboard(selected.Length > 0 ? selected : StatusText(status));
    }

    /// <summary>Right-click ▸ Copy All: every row, labelled, unfolded — what someone asking "what did it
    /// find?" needs in one paste.</summary>
    private void OnCopyAllStatusClick(object? sender, RoutedEventArgs e)
        => CopyToClipboard(string.Join(Environment.NewLine,
               $"Palace: {StatusText(PalaceStatus)}",
               $"MPI launcher: {StatusText(MpiStatus)}",
               $"Gmsh: {StatusText(GmshStatus)}",
               $"openEMS: {StatusText(OpenEmsStatus)}",
               $"EMerge: {StatusText(EmergeStatus)}"));

    private static string StatusText(TextBlock status) => status.Tag as string ?? status.Text ?? "";

    /// <summary>A ContextMenu lives in its own popup tree, so the owning line is the menu's Parent,
    /// not an ancestor of the MenuItem (the walk <c>MessagesView</c> uses).</summary>
    private static SelectableTextBlock? OwningStatus(object? sender)
    {
        for (var current = sender as Avalonia.LogicalTree.ILogical; current is not null; current = current.LogicalParent)
            if (current is ContextMenu menu) return menu.Parent as SelectableTextBlock;
        return null;
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) _ = clipboard.SetTextAsync(text);
    }

    internal static string FoldSpackPadding(string text) => SpackInstalls.FoldPadding(text);

    /// <summary>brief-emi-3 R-emi3-4 — the prerequisite's own installer, labelled with the recipe's name for it, while
    /// one is waiting (and not already started from here).</summary>
    private void ShowAccelerate(RecipePrerequisite? prerequisite)
    {
        EmergeAccelerate.IsVisible = prerequisite is not null;
        EmergeAccelerate.Tag       = prerequisite;
        EmergeAccelerate.IsEnabled = !_accelerateWatching;
        if (prerequisite is not null)
            EmergeAccelerate.Content = _accelerateWatching ? $"Waiting for {prerequisite.Name}…" : $"Install {prerequisite.Name}…";
    }

    /// <summary>One watch per process: a second Settings window must not start the installer twice.</summary>
    private static bool _accelerateWatching;

    /// <summary>
    /// brief-emi-3 R-emi3-4 — starts the prerequisite's own installer, which asks the user itself, then looks every few
    /// seconds until it has installed, and builds EMerge's faster sparse solver then (R-emi3-3). The user presses
    /// this and nothing else.
    /// </summary>
    private async void OnEmergeAccelerate(object? sender, RoutedEventArgs e)
    {
        if (EmergeAccelerate.Tag is not RecipePrerequisite { Installer.Count: > 0 } prerequisite || _accelerateWatching) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(prerequisite.Installer[0]) { UseShellExecute = false };
            foreach (string a in prerequisite.Installer.Skip(1)) psi.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowStatus(EmergeStatus, $"The installer for {prerequisite.Name} could not be started: {ex.Message}");
            return;
        }
        _accelerateWatching = true;
        ShowAccelerate(prerequisite);
        try
        {
            await EmergeAccelerator.FinishWhenInstalledAsync(() => SolverDiscovery.Emerge.Find(out _),
                                                             TimeSpan.FromSeconds(5), TimeSpan.FromHours(2), CancellationToken.None);
        }
        finally
        {
            _accelerateWatching = false;
            Dispatcher.UIThread.Post(() => Refresh(SolverDiscovery.Emerge, EmergeStatus, EmergeInstall));
        }
    }

    /// <summary>brief-emi-9 R-emi9-2b — consent, then Rust fetched into EMerge's home, the GPU solver built, the toolchain deleted.</summary>
    private async void OnEmergeEnableGpu(object? sender, RoutedEventArgs e)
    {
        if (EmergeEnableGpu.Tag is not ValueTuple<SolverRecipe, InstallRecord, RecipePrerequisite> (var recipe, var record, var when)) return;
        EmergeEnableGpu.IsEnabled = false;
        try { await CircuitRF.Ui.Views.Dialogs.SolverInstallRunner.EnableEmergeGpuAsync(recipe, record, when, TopLevel.GetTopLevel(this) as Window, null); }
        catch (Exception ex) { ShowStatus(EmergeStatus, ex.Message); }
        finally { Refresh(SolverDiscovery.Emerge, EmergeStatus, EmergeInstall); }
    }

    /// <summary>brief-emi-9 R-emi9-4 — stores where EMerge factorises; Automatic is stored as nothing.</summary>
    private const string DeviceTip =
        "Automatic uses EMerge's GPU sparse solver, when it is installed, for a driven problem large enough to be faster on it, " +
        "and solves a sweep again on the CPU if the GPU's does not converge. Eigenmode problems always solve on the CPU.";

    private const string NoEmergeDeviceTip = "Where EMerge solves. Install EMerge, or name one above, to choose.";

    private void OnEmergeDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || EmergeDeviceBox.SelectedItem is not ComboBoxItem { Tag: string id } || EmergeDevices.Parse(id) is not { } device) return;
        if (CircuitRF.Ui.Layout.Em.EmEmergeDevicePreference.Preferred != device)
            CircuitRF.Ui.Layout.Em.EmEmergeDevicePreference.Preferred = device;
    }

    /// <summary>brief-em3d-24 — the row's Install …: consent, then the install in the background.</summary>
    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } button || !Enum.TryParse<SolverTool>(name, out var tool)) return;
        button.IsEnabled = false;
        try
        {
            var install = CircuitRF.Ui.Views.Dialogs.SolverInstallRunner.InstallAsync(tool, TopLevel.GetTopLevel(this) as Window, messages: null);
            var (discovery, status, btn, _) = Row(tool);
            Refresh(discovery, status, btn);
            await install;
        }
        catch (Exception ex) { ShowStatus(Row(tool).Item2, ex.Message); }
    }

    /// <summary>
    /// Writes the named path. Blank clears it back to "search", stored as null rather than as an empty
    /// string so "never chosen" and "deliberately cleared" stay the same state.
    /// </summary>
    private void OnPathCommitted(object? sender, RoutedEventArgs e)
    {
        if (_loading || sender is not TextBox box) return;

        string? typed = box.Text?.Trim() is { Length: > 0 } t ? t : null;
        if (box == PalaceBox)       { AppPreferencesIo.Update(p => p.Em3dPalacePath  = typed); Refresh(SolverDiscovery.Palace,  PalaceStatus,  PalaceInstall); RefreshMpi(); }
        else if (box == MpiBox)     { AppPreferencesIo.Update(p => p.Em3dMpiLauncherPath = typed); RefreshMpi(); }
        else if (box == GmshBox)    { AppPreferencesIo.Update(p => p.Em3dGmshPath    = typed); Refresh(SolverDiscovery.Gmsh,    GmshStatus,    GmshInstall); }
        else if (box == OpenEmsBox) { AppPreferencesIo.Update(p => p.Em3dOpenEmsPath = typed); Refresh(SolverDiscovery.OpenEms, OpenEmsStatus, OpenEmsInstall); }
        else if (box == EmergeBox)  { AppPreferencesIo.Update(p => p.Em3dEmergePath  = typed); Refresh(SolverDiscovery.Emerge,  EmergeStatus,  EmergeInstall); }
    }

    /// <summary>brief-emi-10 R-emi10-4 — stores the memory limit, blank as Automatic; a refused value is said and not stored.</summary>
    private void OnMemoryLimitCommitted(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        string text = MemoryLimitBox.Text?.Trim() ?? "";
        string? refused = text.Length == 0 ? CircuitRF.Ui.Layout.Em.EmMemoryLimitPreference.TrySet(null)
            : double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gb)
                ? CircuitRF.Ui.Layout.Em.EmMemoryLimitPreference.TrySet(gb)
                : $"A memory limit for a solver is a number of GB, not '{text}'.";
        MemoryLimitStatus.Text = refused ?? "";
        MemoryLimitStatus.IsVisible = refused is not null;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string boxName } || this.FindControl<TextBox>(boxName) is not { } box) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title         = "Choose the program",
                AllowMultiple = false,
            });
            if (files.Count != 1 || files[0].TryGetLocalPath() is not { Length: > 0 } path) return;

            box.Text = path;
            OnPathCommitted(box, e);
        }
        catch (Exception) { /* a cancelled or unavailable picker is not an error */ }
    }
}
