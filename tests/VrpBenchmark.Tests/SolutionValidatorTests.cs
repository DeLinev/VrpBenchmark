using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;
using VrpBenchmark.Core.Validation;

namespace VrpBenchmark.Tests;

public class SolutionValidatorTests
{
    private readonly ProblemInstance _instance = TestData.Solomon("c101");

    private Solution Bks() => TestData.BksSolution("c101", _instance);

    private static IEnumerable<ViolationKind> Kinds(ValidationReport report) => report.Violations.Select(v => v.Kind).Distinct();

    [Fact]
    public void Detects_missing_customer()
    {
        var solution = Bks();
        int removed = solution.Routes[0].Visits[0];
        solution.Routes[0].Visits.RemoveAt(0);

        var report = SolutionValidator.Validate(_instance, solution);

        var violation = Assert.Single(report.Violations);
        Assert.Equal(ViolationKind.CustomerNotVisited, violation.Kind);
        Assert.Contains($"'{_instance.Nodes[removed].Id}'", violation.Message);
    }

    [Fact]
    public void Detects_duplicate_visit()
    {
        var solution = Bks();
        solution.Routes.Add(new Route(0, [solution.Routes[0].Visits[0]]));

        Assert.Contains(ViolationKind.CustomerVisitedTwice, Kinds(SolutionValidator.Validate(_instance, solution)));
    }

    [Fact]
    public void Detects_capacity_overload()
    {
        var solution = Bks();
        solution.Routes[0].Visits.AddRange(solution.Routes[1].Visits);
        solution.Routes[1].Visits.Clear();

        var report = SolutionValidator.Validate(_instance, solution);

        Assert.Contains(ViolationKind.CapacityExceeded, Kinds(report));
        Assert.True(report.Stats!.Overload > 0);
        Assert.Equal(9, report.Stats.VehiclesUsed);
    }

    [Fact]
    public void Detects_time_window_violation_when_route_is_reversed()
    {
        var solution = Bks();
        solution.Routes[0].Visits.Reverse();

        var report = SolutionValidator.Validate(_instance, solution);

        Assert.Contains(ViolationKind.TimeWindowMissed, Kinds(report));
        Assert.False(Evaluator.Evaluate(_instance, solution.Routes[0]).IsFeasible);
    }

    [Fact]
    public void Detects_late_return_to_depot()
    {
        // 100 customers x 90 service time alone exceed the planning horizon of 1236.
        var solution = new Solution([new Route(0, _instance.Customers)]);

        var kinds = Kinds(SolutionValidator.Validate(_instance, solution)).ToList();

        Assert.Contains(ViolationKind.LateReturnToDepot, kinds);
        Assert.Contains(ViolationKind.CapacityExceeded, kinds);
    }

    [Fact]
    public void Detects_fleet_size_exceeded()
    {
        var solution = new Solution(_instance.Customers.Select(c => new Route(0, [c])));

        var report = SolutionValidator.Validate(_instance, solution);

        Assert.Equal([ViolationKind.FleetSizeExceeded], Kinds(report));
        Assert.Equal(100, report.Stats!.VehiclesUsed);
    }

    [Fact]
    public void Reports_structural_errors_without_stats()
    {
        var solution = new Solution([new Route(5, [1]), new Route(0, [999])]);

        var report = SolutionValidator.Validate(_instance, solution);

        Assert.Null(report.Stats);
        Assert.Contains(ViolationKind.UnknownVehicleType, Kinds(report));
        Assert.Contains(ViolationKind.InvalidNode, Kinds(report));
    }

    [Fact]
    public void Detects_depot_inside_route()
    {
        var solution = Bks();
        solution.Routes[0].Visits.Insert(1, 0);

        Assert.Contains(ViolationKind.DepotInsideRoute, Kinds(SolutionValidator.Validate(_instance, solution)));
    }

    [Fact]
    public void Empty_routes_are_ignored()
    {
        var solution = Bks();
        solution.Routes.Add(new Route(0));

        var report = SolutionValidator.Validate(_instance, solution);

        Assert.True(report.IsFeasible);
        Assert.Equal(10, report.Stats!.VehiclesUsed);
    }
}
