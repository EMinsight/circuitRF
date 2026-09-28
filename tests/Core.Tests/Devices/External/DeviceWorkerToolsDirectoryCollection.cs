using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// Serialises every test class that points <c>DeviceWorkerManifest.ToolsDirectory</c> at a scratch
/// folder of its own.
///
/// <para>It is process-wide static state, and xUnit runs test classes in parallel: a class that sets it,
/// writes a fake worker there and asks the resolver can have another class's folder swapped in between
/// the write and the question — so it sees none of its own workers and nothing is refused. It fails only
/// when the run is busy enough to interleave them (<c>OsdiWorkerArchitectureTests</c> under a whole
/// <c>Core.Tests</c> run), and passes alone.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DeviceWorkerToolsDirectoryCollection
{
    public const string Name = "device-worker-tools-directory";
}
