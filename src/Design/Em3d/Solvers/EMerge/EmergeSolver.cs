// brief-emi-2 — EMerge: a finite-element (FEM) solver that runs as a Python program. This folder is everything circuitRF
// knows about EMerge as a program it can find and install: the probe it asks, and what the answer means. Its descriptor
// (Em3dSolverDescriptor) joins the registry with its run (EMI-4/EMI-5); until then Em3dSolvers.Tools lists its program
// so it is installed, found and listed like every other, and no Solver3D can name it.

namespace CircuitRF.Design.Em3d;

public static class EmergeSolver
{
    /// <summary>The program it needs: a Python interpreter with EMerge importable (R-emi2-3a).</summary>
    public static IReadOnlyList<SolverTool> Tools { get; } = [SolverTool.Emerge];

    /// <summary>
    /// R-emi2-3b — the probe, run as <c>&lt;python&gt; -I -c &lt;this&gt;</c>. A constant: no user string is ever
    /// spliced into it. It prints ONE JSON line: EMerge's, Python's and Gmsh's versions, the platform, the logical
    /// core count, the optional sparse solvers EMerge reports available, and the direct solver EMerge itself picks
    /// for a large driven system.
    ///
    /// <para><b>EMerge's internals are read by pattern, not by name.</b> Its solver flags are module-level
    /// <c>_&lt;NAME&gt;_AVAILABLE</c> booleans (EMerge 3.0.0b2's <c>emerge/_emerge/solver.py</c>), and its own choice
    /// is <c>AutomaticRoutine().pick_solver</c>, which reads only the matrix's size and kind (here 20,000 unknowns,
    /// complex symmetric: what a driven FEM system is). Both are internal and may move, so each is asked inside its
    /// own <c>try</c>, and a missing one reads as "not reported" — never as a failed probe. Only <c>import emerge</c>
    /// failing makes the answer a refusal.</para>
    /// </summary>
    public const string ProbeSource = """
        import json, os, platform, re, sys
        out = {"probe": 1, "python": platform.python_version(), "platform": sys.platform, "machine": platform.machine(), "cores": os.cpu_count()}
        try:
            import emerge
            out["emerge"] = getattr(emerge, "__version__", None)
        except Exception as e:
            out["error"] = "import emerge failed: %s: %s" % (type(e).__name__, e)
            print(json.dumps(out)); sys.exit(0)
        try:
            import gmsh
            out["gmsh"] = getattr(gmsh, "__version__", None)
        except Exception:
            out["gmsh"] = None
        try:
            from emerge._emerge import solver as s
            out["sparseSolvers"] = sorted(m.group(1).lower() for n in dir(s) if (m := re.fullmatch(r"_([A-Z0-9]+)_AVAILABLE", n)) and getattr(s, n) is True)
        except Exception:
            out["sparseSolvers"] = []
        try:
            import scipy.sparse as sp
            out["directSolver"] = type(s.AutomaticRoutine().pick_solver(sp.csc_matrix((20000, 20000), dtype=complex), None, s.MatrixType.COMPLEX_SYMMETRIC)).__name__
        except Exception:
            out["directSolver"] = None
        print(json.dumps(out))
        """;

    /// <summary>How long the probe may take. A first <c>import emerge</c> in an environment without compiled bytecode
    /// took 17 s (macOS arm64, measured); the installer compiles it, so a probe of circuitRF's own install takes ~0.6 s.</summary>
    public static TimeSpan ProbeTimeout { get; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The interpreter a NAMED folder stands for (R-emi2-3a: Settings may name a Python, or a folder holding a venv):
    /// the folder of a virtual environment (<c>bin/python</c>, <c>Scripts\python.exe</c>) or of a circuitRF EMerge home
    /// (its <c>env/</c>). Null when it holds none.
    /// </summary>
    public static string? InterpreterIn(string folder)
    {
        string[] candidates =
        [
            Path.Combine(folder, "bin", "python"), Path.Combine(folder, "Scripts", "python.exe"),
            Path.Combine(folder, "env", "bin", "python"), Path.Combine(folder, "env", "Scripts", "python.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The folders whose change means a different EMerge behind the same interpreter: the environment's
    /// <c>site-packages</c>. A <c>pip install</c> of another version renames a <c>.dist-info</c> folder in it, which
    /// moves its timestamp, while the interpreter itself — a link into a Python installation — stays as it was. Part
    /// of the probe cache's key (R-emi2-3c).
    /// </summary>
    internal static IEnumerable<string> SitePackages(string interpreter)
    {
        string? root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(interpreter)));
        if (root is null) yield break;
        string windows = Path.Combine(root, "Lib", "site-packages");
        if (Directory.Exists(windows)) yield return windows;
        string lib = Path.Combine(root, "lib");
        if (!Directory.Exists(lib)) yield break;
        foreach (string py in Directory.EnumerateDirectories(lib, "python*").Order(StringComparer.Ordinal))
        {
            string site = Path.Combine(py, "site-packages");
            if (Directory.Exists(site)) yield return site;
        }
    }
}
