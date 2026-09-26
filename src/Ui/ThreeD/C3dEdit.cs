// brief-em3d-43 R-em3d43-1c — one undo entry of the 3D editor.
//
// AN ENTRY STORES ONLY WHAT IT CHANGED: each touched object (or instance) as the file spells it, before and
// after — never a snapshot of the document, so a 10,000-object document does not copy itself per edit. The
// spelling is C3dPersistence's own, which is also the elaborator's per-object cache key: an undone object
// is byte for byte what it was, and its elaboration is a cache hit.
//
// ONE SHAPE PER ENTRY — replacements, or removals, or insertions — so an index always means one thing:
// a replacement's and a removal's index is into the list BEFORE the entry, an insertion's into the list
// AFTER it. Removals run from the highest index down and insertions from the lowest up, which is what
// makes deleting three objects and undoing it put each back where it was.

using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One touched slot: an object (or, with <see cref="Instance"/>, an instance) at <see cref="Index"/>,
/// as the file spells it before and after; null before is an insertion, null after a removal.</summary>
public sealed record C3dEditSlot(bool Instance, int Index, string? Before, string? After);

public sealed class C3dEdit : IUiCommand
{
    private readonly Action<IReadOnlyList<C3dEditSlot>, bool> _apply;
    private bool _alreadyApplied;

    public string Description { get; }
    public IReadOnlyList<C3dEditSlot> Slots { get; }

    /// <param name="apply">Writes the slots into the document: forward (after) or back (before).</param>
    /// <param name="alreadyApplied">The document already holds the after state (a gesture committed on
    /// release, R-em3d43-1c): the stack's first Execute does nothing, and every Redo after it applies.</param>
    public C3dEdit(string description, IReadOnlyList<C3dEditSlot> slots, Action<IReadOnlyList<C3dEditSlot>, bool> apply,
                   bool alreadyApplied = false)
    {
        bool replace = slots.All(s => s.Before is not null && s.After is not null);
        bool remove = slots.All(s => s.Before is not null && s.After is null);
        bool insert = slots.All(s => s.Before is null && s.After is not null);
        if (!(replace || remove || insert))
            throw new ArgumentException("A 3D edit replaces, removes or inserts — one of the three.", nameof(slots));
        Description = description;
        Slots = slots;
        _apply = apply;
        _alreadyApplied = alreadyApplied;
    }

    public void Execute()
    {
        if (_alreadyApplied) { _alreadyApplied = false; return; }
        _apply(Slots, true);
    }

    public void Undo() => _apply(Slots, false);

    /// <summary>Writes <paramref name="slots"/> into <paramref name="doc"/>, forward or back.</summary>
    public static void Apply(C3dDocument doc, IReadOnlyList<C3dEditSlot> slots, bool forward)
    {
        foreach (bool instances in new[] { false, true })
        {
            var mine = slots.Where(s => s.Instance == instances).ToList();
            if (mine.Count == 0) continue;
            foreach (var s in mine.Where(s => (forward ? s.After : s.Before) is null).OrderByDescending(s => s.Index))
                if (instances) doc.Instances.RemoveAt(s.Index); else doc.Objects.RemoveAt(s.Index);
            foreach (var s in mine.OrderBy(s => s.Index))
            {
                string? now = forward ? s.After : s.Before;
                string? was = forward ? s.Before : s.After;
                if (now is null) continue;
                if (instances)
                {
                    var inst = C3dPersistence.DeserializeInstance(now);
                    if (was is null) doc.Instances.Insert(s.Index, inst); else doc.Instances[s.Index] = inst;
                }
                else
                {
                    var obj = C3dPersistence.DeserializeObject(now);
                    if (was is null) doc.Objects.Insert(s.Index, obj); else doc.Objects[s.Index] = obj;
                }
            }
        }
    }
}
