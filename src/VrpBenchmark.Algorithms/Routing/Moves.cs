using VrpBenchmark.Core.Evaluation;

namespace VrpBenchmark.Algorithms.Routing;

/// <summary>
/// Evaluation (cost change, +∞ if infeasible) and application of the four neighbourhood moves.
/// Positions always refer to the routes before the move.
/// </summary>
public static class Moves
{
    /// <summary>Moves visits [i, i + k) of A so that they stand before position p of B (A and B may be the same route).</summary>
    public static double RelocateDelta(RouteState a, int i, int k, RouteState b, int p)
    {
        var m = a.Matrix;
        var chain = a.Fold(i, i + k);

        if (!ReferenceEquals(a, b))
        {
            if (b.Load + chain.Load > b.Vehicle.Capacity + Evaluator.Epsilon)
                return double.PositiveInfinity;

            var newA = Segment.Concat(m, a.Prefix(i), a.Suffix(i + k));
            var newB = Segment.Concat(m, b.Prefix(p), chain, b.Suffix(p));
            return a.CostIf(newA, a.Count - k) - a.Cost + b.CostIf(newB, b.Count + k) - b.Cost;
        }

        if (p >= i && p <= i + k)
            return 0;

        var route = p < i
            ? Segment.Concat(m, a.Prefix(p), chain, a.Fold(p, i), a.Suffix(i + k))
            : Segment.Concat(m, a.Prefix(i), a.Fold(i + k, p), chain, a.Suffix(p));
        return a.CostIf(route, a.Count) - a.Cost;
    }

    public static void ApplyRelocate(RouteState a, int i, int k, RouteState b, int p)
    {
        var moved = a.Visits.GetRange(i, k);
        a.Visits.RemoveRange(i, k);
        b.Visits.InsertRange(ReferenceEquals(a, b) && p > i ? p - k : p, moved);

        a.Update();
        if (!ReferenceEquals(a, b))
            b.Update();
    }

    /// <summary>Exchanges visit i of A with visit j of B (different routes).</summary>
    public static double SwapDelta(RouteState a, int i, RouteState b, int j)
    {
        var m = a.Matrix;
        var u = a.NodeAt(i);
        var v = b.NodeAt(j);

        var newA = Segment.Concat(m, a.Prefix(i), v, a.Suffix(i + 1));
        double deltaA = a.CostIf(newA, a.Count) - a.Cost;
        if (double.IsInfinity(deltaA))
            return deltaA;

        var newB = Segment.Concat(m, b.Prefix(j), u, b.Suffix(j + 1));
        return deltaA + b.CostIf(newB, b.Count) - b.Cost;
    }

    public static void ApplySwap(RouteState a, int i, RouteState b, int j)
    {
        (a.Visits[i], b.Visits[j]) = (b.Visits[j], a.Visits[i]);
        a.Update();
        b.Update();
    }

    /// <summary>Tails move together with the end depot, so both vehicles must finish at the same place and time.</summary>
    public static bool CanExchangeTails(RouteState a, RouteState b) =>
        a.Vehicle.EndDepot == b.Vehicle.EndDepot && a.Vehicle.Shift == b.Vehicle.Shift;

    /// <summary>A keeps its first i visits and receives B's tail from j; B keeps its first j visits and receives A's tail from i.</summary>
    public static double TwoOptStarDelta(RouteState a, int i, RouteState b, int j)
    {
        var m = a.Matrix;
        var newA = Segment.Concat(m, a.Prefix(i), b.Suffix(j));
        double deltaA = a.CostIf(newA, i + b.Count - j) - a.Cost;
        if (double.IsInfinity(deltaA))
            return deltaA;

        var newB = Segment.Concat(m, b.Prefix(j), a.Suffix(i));
        return deltaA + b.CostIf(newB, j + a.Count - i) - b.Cost;
    }

    public static void ApplyTwoOptStar(RouteState a, int i, RouteState b, int j)
    {
        var tailA = a.Visits.GetRange(i, a.Count - i);
        var tailB = b.Visits.GetRange(j, b.Count - j);
        a.Visits.RemoveRange(i, tailA.Count);
        b.Visits.RemoveRange(j, tailB.Count);
        a.Visits.AddRange(tailB);
        b.Visits.AddRange(tailA);
        a.Update();
        b.Update();
    }

    /// <summary>Reverses visits i..j (inclusive) of one route.</summary>
    public static double TwoOptDelta(RouteState a, int i, int j)
    {
        var route = Segment.Concat(a.Matrix, a.Prefix(i), a.FoldReversed(i, j + 1), a.Suffix(j + 1));
        return a.CostIf(route, a.Count) - a.Cost;
    }

    public static void ApplyTwoOpt(RouteState a, int i, int j)
    {
        a.Visits.Reverse(i, j - i + 1);
        a.Update();
    }
}
