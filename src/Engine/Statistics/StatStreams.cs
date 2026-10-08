using System.Text;
using CircuitRF.Engine.Optimization;

namespace CircuitRF.Engine.Statistics;

/// <summary>
/// Counter-based random streams (yield overview D5): a draw is a pure function
/// <c>Hash(seed, trial, stream, k) → uniform</c>, not the next number of a generator. So trial N is
/// the same drawn alone or in a batch, in any order and on any thread, and a stream's draws do not
/// depend on which other streams exist. Built on <see cref="SplitMix64.Mix"/>, whose output is fixed on
/// every platform.
/// </summary>
public static class StatStreams
{
    /// <summary>
    /// A stream's id: a stable 64-bit hash of its text — a statistical entry's key (<c>DUT:R3.R</c>,
    /// <c>mag(ZL)</c>) or, for a kit draw, its instance path and parameter. FNV-1a over the UTF-8 bytes,
    /// then mixed, so similar keys land far apart.
    /// </summary>
    public static ulong Id(string text)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            h ^= b;
            h *= 0x100000001B3UL;
        }
        return SplitMix64.Mix(h + SplitMix64.Golden);
    }

    /// <summary>The 64 bits of draw <paramref name="k"/> of <paramref name="stream"/> in
    /// <paramref name="trial"/> under <paramref name="seed"/>. Each input is folded in through its own
    /// mix, so no two input tuples are related by a simple shift.</summary>
    public static ulong Hash(ulong seed, long trial, ulong stream, int k)
    {
        unchecked
        {
            ulong h = SplitMix64.Mix(seed + SplitMix64.Golden);
            h = SplitMix64.Mix(h ^ ((ulong)trial + 2 * SplitMix64.Golden));
            h = SplitMix64.Mix(h ^ (stream + 3 * SplitMix64.Golden));
            return SplitMix64.Mix(h ^ ((ulong)(uint)k + 4 * SplitMix64.Golden));
        }
    }

    /// <summary>Uniform on the OPEN interval (0, 1) — the top 53 bits, offset by half a step — so its
    /// normal quantile is always finite.</summary>
    public static double Uniform(ulong seed, long trial, ulong stream, int k = 0)
        => ((Hash(seed, trial, stream, k) >> 11) + 0.5) * (1.0 / (1UL << 53));

    /// <summary>A standard normal, by inversion: Φ⁻¹ of <see cref="Uniform"/>.</summary>
    public static double Normal(ulong seed, long trial, ulong stream, int k = 0)
        => SpecialFunctions.InverseNormalCdf(Uniform(seed, trial, stream, k));
}
