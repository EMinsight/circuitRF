// brief-emi-9 — where an EMerge run factorises: the CPU's fast sparse solver, or the GPU sparse solver when it is installed.
//
// A property of the MACHINE, never of the design (brief-em3d-136 D1, which Palace's device follows): it is an ARGUMENT to the
// run — the GUI passes its preference (em_emerge_device), `em --device` its flag — and it enters no provenance hash. The
// driver is handed one number, the unknowns from which it uses the GPU (EmergeDevicePolicy.GpuFromUnknowns), because the
// unknowns are known only once EMerge has meshed, inside the driver's own process.

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>The per-machine choice. <see cref="Auto"/> is the default everywhere.</summary>
public enum EmergeDevice { Auto, Cpu, Gpu }

public static class EmergeDevices
{
    /// <summary>The spelling the preference, the CLI and the MCP tool use: <c>auto</c>, <c>cpu</c> or <c>gpu</c>.</summary>
    public static string Id(this EmergeDevice device) => device switch
    {
        EmergeDevice.Cpu => "cpu",
        EmergeDevice.Gpu => "gpu",
        _                => "auto",
    };

    /// <summary><c>auto</c>, <c>cpu</c> or <c>gpu</c>, any case; null for anything else.</summary>
    public static EmergeDevice? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "auto" => EmergeDevice.Auto,
        "cpu"  => EmergeDevice.Cpu,
        "gpu"  => EmergeDevice.Gpu,
        _      => null,
    };
}

/// <summary>
/// brief-emi-9 R-emi9-3 — when a run uses the GPU. <b>Measured</b> (src/Design/RESOLVED.md, EMI-9) on the Cavity, driven,
/// adaptive sweep, on an M4: with refinement on, the GPU converged in the CPU's own sample count at every mesh and matched its
/// S to 1.5e-6; it tied the CPU at 30k unknowns and was 1.3×, 1.65× and 1.84× faster at 67k, 143k and 304k.
/// </summary>
public static class EmergeDevicePolicy
{
    /// <summary>The unknowns from which <see cref="EmergeDevice.Auto"/> uses the GPU: above the measured tie at ~30,000, where
    /// it starts to win.</summary>
    public const long AutoGpuFromUnknowns = 40_000;

    /// <summary>
    /// The unknowns from which the run uses the GPU, or null when it never does: a driven problem only (EMerge's eigensolver
    /// does not use it), only when the GPU solver is installed, never for <see cref="EmergeDevice.Cpu"/>, from any size for
    /// <see cref="EmergeDevice.Gpu"/>, and from <see cref="AutoGpuFromUnknowns"/> for <see cref="EmergeDevice.Auto"/>.
    /// </summary>
    public static long? GpuFromUnknowns(Em3dProblemType problem, bool gpuInstalled, EmergeDevice preference)
        => problem != Em3dProblemType.Driven || !gpuInstalled || preference == EmergeDevice.Cpu ? null
         : preference == EmergeDevice.Gpu ? 0
         : AutoGpuFromUnknowns;

    /// <summary>The device a problem of <paramref name="unknowns"/> is solved on: <see cref="EmergeDevice.Cpu"/> or
    /// <see cref="EmergeDevice.Gpu"/>. The driver makes the same comparison on the threshold it is handed.</summary>
    public static EmergeDevice Choose(Em3dProblemType problem, long unknowns, bool gpuInstalled, EmergeDevice preference)
        => GpuFromUnknowns(problem, gpuInstalled, preference) is { } from && unknowns >= from ? EmergeDevice.Gpu : EmergeDevice.Cpu;

    /// <summary>
    /// R-emi9-3 — whether a finished run is solved again on the CPU: an <see cref="EmergeDevice.Auto"/> run that used the GPU
    /// and whose adaptive sweep stopped at its sample cap. The user always gets a converged answer; a forced GPU run is
    /// reported, not repeated.
    /// </summary>
    public static bool RerunOnCpu(EmergeDevice preference, EmergeRunFacts facts)
        => preference == EmergeDevice.Auto && facts.Device == EmergeDevice.Gpu && facts.Adaptive && facts.Converged == false;
}
