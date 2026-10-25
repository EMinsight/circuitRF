using System;
using System.Linq;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Ui.ViewModels;

public sealed partial class ParameterEditorViewModel
{
    /// <summary>
    /// brief-dfr27-1 R-dfr27-1-3 — a test pad's rows its <c>Feed</c> does not use, greyed with the reason. Greying never
    /// touches a value, so switching <c>Feed</c> back restores what was there. Run at build and on every refresh, since
    /// a <c>Feed</c> change is an ordinary edit that keeps the rows.
    /// </summary>
    private void ConfigureTestPadRows()
    {
        if (_target?.Symbol != SymbolKind.TestPad) return;
        string feed = _target.Parameters.FirstOrDefault(p => p.Name == "Feed")?.Expression.Trim() is { Length: > 0 } f
            ? f : TestPadPCell.ViaFeed;
        bool viaFed = feed.Equals(TestPadPCell.ViaFeed, StringComparison.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            string? reason = TestPadRowUnused(row.Name, viaFed, feed);
            row.SetDisabledByMode(reason is not null, reason);
        }
    }

    /// <summary>Why <paramref name="name"/> does nothing with this feed, or null when it applies.</summary>
    internal static string? TestPadRowUnused(string name, bool viaFed, string feed) => name switch
    {
        "FeedW" when viaFed => "Not used with Feed = Via: there is no side stub. Choose N, E, S or W to use it.",
        "ViaFrom" or "Drill" or "InnerPadD" or "AntipadD" when !viaFed =>
            $"Not used with Feed = {feed}: a side-fed pad has no via. Choose Via to use it.",
        _ => null,
    };
}
