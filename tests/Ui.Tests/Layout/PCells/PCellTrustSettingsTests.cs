using System;
using System.Collections.Generic;
using System.IO;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// Settings changes kit permissions ONE KIT AT A TIME, and an open workspace's gate follows the change
/// without being rebuilt.
/// </summary>
[Collection(UserStateDirectoryCollection.Name)]
public sealed class PCellTrustSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-trust-settings-" + Guid.NewGuid().ToString("N")[..8]);

    public PCellTrustSettingsTests()
    {
        Directory.CreateDirectory(_root);
        AppDataRoot.RedirectTo(_root);
    }

    public void Dispose()
    {
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ChangingOrForgettingOneKit_LeavesEveryOtherKitsAnswer()
    {
        string a = Path.Combine(_root, "kit-a"), b = Path.Combine(_root, "kit-b"), c = Path.Combine(_root, "kit-c");
        PCellTrustPreferences.Set(a, true);
        PCellTrustPreferences.Set(b, true);
        PCellTrustPreferences.Set(c, false);

        PCellTrustPreferences.Set(b, false);
        PCellTrustPreferences.Forget(c);

        var store = new PCellTrustStore(PCellTrustPreferences.Load());
        Assert.Equal(PCellTrustDecision.Allowed, store.Decide(a));
        Assert.Equal(PCellTrustDecision.Denied, store.Decide(b));
        Assert.Equal(PCellTrustDecision.Unknown, store.Decide(c));
        Assert.Equal(2, store.Count);
    }

    /// <summary>The resolver holds <c>store.Decide</c> as its gate, so re-reading into the SAME store is
    /// what makes a refusal in Settings stop a kit in a workspace that is already open.</summary>
    [Fact]
    public void ReplaceAll_ChangesWhatTheHeldGateAnswers()
    {
        string kit = Path.Combine(_root, "kit");
        var store = new PCellTrustStore([new KeyValuePair<string, bool>(kit, true)]);
        Func<string, PCellTrustDecision> gate = store.Decide;

        store.ReplaceAll([new KeyValuePair<string, bool>(kit, false)]);
        Assert.Equal(PCellTrustDecision.Denied, gate(kit));

        store.ReplaceAll([]);
        Assert.Equal(PCellTrustDecision.Unknown, gate(kit));
    }
}
