namespace CircuitRF.Core.Devices;

/// <summary>
/// A linear model whose ω = 0 stamp is NOT its DC behaviour, together with what is. A lossless
/// uniform line's admittance has a pole at ω = 0 (Y ≈ 1/(jω·Z₀·τ): its series inductance), so its
/// stamp there is <see cref="TLineModel.StampUniformLine"/>'s resonance clamp — a large, purely
/// IMAGINARY admittance, which no DC stamp can be. At DC the line is these terminal pairs shorted.
///
/// <para>Read only where a run needs an element's exact DC limit in place of its ω = 0 stamp (the
/// envelope's baseband product, <c>EnvelopeBaseband</c>); the model's own <c>Stamp</c> is unchanged,
/// so a DC, S-parameter or harmonic-balance run never sees this.</para>
/// </summary>
public interface IDcShortedTerminals
{
    /// <summary>Pairs of indices into the element's <c>Nodes</c> that are one node at DC.</summary>
    IReadOnlyList<(int A, int B)> DcShortedTerminals { get; }
}
