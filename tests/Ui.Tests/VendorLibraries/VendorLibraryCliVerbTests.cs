using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Design.VendorLibraries;
using Xunit;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-1 §6 test 7: <c>import library</c> and <c>find --parts</c> run as a PROCESS, against
/// the in-process calls the GUI makes (the <c>AuthoringCliVerbTests</c> pattern) — so a verb that grew
/// its own copy of adding a library, or of matching a part, fails here.
/// </summary>
public sealed class VendorLibraryCliVerbTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-vl1cli-" + Guid.NewGuid().ToString("N")[..12]);

    public VendorLibraryCliVerbTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void ImportLibrary_WritesTheReferenceTheInProcessAddWrites_AndAnEncryptedFolderIsRefused()
    {
        string lib = Path.Combine(_root, "lib");
        VendorLibraryFixture.Write(lib);
        string viaCli = WorkspaceCreate.Create(_root, "ViaCli", null).WorkspaceDir;
        string inProc = WorkspaceCreate.Create(_root, "InProc", null).WorkspaceDir;

        var run = RunCli("import", "library", lib, "--into", viaCli, "--name", "Parts");
        Assert.True(run.ExitCode == 0, run.StdErr);
        Assert.Contains($"{VendorLibraryFixture.Parts} parts in", run.StdOut);
        Assert.Contains("1 file is encrypted for another simulator", run.StdOut);

        string cws = Path.Combine(inProc, ".cws");
        var file = WorkspacePersistence.LoadFromFile(cws);
        var refs = file.VendorLibraries ?? [];
        Assert.NotNull(VendorLibraryRefs.AddOrRepair(inProc, refs, lib, "Parts", out _));
        file.VendorLibraries = refs;
        WorkspacePersistence.SaveToFileAtomic(cws, file);

        Assert.Equal(File.ReadAllText(cws), File.ReadAllText(Path.Combine(viaCli, ".cws")));

        string enc = Path.Combine(_root, "enc");
        Directory.CreateDirectory(enc);
        File.WriteAllText(Path.Combine(enc, "a.lib"), VendorLibraryFixture.EncodedBody("TSTENC0001"));
        var refused = RunCli("import", "library", enc, "--into", viaCli);
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains(VendorLibraryIndex.EncryptedSentence(1), refused.StdErr);
    }

    [Fact]
    public void FindParts_ListsWhatTheSharedMatcherMatches_WithTheParametersToWrite()
    {
        string lib = Path.Combine(_root, "lib");
        VendorLibraryFixture.Write(lib);
        string ws = WorkspaceCreate.Create(_root, "Ws", null, vendorLibraries: [("Parts", lib)]).WorkspaceDir;

        const string query = "10p C0G";
        var expected = VendorLibraryIndex.Build(lib).Entries
            .Where(e => VendorLibrarySearch.Match(e, query))
            .Select(e => e.Part).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(expected);

        var run = RunCli("find", ws, "--parts", query, "--json");
        Assert.True(run.ExitCode == 0, run.StdErr);

        using var doc = JsonDocument.Parse(run.StdOut);
        var parts = doc.RootElement.GetProperty("result").GetProperty("find").GetProperty("parts")
                       .EnumerateArray().ToList();
        Assert.Equal(expected, parts.Select(p => p.GetProperty("part").GetString()!).OrderBy(p => p, StringComparer.Ordinal));

        var first = parts[0].GetProperty("parameters");
        Assert.Equal(VendorLibraryRefs.RefFor("Parts", parts[0].GetProperty("file").GetString()!),
                     first.GetProperty("File").GetString());
        Assert.Equal(parts[0].GetProperty("part").GetString(), first.GetProperty("Name").GetString());
        Assert.Equal("C", first.GetProperty("Glyph").GetString());
    }

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment[CircuitRF.Design.UserStateDirectory.EnvironmentVariable] = Path.Combine(_root, "state");

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(VendorLibraryCliVerbTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }
}
