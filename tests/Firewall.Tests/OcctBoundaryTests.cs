using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CircuitRF.Firewall.Tests;

/// <summary>
/// The OpenCASCADE boundary (brief-em3d-62 R-em3d62-6e) — a sibling of <see cref="SolverBoundaryTests"/>,
/// with one inversion.
///
/// <para><b>What it holds.</b> OCCT is LGPL-2.1 with the Open CASCADE Exception, and circuitRF SHIPS it —
/// unmodified shared libraries in one folder, loaded by exactly one program, <c>tools/geometry-worker</c>,
/// which circuitRF starts as a separate process and speaks to over a pipe. The licence boundary is that file
/// boundary: no managed assembly references or imports an OCCT library, no project references an OCCT
/// package, and no OCCT source is in the tree (the owner's rule, 2026-09-27: not one OCCT file, ever — see
/// brief 61's finding on eight files whose header contradicts the licence).</para>
///
/// <para><b>The inversion.</b> <see cref="SolverBoundaryTests.ThirdPartyNoticesNamesNoSolver"/> requires the
/// solvers to be ABSENT from <c>THIRD-PARTY-NOTICES.md</c>, because circuitRF redistributes none of them.
/// OCCT is redistributed, so here the violation is its entry being MISSING — shipping it without the notice
/// and the written offer is the breach. That check is this file's, beside the solver one, which is
/// unchanged.</para>
///
/// <para>Every rule has a planted-violation twin (R-em3d6-6b): the same scanner pointed at something that
/// breaks the rule, which must be reported.</para>
/// </summary>
public sealed class OcctBoundaryTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-occtwall-" + Guid.NewGuid().ToString("N")[..10]);

    public OcctBoundaryTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ── 1. no managed assembly references or imports an OCCT library ─────────────────────────

    [Fact]
    public void NoCircuitRfAssemblyReferencesOrImportsAnOcctLibrary()
        => AssertNone(Boundary.AssemblyViolations(SolverBoundaryTests.CircuitRfAssemblies(), OcctBoundary.Named), "assembly");

    /// <summary>A real assembly whose one method is a P/Invoke into <c>TKernel</c>, so the ModuleReference the
    /// scanner reads is genuinely in its metadata.</summary>
    [Fact]
    public void Planted_AnAssemblyWithAPInvokeIntoTKernelIsCaught()
    {
        string path = Path.Combine(_tmp, "Planted.dll");
        var builder = new PersistedAssemblyBuilder(new AssemblyName("Planted"), typeof(object).Assembly);
        var module  = builder.DefineDynamicModule("Planted");
        var type    = module.DefineType("Planted.Native", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
        type.DefinePInvokeMethod("OCCT_Version_String_Complete", "TKernel",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl,
            CallingConventions.Standard, typeof(IntPtr), Type.EmptyTypes, CallingConvention.Cdecl, CharSet.Ansi);
        type.CreateType();
        builder.Save(path);

        Assert.NotEmpty(Boundary.AssemblyViolations([path], OcctBoundary.Named));
    }

    [Fact]
    public void NoInteropDeclarationInSrcNamesAnOcctLibrary()
        => AssertNone(Boundary.InteropViolations(SolverBoundaryTests.SourceFiles(Path.Combine(SolverBoundaryTests.RepoRoot(), "src"), "*.cs"),
                                                 OcctBoundary.Named), "source file");

    [Theory]
    [InlineData("""[DllImport("TKernel")] static extern IntPtr OCCT_Version_String_Complete();""")]
    [InlineData("""[LibraryImport("libTKBO.8.0.dylib")] internal static partial void Cut();""")]
    [InlineData("""const string Lib = "OCCTWrapper"; [DllImport(Lib)] static extern void Fuse();""")]
    [InlineData("""var h = NativeLibrary.Load("libTKDESTEP.so.8.0");""")]
    public void Planted_AnInteropDeclarationNamingOcctIsCaught(string code)
        => Assert.NotEmpty(Boundary.InteropViolations([Write("Planted.cs", code)], OcctBoundary.Named));

    // ── 2. no project file references an OCCT package ────────────────────────────────────────

    [Fact]
    public void NoProjectFileReferencesAnOcctPackage()
        => AssertNone(Boundary.ProjectFileViolations(SolverBoundaryTests.ProjectFiles(), OcctBoundary.Named), "project file");

    [Theory]
    [InlineData("""<PackageReference Include="OpenCascade.Net" Version="7.9.0" />""")]
    [InlineData("""<Reference Include="Occt.Interop"><HintPath>lib/Occt.Interop.dll</HintPath></Reference>""")]
    public void Planted_AProjectReferenceToOcctIsCaught(string item)
    {
        string csproj = Write("Planted.csproj", $"""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{item}</ItemGroup></Project>""");
        Assert.NotEmpty(Boundary.ProjectFileViolations([csproj], OcctBoundary.Named));
    }

    // ── 3. no OCCT source is vendored ────────────────────────────────────────────────────────

    [Fact]
    public void NoFileUnderSrcOrToolsCarriesOcctsLicenceHeader()
    {
        string root = SolverBoundaryTests.RepoRoot();
        var files = SolverBoundaryTests.SourceFiles(Path.Combine(root, "src"), "*")
                   .Concat(SolverBoundaryTests.SourceFiles(Path.Combine(root, "tools"), "*"));
        AssertNone(Boundary.HeaderViolations(files, OcctBoundary.HeaderSignatures), "source file");
    }

    /// <summary>OCCT's two headers as they open a source file — the LGPL one on ~10,500 files and the
    /// proprietary one on the eight <c>TKGeomBase</c> files (brief 61 Q11) — plus its copyright lines.
    /// Composed here, not copied from a fixture: no OCCT file enters the repository, a header included.</summary>
    [Theory]
    [InlineData("// Copyright (c) 1999-2014 OPEN CASCADE SAS\n//\n// This file is part of Open CASCADE Technology software library.\n#include <gp_Pnt.hxx>")]
    [InlineData("// Created on: 1991-02-26\n// Copyright (c) 1991-1999 Matra Datavision\n#include <TopoDS.hxx>")]
    [InlineData("// This file is part of commercial software by OPEN CASCADE SAS,\n// furnished in accordance with the terms")]
    public void Planted_AFileCarryingOcctsHeaderIsCaught(string header)
        => Assert.NotEmpty(Boundary.HeaderViolations([Write("planted.cxx", header)], OcctBoundary.HeaderSignatures));

    // ── 4. the notice is PRESENT (the inversion) ─────────────────────────────────────────────

    [Fact]
    public void ThirdPartyNoticesCarriesTheOcctEntryAndTheWrittenOffer()
    {
        string root = SolverBoundaryTests.RepoRoot();
        string recipeVersion = File.ReadAllLines(Path.Combine(root, "tools", "geometry-worker", "occt", "recipe.env"))
                                   .First(l => l.StartsWith("OCCT_VERSION=", StringComparison.Ordinal))["OCCT_VERSION=".Length..];
        AssertNone(OcctBoundary.NoticeViolations(File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md")), recipeVersion),
                   "THIRD-PARTY-NOTICES.md");
        Assert.True(File.Exists(Path.Combine(root, "licenses", "OCCT-exception-1.0.txt")), "licenses/OCCT-exception-1.0.txt is missing");
        Assert.True(File.Exists(Path.Combine(root, "licenses", "LGPL-2.1.txt")), "licenses/LGPL-2.1.txt is missing");
    }

    [Theory]
    [InlineData("## 1. CSparse.NET — LGPL-2.1-only\n")]
    [InlineData("## 2. Open CASCADE Technology — LGPL-2.1-only, with the Open CASCADE Exception 1.0\nOCCT 8.0.1. No offer here.\n")]
    public void Planted_NoticesWithoutTheOcctEntryOrItsOfferAreCaught(string notices)
        => Assert.NotEmpty(OcctBoundary.NoticeViolations(notices, "8.0.1"));

    /// <summary>An offer naming a version other than the recipe's is an offer for source nobody ships.</summary>
    [Fact]
    public void Planted_AnOfferForAnotherVersionIsCaught()
    {
        string notices = File.ReadAllText(Path.Combine(SolverBoundaryTests.RepoRoot(), "THIRD-PARTY-NOTICES.md"));
        Assert.NotEmpty(OcctBoundary.NoticeViolations(notices, "9.9.9"));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static void AssertNone(IReadOnlyList<string> violations, string what)
        => Assert.True(violations.Count == 0,
            $"OpenCASCADE boundary (brief-em3d-62 R-em3d62-6e) crossed in {violations.Count} {what}(s). circuitRF " +
            "reaches OCCT only through tools/geometry-worker, a separate program, and ships it with its notice:\n  " +
            string.Join("\n  ", violations));

    private string Write(string relative, string content)
    {
        string path = Path.Combine(_tmp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}

internal static class OcctBoundary
{
    /// <summary>
    /// OCCT's names: the project's own (<c>OCCT</c>, <c>OpenCASCADE</c>) anywhere, case-insensitively, and a
    /// toolkit library — <c>TK</c> then letters (<c>TKernel</c>, <c>TKBO</c>), alone or as <c>libTK…</c>, which
    /// is how every OCCT library is named on every platform. The toolkit half is case-SENSITIVE and must be
    /// the whole name, so an ordinary word is never caught.
    /// </summary>
    public static readonly Regex Named = new(
        @"(?i:occt|open\W?cascade)|(?:^|[/\\])(?:lib)?TK[A-Za-z][A-Za-z0-9]*(?:\.|$)",
        RegexOptions.Compiled);

    /// <summary>The lines that open an OCCT source file (brief 61 Q11 §3, §4).</summary>
    public static readonly IReadOnlyList<Regex> HeaderSignatures =
    [
        new(@"This\s+file\s+is\s+part\s+of\s+Open\s+CASCADE\s+Technology\s+software\s+library", RegexOptions.Compiled),
        new(@"This\s+file\s+is\s+part\s+of\s+commercial\s+software\s+by\s+OPEN\s+CASCADE\s+SAS", RegexOptions.Compiled),
        new(@"Copyright\s+\(c\)\s+[\d\s,\-–]+(?:OPEN\s+CASCADE\s+SAS|Matra\s+Datavision)", RegexOptions.Compiled),
    ];

    /// <summary>What the OCCT entry must carry (R-em3d62-6a, -6c): the component, both licences, the prominent
    /// notice, and the written offer — naming the version the recipe builds.</summary>
    public static IReadOnlyList<string> NoticeViolations(string notices, string version)
    {
        string flat = Regex.Replace(notices, @"[\s>*]+", " ");
        var found = new List<string>();
        void Need(string text, string why)
        {
            if (!flat.Contains(text, StringComparison.Ordinal)) found.Add($"no \"{text}\" — {why}");
        }
        Need("Open CASCADE Technology", "the component is not named");
        Need("LGPL-2.1-only, with the Open CASCADE Exception 1.0", "the licence is not stated");
        Need("licenses/OCCT-exception-1.0.txt", "the exception text is not referenced");
        Need("uses facilities provided by Open CASCADE Technology", "the prominent notice the exception asks for is missing");
        Need($"circuitRF binaries include Open CASCADE Technology {version}, unmodified, as shared libraries.",
             $"the written offer is missing or names a version other than the recipe's ({version})");
        Need($"corresponding source code of Open CASCADE Technology {version}", "the offer does not name the source it offers");
        Need("\"OCCT source request\"", "the offer does not say how to ask");
        return found;
    }
}
