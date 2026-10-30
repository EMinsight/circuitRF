// Turns a gdsfactory kit's layer stack and material cards into the stack description the ONE technology
// builder reads (brief-gdf-3 R-gdf3-2).
//
// A gdsfactory kit states its process as data — z, thickness and material for each level, and in some
// kits conductivity and permittivity for each material — but it states only PATTERNED levels. Inter-metal
// oxide, passivation and very often the substrate exist nowhere as levels, so the conversion has to fill
// gaps, and every value it fills in is reported: a plausible number nobody chose, sitting silently in a
// stack, is the defect this conversion exists not to make.
//
// Nothing here builds a Technology. It writes a ProcessStackDescription in the same embedded-conductor
// convention a stack file uses and hands it to ProcessTechnologyBuilder.Build, so a stack file and a
// gdsfactory kit can never disagree about what a stack means. Nothing here matches a material, level or
// layer by NAME either: every decision is made from z, thickness and the numbers a card states.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Layout.TechImport;

/// <summary>One level of a kit's layer stack, exactly as <c>gdsfactory_bridge.technology</c> states it.</summary>
public sealed class GdsfactoryStackLevel
{
    public string Name { get; set; } = "";

    /// <summary><c>[layer, datatype]</c> when the level names one layer, else the expression as text.</summary>
    public JsonElement? Layer { get; set; }

    public double? Zmin { get; set; }
    public double? Thickness { get; set; }
    public string? Material { get; set; }

    [JsonPropertyName("sidewall_angle")]
    public double? SidewallAngle { get; set; }

    public Dictionary<string, JsonElement>? Info { get; set; }

    /// <summary>The single (layer, datatype) this level is drawn on, or null.</summary>
    public LayerKey? Key =>
        Layer is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() == 2
        && a[0].TryGetInt32(out int l) && a[1].TryGetInt32(out int d)
            ? new LayerKey(l, d)
            : null;

    /// <summary>The layer expression as the kit wrote it, when the level is not one layer.</summary>
    public string? Expression => Layer is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
}

/// <summary>What one material card states. Null is "not stated", never zero.</summary>
public sealed class GdsfactoryMaterial
{
    public double? Sigma { get; set; }
    public double? Epsr { get; set; }
    public double? Tand { get; set; }
}

/// <summary>The whole document <c>python -m gdsfactory_bridge.technology</c> prints.</summary>
public sealed class GdsfactoryStackDocument
{
    public string Module { get; set; } = "";
    public Dictionary<string, int[]> Layers { get; set; } = [];

    /// <summary>A layer-properties file the kit's own layer views wrote, or null.</summary>
    public string? LayerViews { get; set; }

    public List<GdsfactoryStackLevel> Stack { get; set; } = [];
    public Dictionary<string, GdsfactoryMaterial> Materials { get; set; } = [];
    public List<string> Problems { get; set; } = [];

    public static GdsfactoryStackDocument? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GdsfactoryStackDocument>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The layer table: the kit's own layer views when it wrote them, else its layer map with no colours
    /// (the builder gives each one, and says so).
    /// </summary>
    public ProcessLayerTable LayerTable()
    {
        if (LayerViews is { Length: > 0 } path && File.Exists(path))
            return LayerPropertiesReader.Read(File.ReadAllText(path));

        int order = 0;
        var entries = Layers.Where(kv => kv.Value is { Length: 2 })
                            .Select(kv => new ProcessLayerEntry(kv.Value[0], kv.Value[1], kv.Key, null, null, true, order++))
                            .ToList();
        return new ProcessLayerTable(entries, entries.Count == 0 ? ["The kit states no layer map."] : []);
    }
}

/// <summary>How much a line of the import report asks of its reader.</summary>
public enum TechnologyNoteSeverity { Note, Warning }

public sealed record TechnologyBuildNote(TechnologyNoteSeverity Severity, string Text)
{
    public override string ToString() => Severity == TechnologyNoteSeverity.Warning ? $"WARNING: {Text}" : Text;
}

/// <summary>A technology built from a gdsfactory kit, and everything the conversion decided on the way.</summary>
/// <param name="Summary">The report's first line: conductors, vias and filled values, counted.</param>
/// <param name="Description">The stack description the builder was handed — the conversion's whole output.</param>
public sealed record GdsfactoryTechnologyResult(
    Technology                         Technology,
    string                             Summary,
    IReadOnlyList<TechnologyBuildNote> Notes,
    int                                FilledValues,
    ProcessStackDescription            Description)
{
    public int Warnings => Notes.Count(n => n.Severity == TechnologyNoteSeverity.Warning);
}

public static class GdsfactoryStackConverter
{
    /// <summary>A material at or above this conductivity is a conductor (R-gdf3-2b step 2).</summary>
    public const double ConductorSigmaSm = 1e5;

    /// <summary>
    /// The thinnest level that can be the SUBSTRATE. A substrate is the lowest level lying wholly below
    /// gdsfactory's wafer surface (z = 0); the thickness floor is what keeps a well or an implant, which
    /// also lies below z = 0, from being taken for it. Wells and implants are a few micrometres; every
    /// substrate a kit states is ten or more.
    /// </summary>
    public const double SubstrateMinThicknessUm = 10.0;

    /// <summary>Two z values this close are the same surface. Kits compute z by adding floats.</summary>
    private const double ToleranceUm = 1e-6;

    /// <summary>How many names one note lists before it counts the rest.</summary>
    private const int NamesPerNote = 12;

    private enum Role { Substrate, Device, Via, FrontEndVia, Conductor, Dielectric, Unused }

    private sealed class Level
    {
        public required GdsfactoryStackLevel Source;
        public required int Index;
        public required double Zmin;
        public required double Thickness;
        public double Top => Zmin + Thickness;
        public string Name => Source.Name;
        public GdsfactoryMaterial? Card;
        public double? Sigma => Card?.Sigma;
        public double? Epsr => Card?.Epsr;
        public bool KnownConductor => Sigma is >= ConductorSigmaSm;
        public bool KnownDielectric => Epsr is not null && Sigma is null or < ConductorSigmaSm;
        public Role Role;
        public Level? Lower, Upper;   // a via's two ends
    }

    /// <summary>A stretch of z with one dielectric medium: a stated level, or a fill (Level null).</summary>
    private readonly record struct Piece(double Lo, double Hi, Level? Medium);

    public static GdsfactoryTechnologyResult Convert(
        GdsfactoryStackDocument doc, ProcessLayerTable? layerTable, string fallbackName)
    {
        var notes  = new List<TechnologyBuildNote>();
        int filled = 0;
        void Note(string text)    => notes.Add(new(TechnologyNoteSeverity.Note, text));
        void Warning(string text) => notes.Add(new(TechnologyNoteSeverity.Warning, text));

        foreach (string p in doc.Problems) Warning(p);

        var levels = ReadLevels(doc, Note);

        // ── the substrate ────────────────────────────────────────────────────────
        var substrate = levels.Where(l => l.Top <= ToleranceUm && l.Thickness >= SubstrateMinThicknessUm)
                              .OrderBy(l => l.Zmin).ThenBy(l => l.Index).FirstOrDefault();
        if (substrate is not null) substrate.Role = Role.Substrate;
        double substrateTop = substrate?.Top ?? 0;
        if (substrate is null)
        {
            filled++;
            Note($"The kit states no substrate level (none lies wholly below z = 0 and is at least " +
                 $"{Um(SubstrateMinThicknessUm)} µm thick), so the substrate's top is taken at z = 0, " +
                 "gdsfactory's wafer surface.");
        }

        Classify(levels, substrate, substrateTop);

        // ── what the classification decided, each step stated ────────────────────
        StateClassification(levels, Note);

        var conductors = levels.Where(l => l.Role == Role.Conductor)
                               .OrderByDescending(l => l.Zmin).ThenByDescending(l => l.Top).ThenBy(l => l.Index)
                               .ToList();
        var dielectrics = levels.Where(l => l.Role == Role.Dielectric).ToList();
        var vias = levels.Where(l => l.Role == Role.Via).OrderByDescending(l => l.Zmin).ThenBy(l => l.Index).ToList();

        // ── gap filling ──────────────────────────────────────────────────────────
        double stackTop = levels.Where(l => l.Role is not (Role.Substrate or Role.Device or Role.FrontEndVia))
                                .Select(l => l.Top).DefaultIfEmpty(substrateTop).Max();
        stackTop = Math.Max(stackTop, substrateTop);

        var background = BackgroundCard(dielectrics);
        var pieces = Pieces(substrateTop, stackTop, conductors, dielectrics);

        var names = new HashSet<string>(levels.Select(l => l.Name), StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        int fillNumber = 0;
        var pieceNames = new Dictionary<Piece, string>();
        var pieceEpsr = new Dictionary<Piece, (double Epsr, double TanD)>();
        foreach (var piece in pieces.OrderByDescending(p => p.Lo))
        {
            if (piece.Medium is { } medium)
            {
                pieceNames[piece] = Unique(medium.Name, used);
                pieceEpsr[piece] = (medium.Epsr!.Value, medium.Card?.Tand ?? 0);
                continue;
            }

            string name;
            do name = $"fill_{++fillNumber}"; while (names.Contains(name));
            pieceNames[piece] = Unique(name, used);

            filled++;
            if (background is { } bg)
            {
                pieceEpsr[piece] = (bg.Card.Epsr!.Value, bg.Card.Tand ?? 0);
                Note($"z = {Um(piece.Lo)} to {Um(piece.Hi)} µm: no level states the medium, so \"{pieceNames[piece]}\" " +
                     $"was filled with the kit's card \"{bg.Name}\" (εr {Num(bg.Card.Epsr!.Value)}), the dielectric " +
                     "its stated levels hold most of.");
            }
            else
            {
                pieceEpsr[piece] = (1, 0);
                Warning($"εr unknown between z = {Um(piece.Lo)} and {Um(piece.Hi)} µm; set before running EM " +
                        $"(\"{pieceNames[piece]}\" is εr 1).");
            }
        }

        // ── the substrate slab ───────────────────────────────────────────────────
        string substrateName = substrate is null ? Unique("substrate", used) : Unique(substrate.Name, used);
        double substrateThickness = substrate?.Thickness ?? ProcessTechnologyBuilder.DefaultSubstrateThicknessUm;
        if (substrate is null)
        {
            filled++;
            Warning($"The kit states no substrate level, so \"{substrateName}\" is the builder's default " +
                    $"{Um(substrateThickness)} µm; set it before running EM.");
        }
        double substrateEpsr = substrate?.Epsr ?? 1;
        if (substrate?.Epsr is null)
        {
            filled++;
            Warning($"εr unknown between z = {Um(substrateTop - substrateThickness)} and {Um(substrateTop)} µm " +
                    $"(the substrate{(substrate?.Source.Material is { } m ? $", \"{m}\", has no material card" : "")}); " +
                    $"set before running EM (\"{substrateName}\" is εr 1).");
        }
        if (substrate?.Sigma is double s and > 0)
            Note($"The substrate's card states σ = {Num(s)} S/m. A stack dielectric carries εr and tanδ only, " +
                 "so its conductivity is not carried; set a loss tangent at the frequency of interest if it matters.");

        // ── the description, in the embedded-conductor convention ───────────────
        // Top first. The dielectric run ABOVE each conductor is written, and the lowest piece of that run is
        // lengthened by the conductor's own thickness, because the builder takes a conductor's thickness back
        // OUT of the run directly above it (a stack file's convention). The two cancel, so every slab lands at
        // the z the kit states; a run too thin to give it back — conductors that overlap — is the builder's to
        // report, as it already reports it for a stack file.
        var entries = new List<ProcessStackEntry>();
        void EmitRun(double lo, double hi, double extendLowestBy)
        {
            var run = pieces.Where(p => p.Lo >= lo - ToleranceUm && p.Hi <= hi + ToleranceUm)
                            .OrderByDescending(p => p.Lo).ToList();
            for (int i = 0; i < run.Count; i++)
            {
                var p = run[i];
                double thickness = p.Hi - p.Lo + (i == run.Count - 1 ? extendLowestBy : 0);
                entries.Add(new ProcessStackEntry(ProcessStackEntryKind.Dielectric, pieceNames[p],
                    ThicknessUm: thickness, RelativePermittivity: pieceEpsr[p].Epsr,
                    LossTangent: pieceEpsr[p].TanD, DrawnOn: []));
            }
        }

        double above = stackTop;
        foreach (var c in conductors)
        {
            EmitRun(c.Top, above, c.Thickness);
            entries.Add(new ProcessStackEntry(ProcessStackEntryKind.Conductor, c.Name,
                ThicknessUm: c.Thickness, ConductivitySm: c.Sigma ?? 0, DrawnOn: [c.Source.Key!.Value]));
            above = Math.Min(above, c.Zmin);
        }
        EmitRun(substrateTop, above, 0);
        entries.Add(new ProcessStackEntry(ProcessStackEntryKind.Dielectric, substrateName,
            ThicknessUm: substrateThickness, RelativePermittivity: substrateEpsr,
            LossTangent: substrate?.Card?.Tand ?? 0, DrawnOn: []));

        foreach (var v in vias)
            entries.Add(new ProcessStackEntry(ProcessStackEntryKind.Via, v.Name,
                SpanFrom: v.Lower!.Name, SpanTo: v.Upper!.Name, ConductivitySm: v.Sigma ?? 0,
                DrawnOn: [v.Source.Key!.Value]));

        var description = new ProcessStackDescription("", entries, []);
        var built = ProcessTechnologyBuilder.Build(description, layerTable, fallbackName);

        // The kit's cards, carried as the technology's materials, and each slab that took its numbers from
        // one names it — so the editor shows where a number came from, and a card the stack did not use (a
        // kit's bulk-substrate card, when it states no substrate level) is one pick away rather than
        // retyped. Added AFTER the builder and changing no number: the loader resolves a named material by
        // overwriting the slab's values with the card's, which are the values already there.
        var cardOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in conductors.Concat(vias))
            if (c.Card is not null) cardOf[c.Name] = c.Source.Material!;
        foreach (var (piece, name) in pieceNames)
            if (piece.Medium is { Card: not null } m) cardOf[name] = m.Source.Material!;
            else if (piece.Medium is null && background is { } bg) cardOf[name] = bg.Name;
        if (substrate is { Card: not null }) cardOf[substrateName] = substrate.Source.Material!;
        AttachMaterials(built.Technology, doc, cardOf, Note);

        foreach (string n in built.Notes) Note(n);

        int conductorCount = built.Technology.Stackup.Layers.Count(l => l.Kind == StackupKind.Conductor);
        int viaCount = built.Technology.Stackup.Layers.Count(l => l.Kind == StackupKind.Via);
        string summary = $"Technology built: {conductorCount} conductors, {viaCount} vias, {filled} filled values (see notes)";
        return new GdsfactoryTechnologyResult(built.Technology, summary, notes, filled, description);
    }

    private static void AttachMaterials(
        Technology tech, GdsfactoryStackDocument doc, Dictionary<string, string> cardOf, Action<string> note)
    {
        if (doc.Materials.Count == 0) return;
        tech.Materials = [.. doc.Materials.Select(kv => new TechMaterial
        {
            Name = kv.Key, Epsr = kv.Value.Epsr, TanD = kv.Value.Tand, Sigma20 = kv.Value.Sigma,
        })];
        foreach (var layer in tech.Stackup.Layers)
            if (cardOf.TryGetValue(layer.Name, out string? card)) layer.Material = card;
        note($"The kit's {doc.Materials.Count} material card(s) are the technology's materials; " +
             $"{cardOf.Count} stack entr{(cardOf.Count == 1 ? "y names" : "ies name")} the card its numbers came from.");
    }

    // ── reading ───────────────────────────────────────────────────────────────

    private static List<Level> ReadLevels(GdsfactoryStackDocument doc, Action<string> note)
    {
        var levels = new List<Level>();
        var noGeometry = new List<string>();
        var expressions = new List<string>();
        var sidewalls = new List<string>();
        var uncarded = new SortedSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < doc.Stack.Count; i++)
        {
            var s = doc.Stack[i];
            if (s.Zmin is not { } z || s.Thickness is not { } t || t <= ToleranceUm)
            {
                noGeometry.Add(s.Thickness is { } bad ? $"{s.Name} ({Um(bad)} µm)" : s.Name);
                continue;
            }
            if (s.Key is null)
            {
                expressions.Add(s.Expression is { } e ? $"{s.Name} = {e}" : s.Name);
                continue;
            }
            if (s.SidewallAngle is { } a && Math.Abs(a) > 1e-9)
                sidewalls.Add($"{s.Name} ({Num(a)}°)");

            GdsfactoryMaterial? card = null;
            if (s.Material is { Length: > 0 } m && !doc.Materials.TryGetValue(m, out card)) uncarded.Add(m);

            levels.Add(new Level
            {
                Source = s, Index = i, Card = card,
                Zmin = Math.Round(z, 6), Thickness = Math.Round(t, 6),
            });
        }

        if (noGeometry.Count > 0)
            note($"Levels with no usable z or thickness were not used: {Listed(noGeometry)}.");
        if (expressions.Count > 0)
            note("Levels the kit states as a layer EXPRESSION rather than one drawn layer were not used — a stack " +
                 $"entry is bound to one drawn layer: {Listed(expressions)}.");
        if (sidewalls.Count > 0)
            note($"Sidewall angles the kit states are not carried; circuitRF's stack has vertical walls: {Listed(sidewalls)}.");
        if (uncarded.Count > 0)
            note($"Materials the kit names with no card stating their properties: {Listed([.. uncarded])}.");
        return levels;
    }

    // ── classifying ───────────────────────────────────────────────────────────

    /// <summary>
    /// R-gdf3-2b: via, conductor, device layer, dielectric. What matches none is not used, and its z is
    /// filled like any other gap.
    ///
    /// <para><b>A device layer is never in the stack.</b> Wells, implants and diffusion overlap each other
    /// and the substrate; a stack is an ordered pile of slabs, so carrying them would push every metal above
    /// them up by their summed thickness. They stay drawing layers, and a via that lands on one — a contact
    /// to the front end — is reported and left out, because its lower end is not a conductor of the
    /// stack. A device layer is a level that reaches below the substrate's top, or is made of what the
    /// substrate is made of: the substrate level's own material, or — when the kit states no substrate —
    /// the material of the levels that reach below the wafer surface, which is what a well is implanted
    /// into. Comparing two of the kit's own strings is not matching a name circuitRF knows.</para>
    ///
    /// <para><b>Vias and conductors are decided together, from the top down.</b> Geometry alone cannot
    /// tell them apart: in an alternating stack every metal also runs from the top of one via to the bottom
    /// of the next. What breaks the symmetry is the TOP of each chain — nothing lies on the topmost level,
    /// so it cannot be a via. Walking down from it: a level a via lands on is a conductor; a level whose
    /// top meets a conductor's bottom, with a conductor-capable level beneath it, is a via. A level with no
    /// conductivity stated is conductor-capable, and becomes a conductor only once a via lands on it — so a
    /// via may END on an unconfirmed level at the top of a chain, and that is what confirms it — unless the
    /// via's own card states its conductivity, when only a confirmed conductor will do. Two levels
    /// drawn on the same mask are never each other's via: a via has a cut mask of its own, and a film
    /// patterned with the plate above it (a capacitor's dielectric) is part of that plate's
    /// structure.</para>
    /// </summary>
    private static void Classify(List<Level> levels, Level? substrate, double substrateTop)
    {
        var others = levels.Where(l => l != substrate).ToList();

        bool BelowSurface(Level l) => l.Zmin < substrateTop - ToleranceUm;
        var deviceMaterials = new HashSet<string>(StringComparer.Ordinal);
        if (substrate?.Source.Material is { Length: > 0 } sm) deviceMaterials.Add(sm);
        else
            foreach (var l in others)
                if (BelowSurface(l) && !l.KnownConductor && l.Source.Material is { Length: > 0 } m) deviceMaterials.Add(m);

        foreach (var l in others)
            l.Role = !l.KnownConductor && (BelowSurface(l) || l.Source.Material is { } m && deviceMaterials.Contains(m))
                ? Role.Device : Role.Unused;

        // What can be a via's end: a stated conductor or a level with no conductivity stated. Never a stated
        // dielectric, never a level whose stated conductivity is below a conductor's.
        bool Capable(Level l) => l.KnownConductor || l.Sigma is null && !l.KnownDielectric;
        bool Touch(Level below, Level above) =>
            below != above && Math.Abs(below.Top - above.Zmin) <= ToleranceUm && below.Source.Key != above.Source.Key;

        var walk = others.Where(l => l.Role != Role.Device && Capable(l))
                         .OrderByDescending(l => l.Top).ThenByDescending(l => l.Zmin).ThenBy(l => l.Index).ToList();
        foreach (var l in walk)
        {
            var uppers = walk.Where(u => Touch(l, u)).ToList();
            var lowers = others.Where(a => (a.Role == Role.Device || Capable(a)) && Touch(a, l)).ToList();

            if (uppers.Any(u => u.Role == Role.Via && u.Lower == l)) { l.Role = Role.Conductor; continue; }

            var confirmed = uppers.Where(u => u.Role == Role.Conductor).ToList();
            // A level whose card states its conductivity is a via only under a CONFIRMED conductor: an
            // unconfirmed level above it is a guess, and the kit's own numbers outrank a guess (a passivation
            // with no card, lying on the top metal, would otherwise turn the whole stack's parity over).
            var possible = l.Sigma is null
                ? uppers.Where(u => u.Role == Role.Conductor || u.Role == Role.Unused && u.Sigma is null).ToList()
                : confirmed;
            bool onlyDevices = lowers.Count > 0 && lowers.All(a => a.Role == Role.Device);

            if (onlyDevices && confirmed.Count > 0)
            {
                l.Role = Role.FrontEndVia;
                l.Upper = BestEnd(confirmed);
                l.Lower = BestEnd(lowers);
            }
            else if (!onlyDevices && lowers.Count > 0 && possible.Count > 0)
            {
                l.Role = Role.Via;
                l.Upper = BestEnd(possible);
                l.Upper!.Role = Role.Conductor;   // a via lands on it
                l.Lower = BestEnd(lowers.Where(a => a.Role != Role.Device));   // confirmed when the walk reaches it
            }
            else if (l.KnownConductor) l.Role = Role.Conductor;
        }

        // A via whose chosen lower end did not become a conductor is not a via.
        foreach (var v in walk.Where(l => l.Role == Role.Via && l.Lower?.Role != Role.Conductor))
        {
            v.Role = v.KnownConductor ? Role.Conductor : Role.Unused;
            v.Lower = v.Upper = null;
        }

        foreach (var l in others)
            if (l.Role == Role.Unused && l.KnownDielectric) l.Role = Role.Dielectric;
    }

    /// <summary>
    /// A via's end, when several levels share the surface: a confirmed conductor, then a stated one, then a
    /// level with no stated conductivity, then a device layer; among equals the THINNEST, because the level
    /// a via lands on is the one whose body is right there — a wafer-wide slab reaching the same surface
    /// from microns away is not what the via joins. Then the kit's own order.
    /// </summary>
    private static Level? BestEnd(IEnumerable<Level> candidates)
        => candidates.OrderBy(l => l.Role == Role.Conductor ? 0 : l.KnownConductor ? 1 : l.Role == Role.Device ? 3 : 2)
                     .ThenBy(l => l.Thickness).ThenBy(l => l.Index).FirstOrDefault();

    private static void StateClassification(List<Level> levels, Action<string> note)
    {
        string Names(Role role, Func<Level, bool>? also = null, Func<Level, string>? show = null)
            => Listed([.. levels.Where(l => l.Role == role && (also?.Invoke(l) ?? true)).Select(show ?? (l => l.Name))]);
        bool Any(Role role, Func<Level, bool>? also = null) => levels.Any(l => l.Role == role && (also?.Invoke(l) ?? true));

        if (levels.FirstOrDefault(l => l.Role == Role.Substrate) is { } sub)
            note($"Substrate: \"{sub.Name}\", the lowest level lying wholly below z = 0 and at least " +
                 $"{Um(SubstrateMinThicknessUm)} µm thick ({Um(sub.Thickness)} µm, top at z = {Um(sub.Top)} µm).");
        if (Any(Role.Via))
            note("Vias — a level running from the top of one conductor to the bottom of another: " +
                 Names(Role.Via, show: l => $"{l.Name} ({l.Lower!.Name} → {l.Upper!.Name})") + ".");
        if (Any(Role.Conductor, l => l.KnownConductor))
            note($"Conductors — a material card states σ ≥ {Num(ConductorSigmaSm)} S/m: {Names(Role.Conductor, l => l.KnownConductor)}.");
        if (Any(Role.Conductor, l => !l.KnownConductor))
            note("Conductors decided by the via-span rule — no material property is stated, and a via lands on " +
                 $"each: {Names(Role.Conductor, l => !l.KnownConductor)}.");
        if (Any(Role.Device))
            note("Device layers — wells, implants and diffusion: levels that reach below the substrate's top or are " +
                 "made of what the substrate is made of, and are neither via nor conductor. They stay drawing " +
                 $"layers and are never in the stack, so never an EM conductor: {Names(Role.Device)}.");
        if (Any(Role.FrontEndVia))
            note("Vias that land on a device layer reach the front end, which the stack does not model, so they " +
                 $"were not made stack vias: {Names(Role.FrontEndVia, show: l => $"{l.Name} ({l.Lower!.Name} → {l.Upper!.Name})")}.");
        if (Any(Role.Dielectric))
            note($"Dielectrics — a card states εr and σ below {Num(ConductorSigmaSm)} S/m: {Names(Role.Dielectric)}.");
        if (Any(Role.Unused))
            note("Not used — no material property is stated and no via lands on them, so no rule says what they " +
                 $"are; the z they occupy is filled like any other gap: {Names(Role.Unused)}.");
    }

    // ── gap filling ───────────────────────────────────────────────────────────

    /// <summary>
    /// The card a fill takes its εr from: the material the kit's own stated dielectric levels hold most of,
    /// by thickness (each level is laterally the whole wafer, so thickness is its volume). Null when no
    /// stated level has a card with εr — the fill is then εr 1, with a warning.
    /// </summary>
    private static (string Name, GdsfactoryMaterial Card)? BackgroundCard(List<Level> dielectrics)
    {
        var best = dielectrics.Where(d => d.Source.Material is { Length: > 0 } && d.Epsr is not null)
                              .GroupBy(d => d.Source.Material!, StringComparer.Ordinal)
                              .Select(g => (Name: g.Key, Card: g.First().Card!, Volume: g.Sum(d => d.Thickness), First: g.Min(d => d.Index)))
                              .OrderByDescending(g => g.Volume).ThenBy(g => g.First)
                              .FirstOrDefault();
        return best.Card is null ? null : (best.Name, best.Card);
    }

    /// <summary>
    /// Every stretch of z between the substrate's top and the highest level that no conductor occupies, cut
    /// at every dielectric level's surfaces and given its medium: the first stated dielectric covering it,
    /// or none (a fill). Adjacent stretches with the same medium are one piece.
    /// </summary>
    private static List<Piece> Pieces(double lo, double hi, List<Level> conductors, List<Level> dielectrics)
    {
        var free = new List<(double Lo, double Hi)> { (lo, hi) };
        foreach (var c in conductors)
        {
            var next = new List<(double, double)>();
            foreach (var (a, b) in free)
            {
                if (c.Top <= a + ToleranceUm || c.Zmin >= b - ToleranceUm) { next.Add((a, b)); continue; }
                if (c.Zmin > a + ToleranceUm) next.Add((a, c.Zmin));
                if (c.Top < b - ToleranceUm) next.Add((c.Top, b));
            }
            free = next;
        }

        var pieces = new List<Piece>();
        foreach (var (a, b) in free.OrderBy(f => f.Lo))
        {
            var cuts = dielectrics.SelectMany(d => new[] { d.Zmin, d.Top })
                                  .Where(z => z > a + ToleranceUm && z < b - ToleranceUm)
                                  .Append(a).Append(b).Distinct().Order().ToList();
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                double mid = (cuts[i] + cuts[i + 1]) / 2;
                var medium = dielectrics.Where(d => d.Zmin <= mid && d.Top >= mid).MinBy(d => d.Index);
                if (pieces.Count > 0 && pieces[^1].Medium == medium && Math.Abs(pieces[^1].Hi - cuts[i]) <= ToleranceUm)
                    pieces[^1] = pieces[^1] with { Hi = cuts[i + 1] };
                else
                    pieces.Add(new Piece(cuts[i], cuts[i + 1], medium));
            }
        }
        return pieces.Where(p => p.Hi - p.Lo > ToleranceUm).ToList();
    }

    // ── text ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A stack entry name not yet used. Generated names keep to the stack-file grammar (letters, digits,
    /// underscore), so the description this writes can be written out as a stack file and read back to the
    /// same thing.
    /// </summary>
    private static string Unique(string name, HashSet<string> used)
    {
        string candidate = name;
        for (int i = 2; !used.Add(candidate); i++) candidate = $"{name}_{i}";
        return candidate;
    }

    private static string Listed(IReadOnlyList<string> names)
        => names.Count <= NamesPerNote
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(NamesPerNote))}, and {names.Count - NamesPerNote} more";

    private static string Um(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Num(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}
