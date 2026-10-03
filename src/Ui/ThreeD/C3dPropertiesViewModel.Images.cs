// brief-em3d-101 R-em3d101-3b — the Inspector's Image section, on an image sheet, above the sheet's own rows: the file (Browse…,
// Reveal), its pixels, the rectangle's width and height with Keep aspect, Reset to Image Aspect, Locked and Remove Image. Every
// write is the editor's one function for it (C3dEditorViewModel.Images.cs) — one undo entry each. Transparency is brief 92's row,
// already there: an image sheet's transparency IS its image's (owner decision D2).

using CircuitRF.Design.ThreeD;
using CircuitRF.Render;
using CircuitRF.Ui.ThreeD.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dPropertiesViewModel
{
    [ObservableProperty] private bool _hasImage;
    [ObservableProperty] private string _imageFile = "";
    [ObservableProperty] private string _imagePixels = "";
    [ObservableProperty] private string _imageWidthText = "";
    [ObservableProperty] private string _imageHeightText = "";
    [ObservableProperty] private bool _imageKeepAspect = true;
    [ObservableProperty] private bool _imageLocked;
    [ObservableProperty] private string? _imageSizeError;

    /// <summary>The unit the width and height are typed in (the document's display unit).</summary>
    [ObservableProperty] private string _imageUnit = "";

    private int _imageIndex = -1;

    private void ClearImage()
    {
        HasImage = false;
        _imageIndex = -1;
        ImageSizeError = null;
    }

    /// <summary>The section for the image sheet at <paramref name="index"/>.</summary>
    private void LoadImage(int index, C3dSheet s)
    {
        _imageIndex = index;
        HasImage = true;
        ImageFile = s.Image!.Path;
        string? file = editor.ImagePathOf(s);
        ImagePixels = file is not null && C3dImages.PixelSize(file) is { } px ? $"{px.Width} × {px.Height}"
                    : editor.ImageProblemOf(s) is { } why ? char.ToUpperInvariant(why[0]) + why[1..] : "File not found";
        var doc = editor.Document;
        ImageUnit = CircuitRF.Design.Layout.LayoutUnits.Suffix(doc.DisplayUnit);
        ImageWidthText = s.Rect is { } r ? C3dDimension.Spell(r.Size.U, doc.DisplayUnit, doc.DbuPerMicron) : "";
        ImageHeightText = s.Rect is { } r2 ? C3dDimension.Spell(r2.Size.V, doc.DisplayUnit, doc.DbuPerMicron) : "";
        ImageLocked = s.Locked;
        ImageSizeError = s.Rect is null ? "Its outline was edited: its width and height are its vertices' (below)." : null;
    }

    partial void OnImageLockedChanged(bool value)
    {
        if (_loading || !HasImage || _imageIndex < 0) return;
        editor.SetImageLocked(_imageIndex, value);
    }

    /// <summary>Commits the width box (Enter or leaving it): the height follows with Keep aspect.</summary>
    public void CommitImageWidth() => CommitImageSize(ImageWidthText, width: true);

    /// <summary>Commits the height box: the width follows with Keep aspect.</summary>
    public void CommitImageHeight() => CommitImageSize(ImageHeightText, width: false);

    private void CommitImageSize(string text, bool width)
    {
        if (!HasImage || _imageIndex < 0) return;
        var doc = editor.Document;
        var d = C3dDimension.Parse(text, doc.DisplayUnit, doc.DbuPerMicron);
        // An expression binds that one size through the sheet's own Size row writer (brief 51). With Keep aspect the other size
        // would have to follow a formula, which a number cannot, so it is refused with the way through.
        if (d.Kind == C3dDimensionKind.Expression)
        {
            if (ImageKeepAspect) { ImageSizeError = "An expression is bound with Keep aspect off: the other size cannot follow a formula."; return; }
            ImageSizeError = editor.SetFieldText(_imageIndex, width ? "Rect.Size[0]" : "Rect.Size[1]", text);
            return;
        }
        if (d.Kind != C3dDimensionKind.Value || d.Dbu <= 0) { ImageSizeError = d.Why ?? "A width and a height are above zero."; return; }
        ImageSizeError = null;
        editor.SetImageSize(_imageIndex, width ? d.Dbu : null, width ? null : d.Dbu, ImageKeepAspect);
    }

    [RelayCommand]
    private void ResetImageAspect() { if (_imageIndex >= 0) editor.ResetImageAspect(_imageIndex); }

    [RelayCommand]
    private void RemoveSheetImage() { if (_imageIndex >= 0) editor.RemoveSheetImage(_imageIndex); }

    [RelayCommand]
    private async Task BrowseImage()
    {
        if (_imageIndex < 0 || editor.PickImageFile is not { } pick || await pick("Choose Image") is not { } file) return;
        editor.SetImagePath(_imageIndex, file, "Re-point image of");
    }

    [RelayCommand]
    private void RevealImage()
    {
        if (editor.ImageSheetAt(_imageIndex) is { } s && editor.ImagePathOf(s) is { } file) FileReveal.Reveal(file);
    }
}
