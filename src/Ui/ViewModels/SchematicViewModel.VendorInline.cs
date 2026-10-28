using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The schematic's inline editor on a vendor library part (brief vendorlib-3): double-clicking the part
/// number or the value row opens the ordinary inline box with the library search under it.
///
/// <para><b>Seeded by the row that was double-clicked.</b> On the value row the box holds the value and
/// the list opens on the parts of the SAME KIND in value order — the Value ▲/▼ list — with the placed
/// part highlighted, so changing a value is ↑/↓ and Enter. On the part number it holds the part
/// number and lists the whole file. Typing searches the file with the palette's grammar
/// (<see cref="VendorLibrarySearch"/>).</para>
///
/// <para>Everything comes from the library's index; nothing is parsed. The choice goes through
/// <see cref="ChangeVendorPart"/>, the Parameter Editor's own part change: one undo entry, the
/// footprint following only when it came from the part.</para>
/// </summary>
public sealed partial class SchematicViewModel
{
    /// <summary>The inline box's list: parts of the edited instance's own library file.</summary>
    public ObservableCollection<VendorPartChoice> VendorInlineMatches { get; } = [];

    [ObservableProperty] private VendorPartChoice? _selectedVendorInlineMatch;

    /// <summary>True while the inline box edits a vendor part and its list has rows to show.</summary>
    [ObservableProperty] private bool _isVendorInlineOpen;

    /// <summary>True while the inline box is a vendor part's search — the view then forwards every
    /// keystroke to <see cref="InlineEditValue"/>, which filters the list.</summary>
    public bool IsVendorInlineEditing => _inlineEditKind == InlineEditKind.VendorPart;

    private IReadOnlyList<VendorLibraryEntry> _vendorInlineEntries = [];
    private VendorLibraryEntry? _vendorInlineCurrent;
    private string _vendorInlineSeed = "";
    private bool _vendorInlineValueRow;

    /// <summary>True for a placed vendor part the inline box can search for.</summary>
    private static bool IsVendorPart(EditableComponent comp)
        => comp.Symbol == SymbolKind.SpiceModel
        && VendorLibraryRefs.IsVendorRef(comp.Parameters
               .FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.FileParameter)?.Expression);

    /// <summary>
    /// Opens the vendor inline edit. Returns false when the instance's library has no finished index
    /// yet (or does not resolve) — the caller then falls back to the ordinary edit of that row.
    /// </summary>
    private bool BeginVendorInlineEdit(EditableComponent comp, bool valueRow, double screenX, double screenY)
    {
        string file = comp.Parameters.FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.FileParameter)?.Expression ?? "";
        string name = (comp.Parameters.FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.NameParameter)?.Expression ?? "").Trim();
        var entries = VendorPartSource.IndexedFileEntries(file, EditModel.SchematicDirectory, out _, out _);
        if (entries is null || entries.Count == 0) return false;

        _vendorInlineEntries  = entries;
        _vendorInlineCurrent  = entries.FirstOrDefault(e => e.Part.Equals(name, StringComparison.OrdinalIgnoreCase));
        _vendorInlineValueRow = valueRow && _vendorInlineCurrent is not null;
        _vendorInlineSeed     = _vendorInlineValueRow ? _vendorInlineCurrent!.Header.ValueText() : name;

        InlineEditIncludesName = false;
        InlineEditSelStart = 0;
        InlineEditSelLength = -1;
        SetInlineEdit(InlineEditKind.VendorPart, comp.Id, _vendorInlineSeed, screenX, screenY);
        FilterVendorInline(_vendorInlineSeed);
        return true;
    }

    partial void OnInlineEditValueChanged(string value)
    {
        if (_inlineEditKind == InlineEditKind.VendorPart) FilterVendorInline(value);
    }

    /// <summary>The seed text lists the same-kind parts (value row) or the whole file (part number);
    /// anything typed searches the file.</summary>
    private void FilterVendorInline(string text)
    {
        IEnumerable<VendorLibraryEntry> rows;
        if (string.Equals(text.Trim(), _vendorInlineSeed, StringComparison.OrdinalIgnoreCase))
            rows = _vendorInlineValueRow ? VendorPartChange.SameKind(_vendorInlineEntries, _vendorInlineCurrent!) : _vendorInlineEntries;
        else
        {
            // On the value row a bare number is in the seeded value's own unit: 200 over 400 fF is 200 fF.
            var q = VendorLibrarySearch.Parse(text, _vendorInlineValueRow ? SeedUnit(_vendorInlineSeed) : null);
            rows = q.IsEmpty ? _vendorInlineEntries : _vendorInlineEntries.Where(e => VendorLibrarySearch.Match(e, q));
        }

        VendorInlineMatches.Clear();
        VendorPartChoice? current = null;
        foreach (var e in rows)
        {
            var choice = new VendorPartChoice(e);
            if (ReferenceEquals(e, _vendorInlineCurrent)) current = choice;
            VendorInlineMatches.Add(choice);
        }
        SelectedVendorInlineMatch = current ?? VendorInlineMatches.FirstOrDefault();
        IsVendorInlineOpen = VendorInlineMatches.Count > 0;
    }

    /// <summary>The prefix and unit of a seeded value — <c>fF</c> from <c>400 fF</c> (the form
    /// <see cref="VendorLibraryHeader.ValueText"/> writes), or null when it has none.</summary>
    private static string? SeedUnit(string seed)
    {
        int space = seed.LastIndexOf(' ');
        return space >= 0 && space < seed.Length - 1 ? seed[(space + 1)..] : null;
    }

    /// <summary>↑/↓ in the inline box: moves the list's highlight, clamped to its ends.</summary>
    public void MoveVendorInlineSelection(int direction)
    {
        if (VendorInlineMatches.Count == 0) return;
        int at = SelectedVendorInlineMatch is { } s ? VendorInlineMatches.IndexOf(s) : -1;
        SelectedVendorInlineMatch = VendorInlineMatches[Math.Clamp(at < 0 ? 0 : at + Math.Sign(direction), 0, VendorInlineMatches.Count - 1)];
    }

    /// <summary>
    /// The commit of a vendor inline edit: the part typed exactly, else the highlighted row. The text
    /// left as it was seeded with the placed part still highlighted is no change.
    /// </summary>
    private void CommitVendorInline(string targetId, string text, VendorPartChoice? highlighted)
    {
        var comp = EditModel.FindComponent(targetId);
        if (comp is null) return;

        var chosen = _vendorInlineEntries.FirstOrDefault(e => e.Part.Equals(text, StringComparison.OrdinalIgnoreCase))
                  ?? highlighted?.Entry;
        if (chosen is null)
        {
            _messageSink?.Info($"No part in this library file matches '{text}'.");
            return;
        }
        if (_vendorInlineCurrent is not null && chosen.Part.Equals(_vendorInlineCurrent.Part, StringComparison.OrdinalIgnoreCase))
            return;
        ChangeVendorPart(comp, _vendorInlineCurrent, chosen);
    }

    /// <summary>Closes the list. The entries and the placed part stay until the next begin: a commit
    /// clears the edit state BEFORE it acts, and reads them after.</summary>
    private void ClearVendorInline()
    {
        IsVendorInlineOpen = false;
        VendorInlineMatches.Clear();
        SelectedVendorInlineMatch = null;
    }
}
