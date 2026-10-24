using System.Numerics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// A two-conductor uniform line, ground-referenced across <c>Nodes[0]</c> and <c>Nodes[1]</c>: what its
/// <see cref="ComponentModel.Stamp"/> stamps through <see cref="TLineModel.StampUniformLine"/>, as the two
/// numbers that define it. Read where a run realizes the line some other way than its nodal stamp (the
/// envelope steps a line as its delay, <c>EnvelopeLineDelay</c>); the model's own <c>Stamp</c> is unchanged.
/// </summary>
public interface IUniformLine
{
    /// <summary>The characteristic impedance (Ω, real) and γ·l = αl + jβl over the whole line at
    /// <paramref name="freqHz"/> (≥ 0).</summary>
    (double Z0, Complex GammaLength) LineAt(double freqHz);
}
