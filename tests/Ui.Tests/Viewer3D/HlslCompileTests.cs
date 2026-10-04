// The D3D11 backend compiles every entry point of scene.hlsl when it is constructed, from ONE source: anything the Windows shader
// compiler (d3dcompiler_47, Shader Model 5.0) rejects anywhere in the file fails every entry point, and the whole 3D view with it — not
// just the feature that added the construct. No GPU is needed to compile, so this runs on every Windows machine and CI runner; elsewhere
// it holds only that every entry point the backend names is in the generated file.

using System.Text.RegularExpressions;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

public sealed class HlslCompileTests
{
    /// <summary>Every (entry, profile) the D3D11 backend compiles, read from its source so a new one is never missed.</summary>
    private static IReadOnlyList<(string Entry, string Profile)> Entries()
    {
        string src = File.ReadAllText(Path.Combine(PalaceBackendTests.RepoRoot(), "src", "Ui", "Viewer3D", "D3D11", "D3D11Viewer3DBackend.cs"));
        return Regex.Matches(src, @"Compile\(""(\w+)"", ""([vp]s_5_0)""\)").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).Distinct().ToList();
    }

    [Fact]
    public void EveryEntryPointTheD3D11BackendNames_IsInTheGeneratedHlsl()
    {
        var entries = Entries();
        Assert.Contains(("vs_shadow", "vs_5_0"), entries);
        Assert.Contains(("fs_ao_blur", "ps_5_0"), entries);
        string hlsl = Viewer3DShaders.Hlsl;
        foreach (var (entry, _) in entries)
            Assert.Matches(new Regex($@"^\S.*\b{entry}\(", RegexOptions.Multiline), hlsl);
    }

    [Fact]
    public void EveryEntryPoint_CompilesWithTheWindowsShaderCompiler()
    {
        if (!OperatingSystem.IsWindows()) return;
        string hlsl = Viewer3DShaders.Hlsl;
        var failed = new List<string>();
        foreach (var (entry, profile) in Entries())
        {
            try { Vortice.D3DCompiler.Compiler.Compile(hlsl, entry, "scene.hlsl", profile); }
            catch (Exception e) { failed.Add($"{entry} ({profile}): {e.Message}"); }
        }
        Assert.True(failed.Count == 0, string.Join("\n", failed));
    }
}
