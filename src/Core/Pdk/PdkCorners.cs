using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist.Spice;

namespace CircuitRF.Core.Pdk;

/// <summary>
/// One axis of corner choice a kit offers: the alternatives declared by ONE of its files.
/// </summary>
/// <param name="AxisId">
/// The declaring file, as the kit refers to it. Stable identity — a selection is recorded against
/// this, so it must not be a display string.
/// </param>
/// <param name="DisplayName">The file's own stem, which is the only name the kit gives an axis.</param>
/// <param name="Options">Section names, verbatim and in declaration order — the kit's own vocabulary.</param>
public sealed record PdkCornerAxis(string AxisId, string DisplayName, IReadOnlyList<string> Options);

/// <summary>
/// What reading one corner section produced (<see cref="PdkCorners.SectionFor"/>).
/// </summary>
/// <param name="AxisFile">The corner file, absolute.</param>
/// <param name="Section">The section read.</param>
/// <param name="Variables">The bindings — the process constants the section states, and its includes' globals.</param>
/// <param name="Cells">The subcircuits of the libraries the section includes.</param>
/// <param name="ModelCards">Their model cards.</param>
/// <param name="Statistics">Every distribution call met in the section and its includes.</param>
/// <param name="FilesRead">Every file the section read, the corner file's own text first.</param>
public sealed record PdkCornerSection(
    string                              AxisFile,
    string                              Section,
    IReadOnlyList<Variable>             Variables,
    Library                             Cells,
    IReadOnlyList<SpiceModelCard>       ModelCards,
    IReadOnlyList<SpiceStatisticalUse>  Statistics,
    IReadOnlyList<string>               FilesRead);

/// <summary>
/// The corners a kit offers, and what choosing one actually binds.
///
/// <para><b>A corner is a named set of global variable bindings — nothing else.</b> Measured across a
/// kit's capacitor, resistor, diode, bipolar and both MOS corner files: every section binds a
/// handful of process parameters and then includes the SAME shared model file every other section of
/// that file includes. The subcircuits and model cards are identical across corners. That is what
/// makes this a substitution into the testbench's globals rather than a different netlist, a
/// re-import, or a variant of the parts.</para>
///
/// <para><b>One axis per FILE, which is structural rather than a naming convention.</b> A kit states
/// its corners one file per device family, so choosing a capacitor corner and a resistor corner are
/// two independent choices. Flattening them into one list would offer a single pick where the kit
/// offers several.</para>
///
/// <para><b>Nothing here decides which sections are "really" corners.</b> The kit declaring them as
/// alternatives is the whole semantic; filtering on <c>_typ</c>/<c>_wcs</c> would encode one
/// supplier's habits and go blank on the next kit.</para>
/// </summary>
public static class PdkCorners
{
    /// <summary>
    /// The axes a set of netlists declares. A file declaring no section contributes none, which is
    /// nearly every netlist — an axis per file regardless would put an empty picker in front of every
    /// user of every kit.
    /// </summary>
    /// <param name="netlists">
    /// Absolute paths, paired with the identity a selection should be recorded against. The caller
    /// owns that identity because only it knows what a path is relative TO — a kit that moves must
    /// not lose the corner its designs are set to.
    /// </param>
    public static IReadOnlyList<PdkCornerAxis> Discover(
        IEnumerable<(string AbsolutePath, string AxisId)> netlists)
    {
        var axes = new List<PdkCornerAxis>();

        foreach (var (path, axisId) in netlists)
        {
            // A kit's netlists are mostly model libraries — megabytes of subcircuits and model cards
            // that declare no section at all. Parsing every one of them to learn that costs the whole
            // import; a scan for the directive that OPENS a section costs a read. The check is
            // deliberately the same shape the reader's own section handling keys on (a `.lib` with
            // exactly one word after it — two words is a REQUEST, not a declaration), so a file this
            // skips is one the reader would have reported no sections for.
            if (!DeclaresAnySection(path)) continue;

            SpiceNetlistResult read;
            try { read = SpiceNetlistReader.ReadFile(path); }
            catch { continue; }          // a file that will not read declares no corners we can trust

            foreach (var set in read.Sections)
            {
                if (set.Names.Count == 0) continue;
                axes.Add(new PdkCornerAxis(axisId, Path.GetFileNameWithoutExtension(path), set.Names));
            }
        }

        // Stable order, so a panel does not reshuffle between opens.
        return [.. axes.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Largest file this will read to look for a section declaration. Past it the file is a
    /// data set, not a corner file, and reading it whole would cost more than the answer is worth.</summary>
    private const long SectionScanLimitBytes = 8L * 1024 * 1024;

    private static readonly Regex SectionOpener =
        new(@"^\s*\.lib\s+\S+\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static bool DeclaresAnySection(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > SectionScanLimitBytes) return false;
            return SectionOpener.IsMatch(File.ReadAllText(path));
        }
        catch { return false; }
    }

    /// <summary>
    /// What choosing <paramref name="section"/> on <paramref name="axisFile"/> binds.
    ///
    /// <para><b>Requested the way the dialect itself requests a corner</b> — <c>.lib &lt;file&gt;
    /// &lt;section&gt;</c> — rather than by reaching into the file and reading its parameters
    /// directly. That is the format's own mechanism for "read this one alternative", and using it
    /// means the section's conditionals, nested includes and parameter forms are handled by the one
    /// reader that already handles them, instead of by a second grammar that would drift.</para>
    ///
    /// <para><b>What comes back is the section AND whatever it includes, deliberately.</b> A corner
    /// file's section IS the entry point to the model library — that is what it exists to be — so a
    /// caller uses this INSTEAD of reading that library separately, never in addition, or the two
    /// reads bind the same names twice. Measured the overlap is empty in practice: its
    /// model files declare every parameter inside a subcircuit, so a corner's bindings are exactly
    /// its own two or three process constants.</para>
    /// </summary>
    /// <param name="problems">Anything the read could not use, by file and line. Never silently dropped.</param>
    public static IReadOnlyList<Variable> BindingsFor(
        string axisFile, string section, out IReadOnlyList<string> problems)
        => SectionFor(axisFile, section, out problems)?.Variables ?? [];

    /// <summary>
    /// Everything choosing <paramref name="section"/> reads: the bindings <see cref="BindingsFor"/> returns, and
    /// the subcircuits and model cards of the libraries the section includes — with every distribution call kept
    /// LIVE, because what the section binds reaches only the extraction's <c>netlist.cnl</c>.
    ///
    /// <para><b>The definitions are what a statistical or mismatch section is FOR.</b> A kit's later sections
    /// include a VARIANT of its model library whose subcircuits carry the mismatch draws
    /// (<c>w='agauss(w, …)'</c> on a device inside a subcircuit); the bindings alone would select it and change
    /// nothing. The extraction puts these definitions in place of the part library's own when the section does
    /// not include that library itself (docs/design/spice-models.md §8.11).</para>
    /// </summary>
    /// <returns>Null when nothing was asked for or the section could not be read (the reason is in
    /// <paramref name="problems"/>).</returns>
    public static PdkCornerSection? SectionFor(
        string axisFile, string section, out IReadOnlyList<string> problems)
    {
        problems = [];

        if (string.IsNullOrWhiteSpace(section))
            return null;

        string? dir = Path.GetDirectoryName(Path.GetFullPath(axisFile));
        string file = Path.GetFileName(axisFile);

        SpiceNetlistResult read;
        try
        {
            // Quoted: a kit's own folder may hold a space, and an unquoted path would split into a
            // file and a section that are neither.
            read = SpiceNetlistReader.Read($".lib \"{file}\" \"{section}\"", dir);
        }
        catch (Exception ex)
        {
            problems = [$"'{section}' could not be read from '{file}': {ex.Message}"];
            return null;
        }

        problems = [.. read.Notes.Select(n => n.ToString())];
        return new PdkCornerSection(Path.GetFullPath(axisFile), section, read.Variables, read.Library,
                                    read.ModelCards, read.Statistics, read.FilesRead);
    }

    /// <summary>
    /// The sections of <paramref name="axisFile"/> that carry statistics — a distribution call in the section's own
    /// lines or in a file it includes — in declaration order. What the Info note that a statistical section is
    /// not selected names (docs/design/yield.md §7).
    ///
    /// <para><b>A text scan, not a read</b>, on purpose: it runs over every section of every axis, and reading a
    /// section reads its model library — megabytes, per section. A call is the function name followed by a
    /// bracket, outside a comment line. Two-argument <c>limit</c> is not looked for: the same name is far more
    /// often the clamp, and the text alone cannot cheaply tell which. Each included file is scanned once per call
    /// of this method, and a file past the section-scan limit is not scanned.</para>
    /// </summary>
    public static IReadOnlyList<string> StatisticalSections(string axisFile)
    {
        var verdicts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        return [.. SectionVerdicts(axisFile, verdicts, depth: 0).Where(kv => kv.Value).Select(kv => kv.Key)];
    }

    /// <summary>Each section <paramref name="file"/> declares, in order, and whether it carries statistics.</summary>
    private static List<KeyValuePair<string, bool>> SectionVerdicts(
        string file, Dictionary<string, bool> verdicts, int depth)
    {
        var result = new List<KeyValuePair<string, bool>>();
        string full;
        string[] lines;
        try
        {
            full = Path.GetFullPath(file);
            if (depth > 8 || new FileInfo(full).Length > SectionScanLimitBytes) return result;
            lines = File.ReadAllLines(full);
        }
        catch { return result; }

        string dir = Path.GetDirectoryName(full)!;
        string? open = null;
        bool carries = false;

        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '*') continue;
            var words = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            string directive = words[0].ToLowerInvariant();

            if (open is null)
            {
                if (directive == ".lib" && words.Length == 2) { open = words[1]; carries = false; }
                continue;
            }

            if (directive == ".endl")
            {
                result.Add(new(open, carries));
                open = null;
                continue;
            }

            if (carries) continue;

            if (directive == ".lib" && words.Length >= 3)
            {
                // A request for ONE section of a file: that section's verdict, not the whole file's.
                string target = Path.Combine(dir, words[1].Trim('"', '\''));
                string key    = Path.GetFullPath(target) + "|" + words[2].Trim('"', '\'');
                if (!verdicts.TryGetValue(key, out bool v))
                {
                    verdicts[key] = false;                       // a cycle reads as "nothing more here"
                    v = verdicts[key] = SectionVerdicts(target, verdicts, depth + 1)
                        .Any(kv => kv.Value && kv.Key.Equals(words[2].Trim('"', '\''), StringComparison.OrdinalIgnoreCase));
                }
                carries = v;
            }
            else if (directive is ".include" or ".inc" && words.Length >= 2)
                carries = FileCarriesStatistics(Path.Combine(dir, words[1].Trim('"', '\'')), verdicts, depth + 1);
            else
                carries = DistributionCall.IsMatch(line);
        }

        return result;
    }

    private static readonly Regex DistributionCall =
        new(@"\b(agauss|gauss|aunif|unif)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool FileCarriesStatistics(string path, Dictionary<string, bool> verdicts, int depth)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }
        if (verdicts.TryGetValue(full, out bool known)) return known;
        verdicts[full] = false;                                  // a cycle reads as "nothing more here"
        if (depth > 8) return false;

        bool found = false;
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > SectionScanLimitBytes) return false;
            foreach (var raw in File.ReadLines(full))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '*') continue;
                if (DistributionCall.IsMatch(line)) { found = true; break; }
                if (line.StartsWith(".include", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith(".inc ", StringComparison.OrdinalIgnoreCase))
                {
                    var w = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (w.Length >= 2 && FileCarriesStatistics(
                            Path.Combine(Path.GetDirectoryName(full)!, w[1].Trim('"', '\'')), verdicts, depth + 1))
                    { found = true; break; }
                }
            }
        }
        catch { found = false; }

        return verdicts[full] = found;
    }

    /// <summary>
    /// Whether a section is one this axis actually offers. A recorded selection outlives the kit it
    /// was made against — a kit is updated, or repaired to a different copy — so a stale name must be
    /// caught and reported rather than silently binding nothing, which would leave the design at a
    /// corner nobody chose and every number plausible.
    /// </summary>
    public static bool Offers(PdkCornerAxis axis, string section)
        => axis.Options.Any(o => o.Equals(section, StringComparison.OrdinalIgnoreCase));
}
