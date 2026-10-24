using VrpBenchmark.Algorithms.Routing;
using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Algorithms.Improvement;

public sealed class LocalSearchStats
{
    public int Relocate { get; internal set; }
    public int Swap { get; internal set; }
    public int TwoOptStar { get; internal set; }
    public int TwoOpt { get; internal set; }

    public int Total => Relocate + Swap + TwoOptStar + TwoOpt;
}

/// <summary>
/// Variable neighbourhood descent over four classic VRPTW neighbourhoods. Each pass scans one neighbourhood
/// completely, applies its best improving move and restarts from the first neighbourhood; the search stops
/// in a solution that no single move can improve (a local optimum). Only feasible solutions are visited.
/// </summary>
public sealed class LocalSearch
{
    private const double MinImprovement = 1e-7;

    private readonly ProblemInstance _instance;
    private readonly TravelMatrix _matrix;
    private List<RouteState> _routes = [];

    /// <summary>Longest chain of consecutive customers moved by relocate (1 = plain relocate, 2–3 = or-opt).</summary>
    public int MaxChainLength { get; init; } = 3;

    public LocalSearchStats Stats { get; private set; } = new();

    public LocalSearch(ProblemInstance instance)
    {
        if (instance.VehicleTypes.Any(v => v.CostPerTime != 0))
            throw new NotSupportedException("Time-dependent vehicle costs are not part of the search objective yet.");

        _instance = instance;
        _matrix = instance.Matrix;
    }

    public Solution Improve(Solution start)
    {
        _routes = [.. start.UsedRoutes.Select(r => new RouteState(_instance, r.VehicleType, r.Visits))];
        if (_routes.Any(r => double.IsInfinity(r.Cost)))
            throw new ArgumentException("Local search requires a feasible starting solution.", nameof(start));

        Stats = new LocalSearchStats();
        while (TryRelocate() || TrySwap() || TryTwoOptStar() || TryTwoOpt())
        {
        }

        return new Solution(_routes.Where(r => !r.IsEmpty).Select(r => r.ToRoute()));
    }

    /// <summary>Moves a chain of 1..MaxChainLength consecutive customers to another position, in the same or another route.</summary>
    private bool TryRelocate()
    {
        double bestDelta = -MinImprovement;
        (int A, int I, int K, int B, int P) best = (-1, 0, 0, 0, 0);

        for (int a = 0; a < _routes.Count; a++)
        {
            var A = _routes[a];
            for (int i = 0; i < A.Count; i++)
            {
                var chain = A.NodeAt(i);
                for (int k = 1; k <= MaxChainLength && i + k <= A.Count; k++)
                {
                    if (k > 1)
                        chain = Segment.Concat(_matrix, chain, A.NodeAt(i + k - 1));

                    var withoutChain = Segment.Concat(_matrix, A.Prefix(i), A.Suffix(i + k));
                    double removalDelta = RouteState.CostOf(A.Vehicle, withoutChain, A.Count - k) - A.Cost;

                    if (!double.IsInfinity(removalDelta))
                    {
                        for (int b = 0; b < _routes.Count; b++)
                        {
                            var B = _routes[b];
                            if (b == a || B.IsEmpty || B.Load + chain.Load > B.Vehicle.Capacity + Evaluator.Epsilon)
                                continue;

                            for (int p = 0; p <= B.Count; p++)
                            {
                                var newB = Segment.Concat(_matrix, B.Prefix(p), chain, B.Suffix(p));
                                double delta = removalDelta + RouteState.CostOf(B.Vehicle, newB, B.Count + k) - B.Cost;
                                if (delta < bestDelta)
                                {
                                    bestDelta = delta;
                                    best = (a, i, k, b, p);
                                }
                            }
                        }
                    }

                    // Same route: the chain jumps over a "middle" part that grows one customer at a time.
                    var middle = default(Segment);
                    for (int p = i - 1; p >= 0; p--)
                    {
                        middle = p == i - 1 ? A.NodeAt(p) : Segment.Concat(_matrix, A.NodeAt(p), middle);
                        var route = Segment.Concat(_matrix, A.Prefix(p), chain, middle, A.Suffix(i + k));
                        double delta = RouteState.CostOf(A.Vehicle, route, A.Count) - A.Cost;
                        if (delta < bestDelta)
                        {
                            bestDelta = delta;
                            best = (a, i, k, a, p);
                        }
                    }

                    for (int p = i + k + 1; p <= A.Count; p++)
                    {
                        middle = p == i + k + 1 ? A.NodeAt(i + k) : Segment.Concat(_matrix, middle, A.NodeAt(p - 1));
                        var route = Segment.Concat(_matrix, A.Prefix(i), middle, chain, A.Suffix(p));
                        double delta = RouteState.CostOf(A.Vehicle, route, A.Count) - A.Cost;
                        if (delta < bestDelta)
                        {
                            bestDelta = delta;
                            best = (a, i, k, a, p);
                        }
                    }
                }
            }
        }

        if (best.A < 0)
            return false;

        var from = _routes[best.A];
        var to = _routes[best.B];
        var moved = from.Visits.GetRange(best.I, best.K);
        from.Visits.RemoveRange(best.I, best.K);

        // P is a position in the original route; inside the same route it shifts once the chain is removed.
        int insertAt = best.A == best.B && best.P > best.I ? best.P - best.K : best.P;
        to.Visits.InsertRange(insertAt, moved);

        from.Update();
        if (best.B != best.A)
            to.Update();

        Stats.Relocate++;
        return true;
    }

    /// <summary>Exchanges two customers of different routes.</summary>
    private bool TrySwap()
    {
        double bestDelta = -MinImprovement;
        (int A, int I, int B, int J) best = (-1, 0, 0, 0);

        for (int a = 0; a < _routes.Count; a++)
        {
            var A = _routes[a];
            for (int b = a + 1; b < _routes.Count; b++)
            {
                var B = _routes[b];
                for (int i = 0; i < A.Count; i++)
                {
                    var u = A.NodeAt(i);
                    for (int j = 0; j < B.Count; j++)
                    {
                        var v = B.NodeAt(j);
                        if (A.Load - u.Load + v.Load > A.Vehicle.Capacity + Evaluator.Epsilon ||
                            B.Load - v.Load + u.Load > B.Vehicle.Capacity + Evaluator.Epsilon)
                            continue;

                        var newA = Segment.Concat(_matrix, A.Prefix(i), v, A.Suffix(i + 1));
                        double deltaA = RouteState.CostOf(A.Vehicle, newA, A.Count) - A.Cost;
                        if (double.IsInfinity(deltaA))
                            continue;

                        var newB = Segment.Concat(_matrix, B.Prefix(j), u, B.Suffix(j + 1));
                        double delta = deltaA + RouteState.CostOf(B.Vehicle, newB, B.Count) - B.Cost;
                        if (delta < bestDelta)
                        {
                            bestDelta = delta;
                            best = (a, i, b, j);
                        }
                    }
                }
            }
        }

        if (best.A < 0)
            return false;

        var ra = _routes[best.A];
        var rb = _routes[best.B];
        (ra.Visits[best.I], rb.Visits[best.J]) = (rb.Visits[best.J], ra.Visits[best.I]);
        ra.Update();
        rb.Update();

        Stats.Swap++;
        return true;
    }

    /// <summary>Cuts two routes and exchanges their tails: A = a1 + b2, B = b1 + a2.</summary>
    private bool TryTwoOptStar()
    {
        double bestDelta = -MinImprovement;
        (int A, int I, int B, int J) best = (-1, 0, 0, 0);

        for (int a = 0; a < _routes.Count; a++)
        {
            var A = _routes[a];
            for (int b = a + 1; b < _routes.Count; b++)
            {
                var B = _routes[b];

                // Tails carry their end depot with them, so both vehicles must finish at the same place and time.
                if (A.Vehicle.EndDepot != B.Vehicle.EndDepot || A.Vehicle.Shift != B.Vehicle.Shift)
                    continue;

                for (int i = 0; i <= A.Count; i++)
                {
                    for (int j = 0; j <= B.Count; j++)
                    {
                        if ((i == 0 && j == 0) || (i == A.Count && j == B.Count))
                            continue;

                        var newA = Segment.Concat(_matrix, A.Prefix(i), B.Suffix(j));
                        double deltaA = RouteState.CostOf(A.Vehicle, newA, i + B.Count - j) - A.Cost;
                        if (double.IsInfinity(deltaA))
                            continue;

                        var newB = Segment.Concat(_matrix, B.Prefix(j), A.Suffix(i));
                        double delta = deltaA + RouteState.CostOf(B.Vehicle, newB, j + A.Count - i) - B.Cost;
                        if (delta < bestDelta)
                        {
                            bestDelta = delta;
                            best = (a, i, b, j);
                        }
                    }
                }
            }
        }

        if (best.A < 0)
            return false;

        var ra = _routes[best.A];
        var rb = _routes[best.B];
        var tailA = ra.Visits.GetRange(best.I, ra.Count - best.I);
        var tailB = rb.Visits.GetRange(best.J, rb.Count - best.J);
        ra.Visits.RemoveRange(best.I, tailA.Count);
        rb.Visits.RemoveRange(best.J, tailB.Count);
        ra.Visits.AddRange(tailB);
        rb.Visits.AddRange(tailA);
        ra.Update();
        rb.Update();

        Stats.TwoOptStar++;
        return true;
    }

    /// <summary>Reverses a part of one route.</summary>
    private bool TryTwoOpt()
    {
        double bestDelta = -MinImprovement;
        (int A, int I, int J) best = (-1, 0, 0);

        for (int a = 0; a < _routes.Count; a++)
        {
            var A = _routes[a];
            for (int i = 0; i < A.Count - 1; i++)
            {
                var reversed = A.NodeAt(i);
                for (int j = i + 1; j < A.Count; j++)
                {
                    reversed = Segment.Concat(_matrix, A.NodeAt(j), reversed);
                    var route = Segment.Concat(_matrix, A.Prefix(i), reversed, A.Suffix(j + 1));
                    double delta = RouteState.CostOf(A.Vehicle, route, A.Count) - A.Cost;
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        best = (a, i, j);
                    }
                }
            }
        }

        if (best.A < 0)
            return false;

        var r = _routes[best.A];
        r.Visits.Reverse(best.I, best.J - best.I + 1);
        r.Update();

        Stats.TwoOpt++;
        return true;
    }
}
