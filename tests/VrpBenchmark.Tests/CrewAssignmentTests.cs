using VrpBenchmark.Algorithms;
using VrpBenchmark.Algorithms.Metaheuristics.Alns;
using VrpBenchmark.Algorithms.Routing;
using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;
using VrpBenchmark.Core.Validation;

namespace VrpBenchmark.Tests;

public class CrewAssignmentTests
{
    private static readonly TimeWindow Day = new(0, 200);
    private static readonly VehicleType Van = new VehicleType("van", 800, 1, 0, 0, Day, 100, 2) { VolumeCapacity = 4 };
    private static readonly VehicleType Truck = new VehicleType("truck", 1600, 1, 0, 0, Day, 200, 4) { VolumeCapacity = 12 };

    private static Node Order(int i, double weight, double volume, double x = 10) =>
        new Node(i, $"order-{i}", NodeKind.Customer, x, 0, weight, Day, 0) { Volume = volume };

    private static ProblemInstance Instance(VehicleType[] crews, params Node[] orders)
    {
        Node[] nodes = [new(0, "depot", NodeKind.Depot, 0, 0, 0, Day, 0), .. orders];
        return new ProblemInstance("crews", nodes, crews, TravelMatrix.Euclidean(nodes));
    }

    private static Solution Solve(ProblemInstance instance, bool fleetFirst = true)
    {
        var solver = new Alns(new AlnsParameters { MinimizeFleetFirst = fleetFirst });
        var result = solver.Solve(instance, new SolverOptions(TimeSpan.FromSeconds(10), Seed: 2, MaxIterations: 100));
        var report = SolutionValidator.Validate(instance, result.Solution);
        Assert.True(report.IsFeasible, string.Join(", ", report.Violations.Select(v => v.Message)));
        Assert.Equal(report.Stats!.Cost, result.History[^1].Cost, 7);
        return result.Solution;
    }

    [Theory]
    [InlineData(700, 3, "van", 140)]
    [InlineData(1500, 3, "truck", 280)]
    [InlineData(700, 6, "truck", 280)]
    public void Chooses_cheapest_feasible_crew(double weight, double volume, string expectedCrew, double cost)
    {
        var instance = Instance([Truck, Van], Order(1, weight, volume));
        var route = Assert.Single(Solve(instance).UsedRoutes);
        Assert.Equal(expectedCrew, instance.VehicleTypes[route.VehicleType].Id);
        Assert.Equal(cost, Evaluator.Evaluate(instance, route).Cost);
    }

    [Fact]
    public void Crew_must_be_available_and_return_before_its_shift_ends()
    {
        var instance = Instance([Van with { Count = 0 }, Van with { Shift = new(0, 55) }, Truck],
            Order(1, 100, 1) with { Window = new(50, 65) });
        Assert.Equal(2, Assert.Single(Solve(instance).UsedRoutes).VehicleType);
    }

    [Theory]
    [InlineData(true, 1, 580)]
    [InlineData(false, 2, 280)]
    public void Objective_controls_whether_a_second_cheap_van_is_worth_using(bool fleetFirst, int routes, double cost)
    {
        var instance = Instance([Van, Van with { Id = "second van" }, Truck with { FixedCost = 500 }],
            Order(1, 700, 1), Order(2, 700, 1));
        var solution = Solve(instance, fleetFirst);
        Assert.Equal(routes, solution.UsedRoutes.Count());
        Assert.Equal(cost, Evaluator.Evaluate(instance, solution).Cost);
        Assert.Equal(routes, solution.UsedRoutes.Select(r => r.VehicleType).Distinct().Count());
    }

    [Fact]
    public void Volume_bound_counts_actual_crews_of_each_capacity()
    {
        var instance = Instance([Van with { VolumeCapacity = 10 }, Truck with { Count = 2, VolumeCapacity = 4 }],
            Order(1, 1, 9), Order(2, 1, 4), Order(3, 1, 4));
        var solver = new Alns();
        var result = solver.Solve(instance, new SolverOptions(TimeSpan.FromSeconds(5), MaxIterations: 50));
        Assert.True(SolutionValidator.Validate(instance, result.Solution).IsFeasible);
        Assert.Equal(3, result.Solution.UsedRoutes.Count());
        Assert.Equal(0, solver.LastVehicleMinimizationIterations);
    }

    [Fact]
    public void A_finished_route_can_move_to_a_free_crew_without_reordering_visits()
    {
        var instance = Instance([Truck, Van], Order(1, 300, 2), Order(2, 400, 1));
        var plan = new PartialSolution(instance, [new RouteState(instance, 0, [2, 1])], [], 1, 1000);
        CrewAssignment.Improve(plan);
        var route = Assert.Single(plan.Routes);
        Assert.Equal(1, route.VehicleTypeIndex);
        Assert.Equal(new[] { 2, 1 }, route.Visits);
        Assert.Equal(140, plan.Cost);
        Assert.True(SolutionValidator.Validate(instance, plan.ToSolution()).IsFeasible);
    }

    [Fact]
    public void Two_occupied_crews_can_exchange_routes_if_the_total_cost_drops()
    {
        var instance = Instance([Truck, Van], Order(1, 100, 1, x: 20), Order(2, 100, 1, x: 2));
        var plan = new PartialSolution(instance, [new RouteState(instance, 0, [1]), new RouteState(instance, 1, [2])], [], 2, 1000);
        Assert.Equal(468, plan.Cost);
        CrewAssignment.Improve(plan);
        Assert.Equal(new[] { 1, 0 }, plan.Routes.Select(r => r.VehicleTypeIndex));
        Assert.Equal(396, plan.Cost);
        Assert.True(SolutionValidator.Validate(instance, plan.ToSolution()).IsFeasible);
    }

    [Fact]
    public void Replacement_checks_total_volume_even_if_each_order_fits()
    {
        var instance = Instance([Truck, Van], Order(1, 300, 3), Order(2, 400, 3));
        var plan = new PartialSolution(instance, [new RouteState(instance, 0, [1, 2])], [], 1, 1000);
        CrewAssignment.Improve(plan);
        Assert.Equal(0, plan.Routes[0].VehicleTypeIndex);
        var overloaded = new Route(1, [1, 2]);
        Assert.False(Evaluator.Evaluate(instance, overloaded).IsFeasible);
        Assert.Contains(SolutionValidator.Validate(instance, new Solution([overloaded])).Violations,
            v => v.Kind == ViolationKind.VolumeExceeded);
    }

    [Fact]
    public void Releasing_an_empty_route_makes_its_crew_available_again()
    {
        var instance = Instance([Van, Truck], Order(1, 700, 1));
        var plan = new PartialSolution(instance, [new RouteState(instance, 0, [1])], [], 1, 1000);
        plan.Remove([1]);
        new InsertionRepair(2).Repair(plan, new Random(1));
        Assert.Equal(0, Assert.Single(plan.Routes).VehicleTypeIndex);
        Assert.True(SolutionValidator.Validate(instance, plan.ToSolution()).IsFeasible);
    }

    [Fact]
    public void Insufficient_fleet_does_not_return_an_overbooked_plan()
    {
        var instance = Instance([Van, Truck with { Count = 0 }], Order(1, 700, 1), Order(2, 700, 1));
        var plan = Solve(instance);
        Assert.Single(plan.Unassigned);
        Assert.Single(plan.UsedRoutes);
        Assert.False(SolutionValidator.Validate(instance, plan, requireComplete: true).IsFeasible);
    }
}
