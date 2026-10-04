// brief-em3d-108 R-em3d108-3 — the Look, edited from the Look panel. The Look is DOCUMENT state (brief 106 §5): every edit is one undo
// entry and marks the document dirty, and a drag (a rotation, an intensity, an exposure) previews without writing and writes ONE entry
// on release. Nothing here elaborates or rebuilds a scene: the view reads the Look again (Viewer3DViewModel.LookChanged), which is a
// uniform write — or, for a different environment, one prefilter off the UI thread, cached per environment, the old one drawn until the
// new one is ready.
//
// R-em3d108-3d — the picture camera (overview D17): "Set Camera" writes the live camera into Look.Camera as one entry;
// an orbit never writes it; "Go to Camera View" puts it back in the live view (view state, no entry); Clear removes it.

using CircuitRF.Design.ThreeD;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>A drag's Look, shown and not written; null when no drag is in progress.</summary>
    private C3dLook? _lookPreview;

    /// <summary>The Look the view draws: a drag's, else the document's.</summary>
    private C3dLook? ShownLook => _lookPreview ?? Document.Look;

    private C3dLookPanelViewModel? _lookPanel;

    /// <summary>The Look panel's view model (created on first use).</summary>
    public C3dLookPanelViewModel LookPanel => _lookPanel ??= new C3dLookPanelViewModel(this);

    /// <summary>3D ▸ View ▸ Look…: the view opens the panel (a flyout from the toggle's drop-down).</summary>
    public event Action? LookPanelRequested;

    public void RequestLookPanel() => LookPanelRequested?.Invoke();

    /// <summary>A copy of the document's Look (a new one when it has none), for a mutation to work on.</summary>
    private C3dLook LookCopy() => Document.Look?.Clone() ?? new C3dLook();

    /// <summary>
    /// One Look edit: <paramref name="mutate"/> changes a copy of the Look, which replaces the document's (none, when it states
    /// nothing) as ONE undo entry, and ends any drag's preview. False when nothing changed. A setup's view has no Look to edit.
    /// </summary>
    public bool ChangeLook(string description, Action<C3dLook> mutate)
    {
        if (IsViewOnly) return false;
        bool previewed = _lookPreview is not null;
        _lookPreview = null;
        string before = C3dPersistence.SerializeLook(Document.Look);
        var look = LookCopy();
        mutate(look);
        var next = look.IsEmpty ? null : look;
        string after = C3dPersistence.SerializeLook(next);
        if (after == before)
        {
            if (previewed) LookApplied();
            return false;
        }
        Document.Look = next;
        Push(new C3dRecordsEdit(description, before, after, ApplyLookText, alreadyApplied: true));
        LookApplied();
        return true;
    }

    /// <summary>A drag's value shown in the view, written nowhere: <paramref name="mutate"/> on a copy of the document's Look.</summary>
    public void PreviewLook(Action<C3dLook> mutate)
    {
        if (IsViewOnly) return;
        var look = LookCopy();
        mutate(look);
        _lookPreview = look;
        Viewer.LookChanged();
    }

    /// <summary>A drag ended where it began, or its panel closed: the document's Look is drawn again.</summary>
    public void EndLookPreview()
    {
        if (_lookPreview is null) return;
        _lookPreview = null;
        Viewer.LookChanged();
    }

    /// <summary>An undo or redo put a Look back.</summary>
    private void ApplyLookText(string text)
    {
        DocumentWrites++;
        _lookPreview = null;
        Document.Look = C3dPersistence.DeserializeLook(text);
        LookApplied();
    }

    private void LookApplied()
    {
        Viewer.LookChanged();
        _lookPanel?.Reload();
        OnPropertyChanged(nameof(IsDirty));
        RaiseMenuStateChanged();
    }

    // ── R-em3d108-3d — the picture camera ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Set Camera: the live camera written into the Look, one undo entry.</summary>
    public bool UseViewForPictures()
        => ChangeLook("Set Camera", l => l.Camera = Viewer.PictureCamera(Document.DbuPerMicron));

    /// <summary>Go to Camera View: the live view's camera set to the Look's. False when the Look states none (or a broken one).</summary>
    public bool GoToPictureView()
        => Document.Look?.Camera is { } c && Viewer.GoToPictureCamera(c, Document.DbuPerMicron);

    /// <summary>Clear: the picture camera removed, one undo entry; pictures are taken from the live view again.</summary>
    public bool ClearPictureView() => ChangeLook("Clear the picture camera", l => l.Camera = null);
}
