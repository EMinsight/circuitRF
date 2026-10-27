using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Design.VendorLibraries;
using Xunit;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-1 §6, tests 1–5: the header index, the encrypted-file refusal, and the
/// <c>.cws</c> reference with its <c>vendorlib://</c> resolution. Every library here is generated
/// (<see cref="VendorLibraryFixture"/>).
/// </summary>
public sealed class VendorLibraryIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl1-" + Guid.NewGuid().ToString("N"));

    public VendorLibraryIndexTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // 1 — every readable part, the unreadable files counted, and no circuit parsed.
    [Fact]
    public void Build_IndexesEveryReadablePart_CountsTheRest_AndNeverCallsTheReader()
    {
        string lib = Path.Combine(_dir, "lib");
        var names = VendorLibraryFixture.Write(lib);

        int before = SpiceNetlistReader.ReadsOnThisThread;
        var build = VendorLibraryIndex.Build(lib);
        Assert.Equal(before, SpiceNetlistReader.ReadsOnThisThread);

        Assert.Null(build.Refusal);
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal),
                     build.Entries.Select(e => e.Part).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(1, build.EncryptedFiles);
        Assert.Equal(1, build.EmptyFiles);
        Assert.Contains("1 file is encrypted for another simulator", build.UnreadableSentence);
    }

    // 2 — an entry's byte block is a self-sufficient definition of exactly that part.
    [Fact]
    public void EachEntrysBlock_ReadAlone_YieldsExactlyThatCell()
    {
        string lib = Path.Combine(_dir, "lib");
        VendorLibraryFixture.Write(lib);
        var build = VendorLibraryIndex.Build(lib);

        foreach (var e in build.Entries)
        {
            var r = SpiceNetlistReader.Read(VendorLibraryIndex.ReadBlock(build.Root, e));
            var cell = Assert.Single(r.Library.Cells);
            Assert.Equal(e.Part, cell.Name, ignoreCase: true);
            Assert.Equal(e.PortCount, cell.Ports.Count);
        }
    }

    // 3 — both range spellings, bracketed and bare units, a missing Description, a missing header.
    [Fact]
    public void Header_ParsesBothSpellings_AndAMissingFieldOrHeaderIsEmptyNotAnError()
    {
        var bracketed = VendorLibraryHeader.Parse(
        [
            "* Description : 1005M(0402)/C0G(-55~125degC)/10[pF]/50[V]",
            "* Property : C = 1.00e-05[uF]",
            "*   Frequency Range = 100[MHz]-30[GHz]",
            "*   DC Bias Voltage = 0V",
        ], out int bad1);
        Assert.Equal(0, bad1);
        Assert.Equal(VendorPartQuantity.Capacitance, bracketed.Quantity);
        Assert.Equal(10e-12, bracketed.Value!.Value, 1e-24);
        Assert.Equal(100e6, bracketed.FrequencyLowHz);
        Assert.Equal(30e9, bracketed.FrequencyHighHz);
        Assert.Equal(("1005", "0402"), (bracketed.CaseMetric, bracketed.CaseImperial));
        Assert.Equal("DC Bias Voltage = 0V", bracketed.Bias);

        var bare = VendorLibraryHeader.Parse(["*Property:L=10nH", "*Frequency Range=50MHz - 20GHz"], out int bad2);
        Assert.Equal(0, bad2);
        Assert.Equal(VendorPartQuantity.Inductance, bare.Quantity);
        Assert.Equal(10e-9, bare.Value!.Value, 1e-21);
        Assert.Equal((50e6, 20e9), (bare.FrequencyLowHz!.Value, bare.FrequencyHighHz!.Value));
        Assert.Null(bare.Description);

        var none = VendorLibraryHeader.Parse([], out int bad3);
        Assert.Equal(0, bad3);
        Assert.Equal(VendorLibraryHeader.Empty, none);

        VendorLibraryHeader.Parse(["* Frequency Range = somewhere"], out int bad4);
        Assert.Equal(1, bad4);
    }

    // 4 — a folder of only encrypted files is a refusal carrying the sentence.
    [Fact]
    public void AFolderOfOnlyEncryptedFiles_IsARefusal()
    {
        string lib = Path.Combine(_dir, "enc");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "a.lib"), VendorLibraryFixture.EncodedBody("TSTENC0001"));
        File.WriteAllText(Path.Combine(lib, "b.lib"), ".subckt TSTENC0002 1 2\n.protect\nX\n.unprotect\n.ends\n");

        var build = VendorLibraryIndex.Build(lib);

        Assert.Equal(VendorLibraryIndex.EncryptedSentence(2), build.Refusal);
        Assert.Contains("cannot be read. The plain-text (static) edition of this library can be.", build.Refusal);
    }

    // 5 — the .cws key, and vendorlib:// resolution with its two different failures.
    [Fact]
    public void Cws_RoundTrips_AndVendorlibResolves_WithDistinctReasons()
    {
        Assert.Equal(WorkspacePersistence.Serialize(new CwsFile()),
                     WorkspacePersistence.Serialize(new CwsFile { VendorLibraries = null }));
        Assert.DoesNotContain("VendorLibraries", WorkspacePersistence.Serialize(new CwsFile()));

        string lib = Path.Combine(_dir, "lib");
        VendorLibraryFixture.Write(lib);
        string ws = Path.Combine(_dir, "ws");
        string gone = Path.Combine(_dir, "moved-away");
        Directory.CreateDirectory(Path.Combine(ws, "cells", "top"));
        var cws = new CwsFile
        {
            VendorLibraries =
            [
                new CwsVendorLibraryRef { Name = "Parts", Path = lib },
                new CwsVendorLibraryRef { Name = "Gone",  Path = gone },
            ],
        };
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), cws);

        var back = WorkspacePersistence.LoadFromFile(Path.Combine(ws, ".cws"));
        Assert.Equal([("Parts", lib), ("Gone", gone)], back.VendorLibraries!.Select(r => (r.Name, r.Path)));

        string schDir = Path.Combine(ws, "cells", "top");
        Assert.Equal(Path.Combine(lib, "capacitors", "series-a.lib"),
            SpiceModelSymbolProvider.ResolvePath("vendorlib://Parts/capacitors/series-a.lib", schDir, out _));

        Assert.Null(SpiceModelSymbolProvider.ResolvePath("vendorlib://Nope/x.lib", schDir, out string? unknown));
        Assert.Null(SpiceModelSymbolProvider.ResolvePath("vendorlib://Gone/x.lib", schDir, out string? missing));
        Assert.Contains("references no vendor library named 'Nope'", unknown);
        Assert.Contains("which is not there", missing);
    }
}
