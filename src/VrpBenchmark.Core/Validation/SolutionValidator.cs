using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Core.Validation;

public enum ViolationKind
{
    UnknownVehicleType,
    InvalidNode,
    DepotInsideRoute,
    CustomerNotVisited,
    CustomerVisitedTwice,
    FleetSizeExceeded,
    CapacityExceeded,
    TimeWindowMissed,
    LateReturnToDepot,
    VolumeExceeded,
}

public sealed record Violation(ViolationKind Kind, string Message);

public sealed class ValidationReport
{
    public IReadOnlyList<Violation> Violations { get; }

    /// <summary>Null when the solution is structurally broken (unknown vehicle type or node) and cannot be evaluated.</summary>
    public SolutionStats? Stats { get; }

    public bool IsFeasible => Violations.Count == 0;

    public ValidationReport(IReadOnlyList<Violation> violations, SolutionStats? stats)
    {
        Violations = violations;
        Stats = stats;
    }
}

/// <summary>
/// Checks every constraint independently of <see cref="Evaluator"/>: it re-simulates each route itself
/// so that an error in the shared evaluation code cannot hide an infeasible solution.
/// </summary>
public static class SolutionValidator
{
    private const double Eps = Evaluator.Epsilon;

    public static ValidationReport Validate(ProblemInstance instance, Solution solution)
    {
        var violations = new List<Violation>();
        bool evaluable = CheckStructure(instance, solution, violations);

        if (evaluable)
        {
            CheckCoverage(instance, solution, violations);
            CheckFleetSize(instance, solution, violations);

            for (int r = 0; r < solution.Routes.Count; r++)
                CheckRoute(instance, solution.Routes[r], r + 1, violations);
        }

        var stats = evaluable ? Evaluator.Evaluate(instance, solution) : null;
        return new ValidationReport(violations, stats);
    }

    private static bool CheckStructure(ProblemInstance instance, Solution solution, List<Violation> violations)
    {
        bool ok = true;

        for (int r = 0; r < solution.Routes.Count; r++)
        {
            var route = solution.Routes[r];
            if (route.VehicleType < 0 || route.VehicleType >= instance.VehicleTypes.Count)
            {
                violations.Add(new(ViolationKind.UnknownVehicleType, $"Route {r + 1}: unknown vehicle type {route.VehicleType}."));
                ok = false;
            }

            foreach (int v in route.Visits)
            {
                if (v < 0 || v >= instance.Nodes.Count)
                {
                    violations.Add(new(ViolationKind.InvalidNode, $"Route {r + 1}: node index {v} is out of range."));
                    ok = false;
                }
                else if (instance.Nodes[v].Kind == NodeKind.Depot)
                {
                    violations.Add(new(ViolationKind.DepotInsideRoute, $"Route {r + 1}: depot '{instance.Nodes[v].Id}' appears among the visits."));
                }
            }
        }

        return ok;
    }

    private static void CheckCoverage(ProblemInstance instance, Solution solution, List<Violation> violations)
    {
        var visitCount = new int[instance.Nodes.Count];
        foreach (var route in solution.Routes)
        {
            foreach (int v in route.Visits)
                visitCount[v]++;
        }

        foreach (int c in instance.Customers)
        {
            string id = instance.Nodes[c].Id;
            if (visitCount[c] == 0)
                violations.Add(new(ViolationKind.CustomerNotVisited, $"Customer '{id}' is not visited."));
            else if (visitCount[c] > 1)
                violations.Add(new(ViolationKind.CustomerVisitedTwice, $"Customer '{id}' is visited {visitCount[c]} times."));
        }
    }

    private static void CheckFleetSize(ProblemInstance instance, Solution solution, List<Violation> violations)
    {
        var used = new int[instance.VehicleTypes.Count];
        foreach (var route in solution.UsedRoutes)
            used[route.VehicleType]++;

        for (int t = 0; t < used.Length; t++)
        {
            var type = instance.VehicleTypes[t];
            if (used[t] > type.Count)
                violations.Add(new(ViolationKind.FleetSizeExceeded, $"Vehicle type '{type.Id}': {used[t]} routes, only {type.Count} vehicles available."));
        }
    }

    private static void CheckRoute(ProblemInstance instance, Route route, int routeNumber, List<Violation> violations)
    {
        if (route.IsEmpty)
            return;

        var vehicle = instance.VehicleTypes[route.VehicleType];
        var nodes = instance.Nodes;
        var matrix = instance.Matrix;

        double load = route.Visits.Sum(v => nodes[v].Demand);
        double volume = route.Visits.Sum(v => nodes[v].Volume);
        if (volume > vehicle.VolumeCapacity + Eps)
            violations.Add(new(ViolationKind.VolumeExceeded,
                $"Route {routeNumber}: volume {volume} exceeds capacity {vehicle.VolumeCapacity}."));
        if (load > vehicle.Capacity + Eps)
            violations.Add(new(ViolationKind.CapacityExceeded, $"Route {routeNumber}: load {load:0.##} exceeds capacity {vehicle.Capacity:0.##}."));

        double time = Math.Max(vehicle.Shift.Start, nodes[vehicle.StartDepot].Window.Start);
        if (time > Math.Min(vehicle.Shift.End, nodes[vehicle.StartDepot].Window.End) + Eps)
            violations.Add(new(ViolationKind.TimeWindowMissed, $"Route {routeNumber}: the start depot is closed throughout the crew's shift."));
        int previous = vehicle.StartDepot;

        foreach (int current in route.Visits)
        {
            var node = nodes[current];
            double arrival = time + matrix.Time(previous, current);
            double serviceStart = Math.Max(arrival, node.Window.Start);

            if (serviceStart > node.Window.End + Eps)
            {
                violations.Add(new(ViolationKind.TimeWindowMissed,
                    $"Route {routeNumber}: customer '{node.Id}' served at {serviceStart:0.###}, window [{node.Window.Start:0.##}, {node.Window.End:0.##}]."));
            }

            time = serviceStart + node.ServiceTime;
            previous = current;
        }

        double returnTime = Math.Max(time + matrix.Time(previous, vehicle.EndDepot), nodes[vehicle.EndDepot].Window.Start);
        double deadline = Math.Min(vehicle.Shift.End, nodes[vehicle.EndDepot].Window.End);
        if (returnTime > deadline + Eps)
        {
            violations.Add(new(ViolationKind.LateReturnToDepot,
                $"Route {routeNumber}: returns to depot at {returnTime:0.###}, deadline {deadline:0.##}."));
        }
    }
}
