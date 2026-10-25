// ================================================================
//  PortSlices.cs  —  a pinned i/j slice names a PORT NUMBER
//
//  DFR27-2. An S/Y/Z cube's port axes carry the Terms' own numbers
//  (1, 2, 9, 10 when the bench has gaps), so the number a trace pins
//  is its identity and the position it sits at is a property of the
//  cube being read. A pinned i/j AxisSlice stores number − 1 in
//  Index — the convention every label, goal reader and port readout
//  already uses — and ToPositions turns it into a position against
//  the cube at hand, every time the trace resolves. A port the cube
//  does not have is invalid, naming the ports it does have.
// ================================================================

using System;
using System.Globalization;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

/// <summary>Port-number lookups for the <c>i</c>/<c>j</c> axes of a network-parameter cube.</summary>
public static class PortSlices
{
    /// <summary>True for 1, 2, …, N — the axis every contiguous bench and every Touchstone file has,
    /// on which a port number and a position are the same thing.</summary>
    public static bool IsOneToN(double[] values)
    {
        for (int k = 0; k < values.Length; k++)
            if (Math.Abs(values[k] - (k + 1)) > 1e-9) return false;
        return true;
    }

    /// <summary>The position of port <paramref name="number"/> on an axis, or −1.</summary>
    public static int PositionOf(double[] values, int number)
    {
        for (int k = 0; k < values.Length; k++)
            if (Math.Abs(values[k] - number) < 1e-9) return k;
        return -1;
    }

    /// <summary>"Port 4 is not a port of this result (it has 1, 2, 9, 10)."</summary>
    public static string NotAPort(int number, string axisName, double[] values) =>
        $"Port {number} is not a port of this result on axis '{axisName}' (it has " +
        string.Join(", ", values.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))) + ").";

    /// <summary>
    /// True when the axis holds port NUMBERS — distinct positive integers. Anything else (a 0-based
    /// I/Q axis that merely happens to be called <c>i</c>) keeps the positional reading it always had.
    /// </summary>
    public static bool IsPortNumberAxis(Axis axis) => IsPortNumberAxis(axis.Name, axis.Values);

    /// <inheritdoc cref="IsPortNumberAxis(Axis)"/>
    public static bool IsPortNumberAxis(string axisName, double[] values)
    {
        if (axisName is not ("i" or "j") || values.Length == 0) return false;
        foreach (var v in values)
            if (v < 1 || Math.Abs(v - Math.Round(v)) > 1e-9) return false;
        return values.Distinct().Count() == values.Length;
    }

    /// <summary>
    /// The position a parsed pin reads on <paramref name="axis"/>: for a port-number axis the token's
    /// index is number − 1 (<see cref="SliceTokenParser"/> has already refused a number the axis does
    /// not have), anywhere else it already is the position.
    /// </summary>
    public static int PinPosition(Axis axis, int index)
    {
        if (!IsPortNumberAxis(axis)) return index;
        int pos = PositionOf(axis.Values, index + 1);
        return pos < 0 ? index : pos;
    }

    /// <summary>
    /// The slice with every pinned <c>i</c>/<c>j</c> entry's port number (<c>Index + 1</c>) replaced
    /// by that port's position on <paramref name="cube"/>. The input is never modified — the trace
    /// keeps the number, so the next resolve against a different result looks it up again. False,
    /// with the sentence a card shows, when the cube has no such port: a pinned port past the end
    /// used to be clamped onto the last one, which is how a deleted Term went on reading −35 dB.
    /// </summary>
    public static bool TryToPositions(AxisSlice[] slice, DataCube cube, out AxisSlice[] positional, out string error)
    {
        positional = slice;
        error = "";
        for (int s = 0; s < slice.Length; s++)
        {
            var sl = slice[s];
            if (sl.Role != AxisRole.PinToIndex || sl.AxisName is not ("i" or "j")) continue;
            var axis = cube.Axes.FirstOrDefault(a => a.Name == sl.AxisName);
            if (axis is null || !IsPortNumberAxis(axis)) continue;

            int number = sl.Index + 1;
            int pos = PositionOf(axis.Values, number);
            if (pos < 0)
            {
                error = NotAPort(number, sl.AxisName, axis.Values);
                positional = slice;
                return false;
            }
            if (pos == sl.Index) continue;
            if (ReferenceEquals(positional, slice)) positional = (AxisSlice[])slice.Clone();
            positional[s] = sl with { Index = pos };
        }
        return true;
    }
}
