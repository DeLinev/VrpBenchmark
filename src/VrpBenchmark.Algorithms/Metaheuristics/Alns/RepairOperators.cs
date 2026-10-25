using VrpBenchmark.Algorithms.Routing;

namespace VrpBenchmark.Algorithms.Metaheuristics.Alns;

public interface IRepairOperator
{
    string Name { get; }

    /// <summary>Inserts customers from the request bank; those that fit nowhere stay there.</summary>
    void Repair(PartialSolution solution, Random random);
}

/// <summary>
/// Insertion heuristic parameterised by the regret degree k.
/// k = 1: greedy — always insert the customer with the globally cheapest insertion.
/// k ≥ 2: regret-k — insert the customer that loses most if it is not inserted now, measured as
/// Σ_{h=2..k} (c_h − c_1), where c_h is its best insertion cost into its h-th best route.
/// Customers with fewer than k feasible routes get the highest priority.
/// </summary>
public sealed class InsertionRepair(int regret) : IRepairOperator
{
    private const double Missing = 1e9;

    public string Name => regret == 1 ? "greedy" : $"regret-{regret}";

    public void Repair(PartialSolution solution, Random random)
    {
        var instance = solution.Instance;
        var pending = solution.Unassigned;
        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending));

        var segments = pending.Select(c => Segment.ForNode(instance.Nodes[c])).ToList();

        // costs[r][u] / positions[r][u]: best insertion of pending customer u into route r.
        var costs = new List<double[]>();
        var positions = new List<int[]>();
        for (int r = 0; r < solution.Routes.Count; r++)
            AddColumn(solution.Routes[r], segments, costs, positions);

        while (pending.Count > 0)
        {
            int chosen = -1, chosenRoute = -1;
            double chosenKey = double.NegativeInfinity, chosenBest = double.PositiveInfinity;

            for (int u = 0; u < pending.Count; u++)
            {
                double newRoute = solution.NewRouteCost(pending[u]);
                var (best, bestRoute, regretValue) = Rank(costs, u, newRoute);
                if (double.IsInfinity(best))
                    continue;

                double key = regret == 1 ? -best : regretValue;
                if (key > chosenKey || (key == chosenKey && best < chosenBest))
                {
                    chosenKey = key;
                    chosenBest = best;
                    chosen = u;
                    chosenRoute = bestRoute;
                }
            }

            if (chosen < 0)
                break;

            int customer = pending[chosen];
            if (chosenRoute == costs.Count)
            {
                solution.Routes.Add(new RouteState(instance, 0, [customer]));
                RemovePending(chosen, pending, segments, costs, positions);
                AddColumn(solution.Routes[^1], segments, costs, positions);
            }
            else
            {
                var route = solution.Routes[chosenRoute];
                route.Visits.Insert(positions[chosenRoute][chosen], customer);
                route.Update();
                RemovePending(chosen, pending, segments, costs, positions);
                UpdateColumn(route, segments, costs[chosenRoute], positions[chosenRoute]);
            }
        }
    }

    /// <summary>Best cost, the route giving it (Routes.Count = new route) and the regret value of pending customer u.</summary>
    private (double Best, int Route, double Regret) Rank(List<double[]> costs, int u, double newRouteCost)
    {
        Span<double> top = stackalloc double[Math.Max(regret, 1)];
        top.Fill(double.PositiveInfinity);
        int bestRoute = -1;

        for (int r = 0; r <= costs.Count; r++)
        {
            double c = r < costs.Count ? costs[r][u] : newRouteCost;
            if (double.IsInfinity(c) || c >= top[^1])
                continue;

            if (c < top[0])
                bestRoute = r;

            int slot = top.Length - 1;
            while (slot > 0 && top[slot - 1] > c)
            {
                top[slot] = top[slot - 1];
                slot--;
            }
            top[slot] = c;
        }

        double regretValue = 0;
        for (int h = 1; h < top.Length; h++)
            regretValue += (double.IsInfinity(top[h]) ? Missing : top[h]) - top[0];

        return (top[0], bestRoute, regretValue);
    }

    private static void AddColumn(RouteState route, List<Segment> segments, List<double[]> costs, List<int[]> positions)
    {
        var c = new double[segments.Count];
        var p = new int[segments.Count];
        UpdateColumn(route, segments, c, p);
        costs.Add(c);
        positions.Add(p);
    }

    private static void UpdateColumn(RouteState route, List<Segment> segments, double[] costs, int[] positions)
    {
        for (int u = 0; u < segments.Count; u++)
            (costs[u], positions[u]) = PartialSolution.BestInsertion(route, segments[u]);
    }

    /// <summary>Swap-removes pending entry u so that column arrays stay aligned with the pending list.</summary>
    private static void RemovePending(int u, List<int> pending, List<Segment> segments, List<double[]> costs, List<int[]> positions)
    {
        int last = pending.Count - 1;
        pending[u] = pending[last];
        segments[u] = segments[last];
        pending.RemoveAt(last);
        segments.RemoveAt(last);

        for (int r = 0; r < costs.Count; r++)
        {
            costs[r][u] = costs[r][last];
            positions[r][u] = positions[r][last];
        }
    }
}
