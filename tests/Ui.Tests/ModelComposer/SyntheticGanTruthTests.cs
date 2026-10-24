using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// Brief ENV-PCW §5, gate 9: <see cref="SyntheticGan"/> is a parameter set, and its first instance still writes the raw
/// files <c>examples/Model Composer/synthetic-gan</c> ships, byte for byte — every DC, pulsed and S file, and the pads.
/// </summary>
public sealed class SyntheticGanTruthTests
{
    [Fact]
    public void TheFirstTruth_RegeneratesTheShippedRawFiles_ByteForByte()
    {
        string shipped = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "Model Composer", "synthetic-gan");
        string dir = Directory.CreateTempSubdirectory("crf-gan-raw-").FullName;
        try
        {
            SyntheticGan.WriteRaw(dir);
            var written = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f)).Order().ToList();
            Assert.NotEmpty(written);
            foreach (string rel in written)
                Assert.True(File.ReadAllBytes(Path.Combine(dir, rel)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(shipped, rel))), rel);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
