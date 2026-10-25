using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using CircuitRF.Ui;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Signals;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.DataDisplay;
using CircuitRF.Ui.Views.Signals;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.Views.Tuning;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Views.Layout;
using CircuitRF.Design.Layout;

// UiSnapshot <file.cdd> -o <out.png> [--view canvas|inspector|display] [--plot n] [--size WxH] [--results <dir>]
//            [--click x,y] [--drag x0,y0:x1,y1] [--key <Key>] [--shot <png>] ...
// UiSnapshot --signal "<carrier>" [--signal "<carrier>"] -o <out.png> [--tab n] [--fc <expr>] [--size WxH] [steps]
// UiSnapshot --standalone [--iq <file>] -o <out.png> [--tab n] [--size WxH] [steps]
// UiSnapshot --tuning <file.csch> -o <out.png> [--size WxH] [steps]
// UiSnapshot --clay <file.clay> -o <out.png> [--hide <layer>] [--lock <layer>] [--size WxH] [steps]
//            [--rclick x,y] [--menu <header>] [--type <text>] [--button <label>]
//
// Opens the display through DisplayWindowViewModel.LoadAllAsync — the GUI's own Open — and photographs one
// real view: the plot canvas of the active tab, plot n's Plot Inspector (its trace cards), or the whole Data
// Display document view (toolbar, canvas and its key bindings). The input steps run IN ORDER, as real pointer and
// key events through the window, before the final capture; --shot captures an intermediate frame.
//
// --tuning opens the Tuning window on a schematic, its rows the ones the file has ticked Tune; after each step it
// prints what holds the keyboard and every row's value, so a key's effect is read rather than guessed from pixels.
//
// --signal opens the Signal Designer window itself on a MOD_Source carrying those carriers (netlist form,
// Carrier[i]), waits for its preview — generated through the cache a simulation reads — and photographs the
// window; --tab 1 shows the second preview tab (EVM, Pulse or Frequency). --standalone opens it as Tools ▸ Signal
// Designer does, on no MOD_Source; --iq then reads an I/Q file through Load custom.
//
// --clay opens the layout editor view on a .clay, its technology resolved as the workspace resolves it, fitted. A step
// point written @x,y is in the layout's micrometres. --rclick opens the canvas's context menu, --menu clicks one of its
// items by header (prefix), --type types into the open dialog's text box and --button clicks a dialog button by its
// label. --hide and --lock untick a layer's row in the layer list. Messages print as they are posted. An open context
// menu is drawn in the window's own frame; a dialog is its own window, so a capture also writes <out>.dialog.png.

// --c3d is the 3D editor's picture: no Avalonia window, so it branches off before the headless platform starts (Snapshot3D.cs).
if (args.Contains("--c3d")) return Snapshot3D.Run(args);

string? cdd = null, output = null, view = "canvas", results = null, fc = "2", iq = null, tuning = null, clay = null;
var layerToggles = new List<(string Name, bool Hide)>();
bool standalone = false;
var signal = new List<string>();
int plotIndex = 1, previewTab = 0;
bool sizeGiven = false;
double width = 1000, height = 700;
var steps = new List<(string Kind, string Arg)>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o" when i + 1 < args.Length:     output = args[++i]; break;
        case "--view" when i + 1 < args.Length: view = args[++i]; break;
        case "--plot" when i + 1 < args.Length: plotIndex = int.Parse(args[++i]); break;
        case "--results" when i + 1 < args.Length: results = args[++i]; break;
        case "--signal" when i + 1 < args.Length: signal.Add(args[++i]); break;
        case "--tab" when i + 1 < args.Length: previewTab = int.Parse(args[++i]); break;
        case "--standalone": standalone = true; break;
        case "--iq" when i + 1 < args.Length: iq = Path.GetFullPath(args[++i]); break;
        case "--tuning" when i + 1 < args.Length: tuning = Path.GetFullPath(args[++i]); break;
        case "--clay" when i + 1 < args.Length: clay = Path.GetFullPath(args[++i]); break;
        case "--hide" when i + 1 < args.Length: layerToggles.Add((args[++i], true)); break;
        case "--lock" when i + 1 < args.Length: layerToggles.Add((args[++i], false)); break;
        case "--fc" when i + 1 < args.Length: fc = args[++i]; break;
        case "--size" when i + 1 < args.Length:
            var wh = args[++i].Split('x');
            width = double.Parse(wh[0]); height = double.Parse(wh[1]);
            sizeGiven = true;
            break;
        case "--click" or "--drag" or "--key" or "--shot" or "--rclick" or "--menu" or "--type" or "--button"
            when i + 1 < args.Length:
            steps.Add((args[i], args[++i]));
            break;
        default: cdd = args[i]; break;
    }
}
if ((cdd is null && signal.Count == 0 && !standalone && tuning is null && clay is null) || output is null || view is not ("canvas" or "inspector" or "display"))
{
    Console.Error.WriteLine("usage: UiSnapshot <file.cdd> -o <out.png> [--view canvas|inspector|display] [--plot n] [--size WxH] "
                          + "[--results <dir>] [--click x,y] [--drag x0,y0:x1,y1] [--key <Key>] [--shot <png>] ...\n"
                          + "       UiSnapshot --signal \"<carrier>\" ... -o <out.png> [--tab n] [--fc <expr>] [--size WxH] [steps]\n"
                          + "       UiSnapshot --standalone [--iq <file>] -o <out.png> [--tab n] [--size WxH] [steps]\n"
                          + "       UiSnapshot --tuning <file.csch> -o <out.png> [--size WxH] [steps]\n"
                          + "       UiSnapshot --clay <file.clay> -o <out.png> [--hide <layer>] [--lock <layer>] [--size WxH] [steps]");
    return 2;
}

AppBuilder.Configure<SnapshotApp>()
    .UseSkia()
    // UseHeadlessDrawing = false is load-bearing: the default stubs drawing out and every frame is blank.
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

Window host;
TuningTool? tuningTool = null;
LayoutCanvas? layoutCanvas = null;
LayoutEditorViewModel? layoutVm = null;
if (clay is not null)
{
    // The layout editor as the workspace opens a path-backed layout: the view model on the file, its technology
    // resolved from the document's TechRef, the layer list's own rows for hide and lock, the real LayoutEditorView.
    var model = LayoutPersistence.LoadFromFile(clay);
    layoutVm = new LayoutEditorViewModel(model, clay, new ConsoleSink());
    var (resolution, _) = TechnologyResolver.ResolveForDocument(model.TechRef, clay, null, new TechnologyCache());
    layoutVm.Technology = resolution.Tech;   // ApplyTechResolution is internal; TechRefFrom falls back to the stored TechRef
    layoutVm.OpenCreatedLayout = p => { Console.WriteLine($"opens {p}"); return Task.CompletedTask; };
    foreach (var (name, hide) in layerToggles)
    {
        var row = layoutVm.LayerRows.FirstOrDefault(r => r.Name == name);
        if (row is null)
        {
            Console.Error.WriteLine($"no layer '{name}'; the layer list has: {string.Join(", ", layoutVm.LayerRows.Select(r => r.Name))}");
            return 2;
        }
        if (hide) row.Visible = false; else row.Selectable = false;
    }
    host = new Window { Width = width, Height = height,
                        Content = new LayoutEditorView { DataContext = new LayoutDocument(Path.GetFileName(clay), layoutVm, clay) } };
    host.Show();
    Pump();
    layoutCanvas = host.GetVisualDescendants().OfType<LayoutCanvas>().First();
    layoutCanvas.ZoomToFit();
}
else if (tuning is not null)
{
    // The Tuning window as the shell hosts it: a TuningTool whose panel follows one schematic.
    var (model, _, _) = SchematicPersistence.LoadFromFile(tuning);
    tuningTool = new TuningTool();
    tuningTool.Panel.SetActiveSchematic(new SchematicViewModel(model, messageSink: null), Path.GetFileName(tuning));
    host = new Window { Width = sizeGiven ? width : 420, Height = sizeGiven ? height : 500,
                        Content = new TuningToolView { DataContext = tuningTool } };
    host.Show();
}
else if (standalone)
{
    // Tools ▸ Signal Designer: the view model's own SetStandalone, and Load custom's own path for a file.
    var designer = new SignalDesignerViewModel();
    designer.SetStandalone(preview: false);
    if (iq is not null) designer.LoadCustomFile(iq);
    host = new SignalDesignerWindow { DataContext = designer };
    if (sizeGiven) { host.Width = width; host.Height = height; }
    host.Show();
    Pump();
    Await(designer.PreviewAsync());
    designer.PreviewTab = previewTab;
    Console.WriteLine(designer.Status);
}
else if (signal.Count > 0)
{
    // The Designer as the inspector's Design signal... opens it: a MOD_Source in a schematic, SetTarget, the window.
    var model = new SchematicEditModel();
    var src = new EditableComponent { InstanceName = "SRC1", Symbol = SymbolKind.ModSource };
    src.Parameters.Add(new EditableParameter { Name = "Fc", Expression = fc, Unit = "GHz", Dimension = UnitDimension.Frequency });
    src.Parameters.Add(new EditableParameter { Name = "Pavg", Expression = "10", Unit = "dBm", Dimension = UnitDimension.Power });
    for (int k = 0; k < signal.Count; k++)
        src.Parameters.Add(new EditableParameter { Name = $"Carrier[{k + 1}]", Expression = "\"" + signal[k] + "\"" });
    model.Components.Add(src);
    var designer = new SignalDesignerViewModel();
    designer.SetTarget(new SchematicViewModel(model, messageSink: null), src, preview: false);
    host = new SignalDesignerWindow { DataContext = designer };
    if (sizeGiven) { host.Width = width; host.Height = height; }
    host.Show();
    Pump();
    Await(designer.PreviewAsync());
    designer.PreviewTab = previewTab;
    Console.WriteLine(designer.Status);
}
else
{
var document = new DataDisplayDocumentViewModel();
var window   = document.Window;
// A relative source reference resolves against a workspace's results/ folder, which in the application the
// workspace supplies. Here it is --results, or the .cdd's own folder — where `circuitrf plot --write-cdd` puts
// a document beside its result.
string resultsRoot = Path.GetFullPath(results ?? Path.GetDirectoryName(Path.GetFullPath(cdd))!);
window.DataSourceLibrary.ResultsRootProvider = () => resultsRoot;
window.DataSourceLibrary.RefreshAvailableDataSources();
// Never block on a Task here: its continuations are posted to this same dispatcher, so GetResult() deadlocks.
Await(window.LoadAllAsync(Path.GetFullPath(cdd)));

var tab = window.ActiveTab ?? throw new InvalidOperationException("the display opened no tab");
Control content;
if (view == "canvas")
    content = new PlotCanvasView { DataContext = tab };
else if (view == "display")
    content = new DataDisplayView { DataContext = new DataDisplayDocument(Path.GetFileName(cdd), document, cdd) };
else
{
    var plots = tab.DataDisplay.Plots;
    if (plotIndex < 1 || plotIndex > plots.Count)
    {
        Console.Error.WriteLine($"--plot {plotIndex}: the active tab has {plots.Count} plot(s)");
        return 2;
    }
    content = new PlotInspectorView { DataContext = plots[plotIndex - 1].Inspector };
}

host = new Window { Width = width, Height = height, Content = content };
host.Show();
}
Pump();

foreach (var (kind, arg) in steps)
{
    switch (kind)
    {
        case "--click":
        {
            var p = PointOf(arg);
            host.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
            host.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
            break;
        }
        case "--drag":
        {
            // Pressed, moved in steps — a gesture crosses a click threshold before it becomes a drag — and released.
            var ends = arg.Split(':');
            Point a = PointOf(ends[0]), b = PointOf(ends[1]);
            host.MouseDown(a, MouseButton.Left, RawInputModifiers.None);
            for (int k = 1; k <= 10; k++)
            {
                host.MouseMove(a + (b - a) * (k / 10.0), RawInputModifiers.LeftMouseButton);
                Pump();
            }
            host.MouseUp(b, MouseButton.Left, RawInputModifiers.None);
            break;
        }
        case "--key":
        {
            // "Back", "Delete", "Escape", or with modifiers: "Meta+Z", "Ctrl+Shift+A".
            var parts = arg.Split('+');
            var mods  = RawInputModifiers.None;
            foreach (var m in parts[..^1])
                mods |= Enum.Parse<RawInputModifiers>(m.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ? "Control" : m, ignoreCase: true);
            var key = Enum.Parse<Key>(parts[^1], ignoreCase: true);
            host.KeyPress(key, mods, PhysicalKey.None, null);
            host.KeyRelease(key, mods, PhysicalKey.None, null);
            break;
        }
        case "--shot":
            Capture(arg);
            break;
        case "--rclick":
        {
            var p = PointOf(arg);
            host.MouseDown(p, MouseButton.Right, RawInputModifiers.None);
            host.MouseUp(p, MouseButton.Right, RawInputModifiers.None);
            break;
        }
        case "--menu":
        {
            // The item's own Click handler, raised on the real MenuItem the menu was built with; then the menu closes,
            // as a click on an item closes it.
            var menu = OpenContextMenu() ?? throw new InvalidOperationException("--menu: no context menu is open");
            var all = MenuItems(menu.Items.OfType<MenuItem>()).ToList();
            var item = all.FirstOrDefault(m => m.Header is string h && h.StartsWith(arg, StringComparison.Ordinal));
            if (item is null)
                throw new InvalidOperationException($"--menu '{arg}': the menu has {string.Join(" | ", all.Select(m => m.Header))}");
            if (!item.IsEnabled) Console.WriteLine($"--menu '{arg}': the item is disabled ({ToolTip.GetTip(item)})");
            else item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
            break;
        }
        case "--type":
        {
            var dialog = TopDialog() ?? throw new InvalidOperationException("--type: no dialog is open");
            var box = dialog.GetVisualDescendants().OfType<TextBox>().First();
            box.Focus();
            box.SelectAll();
            dialog.KeyTextInput(arg);
            break;
        }
        case "--button":
        {
            // A real press and release on the button's centre, in the dialog's own window.
            var dialog = TopDialog() ?? throw new InvalidOperationException("--button: no dialog is open");
            var buttons = dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible).ToList();
            var button = buttons.FirstOrDefault(b => b.Content is string c && c == arg)
                ?? throw new InvalidOperationException($"--button '{arg}': the dialog has {string.Join(" | ", buttons.Select(b => b.Content))}");
            var c = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseDown(c, MouseButton.Left, RawInputModifiers.None);
            dialog.MouseUp(c, MouseButton.Left, RawInputModifiers.None);
            break;
        }
    }
    Pump();
    if (tuningTool is not null)
    {
        var focused = host.FocusManager?.GetFocusedElement();
        string tag = focused is Control { Tag: CircuitRF.Ui.Tuning.TuningRowViewModel r } ? $" (row {r.Key})" : "";
        Console.WriteLine($"{kind} {arg}: focus {focused?.GetType().Name ?? "none"}{tag}; "
                        + string.Join(", ", tuningTool.Panel.Rows.Select(r => $"{r.Key}={r.ValueBoxText}")));
    }
}

Capture(output);
return 0;

void Pump()
{
    for (int i = 0; i < 8; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        host?.UpdateLayout();
    }
}

void Capture(string path)
{
    Pump();
    host.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}");
    if (TopDialog() is { } dialog)
    {
        string d = Path.ChangeExtension(path, ".dialog.png");
        dialog.CaptureRenderedFrame()!.Save(d);
        Console.WriteLine($"wrote {d} ({dialog.Title})");
    }
}

Window? TopDialog() => host.OwnedWindows.LastOrDefault(w => w.IsVisible);

ContextMenu? OpenContextMenu() => layoutCanvas?.ContextMenu is { IsOpen: true } m ? m : null;

static IEnumerable<MenuItem> MenuItems(IEnumerable<MenuItem> items)
{
    foreach (var m in items)
    {
        yield return m;
        foreach (var sub in MenuItems(m.Items.OfType<MenuItem>())) yield return sub;
    }
}

// x,y in the window's DIPs; @x,y in the layout's micrometres, through the canvas's own viewport.
Point PointOf(string xy)
{
    if (xy.StartsWith('@') && layoutCanvas is not null && layoutVm is not null)
    {
        var w = ParsePoint(xy[1..]);
        double dbu = layoutVm.Model.DbuPerMicron;
        var (sx, sy) = layoutCanvas.WorldToScreen(w.X * dbu, w.Y * dbu);
        return layoutCanvas.TranslatePoint(new Point(sx, sy), host)!.Value;
    }
    return ParsePoint(xy);
}

static Point ParsePoint(string xy)
{
    var parts = xy.Split(',');
    return new Point(double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                     double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
}

static void Await(Task task)
{
    var until = DateTime.UtcNow.AddSeconds(120);
    while (!task.IsCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    if (!task.IsCompleted) throw new TimeoutException("the display did not finish loading");
    task.GetAwaiter().GetResult();
}

/// <summary>The application's own styles, resources and view templates, without circuitRF's App — whose
/// start-up opens windows and restores a workspace. Unlike tools/DocGen's DocsApp it merges no docs-only paint
/// remap, so the picture is what the application paints.</summary>
sealed class SnapshotApp : Application
{
    public override void Initialize()
    {
        var baseUri = new Uri("avares://CircuitRF.Ui/");
        Resources.MergedDictionaries.Add(
            new ResourceInclude(baseUri) { Source = new Uri("avares://CircuitRF.Ui/Styles/CircuitRfResources.axaml") });
        Styles.Add(
            new StyleInclude(baseUri) { Source = new Uri("avares://CircuitRF.Ui/Styles/CircuitRfStyles.axaml") });
        var app = new App();
        app.Initialize();
        foreach (var template in app.DataTemplates) DataTemplates.Add(template);
    }
}

/// <summary>The Messages panel, for --clay: each line printed as it is posted.</summary>
sealed class ConsoleSink : IMessageSink
{
    public void Post(MessageLevel level, string text, string? filePath = null) => Console.WriteLine($"[{level}] {text}");
    public void Clear() { }
}
