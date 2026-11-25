using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Core.Evaluation;

/// <param name="Overload">Amount by which the load exceeds the vehicle capacity (0 when feasible).</param>
/// <param name="Lateness">Total time by which service starts after window ends, including the late return to the depot.</param>
/// <param name="LateNodes">Number of visits (including the end depot) that violate their time window.</param>
public readonly record struct RouteStats(
    double Distance,
    double Duration,
    double Load,
    double Overload,
    double Lateness,
    int LateNodes,
    double Cost)
{
    public double VolumeOverload { get; init; }
    public bool IsFeasible => Overload <= Evaluator.Epsilon && VolumeOverload <= Evaluator.Epsilon && LateNodes == 0;
}

public sealed record SolutionStats(
    int VehiclesUsed,
    double Distance,
    double Duration,
    double Cost,
    double Overload,
    double Lateness,
    int LateNodes)
{
    public double VolumeOverload { get; init; }
}

/// <summary>
/// Reference evaluation of routes from scratch. Algorithms may use faster incremental evaluation,
/// but their final results are always re-checked with this class and <see cref="Validation.SolutionValidator"/>.
/// </summary>
public static class Evaluator
{
    public const double Epsilon = 1e-6;

    public static RouteStats Evaluate(ProblemInstance instance, Route route)
    {
        if (route.IsEmpty)
            return default;

        var vehicle = instance.VehicleTypes[route.VehicleType];
        var matrix = instance.Matrix;
        var nodes = instance.Nodes;

        // The vehicle departs as early as possible; postponing the departure to cut waiting time is not modelled.
        double startTime = Math.Max(vehicle.Shift.Start, nodes[vehicle.StartDepot].Window.Start);
        double time = startTime;
        double distance = 0, load = 0, volume = 0, lateness = 0;
        double departureDeadline = Math.Min(vehicle.Shift.End, nodes[vehicle.StartDepot].Window.End);
        lateness = Math.Max(0, startTime - departureDeadline);
        int lateNodes = lateness > Epsilon ? 1 : 0;
        int previous = vehicle.StartDepot;

        foreach (int current in route.Visits)
        {
            var node = nodes[current];
            distance += matrix.Distance(previous, current);
            time = Math.Max(time + matrix.Time(previous, current), node.Window.Start);

            if (time > node.Window.End + Epsilon)
            {
                lateness += time - node.Window.End;
                lateNodes++;
            }

            time += node.ServiceTime;
            load += node.Demand;
            volume += node.Volume;
            previous = current;
        }

        distance += matrix.Distance(previous, vehicle.EndDepot);
        time += matrix.Time(previous, vehicle.EndDepot);
        time = Math.Max(time, nodes[vehicle.EndDepot].Window.Start);

        double returnDeadline = Math.Min(vehicle.Shift.End, nodes[vehicle.EndDepot].Window.End);
        if (time > returnDeadline + Epsilon)
        {
            lateness += time - returnDeadline;
            lateNodes++;
        }

        double duration = time - startTime;
        double cost = vehicle.FixedCost + vehicle.CostPerDistance * distance + vehicle.CostPerTime * duration;

        return new RouteStats(distance, duration, load, Math.Max(0, load - vehicle.Capacity), lateness, lateNodes, cost)
        { VolumeOverload = Math.Max(0, volume - vehicle.VolumeCapacity) };
    }

    public static SolutionStats Evaluate(ProblemInstance instance, Solution solution)
    {
        int vehicles = 0, lateNodes = 0;
        double distance = 0, duration = 0, cost = 0, overload = 0, volumeOverload = 0, lateness = 0;

        foreach (var route in solution.UsedRoutes)
        {
            var stats = Evaluate(instance, route);
            vehicles++;
            distance += stats.Distance;
            duration += stats.Duration;
            cost += stats.Cost;
            overload += stats.Overload;
            volumeOverload += stats.VolumeOverload;
            lateness += stats.Lateness;
            lateNodes += stats.LateNodes;
        }

        return new SolutionStats(vehicles, distance, duration, cost, overload, lateness, lateNodes)
        { VolumeOverload = volumeOverload };
    }
}
