// brief-em3d-67 R-em3d67-4 — Select Tangent Chain: every edge of one object that continues the clicked one smoothly.
//
// The walk goes out from both ends of the edge, vertex by vertex. At a vertex it continues along the ONE other edge whose
// tangent there differs from the edge's by less than a degree — the loop round a filleted top, the whole rim of a rounded
// slot. Where more than one qualifies it stops and says so: which way a branch goes is the user's choice, never ours. A
// closed edge (a circle) is a chain of itself.
//
// The tangents are the edge table's: exact from the worker for a kernel edge, a segment's direction for a managed one, so
// on a managed object only collinear edges chain. Headless — no scene, no camera.

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D.Edit;

/// <summary>A chain: its edges (indices into the table, the clicked one first) and, when it stopped at a branch, how many
/// edges qualified there.</summary>
public sealed record Scene3DChain(IReadOnlyList<int> Edges, int BranchCount)
{
    public bool StoppedAtBranch => BranchCount > 1;
}

public static class Scene3DEdgeChain
{
    /// <summary>Two tangents this close (degrees) continue one another.</summary>
    public const double ToleranceDegrees = 1.0;

    private static readonly double CosTolerance = Math.Cos(ToleranceDegrees * Math.PI / 180);

    /// <summary>The tangent chain through edge <paramref name="start"/> of <paramref name="edges"/>.</summary>
    public static Scene3DChain Walk(Scene3DEdges edges, int start)
    {
        var chain = new List<int> { start };
        if (start < 0 || start >= edges.Edges.Length) return new Scene3DChain([], 0);
        var e0 = edges.Edges[start];
        if (e0.Closed) return new Scene3DChain(chain, 0);
        var byVertex = new Dictionary<int, List<int>>();
        for (int i = 0; i < edges.Edges.Length; i++)
            foreach (int v in (ReadOnlySpan<int>)[edges.Edges[i].StartVertex, edges.Edges[i].EndVertex])
            {
                if (!byVertex.TryGetValue(v, out var l)) byVertex[v] = l = [];
                if (!l.Contains(i)) l.Add(i);
            }
        var inChain = new HashSet<int> { start };
        int branch = 0;
        // Out through the end, then out through the start.
        foreach (bool forward in (ReadOnlySpan<bool>)[true, false])
        {
            int at = start;
            int vertex = forward ? e0.EndVertex : e0.StartVertex;
            while (true)
            {
                var leaving = Leaving(edges.Edges[at], vertex);
                var qualifying = new List<int>();
                foreach (int n in byVertex.GetValueOrDefault(vertex) ?? [])
                {
                    if (n == at || edges.Edges[n].Closed) continue;
                    if (Scene3DEdges.Dot(leaving, Entering(edges.Edges[n], vertex)) >= CosTolerance) qualifying.Add(n);
                }
                if (qualifying.Count > 1) { branch = Math.Max(branch, qualifying.Count); break; }
                if (qualifying.Count == 0 || !inChain.Add(qualifying[0])) break;   // an end, or round the loop
                int next = qualifying[0];
                if (forward) chain.Add(next); else chain.Insert(1, next);
                var ne = edges.Edges[next];
                vertex = ne.StartVertex == vertex ? ne.EndVertex : ne.StartVertex;
                at = next;
            }
        }
        return new Scene3DChain(chain, branch);
    }

    /// <summary>The direction an edge LEAVES by, through its end vertex <paramref name="v"/>.</summary>
    private static Point3 Leaving(Scene3DEdge e, int v) => v == e.EndVertex ? e.TangentEnd : Scene3DEdges.Neg(e.TangentStart);

    /// <summary>The direction an edge ENTERS by, from its end vertex <paramref name="v"/>.</summary>
    private static Point3 Entering(Scene3DEdge e, int v) => v == e.StartVertex ? e.TangentStart : Scene3DEdges.Neg(e.TangentEnd);
}
