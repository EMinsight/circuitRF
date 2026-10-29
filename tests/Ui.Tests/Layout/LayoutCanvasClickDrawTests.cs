// The two-point draw tools through the REAL canvas: pointer events raised on a laid-out LayoutCanvas,
// so the press, the bare move and the release take the same route a mouse does. Click-then-click and
// press-drag-release must both draw.

using Avalonia;
using Avalonia.Input;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Tests.RailRf;

namespace CircuitRF.Ui.Tests.Layout;

public sealed class LayoutCanvasClickDrawTests
{
    private static readonly IPointer Mouse = new Avalonia.Input.Pointer(1, PointerType.Mouse, true);

    private static LayoutCanvas Canvas()
    {
        RailBoardViewTests.CursorFactoryShim.Install();
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        model.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });
        var canvas = new LayoutCanvas { ViewModel = new LayoutEditorViewModel(model) };
        canvas.Measure(new Size(800, 600));
        canvas.Arrange(new Rect(0, 0, 800, 600));
        canvas.ZoomToFit();
        canvas.ViewModel!.ActiveTool = LayoutEditorViewModel.Tool.Rect;
        return canvas;
    }

    private static void Press(LayoutCanvas c, Point p) => c.RaiseEvent(new PointerPressedEventArgs(
        c, Mouse, c, p, 0, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
        KeyModifiers.None, 1));

    private static void Move(LayoutCanvas c, Point p, bool held) => c.RaiseEvent(new PointerEventArgs(
        InputElement.PointerMovedEvent, c, Mouse, c, p, 0,
        new PointerPointProperties(held ? RawInputModifiers.LeftMouseButton : RawInputModifiers.None, PointerUpdateKind.Other),
        KeyModifiers.None));

    private static void Release(LayoutCanvas c, Point p) => c.RaiseEvent(new PointerReleasedEventArgs(
        c, Mouse, c, p, 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
        KeyModifiers.None, MouseButton.Left));

    [Fact]
    public void ClickThenClick_DrawsARectangle()
    {
        var c = Canvas();
        Press(c, new Point(300, 200));
        Release(c, new Point(301, 200));          // a click with a pixel of jitter
        Move(c, new Point(450, 350), held: false);
        Assert.True(c.ViewModel!.IsDrawingRect);
        Press(c, new Point(450, 350));
        Release(c, new Point(450, 350));
        Assert.Equal(2, c.ViewModel.Model.Shapes.Count);
        Assert.False(c.ViewModel.IsDrawingRect);
    }

    [Fact]
    public void PressDragRelease_DrawsARectangle()
    {
        var c = Canvas();
        Press(c, new Point(300, 200));
        Move(c, new Point(450, 350), held: true);
        Release(c, new Point(450, 350));
        Assert.Equal(2, c.ViewModel!.Model.Shapes.Count);
    }
}
