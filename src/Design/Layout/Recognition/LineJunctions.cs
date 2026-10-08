// Where traces meet — brief-artsch-5-traces-to-line-elements.md R-as5-4 and R-as5-5, overview D14;
// docs/design/artwork-to-schematic.md §6.4.
//
// The trace review returns each junction with its centre and its member traces (TraceJunction). In a microstrip
// region three arms are an MTEE and four an MCROSS; anywhere else, or with more than four arms, a junction is a
// plain node, reported.
//
// Reference planes are the models' own: an MTEE's through arms start half the BRANCH's width from the centre and
// its branch half the THROUGH line's width (the shortest arm MTeePCell admits — ClampArmLength(stub, W3/2) on
// L1/L2 and (max(W1, W2))/2 on L3 — which is where its artwork ends and where MicrostripTeeModel's star network,
// with "no reference-plane shift beyond what the star itself represents", is referred); an MCROSS's ±X arms start
// half the ±Y arms' width out and the other way round (MCrossPCell's halfCrossX / halfCrossY). A plain node owns
// nothing: every arm runs to the centre.

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>One arm of a junction: the line that ends there and which way it leaves.</summary>
/// <param name="Line">The line draft whose end is at the junction.</param>
/// <param name="AtStart">Whether it is the line's first node (else its last).</param>
/// <param name="End">The trace's end there — its point and its direction of travel INTO the junction.</param>
internal sealed record JunctionArm(LineDraft Line, bool AtStart, ChainEnd End)
{
    /// <summary>The arm's direction leaving the junction, unit.</summary>
    public (double X, double Y) Out => (-End.Dx, -End.Dy);

    public double Width => Line.W;
}

/// <summary>What one junction became.</summary>
/// <param name="Element">The MTEE or MCROSS, or null for a plain node.</param>
/// <param name="Plain">Why it is a plain node, or null.</param>
internal sealed record JunctionReading(LineDraft? Element, string? Plain);

/// <summary>R-as5-4's junction rules and R-as5-5's arm lengths.</summary>
internal static class LineJunctions
{
    /// <summary>The reason a junction with more than four arms is a plain node.</summary>
    public const string TooManyArms = "more than four arms";

    /// <summary>The reason a junction outside a microstrip region is a plain node.</summary>
    public const string NotMicrostrip = "not every arm is microstrip";

    /// <summary>
    /// Resolves <paramref name="junction"/>: writes each arm's node into its line, extends each arm's line from
    /// the trace's end to the centre and takes back what the element owns.
    /// </summary>
    public static JunctionReading Resolve(Em.TraceJunction junction, IReadOnlyList<JunctionArm> arms)
    {
        string id = junction.Id;
        bool mlin = arms.All(a => a.Line.Type == LineElementType.MLIN);
        string? plain = arms.Count > 4 ? TooManyArms
                      : arms.Count < 3 ? null
                      : !mlin ? NotMicrostrip
                      : null;
        if (arms.Count < 3 || plain is not null)
        {
            foreach (var arm in arms) Attach(arm, id, junction, 0);
            return new JunctionReading(null, plain);
        }

        if (arms.Count == 3)
        {
            // The two most nearly collinear arms are the through line; the branch is on the right of travel from
            // pin 1 to pin 2 — MTEE's own convention (through along +X, branch along −Y).
            var pairs = new[] { (0, 1, 2), (0, 2, 1), (1, 2, 0) };
            var (a, b, c) = pairs.MinBy(t => Dot(arms[t.Item1].Out, arms[t.Item2].Out));
            var (bx, by) = arms[b].Out;
            var (cx, cy) = arms[c].Out;
            if (bx * cy - by * cx > 0) (a, b) = (b, a);
            double w1 = arms[a].Width, w2 = arms[b].Width, w3 = arms[c].Width;
            Attach(arms[a], $"{id}.1", junction, 0.5 * w3);
            Attach(arms[b], $"{id}.2", junction, 0.5 * w3);
            Attach(arms[c], $"{id}.3", junction, 0.5 * Math.Max(w1, w2));
            return new JunctionReading(new LineDraft
            {
                Type = LineElementType.MTEE, Kind = LineKind.Mlin, Nodes = [$"{id}.1", $"{id}.2", $"{id}.3"],
                Widths = [w1, w2, w3], Source = id, Layer = junction.Layer, Anchor = [(junction.X, junction.Y)],
            }, null);
        }

        // Four arms, counter-clockwise from the one nearest +X — MCROSS's pin order.
        var order = Enumerable.Range(0, 4).OrderBy(i => Angle(arms[i].Out)).ToList();
        int startAt = order.IndexOf(order.MinBy(i => Math.Abs(Math.IEEERemainder(Angle(arms[i].Out), 2 * Math.PI))));
        order = [.. order.Skip(startAt), .. order.Take(startAt)];
        var widths = order.Select(i => arms[i].Width).ToArray();
        for (int p = 0; p < 4; p++)
            Attach(arms[order[p]], $"{id}.{p + 1}", junction, 0.5 * Math.Max(widths[(p + 1) % 4], widths[(p + 3) % 4]));
        return new JunctionReading(new LineDraft
        {
            Type = LineElementType.MCROSS, Kind = LineKind.Mlin, Nodes = [$"{id}.1", $"{id}.2", $"{id}.3", $"{id}.4"],
            Widths = widths, Source = id, Layer = junction.Layer, Anchor = [(junction.X, junction.Y)],
        }, null);
    }

    /// <summary>Names the arm's node at the junction, and runs its line from the trace's end to the centre less
    /// <paramref name="owned"/> (R-as5-5).</summary>
    private static void Attach(JunctionArm arm, string node, Em.TraceJunction junction, double owned)
    {
        var e = arm.End;
        arm.Line.L += (junction.X - e.X) * e.Dx + (junction.Y - e.Y) * e.Dy - owned;
        if (arm.AtStart) arm.Line.Nodes[0] = node;
        else arm.Line.Nodes[^1] = node;
    }

    private static double Dot((double X, double Y) a, (double X, double Y) b) => a.X * b.X + a.Y * b.Y;

    private static double Angle((double X, double Y) v)
    {
        double a = Math.Atan2(v.Y, v.X);
        return a < 0 ? a + 2 * Math.PI : a;
    }
}
