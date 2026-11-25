using VrpBenchmark.Algorithms.Routing;

namespace VrpBenchmark.Algorithms.Metaheuristics.Alns;

/// <summary>Improves crew assignments while keeping each route's visit order fixed.</summary>
public static class CrewAssignment
{
    public static void Improve(PartialSolution solution)
    {
        var instance = solution.Instance;
        var routes = solution.Routes;
        if (instance.VehicleTypes.Count < 2 || routes.Count == 0)
            return;

        // Route stops remain fixed throughout this descent, so all assignment costs stay valid.
        var costs = new double[routes.Count, instance.VehicleTypes.Count];
        for (int i = 0; i < routes.Count; i++)
        {
            var visits = routes[i].Fold(0, routes[i].Count);
            for (int t = 0; t < instance.VehicleTypes.Count; t++)
            {
                var crew = instance.VehicleTypes[t];
                var start = Segment.ForDepot(instance.Nodes[crew.StartDepot], crew.Shift);
                var end = Segment.ForDepot(instance.Nodes[crew.EndDepot], crew.Shift);
                var trip = Segment.Concat(instance.Matrix, start, visits, end);
                costs[i, t] = RouteState.CostOf(crew, trip, routes[i].Count);
            }
        }

        while (TryImprove()) { }

        bool TryImprove()
        {
            const double tolerance = 1e-7;
            var free = solution.FreeVehicleTypes().ToArray();
            for (int i = 0; i < routes.Count; i++)
            {
                int a = routes[i].VehicleTypeIndex;
                foreach (int t in free)
                {
                    if (costs[i, t] < costs[i, a] - tolerance)
                    {
                        routes[i] = new RouteState(instance, t, routes[i].Visits);
                        return true;
                    }
                }
                for (int j = i + 1; j < routes.Count; j++)
                {
                    int b = routes[j].VehicleTypeIndex;
                    if (a != b && costs[i, b] + costs[j, a] < costs[i, a] + costs[j, b] - tolerance)
                    {
                        routes[i] = new RouteState(instance, b, routes[i].Visits);
                        routes[j] = new RouteState(instance, a, routes[j].Visits);
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
