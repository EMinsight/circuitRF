namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>
/// brief-em3d-97 R-em3d97-4 — what this process is running inside the Linux subsystem right now, each by the
/// sentence a refusal names it with ("the 3D EM run of 'Launch Palace' is running in Ubuntu"). Restarting the
/// subsystem (<c>wsl --shutdown</c>) stops every one of them, so <see cref="WslMemory"/> refuses to while any is held.
///
/// <para>Separate from <see cref="Install.SolverInUse"/>, which holds only homes circuitRF INSTALLED: a Palace the
/// user built themselves holds nothing there, and a run of it is stopped by a restart all the same. Another
/// circuitRF process's run is seen through <see cref="Install.SolverInUse"/>'s lock files instead
/// (<see cref="WslMemory.RestartBlockers"/>).</para>
/// </summary>
public static class WslInUse
{
    private static readonly object Gate = new();
    private static readonly List<string> Holders = [];

    /// <summary>Holds the subsystem for <paramref name="holder"/>, working in <paramref name="distribution"/>,
    /// until the result is disposed.</summary>
    public static IDisposable Hold(string distribution, string holder)
    {
        string sentence = $"{holder} is running in {distribution}";
        lock (Gate) Holders.Add(sentence);
        return new Release(sentence);
    }

    /// <summary>Every holder in this process, in the order they started.</summary>
    public static IReadOnlyList<string> Current()
    {
        lock (Gate) return [.. Holders];
    }

    private sealed class Release(string sentence) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            lock (Gate) Holders.Remove(sentence);
        }
    }
}
