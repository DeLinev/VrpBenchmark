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
public sealed class InsertionRepair(int regret, bool randomOrder = false) : IRepairOperator
{
    private const double Missing = 1e9;

    public string Name => randomOrder ? "random-order" : regret == 1 ? "greedy" : $"regret-{regret}";

    public void Repair(PartialSolution solution, Random random)
    {
        var instance = solution.Instance;
        var pending = solution.Unassigned;
        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending));

        var segments = pending.Select(c => Segment.ForNode(instance.Nodes[c])).ToList();

        var routes = solution.Routes;
        var columns = BuildColumns();

        while (pending.Count > 0)
        {
            int chosen = -1, chosenColumn = -1;
            double chosenKey = double.NegativeInfinity, chosenBest = double.PositiveInfinity;
            for (int u = 0; u < pending.Count; u++)
            {
                var (best, column, regretValue) = Rank(columns, u);
                if (!double.IsFinite(best))
                    continue;
                double key = regret == 1 ? -best : regretValue;
                if (key > chosenKey || (key == chosenKey && best < chosenBest))
                {
                    chosenKey = key;
                    chosenBest = best;
                    chosen = u;
                    chosenColumn = column;
                    // The bank was shuffled. Trying a different order first can displace a cheap, bulky order.
                    if (randomOrder)
                        break;
                }
            }
            if (chosen < 0)
                break;

            var option = columns[chosenColumn];
            int position = option.Positions[chosen];
            int customer = pending[chosen];
            bool newRoute = option.Route == routes.Count;
            bool crewChanged = newRoute || routes[option.Route].VehicleTypeIndex != option.Type;
            if (newRoute)
                routes.Add(new RouteState(instance, option.Type, [customer]));
            else
            {
                var route = routes[option.Route];
                if (crewChanged)
                    routes[option.Route] = route = new RouteState(instance, option.Type, route.Visits);
                route.Visits.Insert(position, customer);
                route.Update();
            }

            int last = pending.Count - 1;
            pending[chosen] = pending[last];
            segments[chosen] = segments[last];
            pending.RemoveAt(last);
            segments.RemoveAt(last);
            if (pending.Count == 0)
                break;

            if (crewChanged)
                columns = BuildColumns();
            else
                foreach (var column in columns)
                {
                    column.Costs[chosen] = column.Costs[last];
                    column.Positions[chosen] = column.Positions[last];
                    if (column.Route == option.Route)
                        Refresh(column);
                }
        }

        List<InsertionColumn> BuildColumns()
        {
            var result = new List<InsertionColumn>();
            var free = solution.FreeVehicleTypes().ToArray();
            for (int i = 0; i < routes.Count; i++)
            {
                Add(i, routes[i].VehicleTypeIndex);
                foreach (int t in free)
                    if (t != routes[i].VehicleTypeIndex && !routes[i].Vehicle.HasSameRoutingProfile(instance.VehicleTypes[t]))
                        Add(i, t);
            }
            if (routes.Count < solution.MaxRoutes)
                foreach (int t in free)
                    Add(routes.Count, t);
            return result;

            void Add(int route, int type)
            {
                var column = new InsertionColumn(route, type, new double[pending.Count], new int[pending.Count]);
                Refresh(column);
                result.Add(column);
            }
        }

        void Refresh(InsertionColumn column)
        {
            var original = column.Route < routes.Count ? routes[column.Route] : null;
            var route = original?.VehicleTypeIndex == column.Type ? original :
                new RouteState(instance, column.Type, original?.Visits ?? []);
            double crewDelta = route.Cost - (original?.Cost ?? 0);
            for (int u = 0; u < pending.Count; u++)
            {
                if (!double.IsFinite(crewDelta))
                    column.Costs[u] = double.PositiveInfinity;
                else
                {
                    var (delta, position) = PartialSolution.BestInsertion(route, segments[u]);
                    column.Costs[u] = delta + crewDelta;
                    column.Positions[u] = position;
                }
            }
        }
    }

    private sealed record InsertionColumn(int Route, int Type, double[] Costs, int[] Positions);

    private (double Best, int Route, double Regret) Rank(List<InsertionColumn> columns, int u)
    {
        Span<double> top = stackalloc double[Math.Max(regret, 1)];
        top.Fill(double.PositiveInfinity);
        int bestRoute = -1;

        for (int r = 0; r < columns.Count; r++)
        {
            double c = columns[r].Costs[u];
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

}
