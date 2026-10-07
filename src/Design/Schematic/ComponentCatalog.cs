using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Schematic;

/// <summary>One parameter of one component, as the registry declares it.</summary>
/// <param name="Expression">The default, as an EXPRESSION — the string a freshly-placed component
/// carries, not an evaluated number. Empty where the registry declares no default.</param>
/// <param name="Meaning">What the parameter is FOR, where the registry knows; empty where it does
/// not. Empty is the honest answer and never a placeholder: see <see cref="ComponentCatalog"/>.</param>
public sealed record CatalogParameter(
    string Name,
    string Expression,
    string Unit,
    string Dimension,
    bool   ShowOnSchematic,
    string Meaning);

/// <summary>
/// How many terminals a component has, and — when that is not a fixed number — what decides it.
///
/// <para>Exactly one of <paramref name="Count"/> and <paramref name="DeterminedBy"/> carries the
/// answer, and both may be absent where nothing below the firewall can say (a primitive with no
/// palette entry at all). A count printed where the truth is "it depends" is the failure class this
/// record exists to prevent (R-aut6-9).</para>
/// </summary>
/// <param name="Names">The terminals in the order a <c>.cnl</c> line writes their nets.</param>
/// <param name="ListedAt">The port count <paramref name="Names"/> was listed at, when the count is
/// parameter-determined — so a reader can tell an example from an answer.</param>
/// <param name="OrderNote">What the ORDER means — whether the terminals may be swapped, and what
/// tells them apart when they may not. From <see cref="ComponentTypeRegistry.TerminalNote"/>; empty
/// where nobody has stated it, which is the honest answer and never a placeholder. Most
/// two-terminal parts have no pin names to give, so without this the table could only restate its
/// own row numbers (owner, 2026-09-05).</param>
public sealed record CatalogPorts(
    int?                  Count,
    IReadOnlyList<string> Names,
    string?               DeterminedBy,
    int?                  ListedAt,
    string                OrderNote = "");

/// <summary>
/// How many nets this primitive's <c>.cnl</c> INSTANCE LINE binds — which is a different quantity
/// from <see cref="CatalogPorts"/>, and the one a caller writing a netlist is actually asking about.
///
/// <para><b>Why it is its own field (R-aut10-2).</b> The catalogue used to publish the symbol's pin
/// count under the heading "nets". They differ wherever a terminal is implicit on the glyph — a
/// <c>Tuner</c> draws one pin and its line takes two, a <c>Port</c> draws one and takes two — and
/// they differ for an <c>SDD</c> by construction. A client that wrote a one-net <c>Tuner</c> from
/// the catalogue's own description got a bench whose bias tee delivered nothing, at the engine's
/// floor sentinel, with every diagnostic clean, and reported the component as broken. It is not.
/// See <see cref="InstanceNetContract"/>, which is where this comes from.</para>
/// </summary>
/// <param name="Count">The fixed number of nets, or null when the count is not fixed.</param>
/// <param name="DeterminedBy">The <c>.cnl</c> parameter that sets it. NOT always
/// <see cref="CatalogPorts.DeterminedBy"/>: the panel calls an SDD's <c>NumPorts</c>, an instance
/// line spells it <c>SddPortCount</c>.</param>
/// <param name="Rule">One sentence, where a number is not the answer. Empty otherwise.</param>
public sealed record CatalogNets(int? Count, string? DeterminedBy, string Rule);

/// <summary>One palette entry that places this primitive: what it is called, where it is found, and
/// the parameters a freshly-placed one carries.</summary>
public sealed record CatalogSymbol(
    string                          Kind,
    string                          DisplayName,
    string                          Category,
    IReadOnlyList<string>           SearchTerms,
    CatalogPorts                    Ports,
    IReadOnlyList<CatalogParameter> Parameters)
{
    /// <summary>Where each terminal sits on the drawn symbol, relative to the component's own
    /// <c>X</c>/<c>Y</c> at rotation <c>R0</c> with no mirror — what a wire in a hand-written
    /// <c>.csch</c> has to end on. From <see cref="SymbolPortDefs"/>, the table extraction connects
    /// wires by; empty where the pins come from a referenced cell's own symbol. A variadic kind's
    /// pins are listed at <see cref="CatalogPorts.ListedAt"/>.</summary>
    public IReadOnlyList<CatalogPin> Pins { get; init; } = [];
}

/// <summary>One terminal of a drawn symbol, in schematic units relative to the component's origin
/// (y grows DOWNWARD).</summary>
public sealed record CatalogPin(string Name, double X, double Y);

/// <summary>
/// One primitive, keyed on the token a <c>.cnl</c> writes.
/// </summary>
/// <param name="Simulatable">Whether <see cref="ComponentModelFactory"/> can build a model for it.
/// False means the token places and reloads but does not elaborate.</param>
/// <param name="Placeable">Whether any <see cref="SymbolKind"/> draws it.</param>
/// <param name="Note">Why the two disagree, when they do — the §2.3 mismatch, reported rather than
/// filtered out (R-aut6-10). Empty when they agree.</param>
/// <param name="Nets">What the INSTANCE LINE binds, which is the netlist contract; <paramref name="Ports"/>
/// is what the SYMBOL draws. See <see cref="CatalogNets"/> for why they are two fields.</param>
public sealed record CatalogEntry(
    string                       Type,
    bool                         Simulatable,
    bool                         Placeable,
    string                       Note,
    CatalogNets                  Nets,
    CatalogPorts                 Ports,
    IReadOnlyList<CatalogSymbol> Symbols);

/// <summary>
/// What a caller may WRITE, generated from the live registries at every call.
///
/// <para><b>It transcribes nothing</b> (R-aut6-7). Every fact here is read from
/// <see cref="ComponentModelFactory"/> (the <c>.cnl</c> tokens and whether a model can be built) or
/// <see cref="ComponentTypeRegistry"/> (parameter names, defaults, units, visibility, meanings,
/// category, search terms) or <see cref="SymbolPortDefs"/> (the terminals, in the order a netlist
/// line writes their nets). This is <see cref="CircuitRF.Ui.Diagnostics.DocTables"/>' own rule —
/// "the prose never re-types a fact the code already knows … every hand-written table restating them
/// is a copy waiting to go stale — silently, because a wrong default in a document does not fail
/// anything" — applied to a machine answer instead of a page, and the two are ONE computation so
/// they cannot disagree (R-aut6-11).</para>
///
/// <para><b>What it deliberately does not know.</b> The registry knows a parameter's name, default,
/// unit and visibility — the facts that drift — but not what it is FOR. Where
/// <see cref="ComponentTypeRegistry.ParameterDescription"/> has nothing,
/// <see cref="CatalogParameter.Meaning"/> is empty and stays empty: an invented meaning is worse
/// than none, and the prose reference answers that half (R-aut6-8).</para>
///
/// <para><b>It constructs no model at all.</b> Not a parameterized one, because that would mean
/// inventing its parameters and reading a port count off the result — a number that is plausible,
/// specific and wrong, with nothing reporting it (R-aut6-9). And not a parameterless one either,
/// because <see cref="CircuitRF.Core.ComponentModel.PortCount"/> is a different quantity from the
/// number of nets an instance line writes: see <see cref="TokenPorts"/>. Where the count is set by a
/// parameter, <see cref="CatalogPorts.DeterminedBy"/> names it and
/// <see cref="CatalogPorts.Count"/> is absent.</para>
///
/// <para><b>The two keyings do not line up, and that is part of the answer.</b>
/// <see cref="ComponentModelFactory"/> keys on the <c>.cnl</c> token;
/// <see cref="ComponentTypeRegistry"/> keys on <see cref="SymbolKind"/>; and
/// <see cref="ComponentTypeRegistry.EngineReference"/> bridges them without being total in either
/// direction. A token nothing draws and a symbol nothing simulates are both things a client needs
/// told, so both appear, each with a <see cref="CatalogEntry.Note"/> saying which it is
/// (R-aut6-10).</para>
/// </summary>
public static class ComponentCatalog
{
    /// <summary>
    /// The port count a variadic component's terminals are LISTED at — the registry's own fallback
    /// (<see cref="ComponentTypeRegistry.DefaultParameters"/> and <see cref="SymbolPortDefs.For"/>
    /// both take 2 when given none), so the listing shows what those functions show and invents no
    /// third answer. It travels beside the terminals as <see cref="CatalogPorts.ListedAt"/>, because
    /// an example that does not say it is an example is indistinguishable from an answer.
    /// </summary>
    public const int ListedPortCount = 2;

    /// <summary>
    /// Every primitive, ordered by its <c>.cnl</c> token. Rebuilt per call — the registries are
    /// static tables, the walk is a few hundred switch arms, and a cache is a second thing that can
    /// be stale.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> All()
    {
        // token → the symbols that place it. Ordinal-insensitive because ComponentModelFactory's own
        // lookup is (`wBond` is registered as "wBond" and spelled that way in a .cnl, but the
        // factory answers to any casing), and EngineReference's spelling is the document's.
        var byToken = new Dictionary<string, List<SymbolKind>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in Enum.GetValues<SymbolKind>())
        {
            if (LibraryCatalog.InternalOnlyKinds.Contains(kind)) continue;
            string token = ComponentTypeRegistry.EngineReference(kind);
            if (token.Length == 0) continue;
            if (!byToken.TryGetValue(token, out var kinds)) byToken[token] = kinds = [];
            kinds.Add(kind);
        }

        var tokens = new SortedSet<string>(ComponentModelFactory.PrimitiveTypeNames, StringComparer.Ordinal);
        // The EngineReference targets no factory entry answers to. They are not dropped: a token the
        // palette can draw and the engine cannot build is precisely a thing a client must be told,
        // and three of the five are not components at all — which is itself the answer for them.
        foreach (string token in byToken.Keys)
            if (!ComponentModelFactory.IsPrimitive(token)) tokens.Add(token);

        var entries = new List<CatalogEntry>(tokens.Count);
        foreach (string token in tokens)
        {
            bool simulatable = ComponentModelFactory.IsPrimitive(token);
            var  kinds       = byToken.TryGetValue(token, out var found) ? found : [];

            var symbols = kinds
                .Select(k => new CatalogSymbol(
                    k.ToString(),
                    ComponentTypeRegistry.DisplayName(k),
                    ComponentTypeRegistry.Get(k).Category.ToString(),
                    ComponentTypeRegistry.Get(k).SearchTerms ?? [],
                    PortsOf(k),
                    Parameters(k, PortCountFor(k)))
                {
                    Pins = [.. SymbolPortDefs.For(k, ListedPortCount).Select(p => new CatalogPin(p.Name, p.LocalX, p.LocalY))],
                })
                .OrderBy(s => s.Kind, StringComparer.Ordinal)
                .ToArray();

            entries.Add(new CatalogEntry(
                token, simulatable, symbols.Length > 0, NoteFor(token, simulatable, symbols.Length > 0),
                NetsFor(token, simulatable), TokenPorts(kinds), symbols));
        }
        return entries;
    }

    /// <summary>
    /// What this token's instance line binds, from the one place that knows — <c>CnlReader</c>'s own
    /// contract (R-aut10-2).
    ///
    /// <para><b>A token the engine cannot build binds nothing.</b> <c>GND</c>, <c>VAR</c>,
    /// <c>MEAS</c> and <c>Pin</c> are schematic elements the extractor consumes and never instance
    /// lines at all, so a net count for them would be an answer to a question that cannot be asked.
    /// <see cref="NoteFor"/> says which of those each one is.</para>
    /// </summary>
    private static CatalogNets NetsFor(string token, bool simulatable)
    {
        if (!simulatable) return new CatalogNets(null, null, "");

        var c = InstanceNetContract.ForToken(token);
        return new CatalogNets(c.Count, c.DeterminedBy, c.Rule);
    }

    /// <summary>
    /// One component's parameter rows, with their meanings — the DATA half of
    /// <see cref="CircuitRF.Ui.Diagnostics.DocTables.ComponentParameters"/>, which renders this list
    /// rather than reading the registry a second time (R-aut6-11).
    ///
    /// <para><b>An empty list carries two meanings and this function cannot separate them.</b>
    /// <see cref="ComponentTypeRegistry.DefaultParameters"/> has an explicit arm for 71 of the 75
    /// <see cref="SymbolKind"/>s and <c>default: return []</c> for the rest — and an empty list is
    /// also the CORRECT answer for a component whose rows the user authors, which the function's own
    /// header says of the SDD. Closing that gap is a registry question and a wrong default written
    /// into the registry to make a catalogue look complete is exactly the silent-wrong-value failure
    /// this whole surface exists to prevent (R-aut6-12), so nothing here guesses.</para>
    /// </summary>
    public static IReadOnlyList<CatalogParameter> Parameters(SymbolKind kind, int portCount)
        => [.. ComponentTypeRegistry.DefaultParameters(kind, portCount)
                                    .Where(p => !IsOpaquePayload(kind, p.Name))
                                    .Select(p => new CatalogParameter(
                                        p.Name, p.Expression, p.Unit, p.Dimension.ToString(),
                                        p.ShowOnSchematic,
                                        ComponentTypeRegistry.ParameterDescription(kind, p.Name)))];

    /// <summary>
    /// A parameter nobody may be offered as a row, because it is not a value: it is a
    /// machine-written payload that CARRIES the component.
    ///
    /// <para>Match's and wBond's <c>Design</c> are base64 of the whole design's JSON. The parameter
    /// panel already refuses to show either as a text row (<c>IsMatchPanelParameter</c>,
    /// <c>IsWBondPanelParameter</c>) for the same reason: nobody can read it, act on it, or safely
    /// edit it, and hand-editing it is the one way to produce a component that refuses at
    /// elaboration. A catalogue listing it invites exactly the edit the interface declines to
    /// offer.</para>
    ///
    /// <para>Moved here from <c>DocTables</c> with the rest of the data half, so the page and the
    /// machine answer omit the same row rather than each deciding for itself.</para>
    /// </summary>
    private static bool IsOpaquePayload(SymbolKind kind, string name)
        => kind is SymbolKind.Match or SymbolKind.WBond
        && string.Equals(name, "Design", StringComparison.Ordinal);

    /// <summary>The port count a variadic kind's rows are listed at; 0 — meaning "the type's own" —
    /// for every fixed one, which is what both registry functions already take.</summary>
    private static int PortCountFor(SymbolKind kind)
        => ComponentTypeRegistry.PortCountParameter(kind) is null ? 0 : ListedPortCount;

    /// <summary>
    /// One palette entry's terminals.
    ///
    /// <para><b>Whether the count is fixed is MEASURED, not declared.</b> A kind whose pin geometry
    /// is generated from N answers differently at 2 and at 3, and that is the test — so a variadic
    /// kind cannot be reported as fixed by anyone forgetting to say it is variadic. A kind whose
    /// engine component is variadic while its own tile is not (the SPST and SPDT switches: two tiles
    /// over one component, each seeding its own <c>Throws</c>) is fixed HERE and variadic at the
    /// token, which is exactly what is true of it.</para>
    ///
    /// <para>An empty pin list from a kind that HAS a port-count parameter is the third case:
    /// <see cref="SymbolPortDefs"/> returns nothing for a wBond on purpose — inventing two terminals
    /// there would let an unresolved one extract as a two-terminal device, a different circuit that
    /// still simulates — so a zero is reported as "not fixed", never as a count of zero.</para>
    /// </summary>
    public static CatalogPorts PortsOf(SymbolKind kind)
    {
        string? by     = ComponentTypeRegistry.PortCountParameter(kind);
        var     listed = SymbolPortDefs.For(kind, ListedPortCount);
        var     names  = listed.Select(p => p.Name).ToArray();
        // What the order MEANS, which is the half a pin name cannot carry. It travels even where
        // the terminals are unnamed — for R, L and C it is the ONLY thing the table has to say.
        string  note   = ComponentTypeRegistry.TerminalNote(kind);

        if (listed.Length != SymbolPortDefs.For(kind, ListedPortCount + 1).Length)
            // Named, not counted. The terminals still travel — a caller writing a 2-port one needs
            // their order — labelled with the count they were listed at.
            return new CatalogPorts(null, names, by, ListedPortCount, note);

        if (names.Length == 0 && by is not null) return new CatalogPorts(null, [], by, null, note);

        return new CatalogPorts(names.Length, names, null, null, note);
    }

    /// <summary>
    /// The token's own answer, which is not always its symbols'. Two tiles over one engine component
    /// (Switch/SwitchD, Coupler and the two hybrids, Term/TermG) can draw different pin sets, so a
    /// count is reported only where every symbol agrees on one.
    /// </summary>
    /// <summary>
    /// The token's own answer, which is not always its symbols'. Two tiles over one engine component
    /// (Switch/SwitchD, Coupler and the two hybrids, Term/TermG) can draw different pin sets, so a
    /// count is reported only where every symbol agrees on one.
    ///
    /// <para><b>Why the MODEL is not asked, even where one could be built without inventing
    /// values.</b> <see cref="CircuitRF.Core.ComponentModel.PortCount"/> is the model's own port
    /// count in the MNA sense and is NOT the number of nets an instance line writes: a current probe
    /// reports 1 and takes two nets, a FET reports 2 and takes three, a 2-port SDD reports 2 and
    /// takes four. Publishing it as "how many nodes this takes" would be a number that is plausible,
    /// specific and wrong for a third of the catalogue — R-aut6-9's own failure class, arrived at
    /// from the other direction. <see cref="SymbolPortDefs"/> is the contract
    /// <c>NetExtractor</c> emits nets in, so it is the one that answers the question a client is
    /// actually asking.
    /// </para>
    /// </summary>
    private static CatalogPorts TokenPorts(IReadOnlyList<SymbolKind> kinds)
    {
        if (kinds.Count == 0)
            // No symbol at all, so nothing below the firewall states this type's net count. Saying
            // nothing is the answer; NoteFor says why, and src/Design/RESOLVED.md records it as the
            // gap in the capability layer that it is.
            return new CatalogPorts(null, [], null, null);

        var     ports = kinds.Select(PortsOf).ToArray();
        string? by    = kinds.Select(ComponentTypeRegistry.PortCountParameter).FirstOrDefault(p => p is not null);

        bool agreed = ports.All(p => Same(p, ports[0]));

        // The order note is a fact about the TYPE, so it survives tiles that disagree about pins —
        // but only if they agree about IT. Two tiles telling a caller different things about their
        // shared token is not something to pick a winner from, and each symbol below carries its own.
        string note = ports.All(p => string.Equals(p.OrderNote, ports[0].OrderNote, StringComparison.Ordinal))
            ? ports[0].OrderNote
            : "";

        if (by is not null)
            // One symbol, or several agreeing: keep its example. Several that disagree — the two
            // switch tiles — give up the example rather than averaging two pin sets into one that is
            // true of neither; each symbol below still carries its own.
            return agreed
                ? ports[0] with { Count = null, DeterminedBy = by, OrderNote = note }
                : new CatalogPorts(null, [], by, null, note);

        return agreed && ports[0].Count is { } n
            ? new CatalogPorts(n, ports[0].Names, null, null, note)
            : new CatalogPorts(null, [], null, null, note);
    }

    /// <summary>Whether two symbols' PIN facts agree. Deliberately not the record's own equality:
    /// <see cref="CatalogPorts.OrderNote"/> is prose about the type and is reconciled separately in
    /// <see cref="TokenPorts"/>, so a differing note must not suppress a pin count the tiles do
    /// agree on.</summary>
    private static bool Same(CatalogPorts a, CatalogPorts b)
        => a.Count == b.Count && a.DeterminedBy == b.DeterminedBy && a.ListedAt == b.ListedAt
        && a.Names.SequenceEqual(b.Names, StringComparer.Ordinal);

    private static string NoteFor(string token, bool simulatable, bool placeable)
    {
        if (simulatable && placeable) return "";
        if (simulatable)
            // What this note used to end with — "nothing below the UI firewall states how many nets
            // its instance line takes" — was the defect, not a disclaimer of it (R-aut10-2). The net
            // count is stated, on its own line, for this token like every other; what a symbol-less
            // type is genuinely missing is the palette's parameter defaults and its pin NAMES.
            return "No palette entry: this type can be written in a .cnl and has no symbol, so " +
                   "nothing places it and no default parameters are declared for it. Its net count " +
                   "is the 'nets' line above; the terminals have no names because nothing draws them.";
        // The sentinels. They are named rather than described as broken, because "not a component"
        // is the answer for them and it is a complete one.
        if (token is "GND" or "VAR" or "MEAS" or "Pin")
            return "Not a component: a schematic element the extractor consumes, never an instance " +
                   "in a .cnl. Nothing elaborates it.";

        // VProbe is the one sentinel that DOES reach a .cnl and the elaborator. It is still not a
        // component: the elaborator reads the net its single terminal names, publishes that net's
        // voltage under the instance's own name, and builds no model -- which is exactly what makes
        // placing one unable to change the circuit.
        if (token is "VProbe")
            return "Not a component: an instance line the elaborator turns into a NET-NAME ALIAS. " +
                   "The net its one terminal touches is reported under this instance's name as well " +
                   "as its own, and nothing is stamped.";

        return "Not simulatable under this name: the symbol resolves to something else before the " +
               "engine sees it, so no primitive of this name exists.";
    }
}
