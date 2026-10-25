using CircuitRF.Engine;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// brief-dfr27-5 R-dfr27-5-3 — the sentences an EM run posted to Messages while it was going, so its end posts only the ones it
/// did not. Added to on the run's thread, read on the UI thread once the run is over.
/// </summary>
internal sealed class EmStreamedSentences
{
    private readonly List<string> _notes = [];
    private readonly List<string> _warnings = [];

    public void Add(RunNote note)
    {
        lock (this) (note.Warning ? _warnings : _notes).Add(note.Text);
    }

    /// <summary><paramref name="sentences"/> less one occurrence of each already posted, in their own order.</summary>
    public IReadOnlyList<string> Unposted(IEnumerable<string> sentences, bool warning)
    {
        List<string> left;
        lock (this) left = [.. warning ? _warnings : _notes];
        var unposted = new List<string>();
        foreach (string s in sentences)
            if (!left.Remove(s)) unposted.Add(s);
        return unposted;
    }
}
