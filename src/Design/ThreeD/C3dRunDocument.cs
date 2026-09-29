// brief-em3d-84 R-em3d84-2 — "has the model changed since this run?" asked in one place. A finished run keeps the document it
// solved beside its fields (R-em3d49-5b); the 3D editor's stale banner and `circuitrf render --field`'s stale note both compare
// that file with the document as it is now, through C3dPersistence.SerializeForRun — which leaves the field plots out, so adding
// or editing a plot never makes a run stale (R-em3d83-2).

namespace CircuitRF.Design.ThreeD;

public static class C3dRunDocument
{
    /// <summary>The file a run's directory keeps the document it solved in.</summary>
    public const string FileName = "document.c3d";

    /// <summary>Where <paramref name="runDirectory"/> keeps the document it solved.</summary>
    public static string PathIn(string runDirectory) => Path.Combine(runDirectory, FileName);

    /// <summary>True when <paramref name="solvedText"/> — the run's kept document — is not the document <paramref name="now"/>
    /// would be run as.</summary>
    public static bool IsStale(string solvedText, C3dDocument now) => solvedText != C3dPersistence.SerializeForRun(now);

    /// <summary>
    /// Whether the run in <paramref name="runDirectory"/> solved a different model from <paramref name="now"/>, and when it
    /// kept its document. Null when the run kept none (a run made before R-em3d49-5b, or by a verb that does not keep one):
    /// there is nothing to compare, so nothing is claimed.
    /// </summary>
    public static (bool Stale, DateTime Written)? Check(string runDirectory, C3dDocument now)
    {
        string f = PathIn(runDirectory);
        if (!File.Exists(f)) return null;
        try { return (IsStale(File.ReadAllText(f), now), File.GetLastWriteTime(f)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
