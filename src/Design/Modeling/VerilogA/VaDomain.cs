using System.Text;
using CircuitRF.Core.Devices.Composed;
using static CircuitRF.Design.Modeling.VerilogA.VaText;

namespace CircuitRF.Design.Modeling.VerilogA;

/// <summary>
/// Guided extrapolation as straight-line Verilog-A (brief-modelcomposer-26 R-mc26-7): <c>DomainGeometry.Project</c> and
/// <c>GuidedExtrapolation.Evaluate</c> ported line for line, so a table or ANN constituent continues outside its training
/// domain exactly as it does in circuitRF.
///
/// <para><b>The same geometry, as literals.</b> Each input is normalised by the domain's own centre and extent
/// (<c>u = (p − centre)/extent</c>); the polygon's vertices are written in those coordinates, counter-clockwise; the inside
/// test is every edge's cross product non-negative; the nearest point is searched edge by edge, in the same order, a later
/// edge replacing the best only when strictly nearer; and every other input is clamped to its interval. Every number the
/// search reads is formed here exactly as the evaluator forms it, so the same point projects to the same point.</para>
///
/// <para><b>One formula inside and out.</b> The value is <c>f(p_b) + ∇f(p_b)·(p − p_b)</c>, plus
/// <c>(G_x/0.05)·s·d</c> for a current, <c>s</c> the continuation's step along the constituent's own branch and <c>d</c> its
/// normalised distance. Inside the domain <c>p_b</c> is <c>p</c> itself (not <c>p</c> normalised and back, which would move
/// it by a rounding), so the continuation's step is zero and the value is the evaluator's. The compiler differentiates this
/// code itself and so forms <c>∇f(p_b) + Pᵀ H (p − p_b)</c> without being told, because <c>p_b</c> moves with <c>p</c>.</para>
/// </summary>
internal static class VaDomain
{
    /// <summary>
    /// The continued constituent: a function of <paramref name="inputs"/> calling <paramref name="payload"/> (a function
    /// returning the value and writing the gradient into one output per input) at the nearest domain point.
    /// </summary>
    /// <param name="gx">The module parameter holding <c>G_x</c>, or null for a charge.</param>
    /// <param name="own">The constituent's own branch voltage over <paramref name="inputs"/> (<c>vds</c> for Id, <c>vgs</c>
    /// for IgGs, <c>vgs − vds</c> for IgGd), or null for a charge.</param>
    public static string Function(string name, string comment, string payload, IReadOnlyList<string> inputs,
                                  TrainingDomain domain, string? gx, double[]? own)
    {
        int d = inputs.Count;
        int iG = IndexOf(inputs, ComposedCoreProgram.Vgs), iD = IndexOf(inputs, ComposedCoreProgram.Vds);

        // DomainGeometry's constructor, exactly.
        var centre = new double[d];
        var extent = new double[d];
        var boxLo = new double[d];
        var boxHi = new double[d];
        var poly = domain.Polygon;
        void Set(int k, double lo, double hi) { centre[k] = 0.5 * (lo + hi); extent[k] = hi - lo; }
        Set(iG, poly.Min(v => v.Vgs), poly.Max(v => v.Vgs));
        Set(iD, poly.Min(v => v.Vds), poly.Max(v => v.Vds));
        foreach (var (input, (lo, hi)) in domain.Box)
        {
            int k = IndexOf(inputs, input);
            Set(k, lo, hi);
            boxLo[k] = (lo - centre[k]) / extent[k];
            boxHi[k] = (hi - centre[k]) / extent[k];
        }
        int n = poly.Count;
        var px = new double[n];
        var py = new double[n];
        for (int k = 0; k < n; k++)
        {
            px[k] = (poly[k].Vgs - centre[iG]) / extent[iG];
            py[k] = (poly[k].Vds - centre[iD]) / extent[iD];
        }
        double area = 0.0;
        for (int k = 0; k < n; k++) area += px[k] * py[(k + 1) % n] - px[(k + 1) % n] * py[k];
        if (area < 0) { Array.Reverse(px); Array.Reverse(py); }

        var u = inputs.Select(x => "u_" + x).ToArray();
        var ub = inputs.Select(x => "b_" + x).ToArray();
        var pb = inputs.Select(x => "p_" + x).ToArray();
        var gb = inputs.Select(x => "g_" + x).ToArray();
        var locals = new List<string>([.. u, .. ub, .. pb, .. gb, "inside", "inbox", "t", "cx", "cy", "e", "best", "cr", "fb", "s", "dist2"]);

        var b = new StringBuilder();
        b.Append("      // normalised by the domain's centre and extent\n");
        for (int k = 0; k < d; k++)
            b.Append($"      {u[k]} = ({inputs[k]} - {Operand(centre[k])})/{Operand(extent[k])};\n");

        b.Append("      inbox = 1;\n");
        for (int k = 0; k < d; k++)
        {
            if (k == iG || k == iD) continue;
            b.Append($"      // Domain.Box.{inputs[k]}\n");
            b.Append($"      if ({u[k]} < {Operand(boxLo[k])}) begin {ub[k]} = {Operand(boxLo[k])}; inbox = 0; end\n");
            b.Append($"      else if ({u[k]} > {Operand(boxHi[k])}) begin {ub[k]} = {Operand(boxHi[k])}; inbox = 0; end\n");
            b.Append($"      else {ub[k]} = {u[k]};\n");
        }

        string qx = u[iG], qy = u[iD];
        b.Append($"      // Domain.Polygon: {n} vertices, counter-clockwise\n");
        b.Append("      inside = 1;\n");
        for (int k = 0; k < n; k++)
        {
            int m = (k + 1) % n;
            b.Append($"      cr = {Operand(px[m] - px[k])}*({qy} - {Operand(py[k])}) - {Operand(py[m] - py[k])}*({qx} - {Operand(px[k])});\n");
            b.Append("      if (cr < 0.0) inside = 0;\n");
        }
        b.Append("      if (inside) begin\n");
        b.Append($"        {ub[iG]} = {qx};\n");
        b.Append($"        {ub[iD]} = {qy};\n");
        b.Append("      end else begin\n");
        bool first = true;
        for (int k = 0; k < n; k++)
        {
            int m = (k + 1) % n;
            double sx = px[m] - px[k], sy = py[m] - py[k];
            double len2 = sx * sx + sy * sy;
            if (len2 == 0) continue;
            b.Append($"        t = (({qx} - {Operand(px[k])})*{Operand(sx)} + ({qy} - {Operand(py[k])})*{Operand(sy)})/{Operand(len2)};\n");
            b.Append("        if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;\n");
            b.Append($"        cx = {Operand(px[k])} + t*{Operand(sx)};\n");
            b.Append($"        cy = {Operand(py[k])} + t*{Operand(sy)};\n");
            b.Append($"        e = ({qx} - cx)*({qx} - cx) + ({qy} - cy)*({qy} - cy);\n");
            b.Append(first
                ? $"        best = e; {ub[iG]} = cx; {ub[iD]} = cy;\n"
                : $"        if (e < best) begin best = e; {ub[iG]} = cx; {ub[iD]} = cy; end\n");
            first = false;
        }
        b.Append("      end\n");

        b.Append("      // the nearest domain point: the point itself inside, else back from normalised coordinates\n");
        b.Append("      if (inside && inbox) begin\n");
        for (int k = 0; k < d; k++) b.Append($"        {pb[k]} = {inputs[k]};\n");
        b.Append("      end else begin\n");
        for (int k = 0; k < d; k++) b.Append($"        {pb[k]} = {Operand(centre[k])} + {Operand(extent[k])}*{ub[k]};\n");
        b.Append("      end\n");

        b.Append($"      fb = {payload}({string.Join(", ", pb)}, {string.Join(", ", gb)});\n");
        var sum = new StringBuilder("fb");
        for (int k = 0; k < d; k++) sum.Append($" + {gb[k]}*({inputs[k]} - {pb[k]})");
        b.Append($"      {name} = {sum};\n");

        if (gx is not null && own is not null)
        {
            var dist = new StringBuilder();
            for (int k = 0; k < d; k++) dist.Append(k == 0 ? "" : " + ").Append($"({u[k]} - {ub[k]})*({u[k]} - {ub[k]})");
            var step = new List<string>();
            for (int k = 0; k < d; k++)
                if (own[k] == 1) step.Add($"({inputs[k]} - {pb[k]})");
                else if (own[k] == -1) step.Add($"-({inputs[k]} - {pb[k]})");
            b.Append($"      // the added conductance on the constituent's own branch, G_x at 5 % of the extent\n");
            b.Append($"      dist2 = {dist};\n");
            b.Append($"      s = {string.Join(" + ", step).Replace("+ -", "- ")};\n");
            b.Append($"      if (dist2 > 0.0) {name} = {name} + ({gx}/{Num(GuidedExtrapolation.BoundaryFraction)})*s*sqrt(dist2);\n");
        }

        var sb = new StringBuilder();
        sb.Append($"  // {comment}\n");
        sb.Append($"  analog function real {name};\n");
        sb.Append($"    input {string.Join(", ", inputs)};\n");
        sb.Append($"    real {string.Join(", ", inputs)};\n");
        foreach (var chunk in locals.Where(l => l is not ("inside" or "inbox")).Chunk(12))
            sb.Append($"    real {string.Join(", ", chunk)};\n");
        sb.Append("    integer inside, inbox;\n");
        sb.Append("    begin\n").Append(b).Append("    end\n");
        sb.Append("  endfunction\n");
        return sb.ToString();
    }

    /// <summary>
    /// A drift charge's piece continued to first order beyond its box (<c>BoxContinuation</c>): every input clamped to its
    /// interval, and <c>f(p) + ∇f(p)·(x − p)</c>. No normalisation, so a point inside is its own clamp exactly.
    /// </summary>
    public static string BoxFunction(string name, string comment, string payload, IReadOnlyList<string> inputs,
                                     IReadOnlyList<(double Lo, double Hi)> box)
    {
        int d = inputs.Count;
        var p = inputs.Select(x => "p_" + x).ToArray();
        var g = inputs.Select(x => "g_" + x).ToArray();
        var b = new StringBuilder();
        for (int k = 0; k < d; k++)
            b.Append($"      {p[k]} = min(max({inputs[k]}, {Operand(box[k].Lo)}), {Operand(box[k].Hi)});\n");
        var sum = new StringBuilder($"{payload}({string.Join(", ", p)}, {string.Join(", ", g)})");
        b.Append($"      {name} = {sum};\n");
        for (int k = 0; k < d; k++) b.Append($"      {name} = {name} + {g[k]}*({inputs[k]} - {p[k]});\n");

        var sb = new StringBuilder();
        sb.Append($"  // {comment}\n");
        sb.Append($"  analog function real {name};\n");
        sb.Append($"    input {string.Join(", ", inputs)};\n");
        sb.Append($"    real {string.Join(", ", inputs)};\n");
        sb.Append($"    real {string.Join(", ", p.Concat(g))};\n");
        sb.Append("    begin\n").Append(b).Append("    end\n");
        sb.Append("  endfunction\n");
        return sb.ToString();
    }

    private static int IndexOf(IReadOnlyList<string> inputs, string name)
    {
        for (int k = 0; k < inputs.Count; k++) if (inputs[k] == name) return k;
        throw new ArgumentException($"'{name}' is not an input.");
    }
}
