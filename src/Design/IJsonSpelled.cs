// brief-emi-1 — a type the file formats write as ONE STRING from a fixed set, though it is not an enum: its set is
// assembled at run time (Em3dSolverChoice's spellings come from the solver registry). `circuitrf reference` lists the
// spellings as it lists an enum's names, rather than expanding the type's properties, which the file never holds.

namespace CircuitRF.Design;

public interface IJsonSpelled
{
    /// <summary>Every string a file may hold for this type.</summary>
    static abstract IReadOnlyList<string> JsonSpellings { get; }
}
