namespace CircuitRF.Ui.Tests;

/// <summary>
/// The test classes that end or replace providers in <c>ExternalDeviceRegistry</c>, a process-wide static: every
/// <c>ResetResolved</c>, <c>EndResolvedProviders</c> or <c>Clear</c> disposes the worker channel of a provider ANOTHER class
/// is evaluating through at that instant.
///
/// <para><b>One schedule with <see cref="PdkToolsDirectoryCollection"/>, not a second collection.</b> Its classes resolve and
/// register providers in the same registry, and <c>MultiWorkspaceRegistryScopeTests</c> clears it; two collections would still
/// run concurrently. That failed under full-solution load only: a Verilog-A bench reported "Cannot access a disposed object
/// … DeviceWorkerChannel" mid-evaluation, and both failing classes passed in isolation. A class can be party to only one
/// xUnit collection, so the two names resolve to one group, as <see cref="SkiaFontsTypefaceCollection"/> does.</para>
/// </summary>
public static class ExternalDeviceRegistryCollection
{
    public const string Name = PdkToolsDirectoryCollection.Name;
}
