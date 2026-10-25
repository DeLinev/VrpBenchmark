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

    public double Cost => Routes.Sum(r => r.Cost) + UnassignedPenalty * Unassigned.Count;

    public double Distance => Routes.Sum(r => r.Whole.Distance);

    public PartialSolution Clone() =>
        new(Instance, Routes.Select(r => r.Clone()), Unassigned, MaxRoutes, UnassignedPenalty);

    public Solution ToSolution() => new(Routes.Where(r => !r.IsEmpty).Select(r => r.ToRoute()));

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

    /// <summary>Cost of serving the customer alone with a new vehicle of type 0, or +∞ if no vehicle is available.</summary>
    public double NewRouteCost(int customer)
    {
        if (Routes.Count >= MaxRoutes)
            return double.PositiveInfinity;

        var route = new RouteState(Instance, 0, [customer]);
        return route.Cost;
    }
}
