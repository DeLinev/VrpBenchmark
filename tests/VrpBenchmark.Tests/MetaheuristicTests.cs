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
