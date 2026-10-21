// A composed FET as Verilog-A source (brief-modelcomposer-26; overview D17, D18; docs/design/model-composer.md §25).
//
// Write turns the ComposedModel CmodelFile.Read returns — or the one CmodelExport.Assemble builds in memory — into one
// self-contained module: the extrinsic network, the core's constituents as functions returning value and gradient, guided
// extrapolation, drain-source exchange, drift charges, periphery scaling and the device temperature. Equations and ANNs
// are written as code and compute the .cmodel's numbers to rounding; a table is refused by name (D17). Every refusal is
// collected and reported in one message. Export writes the file by a temporary file and a rename.

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine;
using static CircuitRF.Design.Modeling.VerilogA.VaText;

namespace CircuitRF.Design.Modeling.VerilogA;

/// <summary>How a model is written as Verilog-A.</summary>
public sealed record VerilogAOptions
{
    /// <summary>The module's name in place of the one the <c>.cmodel</c>'s file name gives it; refused unless it is a legal
    /// identifier.</summary>
    public string? ModuleName { get; init; }

    /// <summary>The <c>.cmodel</c>'s file name, which names the module and the header. <see cref="CmodelVerilogA.Export"/>
    /// sets it; null names the module <c>composed_fet</c>.</summary>
    public string? SourceName { get; init; }

    public static VerilogAOptions Default { get; } = new();
}

/// <summary>A model that cannot be written as Verilog-A. Every reason is collected: <see cref="Refusals"/> holds each
/// field and its sentence, and the message is one line per refusal, each beginning with its field.</summary>
public sealed class VerilogAExportException(IReadOnlyList<(string Field, string Sentence)> refusals)
    : Exception(string.Join("\n", refusals.Select(r => $"{r.Field}: {r.Sentence}")))
{
    public IReadOnlyList<(string Field, string Sentence)> Refusals { get; } = refusals;

    /// <summary>The first refusal's field.</summary>
    public string Field => Refusals[0].Field;
}

/// <summary>A written module: where it went and what it is called.</summary>
public sealed record VerilogAExportResult(string Path, string ModuleName, long Bytes);

public static class CmodelVerilogA
{
    /// <summary>The value <c>Temp</c> takes when it is not set: −273.15 °C, which circuitRF refuses as a device temperature,
    /// so it can mean nothing else.</summary>
    public const double TempUnset = -273.15;

    /// <summary>The constituents in the order the module evaluates and reports them.</summary>
    private static readonly string[] ConstituentNames = ["Id", "Ig", "Qg", "Qd", "IdRF"];

    // ── Export ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads <paramref name="cmodelPath"/>, writes its module to <paramref name="vaPath"/> by a temporary file and a rename.
    /// Refused, failed or cancelled, nothing is written. The header's SHA-256 is of <c>model.json</c> as the file holds it.
    /// </summary>
    public static VerilogAExportResult Export(string cmodelPath, string vaPath, VerilogAOptions? options = null, RunControl? rc = null)
    {
        rc?.BeginStage("Verilog-A", 1);
        var model = CmodelFile.Read(cmodelPath);
        string sha;
        using (var zip = ZipFile.OpenRead(cmodelPath))
        using (var s = zip.GetEntry("model.json")!.Open())
            sha = Convert.ToHexStringLower(SHA256.HashData(s));
        options = (options ?? VerilogAOptions.Default) with { SourceName = Path.GetFileName(cmodelPath) };
        string text = Write(model, options, sha);
        rc?.ThrowIfCancellationRequested();

        vaPath = Path.GetFullPath(vaPath);
        Directory.CreateDirectory(Path.GetDirectoryName(vaPath)!);
        string tmp = vaPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        var bytes = new UTF8Encoding(false).GetBytes(text);
        try
        {
            File.WriteAllBytes(tmp, bytes);
            rc?.ThrowIfCancellationRequested();
            File.Move(tmp, vaPath, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        rc?.TickStage();
        return new VerilogAExportResult(vaPath, ModuleName(options), bytes.Length);
    }

    /// <summary>The module as text — the same model always the same bytes.</summary>
    public static string Write(ComposedModel model, VerilogAOptions options)
        => Write(model, options, Convert.ToHexStringLower(SHA256.HashData(CmodelFile.ModelJson(model))));

    private static string ModuleName(VerilogAOptions o)
        => o.ModuleName ?? ModuleNameFrom(Path.GetFileNameWithoutExtension(o.SourceName ?? "composed_fet"));

    private static string Write(ComposedModel model, VerilogAOptions options, string modelSha)
    {
        CmodelFile.Validate(model);
        var refusals = new List<(string Field, string Sentence)>();

        string module = ModuleName(options);
        if (options.ModuleName is { } given && !IsIdentifier(given))
            refusals.Add(("ModuleName", $"'{given}' is not a legal Verilog-A module name; " +
                          (IsReserved(given) ? "it is a Verilog-A keyword or built-in name." : "use letters, digits and underscores, beginning with a letter.")));

        var k = model.Core;
        foreach (var (name, c) in Constituents(k))
            if (c.Method == Constituent.TableMethod)
                refusals.Add(($"Core.Constituents.{name}.Method",
                    $"'Table' cannot be written as Verilog-A; refit {name} as an ANN or an equation to export it."));
        if (k.IdRF is not null || k.Dispersion is not null)
            refusals.Add(("Core.Dispersion", "DC/RF dispersion cannot be written as Verilog-A by this version of circuitRF."));
        if (model.Thermal is not null)
            refusals.Add(("Thermal", "a self-heating model cannot be written as Verilog-A by this version of circuitRF."));
        if (model.Traps is { } traps)
            refusals.Add(("Traps.Model", $"the trap model '{traps.Model}' has no Verilog-A writer, so a model with traps cannot be exported."));

        var network = VaNetwork.Build(model.Network, refusals);
        var names = new Names(refusals);
        var w = new Writer(model, module, modelSha, options.SourceName, network, names, refusals);
        w.Plan();
        if (refusals.Count > 0) throw new VerilogAExportException(refusals);
        return w.Text();
    }

    private static IEnumerable<(string Name, Constituent C)> Constituents(CoreConstituents k)
    {
        yield return ("Id", k.Id);
        yield return ("Ig", k.Ig);
        yield return ("Qg", k.Qg);
        yield return ("Qd", k.Qd);
        if (k.IdRF is { } rf) yield return ("IdRF", rf);
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every identifier the module declares at its own scope, and what it is — so a network or parameter name that is a
    /// keyword, that is already taken, or (for a parameter) that differs from another only in case is refused, naming both.
    /// Many simulators read netlist parameter names without regard to case; renaming silently would change names the user
    /// chose and will look for in the other simulator.
    /// </summary>
    private sealed class Names(List<(string Field, string Sentence)> refusals)
    {
        private readonly Dictionary<string, string> _taken = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _parameters = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A name the module itself declares.</summary>
        public void Own(string name, string what) => _taken[name] = what;

        /// <summary>A name the model chose; true when it is usable.</summary>
        public bool Claim(string field, string name, string what, bool parameter)
        {
            if (!IdentifierShape(name))
            {
                refusals.Add((field, $"{what} '{name}' is not a legal Verilog-A identifier."));
                return false;
            }
            if (IsReserved(name))
            {
                refusals.Add((field, $"{what} '{name}' is a Verilog-A keyword or built-in name; rename it to export."));
                return false;
            }
            if (_taken.TryGetValue(name, out var other))
            {
                refusals.Add((field, $"{what} '{name}' has the name of {other}; rename it to export."));
                return false;
            }
            if (parameter && _parameters.TryGetValue(name, out var same))
            {
                refusals.Add((field, $"the parameters '{same}' and '{name}' differ only in case, which many simulators read as one name; rename one to export."));
                return false;
            }
            _taken[name] = $"{what} '{name}'";
            if (parameter) _parameters[name] = name;
            return true;
        }

        public void OwnParameter(string name, string what)
        {
            Own(name, what);
            _parameters[name] = name;
        }

        private static bool IdentifierShape(string name)
            => name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_')
               && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    // ── The module ────────────────────────────────────────────────────────────

    private sealed class Writer(ComposedModel model, string module, string modelSha, string? source, VaNetwork? network,
                                Names names, List<(string Field, string Sentence)> refusals)
    {
        private const string Vgs = ComposedCoreProgram.Vgs, Vds = ComposedCoreProgram.Vds, T = ComposedCoreProgram.T;

        private readonly int _n0 = model.BasePeriphery.Fingers;
        private readonly bool _range = model.Temperature.Mode == TemperatureMode.Range;
        private readonly bool _exchange = model.Core.Symmetry == DrainSourceSymmetry.Exchange;
        private readonly bool _drift = model.Core.Qg.Method == Constituent.DriftMethod;

        /// <summary>One line of the header's parameter table: name, unit, default, meaning.</summary>
        private readonly List<(string Name, string Unit, string Default, string Meaning)> _doc = [];
        private readonly List<string> _parameters = [];
        private readonly List<string> _functions = [];

        /// <summary>The module parameter each equation constituent's parameter is, by constituent and name.</summary>
        private readonly Dictionary<(string, string), string> _equationParameter = [];

        /// <summary>What a warning or a refusal calls the model: its file name, safe inside a Verilog-A string.</summary>
        private string Who => new([.. Ascii(source ?? module).Select(c => c is '"' or '\\' or '%' ? '_' : c)]);

        public void Plan()
        {
            foreach (string own in VaNetwork.PinNodes) names.Own(own, $"the module's node '{own}'");
            names.Own(VaNetwork.Ground, "the module's ground node");
            foreach (string v in (string[])["kN", "Tdev", "vgs", "vds", "ugs", "uds", "id", "igs", "igd", "qg", "qd", "swap",
                                            "qgs", "qgd", "qds", "qdr", "g_vgs", "g_vds", "g_T", T])
                names.Own(v, $"the module's own variable '{v}'");
            foreach (string payload in (string[])["Id", "IgGs", "IgGd", "Qg", "Qd", "IdRF"])
                foreach (string prefix in (string[])["f_", "x_"]) names.Own(prefix + payload, $"the module's function '{prefix}{payload}'");
            foreach (string piece in DriftCharges.QgKeys.Concat(DriftCharges.QdKeys).Distinct())
                foreach (string prefix in (string[])["f_", "b_"]) names.Own(prefix + piece, $"the module's function '{prefix}{piece}'");
            names.OwnParameter("N", "the finger count N");
            names.OwnParameter("Temp", "the device temperature Temp");

            Parameter("N", "integer", Num0(_n0), "from [1:inf)", "-",
                      $"finger count, a whole number; the model was measured at N0 = {_n0} ({Ascii(model.BasePeriphery.UnitWidth)} each)");
            Parameter("Temp", "real", Num(TempUnset), $"from [{Num(TempUnset)}:inf)", "degC",
                      "the device's temperature; unset follows the simulator's" +
                      (_range ? $", clamped to the model's {Num(model.Temperature.Tmin)} to {Num(model.Temperature.Tmax)} degC"
                              : $"; the model is valid at {Num(model.Temperature.T0)} degC"),
                      "unset");

            if (network is not null)
            {
                foreach (string net in network.Nets)
                    names.Claim("Network.Netlist", net, "the network's net", parameter: false);
                foreach (var e in network.Elements.Where(e => e.Type == "L"))
                    names.Claim("Network.Netlist", e.BranchName, $"the branch of inductor '{e.Name}'", parameter: false);
                foreach (var v in network.Variables)
                {
                    if (!names.Claim("Network.Netlist", v.Name, "the network variable", parameter: true)) continue;
                    if (v.Scaled != v.Name) names.Claim("Network.Netlist", v.Scaled, $"the scaled copy of '{v.Name}'", parameter: false);
                    Parameter(v.Name, "real", Num(v.Value), v.Positive ? "from (0:inf)" : null, v.Unit ?? "-",
                              "network variable" + v.Rule switch
                              {
                                  PeripheryScaling.PerFinger => ", scaled x N/N0 (perFinger)",
                                  PeripheryScaling.PerFingerInverse => ", scaled x N0/N (perFingerInverse)",
                                  _ => "",
                              });
                }
            }

            foreach (var (name, c) in Constituents(model.Core))
            {
                if (c.Method != Constituent.EquationMethod) continue;
                foreach (var p in c.Parameters)
                {
                    string mp = $"{name}_{p.Key}";
                    if (!names.Claim($"Core.Constituents.{name}.Parameters.{p.Key}", mp, $"the parameter of {name}", parameter: true)) continue;
                    _equationParameter[(name, p.Key)] = mp;
                    Parameter(mp, "real", Num(p.Value), null, "-", $"Core.Constituents.{name}.Parameters.{p.Key}");
                }
            }

            if (model.Extrapolation is { } x)
                foreach (string key in ModelExtrapolation.Keys)
                    if (x.Gx.TryGetValue(key, out double gx))
                    {
                        string mp = $"Gx_{key}";
                        if (!names.Claim($"Extrapolation.Gx.{key}", mp, "the extrapolation conductance", parameter: true)) continue;
                        Parameter(mp, "real", Num(gx), "from (0:inf)", "S",
                                  $"guided extrapolation's added conductance on {key}'s own branch, at the base periphery");
                    }

            if (refusals.Count == 0) Functions();
        }

        private void Parameter(string name, string type, string value, string? range, string unit, string meaning,
                               string? shownDefault = null)
        {
            _parameters.Add($"  parameter {type} {name} = {value}{(range is null ? "" : " " + range)};");
            _doc.Add((name, unit, shownDefault ?? value, meaning));
        }

        private static string Num0(int n) => n.ToString(CultureInfo.InvariantCulture);

        // ── constituents ──────────────────────────────────────────────────────

        /// <summary>What the analog block calls for each payload: <c>id = x_Id(ugs, uds, T)</c>.</summary>
        private readonly List<(string Var, string Call, string Constituent)> _calls = [];

        private void Functions()
        {
            var k = model.Core;
            Payload("Id", k.Id, k.Id.PayloadKey, "id", own: Branch(k.Id.Inputs, 0, 1));
            Payload("Ig", k.Ig, "IgGs", "igs", own: Branch(k.Ig.Inputs, 1, 0));
            Payload("Ig", k.Ig, "IgGd", "igd", own: Branch(k.Ig.Inputs, 1, -1));
            if (_drift) DriftFunctions();
            else
            {
                Payload("Qg", k.Qg, k.Qg.PayloadKey, "qg", own: null);
                Payload("Qd", k.Qd, k.Qd.PayloadKey, "qd", own: null);
            }
        }

        private static double[] Branch(IReadOnlyList<string> inputs, double vgs, double vds)
            => [.. inputs.Select(i => i == Vgs ? vgs : i == Vds ? vds : 0.0)];

        private string Arg(string input) => input switch { Vgs => "ugs", Vds => "uds", _ => T };

        private void Payload(string name, Constituent c, string key, string variable, double[]? own)
        {
            string payload = name == "Ig" ? key : name;
            string field = $"Core.Constituents.{name}" + (name == "Ig" ? $".{key}" : $".{c.PayloadKey}");
            var inputs = c.Inputs;
            string args = string.Join(", ", inputs.Select(Arg));

            if (c.Method == Constituent.EquationMethod)
            {
                string expression = c.Expression(key)!;
                var eq = new VaEquation(inputs, n => inputs.Contains(n) ? n : _equationParameter.GetValueOrDefault((name, n), n));
                (string Value, string?[] Grad) r;
                try { r = eq.Emit(Parser.Parse(expression)); }
                catch (VaUntranslatableException ex)
                {
                    refusals.Add((field, $"{ex.Message} cannot be written as Verilog-A."));
                    return;
                }
                string fn = $"f_{payload}";
                _functions.Add(EquationFunction(fn, $"{field} (Equation): {Ascii(expression)}", inputs, eq, r));
                _calls.Add((variable, $"{fn}({args}, {string.Join(", ", inputs.Select(i => "g_" + i))})", payload));
                return;
            }

            // An ANN, continued by guided extrapolation.
            var net = VaAnnData.Read(model, c.Expression(key)!, inputs.Count, c.Activation ?? AnnActivation.Tanh);
            string hidden = string.Join(" x ", Enumerable.Range(1, net.Layers - 1).Select(net.Width));
            _functions.Add(VaAnn.Function($"f_{payload}",
                $"{field} (ANN, {net.Layers - 1} hidden layer{(net.Layers == 2 ? "" : "s")} of {hidden}, {net.Activation})" +
                (c.Transform is { } t ? $", the current {Num(t.Scale)}*sinh(u) (Transform: Asinh)" : ""),
                inputs, net, c.Transform));
            string gx = $"Gx_{payload}";
            _functions.Add(VaDomain.Function($"x_{payload}",
                $"{field} continued outside Core.Constituents.{name}.Domain (guided extrapolation)",
                $"f_{payload}", inputs, c.Domain!, own is null ? null : gx, own));
            _calls.Add((variable, $"x_{payload}({args})", payload));
        }

        private static string EquationFunction(string fn, string comment, IReadOnlyList<string> inputs, VaEquation eq,
                                               (string Value, string?[] Grad) r)
        {
            var sb = new StringBuilder();
            sb.Append($"  // {comment}\n");
            sb.Append($"  analog function real {fn};\n");
            sb.Append($"    input {string.Join(", ", inputs)};\n");
            sb.Append($"    output {string.Join(", ", inputs.Select(x => "d_" + x))};\n");
            sb.Append($"    real {string.Join(", ", inputs)};\n");
            sb.Append($"    real {string.Join(", ", inputs.Select(x => "d_" + x))};\n");
            foreach (var chunk in eq.Locals.Chunk(12)) sb.Append($"    real {string.Join(", ", chunk)};\n");
            sb.Append("    begin\n");
            foreach (string line in eq.Code) sb.Append($"      {line}\n");
            sb.Append($"      {fn} = {r.Value};\n");
            for (int i = 0; i < inputs.Count; i++) sb.Append($"      d_{inputs[i]} = {r.Grad[i] ?? "0.0"};\n");
            sb.Append("    end\n");
            sb.Append("  endfunction\n");
            return sb.ToString();
        }

        /// <summary>The drift decomposition (<see cref="DriftCharges"/>): each piece an ANN of its own variables, continued
        /// to first order beyond its box.</summary>
        private void DriftFunctions()
        {
            var qg = model.Core.Qg;
            var qd = model.Core.Qd;
            var box = qd.Domain!.Box.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            void Piece(Constituent c, string owner, string key, string[] inputs, string[] intervals)
            {
                var net = VaAnnData.Read(model, c.Expression(key)!, inputs.Length, c.Activation ?? AnnActivation.Tanh);
                string field = $"Core.Constituents.{owner}.{key}";
                _functions.Add(VaAnn.Function($"f_{key}", $"{field} (ANN of {string.Join(", ", inputs)}, drift decomposition)",
                                              inputs, net, null));
                _functions.Add(VaDomain.BoxFunction($"b_{key}", $"{field} continued to first order outside Core.Constituents.Qd.Domain.Box",
                                                    $"f_{key}", inputs, [.. intervals.Select(i => box[i])]));
            }
            Piece(qg, "Qg", DriftCharges.Qgs, ["vgs"], [DriftCharges.Qgs]);
            Piece(qg, "Qg", DriftCharges.Qgd, ["vgd"], [DriftCharges.Qgd]);
            Piece(qd, "Qd", DriftCharges.Qds, ["vds"], [DriftCharges.Qds]);
            Piece(qg, "Qg", DriftCharges.Qdrift, ["vgd", "id"], [DriftCharges.DriftVgd, DriftCharges.DriftId]);
        }

        // ── text ──────────────────────────────────────────────────────────────

        public string Text()
        {
            var sb = new StringBuilder();
            Header(sb);
            sb.Append("`include \"disciplines.vams\"\n");
            sb.Append("`include \"constants.vams\"\n\n");
            sb.Append($"module {module}(g, d, s);\n");
            sb.Append("  inout g, d, s;\n");
            sb.Append("  electrical g, d, s;\n");
            sb.Append("  // Network.Netlist pins 4-6: the intrinsic gate, drain and source\n");
            sb.Append("  electrical gi, di, si;\n");
            if (network!.Nets.Count > 0)
            {
                sb.Append("  // Network.Netlist internal nets\n");
                sb.Append($"  electrical {string.Join(", ", network.Nets)};\n");
            }
            if (network.UsesGround) sb.Append($"  electrical {VaNetwork.Ground};\n  ground {VaNetwork.Ground};\n");
            var inductors = network.Elements.Where(e => e.Type == "L").ToList();
            if (inductors.Count > 0)
            {
                sb.Append("  // Network.Netlist inductors: each a branch whose current is an unknown\n");
                foreach (var e in inductors) sb.Append($"  branch ({e.A}, {e.B}) {e.BranchName};\n");
            }

            sb.Append("\n  // Parameters (the header lists them with their units)\n");
            foreach (string p in _parameters) sb.Append(p).Append('\n');

            sb.Append("\n  // The core's constituents: each payload a function returning its value and writing its gradient into\n");
            sb.Append("  // the d_ outputs; a table or ANN is evaluated at the nearest point of its training domain and continued.\n\n");
            foreach (string f in _functions) sb.Append(f).Append('\n');

            var scaled = network.Variables.Where(v => v.Scaled != v.Name).Select(v => v.Scaled).ToList();
            sb.Append("  real kN, Tdev, vgs, vds, ugs, uds, id, igs, igd, qg, qd, swap;\n");
            if (_range) sb.Append($"  real {T};\n");
            if (_drift) sb.Append("  real qgs, qgd, qds, qdr;\n");
            sb.Append("  // the equations' gradients, which the core does not read\n");
            sb.Append($"  real g_vgs, g_vds{(_range ? ", g_T" : "")};\n");
            foreach (var chunk in scaled.Chunk(12)) sb.Append($"  real {string.Join(", ", chunk)};\n");

            sb.Append("\n  analog begin\n");
            Periphery(sb);
            Temperature(sb);
            sb.Append("\n    // Network.Netlist\n");
            foreach (string c in network.Contributions()) sb.Append($"    {c}\n");
            Core(sb);
            sb.Append("  end\n");
            sb.Append("endmodule\n");
            return sb.ToString();
        }

        private void Header(StringBuilder sb)
        {
            var p = model.Provenance;
            sb.Append($"// {module}: a composed FET written as Verilog-A by circuitRF {Ascii(CmodelExport.Version)}");
            sb.Append(source is null ? ".\n" : $", from {Ascii(source)}.\n");
            sb.Append($"// Model created {Ascii(p.Created)} by circuitRF {Ascii(p.CircuitRfVersion)}; model.json SHA-256 {modelSha}.\n");
            sb.Append("//\n");
            sb.Append("// Terminals: g (gate), d (drain), s (source). Every current, charge and network value scales from the\n");
            sb.Append($"// measured device's N0 = {_n0} fingers to N; the host's own device multiplier (m) multiplies the whole\n");
            sb.Append("// device, so the module declares none.\n");
            sb.Append("//\n");
            sb.Append("// Parameters: name, unit, default, meaning.\n");
            int w0 = Math.Max(4, _doc.Max(d => d.Name.Length)), w1 = Math.Max(4, _doc.Max(d => d.Unit.Length)),
                w2 = Math.Max(7, _doc.Max(d => d.Default.Length));
            foreach (var (name, unit, value, meaning) in _doc)
                sb.Append($"//   {name.PadRight(w0)}  {unit.PadRight(w1)}  {value.PadRight(w2)}  {Ascii(meaning)}\n");
            sb.Append("//\n");
            sb.Append($"// Temperature: {(_range ? $"the constituents read T, the device's temperature held to {Num(model.Temperature.Tmin)} to {Num(model.Temperature.Tmax)} degC" : $"a one-temperature model, valid at {Num(model.Temperature.T0)} degC")};\n");
            sb.Append("// a device outside that says so once, as a warning at the first step.\n");
            if (_exchange)
                sb.Append("// Core.Symmetry: Exchange. A point with vds < 0 is evaluated at its image (vgs - vds, -vds), drain and\n" +
                          "// source exchanged.\n");
            sb.Append("// A constituent that evaluates to a number that is not finite stops the run ($fatal), naming it and the point.\n");
            sb.Append("\n");
        }

        private void Periphery(StringBuilder sb)
        {
            sb.Append($"    // Periphery: N fingers of a device measured at N0 = {_n0}\n");
            sb.Append($"    kN = N/{Num0(_n0)}.0;\n");
            foreach (string s in network!.ScalingStatements(_n0)) sb.Append($"    {s}\n");
        }

        private void Temperature(StringBuilder sb)
        {
            var t = model.Temperature;
            sb.Append("\n    // Temperature: the device's own, or the simulator's\n");
            sb.Append($"    Tdev = (Temp > {Num(TempUnset)}) ? Temp : $temperature - `P_CELSIUS0;\n");
            if (_range)
            {
                sb.Append($"    {T} = min(max(Tdev, {Num(t.Tmin)}), {Num(t.Tmax)});\n");
                sb.Append("    @(initial_step) begin\n");
                sb.Append($"      if (Tdev < {Num(t.Tmin)} - 1e-9 || Tdev > {Num(t.Tmax)} + 1e-9)\n");
                sb.Append($"        $warning(\"{Ascii(Who)} covers {Num(t.Tmin)} to {Num(t.Tmax)} degC; the device is at %g degC, so the model is evaluated at %g degC.\", Tdev, {T});\n");
                sb.Append("    end\n");
            }
            else
            {
                sb.Append("    @(initial_step) begin\n");
                sb.Append($"      if (abs(Tdev - {Num(t.T0)}) > 1e-9)\n");
                sb.Append($"        $warning(\"{Ascii(Who)} is a one-temperature model, valid at {Num(t.T0)} degC; the device is at %g degC and the model is used as it is.\", Tdev);\n");
                sb.Append("    end\n");
            }
        }

        private void Core(StringBuilder sb)
        {
            sb.Append("\n    // The core, on (gi, si) and (di, si), times N/N0\n");
            sb.Append("    vgs = V(gi, si);\n");
            sb.Append("    vds = V(di, si);\n");
            sb.Append("    ugs = vgs;\n");
            sb.Append("    uds = vds;\n");
            if (_exchange)
            {
                sb.Append("    // Core.Symmetry: Exchange - evaluated at the image of a point with vds < 0\n");
                sb.Append("    if (vds < 0.0) begin\n");
                sb.Append("      ugs = vgs - vds;\n");
                sb.Append("      uds = -vds;\n");
                sb.Append("    end\n");
            }
            foreach (var (v, call, _) in _calls) sb.Append($"    {v} = {call};\n");
            if (_drift)
            {
                double k = 1 - model.Core.Qd.Parameters.FirstOrDefault(p => p.Key == DriftCharges.Lambda).Value;
                sb.Append("    // Core.Constituents.Qg and Qd: the drift decomposition, reading the channel current\n");
                sb.Append("    qgs = b_QGS(ugs);\n");
                sb.Append("    qgd = b_QGD(ugs - uds);\n");
                sb.Append("    qds = b_QDS(uds);\n");
                sb.Append("    qdr = b_Qdrift(ugs - uds, id);\n");
                sb.Append("    qg = qgs + qgd + qdr;\n");
                sb.Append($"    qd = -qgd - {Operand(k)}*qdr + qds;\n");
            }

            string at = $"vgs=%g, vds=%g{(_range ? ", T=%g" : "")}";
            string atArgs = $"ugs, uds{(_range ? ", T" : "")}";
            foreach (var (v, name) in (ValueTuple<string, string>[])[("id", "Id"), ("igs", "IgGs"), ("igd", "IgGd"), ("qg", "Qg"), ("qd", "Qd")])
                sb.Append($"    if (!({v} == {v}) || abs({v}) > 1.0e308)\n" +
                          $"      $fatal(1, \"{Ascii(Who)}: constituent {name} evaluated to %g at {at}.\", {v}, {atArgs});\n");

            if (_exchange)
            {
                sb.Append("    if (vds < 0.0) begin\n");
                sb.Append("      id = -id;\n");
                sb.Append("      swap = igs;\n");
                sb.Append("      igs = igd;\n");
                sb.Append("      igd = swap;\n");
                sb.Append("      qd = -qg - qd;\n");
                sb.Append("    end\n");
            }
            sb.Append("    I(gi, si) <+ kN*(igs + igd);\n");
            sb.Append("    I(di, si) <+ kN*(id - igd);\n");
            sb.Append("    I(gi, si) <+ ddt(kN*qg);\n");
            sb.Append("    I(di, si) <+ ddt(kN*qd);\n");
        }
    }
}
