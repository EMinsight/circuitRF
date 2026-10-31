using System.Collections.Generic;
using Avalonia.Input;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A closed symbol shape's fill strength (<see cref="SymbolFill"/>): 0 % the outline alone, 100 % solid, anything
/// between the outline over a fill at that strength — stored without changing any existing file, drawn, clicked,
/// typed and dragged.
/// </summary>
public sealed class SymbolFillTests
{
    private static RectPrimitive Rect(double fill)
    {
        var r = new RectPrimitive { Cx = 0, Cy = 0, W = 200, H = 200, StrokeTier = SymbolStrokeTier.Thick };
        SymbolFill.Set(r, fill);
        return r;
    }

    [Fact]
    public void APartialFill_IsStoredAsTint_SolidAsFilled_AndNoneAsNeither()
    {
        string Json(double f) => SymbolPersistence.Serialize(new Symbol([Rect(f)], []));

        Assert.DoesNotContain("Tint", Json(0));
        Assert.Contains("\"Filled\": false", Json(0));
        Assert.DoesNotContain("Tint", Json(1));
        Assert.Contains("\"Filled\": true", Json(1));

        var back = (RectPrimitive)SymbolPersistence.Deserialize(Json(0.25)).Primitives[0];
        Assert.Equal(0.25, SymbolFill.Of(back), 6);
        Assert.False(back.Filled);

        // Both set — only by hand — is solid.
        Assert.Equal(1, SymbolFill.Of(new RectPrimitive { Filled = true, Tint = 0.3 }));
    }

    [Fact]
    public void AShapeIsDrawnAtItsFillStrength_UnderAFullStrengthOutline()
    {
        Assert.Equal(0, CentreAlpha(0));
        Assert.InRange(CentreAlpha(0.25), 62, 66);
        Assert.InRange(CentreAlpha(0.6), 151, 155);
        Assert.Equal(255, CentreAlpha(1));
        Assert.True(PixelAlpha(Rect(0.25), 10, 50) >= 250);
    }

    [Fact]
    public void AnyFillAboveZero_MakesTheInsidePartOfTheShapeForAClick()
    {
        Assert.True(SymbolGeometry.HitTest(Rect(0.05), 0, 0, 5));
        Assert.False(SymbolGeometry.HitTest(Rect(0), 0, 0, 5));
    }

    [Theory]
    [InlineData("25", 25)]
    [InlineData(" 25 % ", 25)]
    [InlineData("12.5%", 12.5)]
    [InlineData("12,5", 12.5)]
    [InlineData("0", 0)]
    [InlineData("100", 100)]
    [InlineData(".5", 0.5)]
    [InlineData("33.333", 33.3)]
    public void ATypedFill_IsReadAsAPercentage(string text, double expected)
    {
        Assert.True(SymbolFill.TryParsePercent(text, out double percent, out string? problem), problem);
        Assert.Equal(expected, percent, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e2")]
    [InlineData("1,000.5")]
    [InlineData("50%%")]
    [InlineData("-1")]
    [InlineData("100.1")]
    [InlineData("0x10")]
    [InlineData("1,000")]     // a thousands separator as readily as a decimal comma: either reading is a guess
    [InlineData("12,500")]
    public void ATypedFillThatIsNotAPercentageFrom0To100_IsRefusedWithAReason(string text)
    {
        Assert.False(SymbolFill.TryParsePercent(text, out _, out string? problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    /// <summary>The inspector: a whole drag is one undo step, and a refused entry changes nothing but says why.</summary>
    [Fact]
    public void ADragIsOneUndoStep_AndARefusedEntryLeavesTheValue()
    {
        var rect = Rect(0);
        var editable = new EditableSymbol();
        editable.Primitives.Add(rect);
        var editor = new SymbolEditorViewModel(editable);
        editor.OnPointerPressed(-100, 0, KeyModifiers.None);   // on the outline: an unfilled shape's inside is not it
        editor.OnPointerReleased(-100, 0);
        var inspector = new SymbolPrimitiveInspectorViewModel();
        inspector.SetContext(editor);
        Assert.True(inspector.ShowFill);

        foreach (double v in new[] { 10.0, 20, 30, 40 }) inspector.FieldFillSlider = v;
        Assert.Equal(0.4, SymbolFill.Of(rect), 6);                 // previewed as it moved
        Assert.False(editor.UndoCommand.CanExecute(null));          // …and nothing recorded yet
        inspector.CommitFillSlider();
        Assert.Equal("40 %", inspector.FieldFillText);

        editor.UndoCommand.Execute(null);
        Assert.Equal(0, SymbolFill.Of(rect), 6);                    // one step took the whole drag back
        Assert.False(editor.UndoCommand.CanExecute(null));

        inspector.FieldFillText = "150";
        inspector.CommitFillText();
        Assert.True(inspector.FillInvalid);
        Assert.Equal(0, SymbolFill.Of(rect), 6);
        Assert.Equal("150", inspector.FieldFillText);               // left in place to correct

        inspector.FieldFillText = "100";
        inspector.CommitFillText();
        Assert.False(inspector.FillInvalid);
        Assert.True(rect.Filled);
    }

    /// <summary>A drag whose release never arrived belongs to the shape it moved: switching to another shape
    /// records it there, so the next shape's drag starts from its own value and undoes to it.</summary>
    [Fact]
    public void ADragInterruptedBySelectingAnotherShape_IsRecordedOnTheShapeItMoved()
    {
        var a = Rect(0);
        var b = Rect(0.5);
        b.Cx = 1000;
        var editable = new EditableSymbol();
        editable.Primitives.Add(a);
        editable.Primitives.Add(b);
        var editor = new SymbolEditorViewModel(editable);
        var inspector = new SymbolPrimitiveInspectorViewModel();
        inspector.SetContext(editor);

        editor.OnPointerPressed(-100, 0, KeyModifiers.None);
        editor.OnPointerReleased(-100, 0);
        inspector.FieldFillSlider = 30;                               // a drag on a, never released

        editor.OnPointerPressed(900, 0, KeyModifiers.None);           // b's outline
        editor.OnPointerReleased(900, 0);
        inspector.FieldFillSlider = 80;
        inspector.CommitFillSlider();

        editor.UndoCommand.Execute(null);
        Assert.Equal(0.5, SymbolFill.Of(b), 6);                       // b's own starting value, not a's
        editor.UndoCommand.Execute(null);
        Assert.Equal(0, SymbolFill.Of(a), 6);                         // a's drag was its own undo step
    }

    private static int CentreAlpha(double fill) => PixelAlpha(Rect(fill), 50, 50);

    /// <summary>Alpha at pixel (<paramref name="x"/>, <paramref name="y"/>) of a 100 px render, the shape's 200-unit
    /// square spanning pixels 10…90, so a thick (2 px) outline covers pixels 9 and 10.</summary>
    private static int PixelAlpha(RectPrimitive rect, int x, int y)
    {
        var theme = SchematicRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Light);
        using var surface = SKSurface.Create(new SKImageInfo(100, 100, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        surface.Canvas.Clear(SKColors.Transparent);
        // Pixel = (world - pan) * zoom.
        SchematicRenderer.DrawSymbol(surface.Canvas, new List<SymbolPrimitive> { rect },
            compX: 0, compY: 0, SymbolRotation.R0, mirrorX: false, panX: -125, panY: -125, zoom: 0.4, theme);
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.GetPixel(x, y).Alpha;
    }
}
