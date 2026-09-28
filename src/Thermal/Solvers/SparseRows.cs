// brief-em3d-74 R-em3d74-2c — compressed sparse rows: the one matrix type the assembly writes and every solver reads.

namespace CircuitRF.Thermal.Solvers;

/// <summary>A sparse matrix in compressed-row form, columns sorted within each row.</summary>
public sealed class SparseRows(int rows, int cols, int[] ptr, int[] idx, double[] val)
{
    public int Rows { get; } = rows;
    public int Cols { get; } = cols;
    public int[] Ptr { get; } = ptr;
    public int[] Idx { get; } = idx;
    public double[] Val { get; } = val;
    public int Nnz => Ptr[Rows];

    /// <summary>y = A·x.</summary>
    public void Multiply(ReadOnlySpan<double> x, Span<double> y)
    {
        for (int i = 0; i < Rows; i++)
        {
            double s = 0;
            for (int k = Ptr[i]; k < Ptr[i + 1]; k++) s += Val[k] * x[Idx[k]];
            y[i] = s;
        }
    }

    public double[] Diagonal()
    {
        var d = new double[Rows];
        for (int i = 0; i < Rows; i++)
            for (int k = Ptr[i]; k < Ptr[i + 1]; k++)
                if (Idx[k] == i) d[i] = Val[k];
        return d;
    }

    /// <summary>Aᵀ — whose arrays are also A's compressed-column form.</summary>
    public SparseRows Transpose()
    {
        var cnt = new int[Cols + 1];
        for (int k = 0; k < Nnz; k++) cnt[Idx[k] + 1]++;
        for (int j = 0; j < Cols; j++) cnt[j + 1] += cnt[j];
        var idx = new int[Nnz];
        var val = new double[Nnz];
        var next = (int[])cnt.Clone();
        for (int i = 0; i < Rows; i++)
            for (int k = Ptr[i]; k < Ptr[i + 1]; k++)
            {
                int p = next[Idx[k]]++;
                idx[p] = i;
                val[p] = Val[k];
            }
        return new SparseRows(Cols, Rows, cnt, idx, val);
    }

    /// <summary>(A + Aᵀ)/2 on A's own pattern when that pattern is structurally symmetric (an assembled FE matrix's is).</summary>
    public SparseRows SymmetricPart()
    {
        var t = Transpose();
        var val = new double[Nnz];
        for (int i = 0; i < Rows; i++)
            for (int k = Ptr[i]; k < Ptr[i + 1]; k++) val[k] = 0.5 * (Val[k] + t.Val[k]);
        return new SparseRows(Rows, Cols, Ptr, Idx, val);
    }

    /// <summary>A·B, Gustavson's row-by-row product with a dense marker; each row's columns sorted.</summary>
    public SparseRows Times(SparseRows b)
    {
        var ptr = new int[Rows + 1];
        var idx = new List<int>(Nnz * 2);
        var val = new List<double>(Nnz * 2);
        var mark = new int[b.Cols];
        Array.Fill(mark, -1);
        var acc = new double[b.Cols];
        var cols = new List<int>();
        for (int i = 0; i < Rows; i++)
        {
            cols.Clear();
            for (int k = Ptr[i]; k < Ptr[i + 1]; k++)
            {
                int j = Idx[k];
                double a = Val[k];
                for (int l = b.Ptr[j]; l < b.Ptr[j + 1]; l++)
                {
                    int c = b.Idx[l];
                    if (mark[c] != i) { mark[c] = i; acc[c] = 0; cols.Add(c); }
                    acc[c] += a * b.Val[l];
                }
            }
            cols.Sort();
            foreach (int c in cols) { idx.Add(c); val.Add(acc[c]); }
            ptr[i + 1] = idx.Count;
        }
        return new SparseRows(Rows, b.Cols, ptr, [.. idx], [.. val]);
    }

    /// <summary>The slot of entry (i, j), or −1.</summary>
    public int Slot(int i, int j)
    {
        int lo = Ptr[i], hi = Ptr[i + 1] - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int c = Idx[mid];
            if (c == j) return mid;
            if (c < j) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    internal static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    internal static double Norm(ReadOnlySpan<double> a) => Math.Sqrt(Dot(a, a));
}
