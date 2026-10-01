using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CircuitRF.Design.Em3d.Wsl;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Dialogs;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-97 §7 — giving the Linux subsystem more memory from the warning: the .wslconfig edit, the
//  dialog's two saves, the in-flight refusal, the warning row and the 150 % confirmation. All against
//  FakeWsl (WslLocationTests.cs); nothing starts wsl.exe, and the dialog's pixels are the owner's to see.
//
//  One class on purpose: WslInUse is process-wide, and gate 4 holds it. xUnit runs one class's tests in
//  sequence, so no other gate here can see gate 4's hold.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class WslMemoryRaiseTests : IDisposable
{
    private const long GiB = 1L << 30;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-wslmem-" + Guid.NewGuid().ToString("N")[..10]);

    public WslMemoryRaiseTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ── 1. one key changes, nothing else ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null,                                           "[wsl2]\r\nmemory=6GB\r\n")]                                // no file
    [InlineData("[experimental]\nsparseVhd=true\n",               "[experimental]\nsparseVhd=true\n\n[wsl2]\nmemory=6GB\n")]   // no section
    [InlineData("[wsl2]\r\nprocessors=4\r\n",                     "[wsl2]\r\nmemory=6GB\r\nprocessors=4\r\n")]                  // no key
    [InlineData("[wsl2]\n  memory = 4GB   # mine\n",              "[wsl2]\n  memory = 6GB   # mine\n")]                         // trailing comment
    [InlineData("[wsl2]\nmemory=2GB\nswap=0\nmemory=4GB\n",       "[wsl2]\nmemory=2GB\nswap=0\nmemory=6GB\n")]                 // repeated: the last
    [InlineData("[WSL2]\nMemory=4GB\n",                           "[WSL2]\nMemory=6GB\n")]                                     // mixed case
    public void Gate1_WithMemoryChangesOnlyTheOneKey(string? before, string after)
        => Assert.Equal(after, WslConfigFile.WithMemory(before, 6).Text);

    [Fact]
    public void Gate1b_ACrlfBomFileWithCommentsComesBackIdenticalButForTheOneLine()
    {
        string original = "# Docker Desktop wrote this\r\n[wsl2]\r\n; keep\r\nmemory=4GB\r\nnetworkingMode=mirrored\r\n\r\n[experimental]\r\nautoMemoryReclaim=gradual\r\n";
        string path = Path.Combine(_tmp, ".wslconfig");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(original)]);

        var file = WslConfigFile.Read(path);
        var edit = WslConfigFile.WithMemory(file.Text, 6);
        Assert.Equal(("memory=4GB", "memory=6GB", 1), (edit.Before, edit.After, edit.Repeats));
        Assert.NotNull(file.Write(edit.Text));

        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(original.Replace("memory=4GB", "memory=6GB"))], File.ReadAllBytes(path));
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(original)], File.ReadAllBytes(file.BackupPath));
    }

    // ── 2-4. the dialog's two saves, and the refusal ────────────────────────────────────────────

    [Fact]
    public void Gate2_SaveAndRestart_WritesBacksUpShutsDownThenReadsAgain_AndReportsTheNewFigure()
    {
        var (wsl, model, config) = Dialog("[wsl2]\r\nmemory=4GB\r\n");
        wsl.MemoryAfterShutdown = 6 * GiB - 300_000_000;
        int before = wsl.Commands.Count;

        var outcome = model.SaveAndRestart();

        Assert.Equal(WslMemoryStatus.Restarted, outcome.Status);
        Assert.Contains("memory=6GB", File.ReadAllText(config));
        Assert.True(File.Exists(config + WslConfigFile.BackupSuffix));
        var after = wsl.Commands.Skip(before).ToList();
        int shutdown = after.FindIndex(c => c.SequenceEqual(["--shutdown"]));
        int free = after.FindIndex(c => c.SequenceEqual(["-d", "Ubuntu", "--exec", "free", "-b"]));
        Assert.True(shutdown >= 0 && free > shutdown, string.Join(" | ", after.Select(c => string.Join(' ', c))));

        var sink = new Sink();
        WslMemoryDialogModel.Report(sink, outcome, config);
        Assert.Contains("now has 6.1 GB (it had 3.8 GB)", sink.Rows.Single().Text);
    }

    [Fact]
    public void Gate3_SaveOnly_WritesTheFile_AndStopsNothing()
    {
        var (wsl, model, config) = Dialog(null);
        var outcome = model.SaveOnly();
        Assert.Equal(WslMemoryStatus.Saved, outcome.Status);
        Assert.Equal("[wsl2]\r\nmemory=6GB\r\n", File.ReadAllText(config));
        Assert.DoesNotContain(wsl.Commands, c => c.Contains("--shutdown"));
    }

    [Fact]
    public void Gate4_WithARunInTheSubsystem_RestartIsDisabledNamingIt_AndNothingIsShutDown()
    {
        using var run = WslInUse.Hold("Ubuntu", "the 3D EM run of 'Launch Palace'");
        var (wsl, model, _) = Dialog(null);

        Assert.False(model.CanRestart);
        Assert.True(model.CanSave);
        Assert.Contains("the 3D EM run of 'Launch Palace' is running in Ubuntu", model.RestartBlocked);
        Assert.Equal(WslMemoryStatus.Refused, model.SaveAndRestart().Status);
        Assert.DoesNotContain(wsl.Commands, c => c.Contains("--shutdown"));
    }

    // ── 5. the warning row ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Gate5_AWarningNamingDotWslconfig_PostsTheLinkAndTheAction_AndOneNamingNoFilePostsNeither(bool namesFile)
    {
        const string warning = "Palace may need about 4.2 GB…";
        var result = new EmRunResult(EmRunStatus.Ok, null, null, null, null, null, null, Warnings: [warning],
                                     MessageFiles: namesFile ? new Dictionary<string, string> { [warning] = @"C:\Users\someone\.wslconfig" } : null);
        var sink = new Sink();

        WorkspaceViewModel.PostEmSentence(sink, MessageLevel.Warning, result, warning, () => Task.CompletedTask);

        var row = sink.Rows.Single();
        Assert.Equal(namesFile ? @"C:\Users\someone\.wslconfig" : null, row.File);
        Assert.Equal(namesFile ? WslMemoryDialogModel.RowAction : null, row.Action);
    }

    // ── 6. the 150 % confirmation ───────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_MoreMemoryFirst_OpensTheDialog_DoesNotRun_AndShutsNothingDown()
    {
        var wsl = Fake();
        bool opened = false;
        Assert.False(WorkspaceViewModel.AnswerEmMemory(SaveChangesResult.DontSave, () => opened = true));
        Assert.True(opened);
        Assert.True(WorkspaceViewModel.AnswerEmMemory(SaveChangesResult.Save, () => throw new InvalidOperationException()));
        Assert.DoesNotContain(wsl.Commands, c => c.Contains("--shutdown"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private FakeWsl Fake()
        => new(Path.Combine(_tmp, "linux")) { Listing = Encoding.Unicode.GetBytes("* Ubuntu Running 2\r\n"), MemoryBytes = 3_800_000_000 };

    /// <summary>The dialog's model on an 8 GB host, against <paramref name="text"/> as .wslconfig (null: no file),
    /// with no circuitRF-installed subsystem home for another process to hold.</summary>
    private (FakeWsl, WslMemoryDialogModel, string Config) Dialog(string? text)
    {
        var wsl = Fake();
        string config = Path.Combine(_tmp, ".wslconfig");
        if (text is not null) File.WriteAllText(config, text);
        var model = WslMemoryDialogModel.Load(wsl, null, config, 8 * GiB, out string? refusal, localRoots: [_tmp]);
        Assert.True(model is not null, refusal);
        Assert.Equal(6, model!.Proposed);
        return (wsl, model, config);
    }

    private sealed class Sink : IMessageSink
    {
        public List<(string Text, string? File, string? Action)> Rows { get; } = [];

        public void Post(MessageLevel level, string text, string? filePath = null) => Rows.Add((text, filePath, null));

        public void PostAction(MessageLevel level, string text, string actionLabel, Func<Task> action, string? filePath = null)
            => Rows.Add((text, filePath, actionLabel));

        public void Clear() => Rows.Clear();
    }
}
