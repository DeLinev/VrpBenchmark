using VrpBenchmark.Algorithms.Routing;
using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Algorithms.Metaheuristics.Alns;

/// <summary>
/// Routes plus a "request bank" of customers that are currently not served.
/// Unserved customers are allowed during the search but cost <see cref="UnassignedPenalty"/> each.
/// </summary>
public sealed class PartialSolution
{
    public ProblemInstance Instance { get; }
    public List<RouteState> Routes { get; }
    public List<int> Unassigned { get; }

    /// <summary>Repair may open new routes only while there are fewer routes than this.</summary>
    public int MaxRoutes { get; set; }

    public double UnassignedPenalty { get; }

    public PartialSolution(ProblemInstance instance, IEnumerable<RouteState> routes, IEnumerable<int> unassigned, int maxRoutes, double unassignedPenalty)
    {
        Instance = instance;
        Routes = [.. routes];
        Unassigned = [.. unassigned];
        MaxRoutes = maxRoutes;
        UnassignedPenalty = unassignedPenalty;
    }

    public bool IsComplete => Unassigned.Count == 0;

    public double RouteCost => Routes.Sum(r => r.Cost);
    public double Cost => RouteCost + UnassignedPenalty * Unassigned.Count;

    public double Distance => Routes.Sum(r => r.Whole.Distance);

    public PartialSolution Clone() =>
        new(Instance, Routes.Select(r => r.Clone()), Unassigned, MaxRoutes, UnassignedPenalty);

    public Solution ToSolution() => new(Routes.Where(r => !r.IsEmpty).Select(r => r.ToRoute()), Unassigned);

    /// <summary>Compares the nonempty search routes regardless of fleet list order.
    /// Unassigned customers are the complement of the visits in the same instance.</summary>
    public bool HasSameRoutesAs(PartialSolution other)
    {
        if (Instance != other.Instance || Routes.Count != other.Routes.Count || Unassigned.Count != other.Unassigned.Count)
            return false;

        for (int i = 0; i < Routes.Count; i++)
        {
            var route = Routes[i];
            var match = other.Routes[i];
            // A route rebuilt by repair can move to the end. Its first customer uniquely identifies it.
            if (match.Visits[0] != route.Visits[0])
                match = other.Routes.Find(r => r.Visits[0] == route.Visits[0]);

            if (match is null || route.VehicleTypeIndex != match.VehicleTypeIndex || !route.Visits.SequenceEqual(match.Visits))
                return false;
        }
        return true;
    }

    /// <summary>Removes the given customers from their routes and puts them into the request bank.</summary>
    public void Remove(IReadOnlyCollection<int> customers)
    {
        var set = customers as HashSet<int> ?? [.. customers];
        foreach (var route in Routes)
        {
            if (route.Visits.RemoveAll(set.Contains) > 0)
                route.Update();
        }

        Routes.RemoveAll(r => r.IsEmpty);
        Unassigned.AddRange(customers);
    }

    public IEnumerable<int> AssignedCustomers() => Routes.SelectMany(r => r.Visits);

    /// <summary>Cheapest feasible position for <paramref name="customer"/> in <paramref name="route"/>, or (+∞, −1).</summary>
    public static (double Delta, int Position) BestInsertion(RouteState route, in Segment customer)
    {
        if (route.Load + customer.Load > route.Vehicle.Capacity + Evaluator.Epsilon)
            return (double.PositiveInfinity, -1);

        var matrix = route.Matrix;
        double best = double.PositiveInfinity;
        int position = -1;

        for (int p = 0; p <= route.Count; p++)
        {
            var candidate = Segment.Concat(matrix, route.Prefix(p), customer, route.Suffix(p));
            double delta = route.CostIf(candidate, route.Count + 1) - route.Cost;
            if (delta < best)
            {
                best = delta;
                position = p;
            }
        }

        return (best, position);
    }

    /// <summary>One available type per distinct set of routing constraints and tariffs.</summary>
    internal IEnumerable<int> FreeVehicleTypes()
    {
        var types = Instance.VehicleTypes;
        if (types.Count == 1)
        {
            if (Routes.Count < types[0].Count)
                yield return 0;
            yield break;
        }

        var used = new int[types.Count];
        foreach (var route in Routes)
            used[route.VehicleTypeIndex]++;

        var seen = new HashSet<VehicleType>();
        for (int i = 0; i < types.Count; i++)
            if (used[i] < types[i].Count && seen.Add(types[i] with { Id = "", Count = 1 }))
                yield return i;
    }

    /// <summary>Cheapest feasible singleton route that can be added to the current fleet.</summary>
    public double NewRouteCost(int customer)
    {
        if (Routes.Count >= MaxRoutes)
            return double.PositiveInfinity;

        return FreeVehicleTypes().Select(t => new RouteState(Instance, t, [customer]).Cost)
            .DefaultIfEmpty(double.PositiveInfinity).Min();
    }
}
