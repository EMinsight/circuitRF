// brief-em3d-92 R-em3d92-2 / -3 — an object's, an instance's or a group's transparency: one undo entry over every member it
// is written to, and a slider drag previewed without touching the document.
//
// A group is a PATH ON EACH MEMBER (C3dGroups), so there is no group value to hold: the Inspector's one row writes every
// member at every depth, and "each keeps its own until the group's is edited" is simply what that means. The preview is the
// face gesture's (DocumentText): the scene is built from a copy of the document with the dragged value on the targets, and the
// document itself is written once, on release.

using CircuitRF.Design.ThreeD;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The members a slider drag is showing a value on, and the value; null when no drag is in progress.</summary>
    private (IReadOnlyList<int> Objects, IReadOnlyList<int> Instances, int? Value)? _transparencyPreview;

    /// <summary>How many transparency previews have been drawn — a drag's ticks, which write nothing.</summary>
    public int TransparencyPreviews { get; private set; }

    /// <summary>Shows <paramref name="value"/> on the members without writing the document: a slider drag's preview.</summary>
    public void PreviewTransparency(IReadOnlyList<int> objects, IReadOnlyList<int> instances, int? value)
    {
        if (_transparencyPreview is { } now && now.Value == value && now.Objects.SequenceEqual(objects) && now.Instances.SequenceEqual(instances))
            return;
        _transparencyPreview = (objects, instances, value);
        TransparencyPreviews++;
        Viewer.Regenerate();
    }

    /// <summary>Whether a drag is previewing a value on exactly these members.</summary>
    public bool IsPreviewingTransparency(IReadOnlyList<int> objects, IReadOnlyList<int> instances)
        => _transparencyPreview is { } p && p.Objects.SequenceEqual(objects) && p.Instances.SequenceEqual(instances);

    /// <summary>Ends a preview without writing anything: the document is shown again. Nothing to end, nothing done.</summary>
    public void EndTransparencyPreview()
    {
        if (_transparencyPreview is null) return;
        _transparencyPreview = null;
        Viewer.Regenerate();
    }

    /// <summary>
    /// Writes <paramref name="value"/> (null: back to the kind's default) onto the objects and instances named, as ONE undo entry,
    /// and ends any preview. A polyline and an operand inside an operation take none and are skipped. Returns the refusal for a
    /// value outside the range, or null.
    /// </summary>
    public string? SetTransparency(IReadOnlyList<int> objects, IReadOnlyList<int> instances, int? value, string description)
    {
        if (value is { } v && !C3dTransparency.InRange(v)) return C3dTransparency.OutOfRange("The selection", v);
        bool previewed = _transparencyPreview is not null;
        _transparencyPreview = null;
        var slots = new List<C3dEditSlot>();
        foreach (int i in objects.Where(i => !IsOperandIndex(i) && i >= 0 && i < Document.Objects.Count).Distinct().Order())
        {
            if (Document.Objects[i] is C3dPolyline) continue;
            string before = C3dPersistence.SerializeObject(Document.Objects[i]);
            var copy = C3dPersistence.DeserializeObject(before);
            copy.Transparency = value;
            string after = C3dPersistence.SerializeObject(copy);
            if (after != before) slots.Add(new C3dEditSlot(false, i, before, after));
        }
        foreach (int i in instances.Where(i => i >= 0 && i < Document.Instances.Count).Distinct().Order())
        {
            string before = C3dPersistence.SerializeInstance(Document.Instances[i]);
            var copy = C3dPersistence.DeserializeInstance(before);
            copy.Transparency = value;
            string after = C3dPersistence.SerializeInstance(copy);
            if (after != before) slots.Add(new C3dEditSlot(true, i, before, after));
        }
        if (slots.Count > 0) Push(new C3dEdit(description, slots, ApplySlots));
        else if (previewed) Viewer.Regenerate();   // the drag came back to where it started: show the document again
        return null;
    }

    /// <summary>The document's text with a drag's value on its targets — a copy; the document is never written here.</summary>
    private string TransparencyPreviewText((IReadOnlyList<int> Objects, IReadOnlyList<int> Instances, int? Value) p)
    {
        var copy = C3dPersistence.Deserialize(C3dPersistence.Serialize(Document));
        foreach (int i in p.Objects.Where(i => i >= 0 && i < copy.Objects.Count && copy.Objects[i] is not C3dPolyline))
            copy.Objects[i].Transparency = p.Value;
        foreach (int i in p.Instances.Where(i => i >= 0 && i < copy.Instances.Count)) copy.Instances[i].Transparency = p.Value;
        return C3dPersistence.Serialize(copy);
    }
}
