using System;
using System.Collections.Generic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tuning;

/// <summary>
/// What the Properties Inspector and the canvas ask the Tuning panel (brief-tuneopt-4 R-to4-3): is
/// this row tunable, is it tuned, and tune it. All three ways of activating a tunable end at
/// <see cref="SetTuned"/>, so they set the same flag in the same single undo step.
/// </summary>
public interface ITuningSurface
{
    /// <summary>The keys of a parameter row as drawn in <paramref name="drawing"/>: one for a plain
    /// number, the four parts — real, imaginary, magnitude, phase — for a complex value (overview D18),
    /// none when the focused design does not offer it; the Inspector then shows no toggle at all, never
    /// a greyed one.</summary>
    IReadOnlyList<string> KeysFor(SchematicEditModel drawing, EditableComponent component, EditableParameter parameter);

    /// <summary>Whether the entry for <paramref name="key"/> has its tune flag set.</summary>
    bool IsTuned(string key);

    /// <summary>Sets or clears the tune flag — one undo step in the tuned schematic.</summary>
    void SetTuned(string key, bool on);

    /// <summary>Raised when the set of tuned keys, or what is offered, may have changed.</summary>
    event EventHandler? Changed;
}
