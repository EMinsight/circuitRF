using System.Globalization;

namespace CircuitRF.Engine.Statistics;

/// <summary>
/// The Sobol low-discrepancy sequence in Gray-code order, a point at a time: point n of dimension d is
/// the XOR of the direction numbers V_d[j] for the bits j of gray(n) = n ⊕ (n ≫ 1) — so any point is a
/// pure function of its index, with no state to carry from the one before. Dimension 0 is the van der
/// Corput sequence; dimensions 1 … <see cref="MaxDimensions"/>−1 take their primitive polynomials and
/// initial direction numbers from S. Joe and F. Y. Kuo's published table (new-joe-kuo-6.21201, its
/// first 1111 dimensions, embedded unmodified as <c>SobolDirections.txt</c> with its licence). 32-bit
/// direction numbers: 2³² points.
///
/// <para><b>Scrambling.</b> A scrambled sequence applies, per dimension, a random linear matrix
/// scramble (a lower-triangular binary matrix with a unit diagonal — each output digit mixes in the
/// digits above it) and then a random digital shift. Both preserve the net structure, both are drawn
/// from <see cref="StatStreams"/> with the run seed and the dimension, so a scrambled sequence is a
/// pure function of (seed, dimension, index).</para>
/// </summary>
public static class Sobol
{
    private const int Bits = 32;
    private static readonly Lazy<uint[][]> Directions = new(Load);

    /// <summary>How many dimensions the embedded table provides.</summary>
    public static int MaxDimensions => Directions.Value.Length;

    /// <summary>Unscrambled point <paramref name="index"/> (0-based) of dimension <paramref name="dim"/>
    /// (0-based), as a 32-bit binary fraction: the coordinate is the value / 2³².</summary>
    public static uint Raw(int dim, long index) => Combine(Directions.Value[dim], index);

    /// <summary>Point <paramref name="index"/> of dimension <paramref name="dim"/>, unscrambled, in [0, 1).</summary>
    public static double Point(int dim, long index) => Raw(dim, index) / 4294967296.0;

    /// <summary>The direction numbers of one dimension after a linear matrix scramble seeded by
    /// <paramref name="seed"/>, and the digital shift to XOR the point with.</summary>
    public static (uint[] Directions, uint Shift) Scramble(int dim, ulong seed)
    {
        var v = Directions.Value[dim];
        ulong stream = StatStreams.Id("sobol-scramble") ^ (ulong)dim;
        var rows = new uint[Bits];
        for (int i = 0; i < Bits; i++)
        {
            // Output digit i (0 = most significant) reads input digit i and a random choice of the
            // digits above it.
            uint diag = 1u << (Bits - 1 - i);
            uint above = ~((diag << 1) - 1);
            rows[i] = ((uint)StatStreams.Hash(seed, -1, stream, i) & above) | diag;
        }
        var scrambled = new uint[Bits];
        for (int j = 0; j < Bits; j++)
        {
            uint y = 0;
            for (int i = 0; i < Bits; i++)
                if ((System.Numerics.BitOperations.PopCount(rows[i] & v[j]) & 1) != 0) y |= 1u << (Bits - 1 - i);
            scrambled[j] = y;
        }
        return (scrambled, (uint)StatStreams.Hash(seed, -2, stream, 0));
    }

    /// <summary>The XOR of <paramref name="v"/> over the bits of gray(<paramref name="index"/>).</summary>
    public static uint Combine(uint[] v, long index)
    {
        ulong g = (ulong)index ^ ((ulong)index >> 1);
        uint x = 0;
        for (int j = 0; g != 0 && j < Bits; j++, g >>= 1)
            if ((g & 1) != 0) x ^= v[j];
        return x;
    }

    private static uint[][] Load()
    {
        var dims = new List<uint[]>();
        var first = new uint[Bits];
        for (int j = 0; j < Bits; j++) first[j] = 1u << (Bits - 1 - j);
        dims.Add(first);

        using var stream = typeof(Sobol).Assembly.GetManifestResourceStream("CircuitRF.Engine.Statistics.SobolDirections.txt")
                           ?? throw new InvalidOperationException("The Sobol direction numbers are not embedded.");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#' || line[0] == 'd') continue;
            var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int s = int.Parse(f[1], CultureInfo.InvariantCulture);
            uint a = uint.Parse(f[2], CultureInfo.InvariantCulture);

            // V[i] (1-based) = m_i · 2^(32−i) for i ≤ s; above that the recurrence of the primitive
            // polynomial x^s + a_1 x^(s−1) + … + a_(s−1) x + 1.
            var v = new uint[Bits + 1];
            for (int i = 1; i <= Math.Min(s, Bits); i++) v[i] = uint.Parse(f[2 + i], CultureInfo.InvariantCulture) << (Bits - i);
            for (int i = s + 1; i <= Bits; i++)
            {
                v[i] = v[i - s] ^ (v[i - s] >> s);
                for (int k = 1; k < s; k++)
                    if (((a >> (s - 1 - k)) & 1) != 0) v[i] ^= v[i - k];
            }
            dims.Add(v[1..]);
        }
        return [.. dims];
    }
}
