using System.Text;
using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Ui.Tests.ThreeD.Occ;

/// <summary>
/// brief-em3d-63 gates 2 and 3 — the handshake pins the recipe's kernel exactly (R-em3d63-2b), and every absence is
/// worded through the one sentence (R-em3d63-3b). The scan that no other file words it is in
/// <c>tests/Firewall.Tests/GeometryKernelBoundaryTests.cs</c>.
/// </summary>
public sealed class GeometryKernelCapabilityTests
{
    [Theory]
    [InlineData(GeometryKernelAbsence.NotBuilt, null, "tools/geometry-worker/build.sh")]
    [InlineData(GeometryKernelAbsence.NotShippedOnThisPlatform, null, "64-bit build")]
    [InlineData(GeometryKernelAbsence.Missing, GeometryKernelRoute.BesideTheApplication, "Reinstall circuitRF to restore it.")]
    [InlineData(GeometryKernelAbsence.Broken, GeometryKernelRoute.BesideTheApplication, "report it with the log at /logs/start.log")]
    [InlineData(GeometryKernelAbsence.WrongVersion, GeometryKernelRoute.BesideTheApplication, "Reinstall circuitRF to restore it.")]
    [InlineData(GeometryKernelAbsence.WrongVersion, GeometryKernelRoute.EnvironmentVariable, "Unset CIRCUITRF_GEOMETRY_WORKER")]
    [InlineData(GeometryKernelAbsence.Broken, GeometryKernelRoute.SourceTree, "Rebuild it with tools/geometry-worker/build.sh")]
    public void EveryAbsence_IsWordedThroughTheOneSentence(GeometryKernelAbsence absence, GeometryKernelRoute? route, string action)
    {
        string act = GeometryKernel.ActionFor(absence, route, "/logs/start.log");
        var cap = new GeometryKernelCapability(false, absence, null, null, "", "The reason.", act);

        Assert.Contains(action, act, StringComparison.Ordinal);
        Assert.Equal($"Boolean needs the geometry kernel, which this installation does not have: The reason. {act}",
                     GeometryKernel.NeedsKernel("Boolean", cap));
        Assert.Equal(GeometryKernel.NeedsKernel("Boolean", cap), GeometryKernel.DisabledReason("Boolean", cap));
        Assert.Equal($"The reason. {act}", GeometryKernel.SettingsStatus(cap));
    }

    [Fact]
    public void BeforeTheProbe_ACommandSaysItIsChecking_AndAfterIt_IsEnabled()
    {
        Assert.Equal(GeometryKernel.Checking, GeometryKernel.DisabledReason("Boolean", null));
        var here = new GeometryKernelCapability(true, null, "/k/geometry-worker", "8.0.1", "included with circuitRF", "", "");
        Assert.Null(GeometryKernel.DisabledReason("Boolean", here));
        Assert.Equal("Open CASCADE Technology 8.0.1, included with circuitRF", GeometryKernel.SettingsStatus(here));
    }

    /// <summary>R-em3d63-2b: a different OCCT, protocol or architecture is WrongVersion, naming both sides — and it is
    /// not retried (one start), because a different kernel does not become right by being asked again.</summary>
    [Theory]
    [InlineData("occt", "7.9.0", "is Open CASCADE Technology 7.9.0; this build of circuitRF was made with")]
    [InlineData("protocol", "2", "speaks protocol 2; this build of circuitRF speaks 1")]
    [InlineData("rid", "linux-riscv64", "was built for linux-riscv64; this circuitRF runs as")]
    public void AHandshakeFromADifferentKernel_IsWrongVersion_NamingBoth(string field, string value, string words)
    {
        var fake = new FakeKernel();
        if (field == "occt") fake.HelloOcct = value;
        if (field == "protocol") fake.HelloProtocol = int.Parse(value);
        if (field == "rid") fake.HelloRid = value;
        using var kernel = fake.Create();

        var cap = kernel.Probe();

        Assert.Equal(GeometryKernelAbsence.WrongVersion, cap.Absence);
        Assert.Contains(words, cap.Reason, StringComparison.Ordinal);
        if (field == "occt") Assert.EndsWith($"made with {GeometryKernelRecipe.OcctVersion}.", cap.Reason, StringComparison.Ordinal);
        Assert.Equal(1L, kernel.WorkerStarts);
    }

    /// <summary>Gate 2's other half, as a real process: a stub that answers the handshake with another OCCT version.</summary>
    [NonWindowsFact("the stub worker is a POSIX shell script")]
    public void AStubWorkerReportingAnotherOcct_IsWrongVersion()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-kernel-stub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string json = $$"""{"ok":true,"protocol":1,"worker":"stub","occt":"7.9.0","rid":"{{GeometryKernelLocator.ProcessRid}}"}""";
            var frame = new List<byte>(BitConverter.GetBytes((uint)json.Length));
            frame.AddRange(BitConverter.GetBytes(0u));
            frame.AddRange(Encoding.UTF8.GetBytes(json));
            string octal = string.Concat(frame.Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
            string stub = Path.Combine(dir, "geometry-worker");
            File.WriteAllText(stub, $"#!/bin/sh\nhead -c 8 >/dev/null\nprintf '{octal}'\nsleep 5\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            using var kernel = new GeometryKernel(new GeometryKernelOptions
            {
                Locate = () => new GeometryKernelLocation(stub, GeometryKernelRoute.EnvironmentVariable, "from CIRCUITRF_GEOMETRY_WORKER", null, "", ""),
                DiskCache = false,
                LogPath = Path.Combine(dir, "start.log"),
            });
            var cap = kernel.Probe();

            Assert.Equal(GeometryKernelAbsence.WrongVersion, cap.Absence);
            Assert.Equal($"The geometry kernel at {stub} is Open CASCADE Technology 7.9.0; this build of circuitRF was made with {GeometryKernelRecipe.OcctVersion}.",
                         cap.Reason);
            Assert.Equal($"Unset {GeometryKernel.EnvironmentVariable} or point it at a matching build.", cap.Action);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>R-em3d63-2b: the pinned version is brief 62's recipe, read out of the assembly, not a second copy.</summary>
    [Fact]
    public void ThePinnedVersion_IsTheRecipes()
    {
        string recipe = File.ReadAllText(Path.Combine(GeometryKernelLocator.SourceTreeRoot(AppContext.BaseDirectory)!,
                                                      "tools", "geometry-worker", "occt", "recipe.env"));
        var values = GeometryKernelRecipe.Parse(recipe);
        Assert.Equal(values["OCCT_VERSION"], GeometryKernelRecipe.OcctVersion);
        Assert.Equal(values["KERNEL_RIDS"].Split(' ', StringSplitOptions.RemoveEmptyEntries), GeometryKernelRecipe.ShippedRids);
    }
}
