using VrpBenchmark.Algorithms;
using VrpBenchmark.Algorithms.Metaheuristics;
using VrpBenchmark.Algorithms.Metaheuristics.Alns;
using VrpBenchmark.Algorithms.Routing;
using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;
using VrpBenchmark.Core.Validation;
using VrpBenchmark.Instances;
using VrpBenchmark.OrTools;

namespace VrpBenchmark.Tests;

public class MetaheuristicTests
{
    // Iteration budgets make the runs reproducible; the generous time limit never binds.
    private static SolverOptions Iterations(long count, int seed = 1) => new(TimeSpan.FromMinutes(5), seed, count);

    private static double Cost(ProblemInstance instance, Solution solution) => Evaluator.Evaluate(instance, solution).Cost;

    [Theory]
    [InlineData("c104")]
    [InlineData("r105")]
    [InlineData("rc203")]
    public void Simulated_annealing_is_feasible_and_not_worse_than_its_start(string name)
    {
        var instance = TestData.Solomon(name);
        double baseline = Cost(instance, new I1LocalSearch().Solve(instance, Iterations(1)).Solution);

        var result = new SimulatedAnnealing().Solve(instance, Iterations(300_000));

        Assert.True(SolutionValidator.Validate(instance, result.Solution).IsFeasible);
        Assert.True(Cost(instance, result.Solution) <= baseline + 1e-6);
        Assert.True(result.History.Count >= 2);
    }

    [Theory]
    [InlineData("c104")]
    [InlineData("r105")]
    [InlineData("rc203")]
    public void Alns_is_feasible_and_not_worse_than_its_start(string name)
    {
        var instance = TestData.Solomon(name);
        double baseline = Cost(instance, new I1LocalSearch().Solve(instance, Iterations(1)).Solution);

        var result = new Alns().Solve(instance, Iterations(400));

        Assert.True(SolutionValidator.Validate(instance, result.Solution).IsFeasible);
        Assert.True(Cost(instance, result.Solution) <= baseline + 1e-6);
    }

    [Theory]
    [InlineData("c101", true, 0)]    // 1810 units of demand require all ten starting routes.
    [InlineData("c101", false, 50)]
    [InlineData("r101", true, 50)]   // Time windows require more routes than the capacity bound.
    public void Alns_uses_the_remaining_budget_for_distance(string name, bool stopAtBound, int fleetIterations)
    {
        var instance = TestData.Solomon(name);
        var solver = new Alns(new AlnsParameters { StopAtCapacityBound = stopAtBound });
        var result = solver.Solve(instance, Iterations(100));

        Assert.Equal(fleetIterations, solver.LastVehicleMinimizationIterations);
        Assert.Equal(100, result.Iterations);
        Assert.True(SolutionValidator.Validate(instance, result.Solution).IsFeasible);
    }

    [Fact]
    public void Alns_switches_after_reducing_the_fleet_to_the_capacity_bound()
    {
        var instance = TestData.Solomon("r208");
        var solver = new Alns();
        var result = solver.Solve(instance, Iterations(400));

        Assert.InRange(solver.LastVehicleMinimizationIterations, 1, 199);
        Assert.Equal(400, result.Iterations);
        Assert.Equal(2, result.Solution.UsedRoutes.Count());
        Assert.True(SolutionValidator.Validate(instance, result.Solution).IsFeasible);
    }

    [Fact]
    public void Reordering_routes_or_the_request_bank_preserves_the_plan()
    {
        var instance = TestData.Solomon("c101", new SolomonOptions { MaxCustomers = 6 });
        var current = new PartialSolution(instance,
            [new RouteState(instance, 0, [1, 2]), new RouteState(instance, 0, [3, 4])], [5, 6], 2, 1000);
        var candidate = current.Clone();
        candidate.Routes.Reverse();
        candidate.Unassigned.Reverse();
        Assert.True(current.HasSameRoutesAs(candidate));

        candidate.Routes[0].Visits.Reverse();
        Assert.False(current.HasSameRoutesAs(candidate));
    }

    [Theory]
    [InlineData(1, 1, 0.05)] // The only possible route is rebuilt; both weights fall to their floor.
    [InlineData(2, 0, 13)]   // Repair reverses the visits on an equal-length symmetric tour.
    public void Accepted_equal_cost_plans_earn_points_only_when_visits_change(int customers, int repeats, double weight)
    {
        var data = TestData.Solomon("c101", new SolomonOptions { MaxCustomers = customers });
        var nodes = data.Nodes.Select(n => n with { Window = data.Nodes[0].Window }).ToArray();
        var instance = new ProblemInstance("equal tours", nodes, data.VehicleTypes, data.Matrix);
        var solver = new Alns(new AlnsParameters { SegmentLength = 1, ReactionFactor = 1 });
        solver.Solve(instance, Iterations(1));

        Assert.Equal(repeats, solver.LastAcceptedRepeats);
        Assert.Equal(2, solver.LastWeights.Values.Count(w => w == weight));
    }

    [Fact]
    public void Same_seed_gives_the_same_result()
    {
        var instance = TestData.Solomon("r102");

        foreach (ISolver solver in new ISolver[] { new SimulatedAnnealing(), new Alns() })
        {
            long iterations = solver is Alns ? 300 : 200_000;
            var first = solver.Solve(instance, Iterations(iterations, seed: 7)).Solution;
            var second = solver.Solve(instance, Iterations(iterations, seed: 7)).Solution;
            Assert.Equal(first.Routes.Select(r => r.Visits), second.Routes.Select(r => r.Visits));
        }
    }

    [Fact]
    public void Alns_removes_vehicles_from_the_constructive_solution()
    {
        var instance = TestData.Solomon("rc101");
        int baseline = new I1LocalSearch().Solve(instance, Iterations(1)).Solution.UsedRoutes.Count();

        var result = new Alns().Solve(instance, Iterations(3000));

        Assert.True(result.Solution.UsedRoutes.Count() < baseline);
    }

    [Fact]
    public void Destroy_operators_keep_every_customer_exactly_once()
    {
        var instance = TestData.Solomon("r101");
        var start = new I1LocalSearch().Solve(instance, Iterations(1)).Solution;
        var random = new Random(5);
        IDestroyOperator[] operators = [new RandomRemoval(), new WorstRemoval(), new ShawRemoval(instance), new RouteRemoval()];

        foreach (var op in operators)
        {
            var partial = new PartialSolution(instance, start.UsedRoutes.Select(r => new RouteState(instance, 0, r.Visits)), [], 25, 1000);
            op.Destroy(partial, 10, random);

            var all = partial.AssignedCustomers().Concat(partial.Unassigned).Order();
            Assert.Equal(instance.Customers.Order(), all);
            if (op is RouteRemoval)
                Assert.NotEmpty(partial.Unassigned);
            else
                Assert.Equal(10, partial.Unassigned.Count);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Repair_rebuilds_a_feasible_solution_from_scratch(int regret)
    {
        var instance = TestData.Solomon("c201");
        var empty = new PartialSolution(instance, [], instance.Customers, 25, 1000);

        new InsertionRepair(regret).Repair(empty, new Random(1));

        Assert.True(empty.IsComplete);
        Assert.True(SolutionValidator.Validate(instance, empty.ToSolution()).IsFeasible);
    }

    [Fact]
    public void Repair_respects_the_route_limit()
    {
        var instance = TestData.Solomon("r101");
        var empty = new PartialSolution(instance, [], instance.Customers, 3, 1000);

        new InsertionRepair(2).Repair(empty, new Random(1));

        Assert.Equal(3, empty.Routes.Count);
        Assert.NotEmpty(empty.Unassigned);
        Assert.Equal(1000 * empty.Unassigned.Count + empty.Routes.Sum(r => r.Cost), empty.Cost, 6);
    }

    [Theory]
    [InlineData("c101", null)]
    [InlineData("rc201", 50)]
    public void OrTools_solution_is_feasible_in_the_exact_model(string name, int? customers)
    {
        var instance = TestData.Solomon(name, new SolomonOptions { MaxCustomers = customers });

        var result = new OrToolsSolver().Solve(instance, new SolverOptions(TimeSpan.FromSeconds(2)));
        var report = SolutionValidator.Validate(instance, result.Solution);

        Assert.True(report.IsFeasible, string.Join("; ", report.Violations.Select(v => v.Message)));
        Assert.NotEmpty(result.History);
    }
}
