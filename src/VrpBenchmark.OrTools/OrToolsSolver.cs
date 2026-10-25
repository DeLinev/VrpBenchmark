using System.Diagnostics;
using Google.OrTools.ConstraintSolver;
using Google.Protobuf.WellKnownTypes;
using VrpBenchmark.Algorithms;
using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.OrTools;

/// <summary>
/// Google OR-Tools routing solver as an industrial reference: a first solution by parallel cheapest insertion,
/// improved by guided local search until the time limit. OR-Tools works with integers, so distances and times
/// are scaled by <see cref="Scale"/>; travel times are rounded up so that every OR-Tools solution stays
/// feasible for the exact (double) model.
/// </summary>
public sealed class OrToolsSolver : ISolver
{
    public const long Scale = 1000;

    public static string LibraryVersion => typeof(RoutingModel).Assembly.GetName().Version?.ToString() ?? "unknown";

    public string Name => "OR-Tools";

    public SolverResult Solve(ProblemInstance instance, SolverOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        var nodes = instance.Nodes;
        var matrix = instance.Matrix;

        var vehicles = instance.VehicleTypes
            .SelectMany((type, index) => Enumerable.Repeat((Type: type, Index: index), type.Count))
            .ToArray();
        int[] starts = [.. vehicles.Select(v => v.Type.StartDepot)];
        int[] ends = [.. vehicles.Select(v => v.Type.EndDepot)];

        var manager = new RoutingIndexManager(nodes.Count, vehicles.Length, starts, ends);
        var routing = new RoutingModel(manager);

        // Arc costs and fixed costs per vehicle type.
        var costCallbacks = instance.VehicleTypes.Select(type => routing.RegisterTransitCallback((long from, long to) =>
            (long)Math.Round(type.CostPerDistance * matrix.Distance(manager.IndexToNode(from), manager.IndexToNode(to)) * Scale))).ToArray();
        for (int v = 0; v < vehicles.Length; v++)
        {
            routing.SetArcCostEvaluatorOfVehicle(costCallbacks[vehicles[v].Index], v);
            routing.SetFixedCostOfVehicle((long)Math.Round(vehicles[v].Type.FixedCost * Scale), v);
        }

        // Time: transit from i to j = service at i + travel time i → j.
        int timeCallback = routing.RegisterTransitCallback((long from, long to) =>
        {
            int i = manager.IndexToNode(from), j = manager.IndexToNode(to);
            double service = nodes[i].Kind == NodeKind.Customer ? nodes[i].ServiceTime : 0;
            return (long)Math.Ceiling((service + matrix.Time(i, j)) * Scale - 1e-6);
        });

        long horizon = (long)Math.Ceiling(Math.Max(
            nodes.Max(n => n.Window.End),
            instance.VehicleTypes.Max(t => t.Shift.End)) * Scale);
        routing.AddDimension(timeCallback, horizon, horizon, false, "Time");
        var time = routing.GetMutableDimension("Time");

        foreach (int c in instance.Customers)
            time.CumulVar(manager.NodeToIndex(c)).SetRange(Scaled(nodes[c].Window.Start), Floor(nodes[c].Window.End));

        for (int v = 0; v < vehicles.Length; v++)
        {
            var type = vehicles[v].Type;
            var start = nodes[type.StartDepot].Window;
            var end = nodes[type.EndDepot].Window;
            time.CumulVar(routing.Start(v)).SetRange(Scaled(Math.Max(start.Start, type.Shift.Start)), Floor(Math.Min(start.End, type.Shift.End)));
            time.CumulVar(routing.End(v)).SetRange(Scaled(Math.Max(end.Start, type.Shift.Start)), Floor(Math.Min(end.End, type.Shift.End)));
        }

        // Capacity (demands are scaled too, so fractional demands are supported).
        int demandCallback = routing.RegisterUnaryTransitCallback((long from) =>
            Scaled(nodes[manager.IndexToNode(from)].Demand));
        long[] capacities = [.. vehicles.Select(v => Floor(v.Type.Capacity))];
        routing.AddDimensionWithVehicleCapacity(demandCallback, 0, capacities, true, "Capacity");

        var history = new List<ProgressPoint>();
        // The callback fires for every accepted solution, not only improving ones, so only new bests are kept.
        double bestSoFar = double.PositiveInfinity;
        routing.AddAtSolutionCallback(() =>
        {
            double cost = routing.CostVar().Value() / (double)Scale;
            if (cost < bestSoFar - 1e-9)
            {
                bestSoFar = cost;
                history.Add(new ProgressPoint(stopwatch.Elapsed, cost));
            }
        });

        var parameters = operations_research_constraint_solver.DefaultRoutingSearchParameters();
        parameters.FirstSolutionStrategy = FirstSolutionStrategy.Types.Value.ParallelCheapestInsertion;
        parameters.LocalSearchMetaheuristic = LocalSearchMetaheuristic.Types.Value.GuidedLocalSearch;
        var remaining = options.TimeLimit - stopwatch.Elapsed;
        parameters.TimeLimit = Duration.FromTimeSpan(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));

        var assignment = routing.SolveWithParameters(parameters)
            ?? throw new InvalidOperationException($"OR-Tools found no solution for {instance.Name} within the time limit.");

        var solution = new Solution();
        for (int v = 0; v < vehicles.Length; v++)
        {
            var visits = new List<int>();
            for (long index = assignment.Value(routing.NextVar(routing.Start(v))); !routing.IsEnd(index); index = assignment.Value(routing.NextVar(index)))
                visits.Add(manager.IndexToNode(index));

            if (visits.Count > 0)
                solution.Routes.Add(new Route(vehicles[v].Index, visits));
        }

        // The integer objective is rounded; the last point carries the exact cost like for the other solvers.
        history.Add(new ProgressPoint(stopwatch.Elapsed, Evaluator.Evaluate(instance, solution).Cost));
        return new SolverResult(solution, stopwatch.Elapsed, history);
    }

    private static long Scaled(double value) => (long)Math.Ceiling(value * Scale - 1e-6);

    private static long Floor(double value) => (long)Math.Floor(value * Scale + 1e-6);
}
