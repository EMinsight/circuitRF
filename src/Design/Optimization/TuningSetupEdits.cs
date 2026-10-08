using CircuitRF.Core.Design;

namespace CircuitRF.Design.Optimization;

/// <summary>
/// The edits the Tuning and Optimizer panels make to a schematic's variable entries (overview D4/D5),
/// as pure functions from one setup to the next. The caller wraps the result in an undoable command;
/// nothing here touches a document. Every function returns a NEW setup and leaves its argument alone,
/// so the old one can stand as the undo state.
/// </summary>
public static class TuningSetupEdits
{
    /// <summary>
    /// Sets or clears the <c>tune</c> flag of <paramref name="tunable"/>. Turning it on for a key with
    /// no entry creates one with the D4 default range; turning it off leaves the entry (its range is
    /// the user's) unless nothing else of it is set, in which case the entry goes.
    /// </summary>
    public static TuningSetup WithTune(TuningSetup? setup, Tunable tunable, bool on)
        => WithFlag(setup, tunable, on, (e, v) => e.Tune = v);

    /// <summary>The same for the <c>opt</c> flag — the Optimizer panel's half of one shared entry.</summary>
    public static TuningSetup WithOpt(TuningSetup? setup, Tunable tunable, bool on)
        => WithFlag(setup, tunable, on, (e, v) => e.Opt = v);

    private static TuningSetup WithFlag(TuningSetup? setup, Tunable tunable, bool on, Action<TunableEntry, bool> set)
    {
        var next  = setup?.Clone() ?? new TuningSetup();
        var entry = next.Variables.FirstOrDefault(v => v.Key == tunable.Key);
        if (entry is null)
        {
            if (!on) return next;
            entry = NewEntry(tunable);
            next.Variables.Add(entry);
        }
        set(entry, on);
        if (!entry.Tune && !entry.Opt && IsDefaultShaped(entry, tunable)) next.Variables.Remove(entry);
        return next;
    }

    /// <summary>A first activation's entry: the D4 default range, everything else default — except a
    /// phase, which is linear: an angle's range is not a ratio (overview D18).</summary>
    public static TunableEntry NewEntry(Tunable tunable) => new()
    {
        Key      = tunable.Key,
        Min      = tunable.DefaultMin,
        Max      = tunable.DefaultMax,
        Scale    = DefaultScale(tunable),
        Discrete = tunable.IsInteger ? TuneDiscrete.Integer : TuneDiscrete.None,
    };

    private static TuneScale DefaultScale(Tunable t) => t.Part == ComplexPart.Phase ? TuneScale.Lin : TuneScale.Auto;

    /// <summary>Removes the entry for <paramref name="key"/> outright — the row's "Remove from tuning"
    /// when the optimizer does not use it either; otherwise only its tune flag clears.</summary>
    public static TuningSetup WithoutTuning(TuningSetup? setup, string key)
    {
        var next  = setup?.Clone() ?? new TuningSetup();
        var entry = next.Variables.FirstOrDefault(v => v.Key == key);
        if (entry is null) return next;
        if (entry.Opt) entry.Tune = false;
        else next.Variables.Remove(entry);
        return next;
    }

    /// <summary>Changes one entry in place (range, scale, step), creating it from
    /// <paramref name="tunable"/> when absent.</summary>
    public static TuningSetup WithEntry(TuningSetup? setup, string key, Tunable? tunable, Action<TunableEntry> change)
    {
        var next  = setup?.Clone() ?? new TuningSetup();
        var entry = next.Variables.FirstOrDefault(v => v.Key == key);
        if (entry is null)
        {
            if (tunable is null) return next;
            entry = NewEntry(tunable);
            next.Variables.Add(entry);
        }
        change(entry);
        return next;
    }

    private static bool IsDefaultShaped(TunableEntry e, Tunable t)
        => e.Min == t.DefaultMin && e.Max == t.DefaultMax && e.Scale == DefaultScale(t) && e.Step is null
           && e.Discrete == (t.IsInteger ? TuneDiscrete.Integer : TuneDiscrete.None) && e.Extra is null;
}
