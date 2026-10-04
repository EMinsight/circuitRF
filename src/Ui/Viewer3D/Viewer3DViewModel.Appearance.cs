// brief-em3d-108 R-em3d108-1c / R-em3d108-2c — an appearance shown before it is written. Two kinds of preview stand over the scene the
// view adopted: a MATERIAL's (the Materials dialog's unsaved edit, by material name, pushed to every open view whose technology has
// that material) and an OBJECT's (the Inspector's slider, as an override map). Either is a RESTYLE (Scene3DLooks.Restyle): the same
// geometry, colours and generation with the looks asked again — a table upload and a shade-stream patch for the objects whose row
// moved, and nothing else. No elaboration, no tessellation, no geometry upload (gate 2).
//
// The scene the view ADOPTED is kept (_adopted); Scene is it, restyled while a preview stands. Ending a preview puts the adopted scene
// back by reference, so the table returns byte for byte. A new scene adopted while a preview stands is restyled the same way, so a
// regeneration mid-drag does not drop the preview — unless the preview was asked to end at the next scene (OK: the edit is being
// written, and the scene built from it will show it).

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD.Appearance;
using CircuitRF.Render.Scene3D;

namespace CircuitRF.Ui.Viewer3D;

public sealed partial class Viewer3DViewModel
{
    /// <summary>The scene as adopted, before any preview: what ending a preview restores. Null until a preview has been applied.</summary>
    private Scene3DModel? _adopted;

    private readonly Dictionary<string, TechAppearance?> _materialPreviews = new(StringComparer.OrdinalIgnoreCase);
    private Func<string, AppearanceOverride?, AppearanceOverride?>? _objectPreview;

    /// <summary>The generation after which the previews end on their own: set by <see cref="EndAppearancePreviewAtNextScene"/>.</summary>
    private long? _previewEndsAfter;

    /// <summary>How many times a preview restyled the scene — each a table upload and a shade patch, never a rebuild.</summary>
    public int AppearanceRestyles { get; private set; }

    /// <summary>Whether a material's or an object's appearance is being previewed.</summary>
    public bool IsPreviewingAppearance => _materialPreviews.Count > 0 || _objectPreview is not null;

    /// <summary>Whether this view's scene draws <paramref name="material"/>: what the workspace asks before pushing a dialog's edit.</summary>
    public bool DrawsMaterial(string material)
        => (_adopted ?? Scene).AppearanceRequests.Any(r => r is { Technology: { } t } && t.FindMaterial(material) is not null);

    /// <summary>
    /// R-em3d108-1c — shows <paramref name="material"/> with <paramref name="appearance"/> (null: states none) in place of its own,
    /// wherever this view resolves it (a <c>Like</c> naming it too). Writes nothing. False when the scene draws no such material.
    /// </summary>
    public bool PreviewAppearance(string material, TechAppearance? appearance)
    {
        if (!DrawsMaterial(material)) return false;
        _materialPreviews[material] = appearance?.Clone();
        _previewEndsAfter = null;
        Restyle();
        return true;
    }

    /// <summary>R-em3d108-2c — the Inspector's preview: <paramref name="map"/> gives each object's override (its own and its instances')
    /// from the one the document states; null ends it.</summary>
    public void PreviewObjectAppearances(Func<string, AppearanceOverride?, AppearanceOverride?>? map)
    {
        if (map is null && _objectPreview is null) return;
        _objectPreview = map;
        if (map is not null) _previewEndsAfter = null;
        Restyle();
    }

    /// <summary>Cancel: every material preview ends now, and the scene as adopted is drawn again — its table byte for byte.</summary>
    public void EndAppearancePreview()
    {
        if (_materialPreviews.Count == 0) return;
        _materialPreviews.Clear();
        Restyle();
    }

    /// <summary>OK, or a slider's release: the previews stand until a scene built after now is adopted — the one built from what is
    /// being written, which shows it — so the view never flashes back to the old look in between.</summary>
    public void EndAppearancePreviewAtNextScene()
    {
        if (!IsPreviewingAppearance) return;
        _previewEndsAfter = Source.Requested;
    }

    /// <summary>A new scene arrived: the previews over it, unless they were to end with it. Returns what <see cref="Scene"/> becomes.</summary>
    private Scene3DModel PreviewOver(Scene3DModel adopted)
    {
        if (_previewEndsAfter is { } after && adopted.Generation > after)
        {
            _materialPreviews.Clear();
            _objectPreview = null;
            _previewEndsAfter = null;
        }
        _adopted = adopted;
        return IsPreviewingAppearance ? Restyled(adopted) : adopted;
    }

    private void Restyle()
    {
        var basis = _adopted ??= Scene;
        Scene = IsPreviewingAppearance ? Restyled(basis) : basis;
        RealisticSceneAdopted();
        FrameRequested?.Invoke();
    }

    private Scene3DModel Restyled(Scene3DModel basis)
    {
        AppearanceRestyles++;
        var previewed = new Dictionary<Technology, Technology>(ReferenceEqualityComparer.Instance);
        var materials = _materialPreviews.Count == 0 ? null : new Dictionary<string, TechAppearance?>(_materialPreviews, StringComparer.OrdinalIgnoreCase);
        var objects = _objectPreview;
        return Scene3DLooks.Restyle(basis, (name, r) =>
        {
            if (materials is not null && r.Technology is { } t)
            {
                if (!previewed.TryGetValue(t, out var p)) previewed[t] = p = AppearanceResolver.WithAppearances(t, materials);
                r = r with { Technology = p };
            }
            return objects is null ? r : r with { Override = objects(name, r.Override) };
        });
    }

    /// <summary>The look object <paramref name="o"/> is drawn with now — previews included — and where each field came from; null for
    /// what has no appearance (air, a port, a boundary).</summary>
    public ResolvedAppearance? AppearanceOf(Scene3DObject o)
    {
        var scene = Scene;
        var owner = o.Prototype != 0 && scene.Object(o.Prototype) is { } proto ? proto : o;
        int k = (int)owner.Id - 1;
        return k >= 0 && k < scene.AppearanceRequests.Length && scene.AppearanceRequests[k] is { } r ? AppearanceResolver.Resolve(r) : null;
    }
}
