// ================================================================
//  TrialSelection.cs  —  the trials selected in each source, shared by
//  every plot in every Data Display (brief-yield-9 R-ya9-6)
//
//  A selection belongs to a SOURCE, not to a plot: clicking trial 417 on
//  a scatter highlights trial 417 on the family two displays away,
//  because both are drawn from the same result. Each Data Display has a
//  library of its own, so the selection lives above them all, here, keyed
//  by the source's full path. It is session state and is never written.
//
//  Listeners are held WEAKLY. An inspector has no dispose path (it
//  subscribes to its library the same way), and a process-wide store
//  holding every inspector ever opened would keep every closed display
//  alive.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CircuitRF.Ui.DataDisplay;

/// <summary>Something that redraws when a trial selection changes.</summary>
public interface ITrialSelectionListener
{
    void OnTrialSelectionChanged();
}

public sealed class TrialSelection
{
    /// <summary>The application's one store.</summary>
    public static TrialSelection Shared { get; } = new();

    private readonly Dictionary<string, HashSet<int>> _bySource = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WeakReference<ITrialSelectionListener>> _listeners = new();

    /// <summary>The trials selected in the source at <paramref name="sourcePath"/>, or null when none are.</summary>
    public IReadOnlySet<int>? For(string? sourcePath)
        => Key(sourcePath) is { } k && _bySource.TryGetValue(k, out var s) && s.Count > 0 ? s : null;

    /// <summary>Every source with a selection, and its trials.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<int>> All
        => _bySource.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => (IReadOnlySet<int>)kv.Value);

    /// <summary>Replaces the selection in <paramref name="sourcePath"/> with <paramref name="trials"/>.</summary>
    public void Select(string sourcePath, IEnumerable<int> trials)
    {
        if (Key(sourcePath) is not { } k) return;
        _bySource[k] = [.. trials];
        Notify();
    }

    /// <summary>Clears the selection in <paramref name="sourcePath"/>, or in every source when it is null (Esc).</summary>
    public void Clear(string? sourcePath = null)
    {
        bool changed = sourcePath is null
            ? _bySource.Values.Any(s => s.Count > 0)
            : Key(sourcePath) is { } k && _bySource.Remove(k);
        if (sourcePath is null) _bySource.Clear();
        if (changed) Notify();
    }

    /// <summary>Adds a listener, held weakly.</summary>
    public void Subscribe(ITrialSelectionListener listener)
    {
        _listeners.RemoveAll(w => !w.TryGetTarget(out _));
        _listeners.Add(new WeakReference<ITrialSelectionListener>(listener));
    }

    private void Notify()
    {
        foreach (var w in _listeners.ToList())
            if (w.TryGetTarget(out var l)) l.OnTrialSelectionChanged();
        _listeners.RemoveAll(w => !w.TryGetTarget(out _));
    }

    private static string? Key(string? path) => string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);
}
