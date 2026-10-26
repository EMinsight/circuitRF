using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The test classes that assert on the process-wide 3D run counters — <c>Em3dRunService.ProblemsGenerated</c>,
/// <c>PalaceRun.GmshInvocations</c> and <c>PalaceRun.PalaceInvocations</c> — by reading one before a run and comparing after.
///
/// <para><b>Why <c>DisableParallelization</c>.</b> Every in-process 3D run increments them, and more than fifty classes start
/// one, most in no collection at all; any of them overlapping a "nothing was generated" check moves the count by one. That
/// failed under full-solution load only (<c>RunBothTests</c>' and <c>PalaceEigenTests</c>' Gate8, one more than expected),
/// and passed in isolation. Running the readers alone is maintainable where listing every writer is not.</para>
///
/// <para>Running alone also subsumes <see cref="SkiaFontsTypefaceCollection"/>, which several of these were in before. The cost
/// was measured: the eight classes are about 46 s together. Add a class here the moment it compares one of those counters.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Em3dProcessCountersCollection
{
    public const string Name = "Em3d process counters";
}
