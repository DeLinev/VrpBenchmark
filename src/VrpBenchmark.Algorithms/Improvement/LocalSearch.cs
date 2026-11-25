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

        return new Solution(_routes.Where(r => !r.IsEmpty).Select(r => r.ToRoute()), start.Unassigned);
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
                    double removalDelta = A.CostIf(withoutChain, A.Count - k) - A.Cost;

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
                                double delta = removalDelta + B.CostIf(newB, B.Count + k) - B.Cost;
                                if (delta < bestDelta)
                                {
                                    bestDelta = delta;
                                    best = (a, i, k, b, p);
                                }
                            }
                        }
                    }

                    // Same route: the chain jumps over a "middle" part that grows one customer at a time,
                    // which keeps every evaluation O(1) instead of re-folding the middle part.
                    var middle = default(Segment);
                    for (int p = i - 1; p >= 0; p--)
                    {
                        middle = p == i - 1 ? A.NodeAt(p) : Segment.Concat(_matrix, A.NodeAt(p), middle);
                        var route = Segment.Concat(_matrix, A.Prefix(p), chain, middle, A.Suffix(i + k));
                        double delta = A.CostIf(route, A.Count) - A.Cost;
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
                        double delta = A.CostIf(route, A.Count) - A.Cost;
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

        Moves.ApplyRelocate(_routes[best.A], best.I, best.K, _routes[best.B], best.P);
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
                    double demandU = _instance.Nodes[A.Visits[i]].Demand;
                    for (int j = 0; j < B.Count; j++)
                    {
                        double demandV = _instance.Nodes[B.Visits[j]].Demand;
                        if (A.Load - demandU + demandV > A.Vehicle.Capacity + Evaluator.Epsilon ||
                            B.Load - demandV + demandU > B.Vehicle.Capacity + Evaluator.Epsilon)
                            continue;

                        double delta = Moves.SwapDelta(A, i, B, j);
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

        Moves.ApplySwap(_routes[best.A], best.I, _routes[best.B], best.J);
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
                if (!Moves.CanExchangeTails(A, B))
                    continue;

                for (int i = 0; i <= A.Count; i++)
                {
                    for (int j = 0; j <= B.Count; j++)
                    {
                        if ((i == 0 && j == 0) || (i == A.Count && j == B.Count))
                            continue;

                        double delta = Moves.TwoOptStarDelta(A, i, B, j);
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

        Moves.ApplyTwoOptStar(_routes[best.A], best.I, _routes[best.B], best.J);
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
                // The reversed part grows by prepending one customer at a time.
                var reversed = A.NodeAt(i);
                for (int j = i + 1; j < A.Count; j++)
                {
                    reversed = Segment.Concat(_matrix, A.NodeAt(j), reversed);
                    var route = Segment.Concat(_matrix, A.Prefix(i), reversed, A.Suffix(j + 1));
                    double delta = A.CostIf(route, A.Count) - A.Cost;
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

        Moves.ApplyTwoOpt(_routes[best.A], best.I, best.J);
        Stats.TwoOpt++;
        return true;
    }
}
