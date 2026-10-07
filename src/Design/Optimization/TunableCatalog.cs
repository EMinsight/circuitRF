using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Optimization;

/// <summary>What a tunable is: a component's parameter, a VAR row, or a sub-circuit instance's own
/// declared cell parameter.</summary>
public enum TunableKind { Parameter, Variable, CellParameter }

/// <summary>
/// One value of a design that can be tuned (overview D1/D2).
/// </summary>
/// <param name="Key">The identity (overview D3).</param>
/// <param name="Location"><c>top</c>, or <c>DUT · ×2</c> — the cell it lives in and how many
/// instances of that cell share it.</param>
/// <param name="Cell">The cell as the <c>.cnl</c> spells its instance type; null at the top.</param>
/// <param name="InstanceCount">How many instances of <paramref name="Cell"/> the whole hierarchy holds
/// (1 at the top). Tuning a value inside a cell moves it in all of them.</param>
/// <param name="Owner">The instance, or the variable's own name.</param>
/// <param name="Parameter">The parameter's name; null for a variable.</param>
/// <param name="ValueText">The value as the design holds it (<c>47 pF</c>).</param>
/// <param name="Value">The number in <paramref name="Unit"/> (47).</param>
/// <param name="Unit">The engine spelling of its unit, "" for none.</param>
/// <param name="IsInteger">The parameter only takes whole numbers (a finger count, a device multiplier).</param>
/// <param name="IsDefault">A cell parameter the instance does not override — the value is the cell's
/// declared default, and Push adds an override rather than editing one.</param>
/// <param name="ReadOnlyReason">Why Push cannot write it; null when it can. Tuning and optimizing it
/// still work.</param>
/// <param name="DisabledReason">Why it is offered but cannot be moved right now (a parametric sweep
/// sweeps it); null when it can.</param>
/// <param name="DefaultMin">The range a first activation gives it (overview D4), in its own unit.</param>
/// <param name="DefaultMax">Upper end of that range.</param>
/// <param name="RangeGuessed">The value is zero, so the default range is a guess.</param>
public sealed record Tunable(
    string      Key,
    string      Location,
    string?     Cell,
    int         InstanceCount,
    string      Owner,
    string?     Parameter,
    TunableKind Kind,
    string      ValueText,
    double      Value,
    string      Unit,
    bool        IsInteger,
    bool        IsDefault,
    string?     ReadOnlyReason,
    string?     DisabledReason,
    string      DefaultMin,
    string      DefaultMax,
    bool        RangeGuessed);

/// <summary>
/// Every tunable of a schematic, at any depth, plus the keys its tuning setup names that resolve to
/// nothing (overview D3).
///
/// <para><b>It walks the netlist the design extracts to, not the drawing.</b> The hierarchy is
/// descended once, by <see cref="NetExtractor"/> with the caller's resolver — the one Simulate uses —
/// so the cell names in the keys are spelled exactly as the <c>.cnl</c> spells the instance types,
/// a cell two workspaces both call <c>Amp</c> gets the same <c>Amp_2</c> here as in the netlist, and
/// there is no second resolver to disagree with the first.</para>
/// </summary>
public sealed class TunableCatalog
{
    /// <summary>Parameters that only take whole numbers. Case matters: <c>m</c> is the device
    /// multiplier, <c>M</c> a diode's grading coefficient.</summary>
    private static readonly HashSet<string> IntegerParameters = new(StringComparer.Ordinal)
        { "m", "Nf", "NumFingers", "Fingers" };

    /// <summary>Numbers that are identities, not values: a port's number, a block's port count.</summary>
    private static readonly HashSet<string> IdentityParameters = new(StringComparer.Ordinal)
        { "Num", "NumPorts", "NumFreqs" };

    private readonly Dictionary<string, Tunable> _byKey;
    private readonly Dictionary<string, string>  _notOffered;
    private IReadOnlyDictionary<string, SchematicEditModel> _drawings =
        new Dictionary<string, SchematicEditModel>(StringComparer.Ordinal);

    private TunableCatalog(List<Tunable> tunables, Dictionary<string, string> notOffered, List<string> unresolved)
    {
        Tunables       = tunables;
        _byKey         = tunables.GroupBy(t => t.Key, StringComparer.Ordinal)
                                 .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _notOffered    = notOffered;
        UnresolvedKeys = unresolved;
    }

    /// <summary>Every tunable, top level first, then each cell in the netlist's own order.</summary>
    public IReadOnlyList<Tunable> Tunables { get; }

    /// <summary>Keys the design's tuning setup names (entries and preset values) that resolve to
    /// nothing at all — a warning, never an error (overview D3).</summary>
    public IReadOnlyList<string> UnresolvedKeys { get; }

    /// <summary>The tunable with this key, or null.</summary>
    public Tunable? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>Why a key that DOES name a value of the design is not offered (it is an expression,
    /// a string, a port number); null when it is offered or names nothing.</summary>
    public string? WhyNotOffered(string key) => _notOffered.GetValueOrDefault(key);

    /// <summary>
    /// The drawing each scope's values live in: <c>""</c> for the tuned schematic itself, otherwise the
    /// cell as the keys spell it. These are the very models the resolver handed out — in the GUI the
    /// open sessions' own — so Push writes into the drawing that is on screen. Empty for a catalog of
    /// a netlist, which has no drawing.
    /// </summary>
    public IReadOnlyDictionary<string, SchematicEditModel> Drawings => _drawings;

    /// <summary>The cell a drawing is, as the keys spell it — <c>""</c> for the tuned schematic, null for
    /// a drawing this design does not reach.</summary>
    public string? CellOf(SchematicEditModel drawing)
    {
        foreach (var (cell, model) in _drawings)
            if (ReferenceEquals(model, drawing)) return cell;
        return null;
    }

    /// <summary>
    /// The key of a parameter row as drawn in <paramref name="drawing"/> — a VAR row is its variable,
    /// anything else <c>instance.parameter</c> — or null when the catalog does not offer it. The one
    /// mapping the Inspector, the canvas and Push share, so the three cannot disagree on what a row is.
    /// </summary>
    public string? KeyFor(SchematicEditModel drawing, EditableComponent component, EditableParameter parameter)
    {
        if (CellOf(drawing) is not { } cell || string.IsNullOrWhiteSpace(parameter.Name)) return null;
        string prefix = cell.Length == 0 ? "" : cell + ":";
        string key = component.Symbol == SymbolKind.Var
            ? prefix + parameter.Name.Trim()
            : $"{prefix}{component.InstanceName}.{parameter.Name}";
        return _byKey.ContainsKey(key) ? key : null;
    }

    /// <summary>
    /// The catalog of a schematic. <paramref name="cells"/> is the resolver Simulate uses — the
    /// workspace's in the GUI, <see cref="DiskCellResolver.Instance"/> headless.
    /// </summary>
    /// <param name="workspaceRoot">The workspace the schematic belongs to. A cell outside it is
    /// read-only (Push would write another workspace). Null skips that test.</param>
    public static TunableCatalog Discover(SchematicEditModel schematic, ICellResolver cells, string? workspaceRoot = null)
    {
        var recording = new RecordingResolver(cells);
        var extracted = NetExtractor.Extract(schematic, "tb", recording);

        // Only the schematic's own VAR rows are top-level variables. The extracted globals also hold a
        // cell's own parameter defaults, a kit's process constants and the selected corners — none of
        // which is a row anyone could push a value into.
        var varRows = schematic.Components
            .Where(c => c.Symbol == SymbolKind.Var && c.Disable is not (DisableState.Open or DisableState.Short))
            .SelectMany(c => c.Parameters)
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => p.Name.Trim())
            .ToHashSet(StringComparer.Ordinal);

        // Extraction ADDS parameters a drawing never stated — a microstrip's substrate comes from the
        // workspace technology. Those are not values of this design and Push has no row to write them
        // into, so an instance parameter is offered only when the component in the drawing holds it.
        var drawings = new Dictionary<string, SchematicEditModel>(StringComparer.Ordinal) { [""] = schematic };
        foreach (var (libName, key) in extracted.CellKeys)
            if (recording.Seen.TryGetValue(key, out var model)) drawings[libName] = model;

        bool Stored(string? cell, string instance, string parameter)
            => !drawings.TryGetValue(cell ?? "", out var m)          // a kit cell: no drawing to ask
            || m.Components.Any(c => c.InstanceName == instance && c.Parameters.Any(p => p.Name == parameter));

        // A disabled component is not in the netlist; one excluded from it (a probe, a MEAS block) has
        // nothing to tune. Instances the extraction emitted are exactly the rest.
        var catalog = FromNetlist(extracted.TestBench, extracted.Library, varRows, extracted.CellKeys, workspaceRoot, Stored);
        catalog._drawings = drawings;
        return catalog;
    }

    /// <summary>Passes every resolution through, remembering each cell's drawing by its key.</summary>
    private sealed class RecordingResolver(ICellResolver inner) : ICellResolver
    {
        public Dictionary<string, SchematicEditModel> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);

        public CellResolution? Resolve(EditableComponent cellInstance, SchematicEditModel containingModel)
        {
            var r = inner.Resolve(cellInstance, containingModel);
            if (r is not null) Seen.TryAdd(r.Key, r.Schematic);
            return r;
        }
    }

    /// <summary>
    /// The catalog of a netlist — what <see cref="Discover"/> computes after extraction, and what
    /// <c>check</c> asks of a hand-written <c>.cnl</c>.
    /// </summary>
    /// <param name="variableNames">The top-level names that are VAR rows. Null treats every global as
    /// one, which is right for a <c>.cnl</c>, whose globals are its variables.</param>
    /// <param name="cellKeys">Library name → cell folder (<see cref="NetExtractor.ExtractionResult.CellKeys"/>).
    /// Null when unknown — then nothing is reported read-only.</param>
    /// <param name="stored">Whether the drawing of a scope (null cell = the top) holds a parameter on an
    /// instance. Null when there is no drawing — a <c>.cnl</c> holds every value it states.</param>
    public static TunableCatalog FromNetlist(
        TestBench tb, Library lib, IReadOnlyCollection<string>? variableNames = null,
        IReadOnlyDictionary<string, string>? cellKeys = null, string? workspaceRoot = null,
        Func<string?, string, string, bool>? stored = null)
    {
        var tunables   = new List<Tunable>();
        var notOffered = new Dictionary<string, string>(StringComparer.Ordinal);
        var counts     = InstanceCounts(tb, lib);

        var swept = tb.Analyses.OfType<ParametricSweepAnalysis>()
            .Where(a => a.Enabled)
            .GroupBy(a => a.SweepVarName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        var topVars = tb.GlobalVariables.Where(v => variableNames is null || variableNames.Contains(v.Name));
        Scope(null, 1, null, tb.Instances, topVars);

        // Cells in the library's own (leaf-first) order; one the hierarchy never reaches is not part
        // of this design, however much a kit's netlist brought along.
        foreach (var cell in lib.Cells)
        {
            if (!counts.TryGetValue(cell.Name, out int n)) continue;
            Scope(cell.Name, n, ReadOnlyReason(cell.Name, cellKeys, workspaceRoot), cell.Instances, cell.Variables);
        }

        var unresolved = new List<string>();
        if (tb.Tuning is { } setup)
        {
            var named = setup.Variables.Select(v => v.Key)
                .Concat(setup.Presets.SelectMany(p => p.Values.Keys));
            var known = tunables.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in named.Distinct(StringComparer.Ordinal))
                if (!known.Contains(key) && !notOffered.ContainsKey(key))
                    unresolved.Add(key);
        }

        return new TunableCatalog(tunables, notOffered, unresolved);

        void Scope(string? cell, int count, string? readOnly, IEnumerable<Instance> instances, IEnumerable<Variable> vars)
        {
            string prefix   = cell is null ? "" : cell + ":";
            string location = cell is null ? "top" : $"{cell} · ×{count}";

            foreach (var inst in instances)
            {
                if (lib.Find(inst.Reference) is { } def)
                {
                    // A sub-circuit instance: its cell's DECLARED parameters, per instance, whether
                    // this instance overrides them or inherits the default (overview D2).
                    foreach (var decl in def.Parameters)
                    {
                        var ov = inst.Overrides.FirstOrDefault(o => o.Name == decl.Name);
                        var (expr, unit) = ov is null ? (decl.DefaultExpression, decl.Unit) : (ov.Expression, ov.Unit);
                        Consider($"{prefix}{inst.InstanceName}.{decl.Name}", inst.InstanceName, decl.Name,
                                 TunableKind.CellParameter, expr, unit, ov is null);
                    }
                    continue;
                }

                foreach (var ov in inst.Overrides)
                {
                    if (stored?.Invoke(cell, inst.InstanceName, ov.Name) == false)
                    {
                        notOffered[$"{prefix}{inst.InstanceName}.{ov.Name}"] =
                            "the netlist supplies it (a workspace technology's substrate, say); the drawing does not hold it";
                        continue;
                    }
                    Consider($"{prefix}{inst.InstanceName}.{ov.Name}", inst.InstanceName, ov.Name,
                             TunableKind.Parameter, ov.Expression, ov.Unit, isDefault: false);
                }
            }

            foreach (var v in vars)
                Consider(prefix + v.Name, v.Name, null, TunableKind.Variable, v.Expression, v.Unit, isDefault: false);

            void Consider(string key, string owner, string? parameter, TunableKind kind,
                          string expression, string? unit, bool isDefault)
            {
                string name = parameter ?? owner;
                if (name.StartsWith("__", StringComparison.Ordinal)) return;   // circuitRF plumbing, not a value

                string text = TunableValue.Text(expression, unit);
                if (parameter is not null && IdentityParameters.Contains(parameter))
                {
                    notOffered[key] = $"{parameter} is a number that identifies, not a value to tune";
                    return;
                }
                if (!TunableValue.TryParse(text, out double number, out string displayUnit, out _))
                {
                    notOffered[key] = $"its value '{text}' is not a plain number — tune the variable it reads instead";
                    return;
                }

                var (min, max, guessed) = TunableValue.DefaultRange(number, displayUnit);
                string? disabled = cell is null && kind == TunableKind.Variable && swept.TryGetValue(owner, out var sweep)
                    ? $"swept by {sweep}" : null;

                tunables.Add(new Tunable(
                    key, location, cell, count, owner, parameter, kind, text, number, displayUnit,
                    parameter is not null && IntegerParameters.Contains(parameter), isDefault,
                    readOnly, disabled, min, max, guessed));
            }
        }
    }

    /// <summary>How many instances of each cell the flattened hierarchy holds. Cells no instance
    /// reaches are absent.</summary>
    private static Dictionary<string, int> InstanceCounts(TestBench tb, Library lib)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        Walk(tb.Instances, 1, 0);
        return counts;

        void Walk(IEnumerable<Instance> instances, int multiplicity, int depth)
        {
            if (depth > 64) return;   // extraction refuses a cycle; this only guards a hand-written one
            foreach (var inst in instances)
            {
                if (lib.Find(inst.Reference) is not { } cell) continue;
                counts[cell.Name] = counts.GetValueOrDefault(cell.Name) + multiplicity;
                Walk(cell.Instances, multiplicity, depth + 1);
            }
        }
    }

    /// <summary>Why Push cannot write into a cell (overview D2), or null when it can.</summary>
    private static string? ReadOnlyReason(string cell, IReadOnlyDictionary<string, string>? cellKeys, string? workspaceRoot)
    {
        if (cellKeys is null) return null;
        if (!cellKeys.TryGetValue(cell, out var folder))
            return "it is a kit or library cell, defined by a netlist rather than a schematic";

        if (workspaceRoot is { Length: > 0 } && Path.IsPathFullyQualified(folder))
        {
            string root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(folder).StartsWith(root, StringComparison.Ordinal))
                return "it belongs to another workspace";
        }

        string schematicDir = CellFolder.SubFolderPath(folder, ViewType.Schematic);
        if (Directory.Exists(schematicDir)
            && Directory.EnumerateFiles(schematicDir, "*.csch")
                        .Any(f => File.GetAttributes(f).HasFlag(FileAttributes.ReadOnly)))
            return "its schematic file is read-only";

        return null;
    }
}
