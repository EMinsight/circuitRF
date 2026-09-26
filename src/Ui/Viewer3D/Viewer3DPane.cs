// brief-em3d-28 R-em3d28-1b — the 3D pane: composition GPU interop (em-3d.md §8.3), in the spike's
// InteropPane shape, which passed every counter on the owner's Mac.
//
//   * a RENDER THREAD of its own draws each frame into a free image of a three-image swapchain;
//   * the swapchain is imported into the compositor ONCE (and again only on a resize or a re-host);
//   * the UI thread's share of a frame (OnTick) is handing the finished image over and planning the
//     next — it never touches geometry and never waits on the GPU.
//
// So the compositor's share of a 3D frame is placing a finished image, and a frame still rendering or a
// problem still regenerating leaves the previous image up: the rest of the window cannot be starved by
// it (the lesson of "the whole UI crawled", src/Ui/RESOLVED.md).
//
// INPUT (R-em3d28-4a, brief-em3d-43 R-em3d43-2a): orbit = left drag; pan = middle drag, right drag or
// Alt + left drag; a click selects and Shift-click adds or removes; a right-CLICK (no drag) opens the
// selection's context menu; zoom to the cursor = wheel or pinch. The keys are the view model's
// (Viewer3DViewModel.HandleKey): O / F / V the selection mode, B / Shift+B next behind / in front, Home
// fits, P toggles the projection, 1–7 the standard views, C the clip plane, A the axis indicator, Esc
// cancels the gesture in progress or clears the selection. A trackpad's two-finger scroll zooms and its pinch zooms (macOS). No
// modal dialog opens mid-gesture (§8.2 point 5): nothing here opens a dialog at all.
//
// A present step that cannot signal is a FAULT (Viewer3DPresentFault): the pane stops, says why in
// its place, and never loops (R-em3d28-1d).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using CircuitRF.Render.Scene3D;

namespace CircuitRF.Ui.Viewer3D;

public sealed class Viewer3DPane : Control
{
    /// <summary>Images in the swapchain: one being drawn, one being shown, one spare.</summary>
    public const int SwapchainImages = 3;

    /// <summary>How long the render thread waits for the compositor to release an image before it
    /// counts a release timeout (reported on the status line, never retried in a loop).</summary>
    public const int ReleaseTimeoutMs = 250;

    /// <summary>A press and release closer than this (DIPs) is a click, not a drag: a mouse click or a
    /// trackpad tap routinely reports a pixel of motion.</summary>
    public const double ClickSlopDips = 3;

    private Viewer3DViewModel? _vm;
    private Compositor? _compositor;
    private ICompositionGpuInterop? _interop;
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceVisual? _visual;
    private readonly Action _tick;
    private bool _tickQueued;
    private int _imgW, _imgH;
    private int _attachGen;
    private TopLevel? _topLevel;

    private Thread? _thread;
    private readonly AutoResetEvent _go = new(false);
    private volatile bool _stop, _busy, _done;
    private readonly Scene3DFramePlan _plan = new();
    private Scene3DModel _planScene = Scene3DModel.Empty();
    private Scene3DOverlay _pMesh = Scene3DOverlay.None, _pSection = Scene3DOverlay.None, _pGrid = Scene3DOverlay.None;
    private CircuitRF.Render.Scene3D.Fields.Scene3DFieldGeometry _pField = CircuitRF.Render.Scene3D.Fields.Scene3DFieldGeometry.None;
    private bool _pOrbit;
    private int _nextImage, _doneImage;
    private ulong _frame, _doneValue;
    private volatile string? _fault;

    /// <summary>Why the pane is not drawing, or null while it is.</summary>
    public string? Fault => _fault;

    /// <summary>Raised on the UI thread after each presented frame — the overlay redraws its axes.</summary>
    public event Action? FramePresented;

    /// <summary>A right-click that did not move — the view's context menu. A right DRAG still pans.</summary>
    public event Action? ContextMenuRequested;

    /// <summary>Raised when the pane faults or recovers, for the view to show the reason.</summary>
    public event Action<string?>? FaultChanged;

    public int ReleaseTimeouts { get; private set; }

    public Viewer3DPane()
    {
        _tick = OnTick;
        AddHandler(PointerTouchPadGestureMagnifyEvent, OnMagnify);
        ClipToBounds = true;
        Focusable = true;
        Background = Brushes.Transparent;
    }

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<Viewer3DPane>();

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    /// <summary>Hit-testable everywhere: the composition visual draws on top, but pointer input is ours.</summary>
    public override void Render(DrawingContext context) => context.FillRectangle(Background ?? Brushes.Transparent, new Rect(Bounds.Size));

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.FrameRequested -= RequestFrame;
        _vm = DataContext as Viewer3DViewModel;
        if (_vm is not null) _vm.FrameRequested += RequestFrame;
        RequestFrame();
    }

    // ── hosting ─────────────────────────────────────────────────────────────────────────────

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // A Dock float or re-dock can detach and re-attach before the await below returns: only the
        // LATEST attach may build a surface and start a render thread, or a second thread would share
        // _go, _plan and the images with the first, and no detach would ever join it.
        int gen = ++_attachGen;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.ScalingChanged += OnScalingChanged;
        try
        {
            var visual = ElementComposition.GetElementVisual(this);
            if (visual is null) { SetFault("the view has no composition visual."); return; }
            var compositor = visual.Compositor;
            var interop = await compositor.TryGetCompositionGpuInterop();
            if (gen != _attachGen) return;
            _compositor = compositor;
            _interop = interop;
            if (_interop is null) { SetFault("this window's compositor offers no GPU interop, so the 3D view cannot present."); return; }
            if (_vm is null) return;
            var backend = _vm.Session.EnsureBackend();
            if (backend.CheckInterop(_interop) is { } why) { SetFault($"{backend.Description}: {why}."); return; }

            _surface = _compositor.CreateDrawingSurface();
            _visual = _compositor.CreateSurfaceVisual();
            _visual.Surface = _surface;
            _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            ElementComposition.SetElementChildVisual(this, _visual);

            if (_thread is { IsAlive: false }) _thread = null;
            if (_thread is not null)
            {
                // The last detach could not join its thread (it outlived Join's timeout): it must stop
                // before a new one starts, never be revived by clearing _stop under it.
                SetFault("the previous render thread has not stopped; close and reopen the 3D view.");
                return;
            }
            _stop = false;
            _thread = new Thread(RenderLoop) { IsBackground = true, Name = "viewer3d-render" };
            _thread.Start();
            SetFault(null);
            RequestFrame();
        }
        catch (Exception ex) { SetFault(ex.Message); }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ++_attachGen;
        if (_topLevel is not null) { _topLevel.ScalingChanged -= OnScalingChanged; _topLevel = null; }
        _stop = true;
        _go.Set();
        if (_thread is null || _thread.Join(2000)) _thread = null;
        ElementComposition.SetElementChildVisual(this, null);
        // The images belong to THIS compositor; the device and the uploaded buffers stay with the
        // session, so a re-dock re-imports three images and uploads no geometry.
        if (_vm?.Session.Backend is { } b) lock (_vm.Session.RenderLock) b.ReleaseImages();
        _surface?.Dispose(); _surface = null;
        _imgW = _imgH = 0;
        _busy = _done = false;
        _tickQueued = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            if (_visual is not null) _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            _vm?.Resized((float)Bounds.Width, (float)Bounds.Height);
            RequestFrame();
        }
    }

    /// <summary>The window moved to a monitor of another scale: the images are re-sized on the next frame.</summary>
    private void OnScalingChanged(object? sender, EventArgs e) => RequestFrame();

    private void SetFault(string? why)
    {
        if (_fault == why) return;
        _fault = why;
        FaultChanged?.Invoke(why);
    }

    public void RequestFrame()
    {
        if (_compositor is null || _tickQueued || _fault is not null) return;
        _tickQueued = true;
        _compositor.RequestCompositionUpdate(_tick);
    }

    /// <summary>The UI thread's whole per-frame cost: present what is finished, plan the next.</summary>
    private void OnTick()
    {
        _tickQueued = false;
        if (_surface is null || _stop || _vm is null) return;
        var s = _vm.Session;
        var backend = s.Backend;
        if (backend is null) return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)), h = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
        var view = _vm.View;
        s.Ui.BeginFrame(view.Orbiting);

        if (_done)
        {
            try { backend.Present(_surface, _doneImage, _doneValue); }
            catch (Exception ex) { SetFault(ex.Message); }
            _done = false;
            _busy = false;
            _vm.OnPicked(backend.PickedId, backend.PickedFace, backend.PickedPoint, backend.PickedSomething, backend.PickPatch);
            FramePresented?.Invoke();
        }

        bool more = view.Orbiting;
        if (!_busy && _fault is null)
        {
            try
            {
                if (w != _imgW || h != _imgH)
                {
                    lock (s.RenderLock) backend.CreateImages(_interop!, w, h, SwapchainImages);
                    _imgW = w; _imgH = h;
                }
                // The cursor is in device pixels for the ID pass.
                float cx = view.CursorX, cy = view.CursorY;
                if (cx >= 0) { view.CursorX = (float)(cx * scale); view.CursorY = (float)(cy * scale); }
                // brief-em3d-44: the ID pass reads the snap's patch around the cursor, in device pixels.
                _plan.PickSize = _vm.PickSizeFor(scale, backend.MaxPickSize);
                _plan.PickPixelsPerDip = (float)scale;
                _plan.Plan(_vm.Scene, view, w, h, backend.FlipY, pick: view.CursorX >= 0,
                           _vm.MeshOverlay, _vm.SectionOverlay, _vm.GridOverlay, _vm.FieldGeometry);
                _pField = _vm.FieldGeometry;
                view.CursorX = cx; view.CursorY = cy;
                _planScene = _vm.Scene;
                (_pMesh, _pSection, _pGrid) = (_vm.MeshOverlay, _vm.SectionOverlay, _vm.GridOverlay);
                _pOrbit = view.Orbiting;
                view.Orbiting = false;
                _nextImage = (int)(_frame % SwapchainImages);
                _frame++;
                _busy = true;
                _go.Set();
            }
            catch (Exception ex) { SetFault(ex.Message); }
        }
        s.Ui.EndFrame();
        if (more || _busy) RequestFrame();
    }

    private void RenderLoop()
    {
        while (true)
        {
            _go.WaitOne();
            if (_stop) return;
            var s = _vm!.Session;
            int image = _nextImage;
            ulong value = _frame;
            try
            {
                // The whole frame, the wait included, holds the render lock: a tab closed mid-frame
                // disposes the session under this lock, and a backend freed during WaitReusable would
                // leave a native call (D3D11's AcquireSync) on a released object — an access violation
                // no catch can stop. The UI thread takes the lock only to create or release images,
                // which it never does while a frame is in flight.
                lock (s.RenderLock)
                {
                    if (s.Backend is not { } backend) return;      // the session was disposed
                    if (!backend.WaitReusable(image, ReleaseTimeoutMs))
                    {
                        // The compositor still holds this image (a minimised or occluded window may not
                        // composite at all). Never draw into an image we do not hold — D3D11's keyed
                        // mutex and Vulkan's release semaphore both forbid it — so this frame is skipped,
                        // counted, and asked for again; nothing loops here.
                        ReleaseTimeouts++;
                        _busy = false;
                        Avalonia.Threading.Dispatcher.UIThread.Post(RequestFrame, Avalonia.Threading.DispatcherPriority.Background);
                        continue;
                    }
                    if (!s.Frame(image, _plan, value, _planScene, _pMesh, _pSection, _pGrid, _pOrbit, _pField)) return;
                }
                _doneImage = image;
                _doneValue = value;
                _done = true;
            }
            catch (Exception ex)
            {
                _fault = ex.Message;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => FaultChanged?.Invoke(ex.Message));
                return;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(RequestFrame);
        }
    }

    // ── input (R-em3d28-4a) ─────────────────────────────────────────────────────────────────

    private Point? _last, _pressedAt;
    private bool _orbiting, _panning;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetCurrentPoint(this);
        _last = _pressedAt = p.Position;
        // brief-em3d-43: Shift + left is Shift-CLICK now (add to the selection), so a Shift + left DRAG still
        // pans — decided on release, by whether it moved — and Alt + left pans outright.
        // brief-em3d-46 R-em3d46-5 — a plain left press on a gizmo handle is a constrained move, not an orbit: the
        // drag's moves are hovers (the snap and the preview follow them) and the release commits.
        if (p.Properties.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None && _vm?.PressGizmo() == true)
        {
            _gizmoDrag = true;
            _orbiting = _panning = _rightPressed = _shiftPress = false;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        _shiftPress = p.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _panning = p.Properties.IsMiddleButtonPressed || (p.Properties.IsLeftButtonPressed && (_shiftPress || e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
                   || p.Properties.IsRightButtonPressed;
        _orbiting = !_panning && p.Properties.IsLeftButtonPressed;
        _rightPressed = p.Properties.IsRightButtonPressed;
        _pressModifiers = e.KeyModifiers;
        _pressClicks = e.ClickCount;
        _moved = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private bool _moved, _rightPressed, _shiftPress;
    // brief-em3d-45 — what the press carried, for the drawing: Ctrl/Cmd for a plane gesture, 2 for a double-click.
    private KeyModifiers _pressModifiers;
    private int _pressClicks = 1;

    /// <summary>brief-em3d-46 — a gizmo handle is being dragged.</summary>
    private bool _gizmoDrag;

    /// <summary>A drag (orbit, pan or a gizmo handle) is under way — the mode keys wait for it (R-em3d43-2a).</summary>
    public bool GestureInProgress => ((_orbiting || _panning) && _moved) || _gizmoDrag;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_last is { } last && (_orbiting || _panning))
        {
            var d = pos - last;
            if (_pressedAt is { } at && Math.Abs(pos.X - at.X) + Math.Abs(pos.Y - at.Y) > ClickSlopDips) _moved = true;
            if (_orbiting) _vm?.Orbit((float)d.X, (float)d.Y);
            else _vm?.Pan((float)d.X, (float)d.Y, (float)Bounds.Height);
            _last = pos;
        }
        _vm?.SetGeometrySnapSuspended(e.KeyModifiers.HasFlag(KeyModifiers.Alt));
        _vm?.SetShiftHeld(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        _vm?.Hover((float)pos.X, (float)pos.Y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_gizmoDrag)
        {
            _gizmoDrag = false;
            e.Pointer.Capture(null);
            _vm?.ReleaseGizmo();
            return;
        }
        if (_orbiting && !_moved) _vm?.Click(shift: false, _pressModifiers, _pressClicks);
        else if (_shiftPress && !_moved) _vm?.Click(shift: true, _pressModifiers, _pressClicks);
        bool menu = _rightPressed && !_moved && e.InitialPressMouseButton == MouseButton.Right;
        _orbiting = _panning = _rightPressed = _shiftPress = false;
        _last = _pressedAt = null;
        e.Pointer.Capture(null);
        if (menu) ContextMenuRequested?.Invoke();
    }

    /// <summary>Alt-Tab or a focus steal mid-drag loses the capture and the release never arrives:
    /// without this, every later hover would keep orbiting or panning until the next click.</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _orbiting = _panning = _rightPressed = false;
        _last = _pressedAt = null;
        if (_gizmoDrag) { _gizmoDrag = false; _vm?.CancelGizmo(); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_orbiting && !_panning) _vm?.Leave();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        _vm?.Zoom((float)e.Delta.Y, (float)p.X, (float)p.Y, (float)Bounds.Width, (float)Bounds.Height);
        e.Handled = true;
    }

    private void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        var p = e.GetPosition(this);
        // A pinch reports a relative magnification; ~0.1 per notch reads naturally on a trackpad.
        _vm?.Zoom((float)(e.Delta.X * 10), (float)p.X, (float)p.Y, (float)Bounds.Width, (float)Bounds.Height);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_vm is null) return;
        // Esc with a drag under way cancels the drag: the camera stays where the drag left it (a camera
        // move is not an edit), and the button's release is no longer a click.
        if (e.Key == Key.Escape && GestureInProgress && !_gizmoDrag)
        {
            _orbiting = _panning = _rightPressed = _shiftPress = false;
            _last = _pressedAt = null;
            e.Handled = true;
            return;
        }
        if (e.Key is Key.LeftAlt or Key.RightAlt) _vm.SetGeometrySnapSuspended(true);
        if (e.Key is Key.LeftShift or Key.RightShift) _vm.SetShiftHeld(true);
        // brief-em3d-46 — keys during a gizmo drag belong to the move it started (X/Y/Z, a typed distance, Esc).
        if (_gizmoDrag)
        {
            if (_vm.HandleKey(e.Key, e.KeyModifiers, gestureInProgress: false)) e.Handled = true;
            if (e.Key == Key.Escape) { _gizmoDrag = false; _vm.CancelGizmo(); }
            return;
        }
        if (_vm.HandleKey(e.Key, e.KeyModifiers, GestureInProgress)) e.Handled = true;
    }

    /// <summary>brief-em3d-44 — Alt / Option released: geometry snap resumes.</summary>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.LeftAlt or Key.RightAlt) _vm?.SetGeometrySnapSuspended(false);
        if (e.Key is Key.LeftShift or Key.RightShift) _vm?.SetShiftHeld(false);
    }

    /// <summary>The latched-key lesson: a key-up delivered to another window never reaches this one, so every
    /// held-key latch is cleared on losing focus — otherwise geometry snap would silently stay suspended.</summary>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _vm?.ClearHeldKeys();
        _vm?.SetShiftHeld(false);
    }
}
