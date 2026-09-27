using System.Diagnostics;

namespace CircuitRF.Ui;

/// <summary>
/// The About box's line about the geometry kernel (brief-em3d-62 R-em3d62-6d) — the <b>prominent
/// notice</b> the Open CASCADE Exception asks of a program that includes material from OCCT's headers.
///
/// <para>It names the OCCT version <b>the installed worker reports</b> (<c>geometry-worker --version</c>),
/// never a constant: an installer built without the kernel (a developer build with an empty cache, a
/// platform that does not ship it) says so rather than naming a library that is not there.</para>
///
/// <para>This is not discovery. Finding, starting and speaking to the worker is brief 63's client; this
/// only reads the one fixed place a packaged build puts it, beside the assemblies.</para>
/// </summary>
public static class GeometryKernelNotice
{
    /// <summary>The worker a build or an installer puts beside the assemblies.</summary>
    public static string WorkerPath(string baseDirectory) =>
        Path.Combine(baseDirectory, "geometry-kernel", OperatingSystem.IsWindows() ? "geometry-worker.exe" : "geometry-worker");

    /// <summary>The OCCT version out of <c>--version</c>'s output (its <c>occt &lt;version&gt;</c> line), or
    /// null when there is no such line or the worker reported a mismatch.</summary>
    public static string? ParseOcctVersion(string? versionOutput)
    {
        if (versionOutput is null) return null;
        foreach (string raw in versionOutput.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("occt ", StringComparison.Ordinal)) continue;
            string rest = line["occt ".Length..].Trim();
            return rest.Length == 0 || rest.Contains(' ') ? null : rest;
        }
        return null;
    }

    /// <summary>The sentence the About box shows.</summary>
    /// <param name="workerExists">Whether a worker is installed at all.</param>
    /// <param name="occtVersion">What it reported, or null when it did not answer.</param>
    public static string Describe(bool workerExists, string? occtVersion) =>
        !workerExists
            ? "Geometry kernel: not included in this installation, so booleans, fillets and STEP are unavailable."
            : occtVersion is null
                ? "Uses Open CASCADE Technology (LGPL-2.1 with the Open CASCADE Exception). Its geometry kernel "
                  + "did not report a version; reinstalling circuitRF should repair it."
                : $"Uses Open CASCADE Technology {occtVersion} (LGPL-2.1 with the Open CASCADE Exception).";

    /// <summary>Asks the installed worker, off the UI thread. Never throws.</summary>
    public static async Task<string> ProbeAsync(string baseDirectory, TimeSpan timeout)
    {
        string worker = WorkerPath(baseDirectory);
        if (!File.Exists(worker)) return Describe(false, null);
        try
        {
            var psi = new ProcessStartInfo(worker, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return Describe(true, null);
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return Describe(true, null);
            }
            return Describe(true, p.ExitCode == 0 ? ParseOcctVersion(await stdout) : null);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return Describe(true, null);
        }
    }

    /// <summary>The notices file every installer carries beside the executable (the .csproj copies it).</summary>
    public static string NoticesPath(string baseDirectory) => Path.Combine(baseDirectory, "THIRD-PARTY-NOTICES.md");
}
