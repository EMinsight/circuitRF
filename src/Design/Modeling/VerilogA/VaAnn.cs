using System.Text;
using CircuitRF.Core.Devices.Composed;
using static CircuitRF.Design.Modeling.VerilogA.VaText;

namespace CircuitRF.Design.Modeling.VerilogA;

/// <summary>An ANN payload's numbers, read from a <c>.cmodel</c>'s arrays (<see cref="ComposedArrays.Ann"/>).</summary>
internal sealed record VaAnnData(
    double[] InOffset, double[] InScale, IReadOnlyList<double[]> Weights, IReadOnlyList<double[]> Biases,
    double OutOffset, double OutScale, AnnActivation Activation)
{
    public int Inputs => InOffset.Length;

    /// <summary>Layer <c>k</c>'s width; layer 0 is the inputs.</summary>
    public int Width(int k) => k == 0 ? Inputs : Biases[k - 1].Length;

    public int Layers => Weights.Count;

    /// <summary>The network <paramref name="name"/> over <paramref name="d"/> inputs, from a model the validator has
    /// accepted.</summary>
    public static VaAnnData Read(ComposedModel model, string name, int d, AnnActivation activation)
    {
        double[] Vec(string suffix) => model.Arrays[$"{name}.{suffix}"].RealValues;
        var weights = new List<double[]>();
        var biases = new List<double[]>();
        for (int k = 1; model.Arrays.TryGetValue($"{name}.W{k}", out var w); k++)
        {
            weights.Add(w.RealValues);
            biases.Add(Vec($"b{k}"));
        }
        var inOffset = Vec("inputOffset");
        if (inOffset.Length != d) throw new InvalidOperationException($"'{name}' has {inOffset.Length} inputs, not {d}.");
        return new VaAnnData(inOffset, Vec("inputScale"), weights, biases, Vec("outputOffset")[0], Vec("outputScale")[0],
                             activation);
    }
}

/// <summary>
/// An ANN payload as one Verilog-A function returning its value and writing its gradient into output arguments
/// (brief-modelcomposer-26 R-mc26-6): the derivative network <see cref="AnnEvaluator"/> carries, written out neuron by
/// neuron because Verilog-A has no arrays.
///
/// <para><b>The same arithmetic in the same order.</b> Each neuron's sum starts at its bias and adds the weighted inputs in
/// column order; each gradient sum starts at zero; <c>tanh</c> is <c>h' = (1 − h²)·z'</c> and the logistic sigmoid is
/// <c>1/(1 + exp(−z))</c> with <c>h' = h(1 − h)·z'</c>. So the value is the evaluator's to rounding. The first layer's
/// gradient is a product of two literals, <c>W·scale</c>, formed here in the same order the evaluator forms it.</para>
///
/// <para>An <see cref="OutputTransform.Asinh"/> payload ends with <c>f = I0·sinh(u)</c> and <c>∇f = ∇u·I0·cosh(u)</c>, as
/// <see cref="AsinhOutput"/> forms them.</para>
/// </summary>
internal static class VaAnn
{
    /// <summary>The function, its comment first. <paramref name="inputs"/> name its arguments and, prefixed <c>d_</c>, the
    /// gradient's outputs.</summary>
    public static string Function(string name, string comment, IReadOnlyList<string> inputs, VaAnnData net,
                                  OutputTransform? transform)
    {
        int d = inputs.Count;
        var body = new StringBuilder();
        var locals = new List<string> { "z" };

        // The input layer: x̂ = (x − offset)·scale.
        var prev = new string[d];
        for (int i = 0; i < d; i++)
        {
            prev[i] = $"x{i + 1}";
            locals.Add(prev[i]);
            body.Append($"      {prev[i]} = ({inputs[i]} - {Operand(net.InOffset[i])})*{Operand(net.InScale[i])};\n");
        }
        // ∂x̂/∂x is diagonal; layer 1's gradient sum collapses to one product per input.
        string[,]? prevGrad = null;

        for (int layer = 0; layer < net.Layers; layer++)
        {
            int nIn = net.Width(layer), nOut = net.Width(layer + 1);
            double[] W = net.Weights[layer], b = net.Biases[layer];
            bool hidden = layer < net.Layers - 1;
            var next = new string[nOut];
            var nextGrad = new string[nOut, d];
            body.Append(hidden ? $"      // hidden layer {layer + 1}\n" : "      // output\n");

            for (int r = 0; r < nOut; r++)
            {
                string h = hidden ? $"h{layer + 1}_{r + 1}" : "u";
                next[r] = h;
                locals.Add(h);
                var z = new StringBuilder(Num(b[r]));
                for (int c = 0; c < nIn; c++) z.Append(Term(W[r * nIn + c], prev[c]));

                // dz/dx_g: Σ W·∂a/∂x_g from zero, in column order.
                var dz = new string[d];
                for (int g = 0; g < d; g++)
                {
                    if (prevGrad is null) { dz[g] = Num(W[r * nIn + g] * net.InScale[g]); continue; }
                    var s = new StringBuilder();
                    for (int c = 0; c < nIn; c++)
                    {
                        string t = Term(W[r * nIn + c], prevGrad[c, g]);
                        s.Append(s.Length == 0 ? t.TrimStart(' ', '+') : t);
                    }
                    dz[g] = s.ToString();
                }

                if (!hidden)
                {
                    body.Append($"      {h} = {z};\n");
                    for (int g = 0; g < d; g++)
                    {
                        string hg = $"u_{inputs[g]}";
                        locals.Add(hg);
                        nextGrad[r, g] = hg;
                        body.Append($"      {hg} = {dz[g]};\n");
                    }
                    continue;
                }

                body.Append($"      z = {z};\n");
                body.Append(net.Activation == AnnActivation.Tanh
                    ? $"      {h} = tanh(z);\n"
                    : $"      {h} = 1.0/(1.0 + exp(-z));\n");
                string slope = net.Activation == AnnActivation.Tanh ? $"(1.0 - {h}*{h})" : $"({h}*(1.0 - {h}))";
                for (int g = 0; g < d; g++)
                {
                    string hg = $"{h}_{inputs[g]}";
                    locals.Add(hg);
                    nextGrad[r, g] = hg;
                    body.Append($"      {hg} = {slope}*({dz[g]});\n");
                }
            }
            prev = next;
            prevGrad = nextGrad;
        }

        // De-normalisation: y = offset + scale·u, ∇y = scale·∇u.
        var tail = new StringBuilder();
        string value = $"{Operand(net.OutOffset)} + {Operand(net.OutScale)}*u";
        if (transform is null)
        {
            tail.Append($"      {name} = {value};\n");
            for (int g = 0; g < d; g++) tail.Append($"      d_{inputs[g]} = {Operand(net.OutScale)}*u_{inputs[g]};\n");
        }
        else
        {
            // The payload is u of I0·sinh(u) (Transform: Asinh).
            locals.AddRange(["w", "sh", "ch"]);
            tail.Append($"      w = {value};\n");
            tail.Append($"      sh = {Operand(transform.Scale)}*sinh(w);\n");
            tail.Append($"      ch = {Operand(transform.Scale)}*cosh(w);\n");
            tail.Append($"      {name} = sh;\n");
            for (int g = 0; g < d; g++)
                tail.Append($"      d_{inputs[g]} = ({Operand(net.OutScale)}*u_{inputs[g]})*ch;\n");
        }

        var sb = new StringBuilder();
        sb.Append($"  // {comment}\n");
        sb.Append($"  analog function real {name};\n");
        sb.Append($"    input {string.Join(", ", inputs)};\n");
        sb.Append($"    output {string.Join(", ", inputs.Select(x => "d_" + x))};\n");
        sb.Append($"    real {string.Join(", ", inputs)};\n");
        sb.Append($"    real {string.Join(", ", inputs.Select(x => "d_" + x))};\n");
        foreach (var chunk in locals.Chunk(12)) sb.Append($"    real {string.Join(", ", chunk)};\n");
        sb.Append("    begin\n").Append(body).Append(tail).Append("    end\n");
        sb.Append("  endfunction\n");
        return sb.ToString();
    }

    /// <summary>
    /// The same network with its Hessian along a direction (brief-modelcomposer-27): <c>name(x…, w_x…, d_x…, hw_x…)</c>
    /// returns the value, writes the gradient into the <c>d_</c> outputs exactly as <see cref="Function"/> forms it, and
    /// <c>H·w</c> into the <c>hw_</c> outputs — forward mode over the gradient code, as the evaluator's own
    /// <c>Evaluate(x, grad, w, hw)</c> carries it. Guided extrapolation's exact gradient outside the domain,
    /// <c>∇f(p_b) + Pᵀ H (p − p_b)</c>, reads it, and the bias-point charge needs that gradient at points the circuit is
    /// not at.
    /// </summary>
    public static string HessianFunction(string name, string comment, IReadOnlyList<string> inputs, VaAnnData net,
                                         OutputTransform? transform)
    {
        int d = inputs.Count;
        var body = new StringBuilder();
        var locals = new List<string> { "z", "zw", "sl", "slw" };
        foreach (string x in inputs) locals.AddRange([$"zg_{x}", $"zgw_{x}"]);

        // The input layer: x̂ = (x − offset)·scale, and its derivative along w.
        var prev = new string[d];
        var prevW = new string[d];
        for (int i = 0; i < d; i++)
        {
            prev[i] = $"x{i + 1}";
            prevW[i] = $"x{i + 1}_w";
            locals.AddRange([prev[i], prevW[i]]);
            body.Append($"      {prev[i]} = ({inputs[i]} - {Operand(net.InOffset[i])})*{Operand(net.InScale[i])};\n");
            body.Append($"      {prevW[i]} = w_{inputs[i]}*{Operand(net.InScale[i])};\n");
        }
        string[,]? prevGrad = null, prevGradW = null;

        for (int layer = 0; layer < net.Layers; layer++)
        {
            int nIn = net.Width(layer), nOut = net.Width(layer + 1);
            double[] W = net.Weights[layer], b = net.Biases[layer];
            bool hidden = layer < net.Layers - 1;
            var next = new string[nOut];
            var nextW = new string[nOut];
            var nextGrad = new string[nOut, d];
            var nextGradW = new string[nOut, d];
            body.Append(hidden ? $"      // hidden layer {layer + 1}\n" : "      // output\n");

            for (int r = 0; r < nOut; r++)
            {
                string h = hidden ? $"h{layer + 1}_{r + 1}" : "u";
                next[r] = h;
                nextW[r] = h + "_w";
                locals.AddRange([h, h + "_w"]);
                var z = new StringBuilder(Num(b[r]));
                for (int c = 0; c < nIn; c++) z.Append(Term(W[r * nIn + c], prev[c]));
                body.Append($"      z = {z};\n");
                var row = Enumerable.Range(0, nIn);
                body.Append($"      zw = {Sum(row.Select(c => Term(W[r * nIn + c], prevW[c])))};\n");
                for (int g = 0; g < d; g++)
                {
                    string zg = prevGrad is null
                        ? Num(W[r * nIn + g] * net.InScale[g])
                        : Sum(row.Select(c => Term(W[r * nIn + c], prevGrad[c, g])));
                    string zgw = prevGradW is null ? "0.0" : Sum(row.Select(c => Term(W[r * nIn + c], prevGradW[c, g])));
                    body.Append($"      zg_{inputs[g]} = {zg};\n");
                    body.Append($"      zgw_{inputs[g]} = {zgw};\n");
                }

                if (!hidden)
                {
                    body.Append($"      {h} = z;\n      {h}_w = zw;\n");
                    for (int g = 0; g < d; g++)
                    {
                        string hg = $"u_{inputs[g]}";
                        locals.AddRange([hg, hg + "_w"]);
                        nextGrad[r, g] = hg;
                        nextGradW[r, g] = hg + "_w";
                        body.Append($"      {hg} = zg_{inputs[g]};\n      {hg}_w = zgw_{inputs[g]};\n");
                    }
                    continue;
                }

                // h' = s·z' and (h')_w = s·(z')_w + s_w·z', s the activation's slope.
                if (net.Activation == AnnActivation.Tanh)
                {
                    body.Append($"      {h} = tanh(z);\n");
                    body.Append($"      sl = (1.0 - {h}*{h});\n");
                    body.Append($"      {h}_w = sl*zw;\n");
                    body.Append($"      slw = -2.0*{h}*{h}_w;\n");
                }
                else
                {
                    body.Append($"      {h} = 1.0/(1.0 + exp(-z));\n");
                    body.Append($"      sl = ({h}*(1.0 - {h}));\n");
                    body.Append($"      {h}_w = sl*zw;\n");
                    body.Append($"      slw = {h}_w*(1.0 - 2.0*{h});\n");
                }
                for (int g = 0; g < d; g++)
                {
                    string hg = $"{h}_{inputs[g]}";
                    locals.AddRange([hg, hg + "_w"]);
                    nextGrad[r, g] = hg;
                    nextGradW[r, g] = hg + "_w";
                    body.Append($"      {hg} = sl*(zg_{inputs[g]});\n");
                    body.Append($"      {hg}_w = sl*zgw_{inputs[g]} + slw*zg_{inputs[g]};\n");
                }
            }
            prev = next;
            prevW = nextW;
            prevGrad = nextGrad;
            prevGradW = nextGradW;
        }

        var tail = new StringBuilder();
        string sc = Operand(net.OutScale);
        string value = $"{Operand(net.OutOffset)} + {sc}*u";
        if (transform is null)
        {
            tail.Append($"      {name} = {value};\n");
            for (int g = 0; g < d; g++)
            {
                tail.Append($"      d_{inputs[g]} = {sc}*u_{inputs[g]};\n");
                tail.Append($"      hw_{inputs[g]} = {sc}*u_{inputs[g]}_w;\n");
            }
        }
        else
        {
            // f = I0·sinh(y): ∇f = ∇y·I0·cosh(y), and H·w = (∇y)_w·I0·cosh(y) + ∇y·I0·sinh(y)·y_w.
            locals.AddRange(["w", "sh", "ch", "yw"]);
            tail.Append($"      w = {value};\n");
            tail.Append($"      sh = {Operand(transform.Scale)}*sinh(w);\n");
            tail.Append($"      ch = {Operand(transform.Scale)}*cosh(w);\n");
            tail.Append($"      yw = {sc}*u_w;\n");
            tail.Append($"      {name} = sh;\n");
            for (int g = 0; g < d; g++)
            {
                tail.Append($"      d_{inputs[g]} = ({sc}*u_{inputs[g]})*ch;\n");
                tail.Append($"      hw_{inputs[g]} = ({sc}*u_{inputs[g]}_w)*ch + ({sc}*u_{inputs[g]})*sh*yw;\n");
            }
        }

        var args = inputs.Concat(inputs.Select(x => "w_" + x)).ToList();
        var outs = inputs.Select(x => "d_" + x).Concat(inputs.Select(x => "hw_" + x)).ToList();
        var sb = new StringBuilder();
        sb.Append($"  // {comment}\n");
        sb.Append($"  analog function real {name};\n");
        sb.Append($"    input {string.Join(", ", args)};\n");
        sb.Append($"    output {string.Join(", ", outs)};\n");
        sb.Append($"    real {string.Join(", ", args)};\n");
        sb.Append($"    real {string.Join(", ", outs)};\n");
        foreach (var chunk in locals.Distinct().Chunk(12)) sb.Append($"    real {string.Join(", ", chunk)};\n");
        sb.Append("    begin\n").Append(body).Append(tail).Append("    end\n");
        sb.Append("  endfunction\n");
        return sb.ToString();
    }

    /// <summary>A sum of <see cref="Term"/>s from zero; <c>0.0</c> when there are none.</summary>
    private static string Sum(IEnumerable<string> terms)
    {
        var s = new StringBuilder();
        foreach (string t in terms) s.Append(s.Length == 0 ? t.TrimStart(' ', '+') : t);
        return s.Length == 0 ? "0.0" : s.ToString();
    }

    /// <summary><c> + w*a</c>, with the sign folded into the operator.</summary>
    private static string Term(double w, string a)
        => w < 0 || (w == 0 && double.IsNegative(w)) ? $" - {Num(-w)}*{a}" : $" + {Num(w)}*{a}";
}
