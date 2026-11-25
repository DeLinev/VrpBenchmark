using System.Diagnostics;
using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Algorithms.Construction;

public enum SeedRule
{
    /// <summary>Unrouted customer farthest from the depot.</summary>
    Farthest,

    /// <summary>Unrouted customer with the earliest window end.</summary>
    EarliestDeadline,
}

/// <param name="Mu">Weight of the removed edge (i, j) in the detour criterion c11.</param>
/// <param name="Lambda">How strongly customers far from the depot are preferred (c2 = λ·d0u − c1).</param>
/// <param name="Alpha1">Weight of the detour c11; the weight of the time shift c12 is 1 − Alpha1.</param>
public sealed record I1Parameters(double Mu, double Lambda, double Alpha1, SeedRule Seed)
{
    /// <summary>Parameter grid in the spirit of Solomon (1987); the best of all runs is kept.</summary>
    public static IReadOnlyList<I1Parameters> DefaultGrid { get; } =
    [
        .. from seed in new[] { SeedRule.Farthest, SeedRule.EarliestDeadline }
           from lambda in new[] { 1.0, 2.0 }
           from alpha1 in new[] { 1.0, 0.5, 0.0 }
           select new I1Parameters(1.0, lambda, alpha1, seed),
    ];
}

/// <summary>
/// Sequential insertion heuristic I1 (Solomon, 1987). Builds one route at a time: starts it with a seed customer,
/// then repeatedly inserts the customer with the best insertion criterion until no customer fits.
/// Homogeneous fleet only: all routes use vehicle type 0.
/// </summary>
public sealed class SolomonI1 : ISolver
{
    private readonly IReadOnlyList<I1Parameters> _grid;

    public SolomonI1(IReadOnlyList<I1Parameters>? grid = null) => _grid = grid ?? I1Parameters.DefaultGrid;

    public string Name => "I1";

    public SolverResult Solve(ProblemInstance instance, SolverOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        Solution? best = null;
        double bestCost = double.PositiveInfinity;
        var history = new List<ProgressPoint>();

        foreach (var parameters in _grid)
        {
            var solution = Build(instance, parameters);
            double cost = Evaluator.Evaluate(instance, solution).Cost;
            if (cost < bestCost)
            {
                best = solution;
                bestCost = cost;
                history.Add(new ProgressPoint(stopwatch.Elapsed, cost));
            }
        }

        return new SolverResult(best!, stopwatch.Elapsed, history);
    }

    public static Solution Build(ProblemInstance instance, I1Parameters p)
    {
        const int vehicleType = 0;
        var vehicle = instance.VehicleTypes[vehicleType];
        var nodes = instance.Nodes;
        var matrix = instance.Matrix;
        int depot = vehicle.StartDepot;

        var unrouted = new HashSet<int>(instance.Customers);
        var solution = new Solution();

        while (unrouted.Count > 0)
        {
            int seed = p.Seed == SeedRule.Farthest
                ? unrouted.MaxBy(c => matrix.Distance(depot, c))
                : unrouted.MinBy(c => nodes[c].Window.End);
            unrouted.Remove(seed);

            var route = new List<int> { seed };
            double load = nodes[seed].Demand;
            double volume = nodes[seed].Volume;

            while (true)
            {
                var schedule = new Schedule(instance, vehicle, route);
                int bestCustomer = -1, bestPosition = -1;
                double bestC2 = double.NegativeInfinity;

                foreach (int u in unrouted)
                {
                    if (load + nodes[u].Demand > vehicle.Capacity + Evaluator.Epsilon ||
                        volume + nodes[u].Volume > vehicle.VolumeCapacity + Evaluator.Epsilon)
                        continue;

                    var (position, c1) = BestInsertion(instance, schedule, u, p);
                    if (position < 0)
                        continue;

                    double c2 = p.Lambda * matrix.Distance(depot, u) - c1;
                    if (c2 > bestC2)
                    {
                        bestC2 = c2;
                        bestCustomer = u;
                        bestPosition = position;
                    }
                }

                if (bestCustomer < 0)
                    break;

                route.Insert(bestPosition, bestCustomer);
                load += nodes[bestCustomer].Demand;
                volume += nodes[bestCustomer].Volume;
                unrouted.Remove(bestCustomer);
            }

            solution.Routes.Add(new Route(vehicleType, route));
        }

        return solution;
    }

    /// <summary>Returns the visit index to insert at and the criterion c1, or (-1, ∞) if no position is feasible.</summary>
    private static (int Position, double C1) BestInsertion(ProblemInstance instance, Schedule s, int u, I1Parameters p)
    {
        var matrix = instance.Matrix;
        var nu = instance.Nodes[u];
        int bestPosition = -1;
        double bestC1 = double.PositiveInfinity;

        // Insert between schedule points k and k + 1 (point 0 is the start depot, the last one is the end depot).
        for (int k = 0; k < s.Length - 1; k++)
        {
            int i = s.Node[k], j = s.Node[k + 1];

            double startU = Math.Max(s.Start[k] + s.Service[k] + matrix.Time(i, u), nu.Window.Start);
            if (startU > nu.Window.End + Evaluator.Epsilon)
                continue;

            double newStartJ = Math.Max(startU + nu.ServiceTime + matrix.Time(u, j), s.Ready[k + 1]);
            if (newStartJ > s.Latest[k + 1] + Evaluator.Epsilon)
                continue;

            double c11 = matrix.Distance(i, u) + matrix.Distance(u, j) - p.Mu * matrix.Distance(i, j);
            double c12 = newStartJ - s.Start[k + 1];
            double c1 = p.Alpha1 * c11 + (1 - p.Alpha1) * c12;

            if (c1 < bestC1)
            {
                bestC1 = c1;
                bestPosition = k;
            }
        }

        return (bestPosition, bestC1);
    }

    /// <summary>
    /// Earliest service start times and latest feasible start times for depot → route → depot.
    /// Inserting a customer is feasible iff it is served within its window and the following point
    /// is still reached no later than its latest start.
    /// </summary>
    private sealed class Schedule
    {
        public int Length { get; }
        public int[] Node { get; }
        public double[] Start { get; }
        public double[] Latest { get; }
        public double[] Ready { get; }
        public double[] Service { get; }

        public Schedule(ProblemInstance instance, VehicleType vehicle, List<int> route)
        {
            var nodes = instance.Nodes;
            var matrix = instance.Matrix;
            Length = route.Count + 2;
            Node = new int[Length];
            Start = new double[Length];
            Latest = new double[Length];
            Ready = new double[Length];
            Service = new double[Length];

            Node[0] = vehicle.StartDepot;
            Node[^1] = vehicle.EndDepot;
            route.CopyTo(Node, 1);

            for (int k = 0; k < Length; k++)
            {
                var node = nodes[Node[k]];
                bool depot = k == 0 || k == Length - 1;
                Ready[k] = depot ? Math.Max(node.Window.Start, vehicle.Shift.Start) : node.Window.Start;
                Latest[k] = depot ? Math.Min(node.Window.End, vehicle.Shift.End) : node.Window.End;
                Service[k] = depot ? 0 : node.ServiceTime;
            }

            Start[0] = Ready[0];
            for (int k = 1; k < Length; k++)
                Start[k] = Math.Max(Start[k - 1] + Service[k - 1] + matrix.Time(Node[k - 1], Node[k]), Ready[k]);

            for (int k = Length - 2; k >= 0; k--)
                Latest[k] = Math.Min(Latest[k], Latest[k + 1] - Service[k] - matrix.Time(Node[k], Node[k + 1]));
        }
    }
}
