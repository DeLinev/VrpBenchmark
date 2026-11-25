using VrpBenchmark.Algorithms;
using VrpBenchmark.Algorithms.Metaheuristics.Alns;
using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;
using VrpBenchmark.Core.Validation;
using VrpBenchmark.Instances;

namespace VrpBenchmark.Tests;

public class PartialPlanTests
{
    private readonly ProblemInstance _task = InstanceFile.Read(Path.Combine(TestData.Dir, "crews", "instances", "06-partial-plan.json"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void More_served_orders_win_even_when_the_route_is_more_expensive(bool fleetFirst)
    {
        var result = new Alns(new AlnsParameters { MinimizeFleetFirst = fleetFirst }).Solve(_task,
            new SolverOptions(TimeSpan.FromSeconds(10), Seed: 1, MaxIterations: 120));
        var plan = result.Solution;
        Assert.Equal(new[] { "shop-a", "shop-b" }, Assert.Single(plan.UsedRoutes).Visits.Select(i => _task.Nodes[i].Id).Order());
        Assert.Equal(4, plan.Unassigned.Count);
        Assert.True(SolutionValidator.Validate(_task, plan).IsFeasible);
        Assert.Equal(120d, result.History[^1].Cost);
        Assert.Equal(4, result.History[^1].UnassignedCount);

        using var output = new StringWriter();
        SolutionFile.Write(output, _task, plan, "test", includeCrew: true);
        var restored = SolutionFile.Parse(new StringReader(output.ToString()), _task);
        Assert.Equal(plan.Unassigned, restored.Unassigned);
        Assert.True(SolutionValidator.Validate(_task, restored).IsFeasible);
        Assert.False(SolutionValidator.Validate(_task, restored, requireComplete: true).IsFeasible);
    }

    [Theory]
    [InlineData("press", "Load exceeds")]
    [InlineData("foam", "Volume exceeds")]
    [InlineData("night", "time windows")]
    [InlineData("near-pallet", "dedicated trip")]
    public void Explains_individual_constraints(string id, string reason) =>
        Assert.Contains(reason, UnassignedOrders.Explain(_task, _task.IndexOf(id)));

    [Fact]
    public void No_crew_returns_all_orders_immediately()
    {
        var task = new ProblemInstance("no crews", _task.Nodes, [], _task.Matrix);
        var result = new Alns().Solve(task, new SolverOptions(TimeSpan.FromSeconds(10)));
        Assert.Empty(result.Solution.UsedRoutes);
        Assert.Equal(task.Customers, result.Solution.Unassigned);
        Assert.Equal(0, result.Iterations);
        Assert.True(SolutionValidator.Validate(task, result.Solution).IsFeasible);
        Assert.Contains("No crew", UnassignedOrders.Explain(task, 1));
    }

    [Fact]
    public void Available_crew_that_cannot_serve_any_order_returns_an_empty_dispatch_plan()
    {
        var crew = _task.VehicleTypes[0] with { Capacity = 0 };
        var task = new ProblemInstance("too heavy", _task.Nodes, [crew], _task.Matrix);
        var result = new Alns().Solve(task, new SolverOptions(TimeSpan.FromSeconds(10), MaxIterations: 100));
        Assert.Empty(result.Solution.UsedRoutes);
        Assert.Equal(task.Customers.Order(), result.Solution.Unassigned.Order());
        Assert.Equal(0, result.Iterations);
        Assert.True(SolutionValidator.Validate(task, result.Solution).IsFeasible);
    }

    [Fact]
    public void Every_order_must_appear_exactly_once_across_routes_and_unassigned()
    {
        var valid = new Solution([new Route(0, [1])], _task.Customers.Skip(1));
        Assert.True(SolutionValidator.Validate(_task, valid).IsFeasible);
        foreach (int invalid in new[] { 0, -1, 1, 2, _task.Nodes.Count })
        {
            var copy = valid.Clone();
            copy.Unassigned.Add(invalid);
            Assert.False(SolutionValidator.Validate(_task, copy).IsFeasible);
        }
        valid.Unassigned.RemoveAt(0);
        Assert.Contains(SolutionValidator.Validate(_task, valid).Violations, v => v.Kind == ViolationKind.CustomerNotVisited);
    }
}
