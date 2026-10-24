using VrpBenchmark.Algorithms;
using VrpBenchmark.Algorithms.Construction;
using VrpBenchmark.Algorithms.Improvement;
using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;
using VrpBenchmark.Core.Validation;
using VrpBenchmark.Instances;

namespace VrpBenchmark.Tests;

public class AlgorithmTests
{
    private static readonly SolverOptions Options = new(TimeSpan.FromSeconds(1));

    [Theory]
    [MemberData(nameof(TestData.AllSolomonNames), MemberType = typeof(TestData))]
    public void I1_and_local_search_give_feasible_solutions_and_search_never_worsens(string name)
    {
        var instance = TestData.Solomon(name);

        var construction = new SolomonI1().Solve(instance, Options).Solution;
        var improved = new LocalSearch(instance).Improve(construction);

        var constructionReport = SolutionValidator.Validate(instance, construction);
        var improvedReport = SolutionValidator.Validate(instance, improved);

        Assert.Empty(constructionReport.Violations);
        Assert.Empty(improvedReport.Violations);
        Assert.True(improvedReport.Stats!.Cost <= constructionReport.Stats!.Cost + 1e-6);
    }

    [Fact]
    public void I1_is_deterministic()
    {
        var instance = TestData.Solomon("rc101");

        var first = new SolomonI1().Solve(instance, Options).Solution;
        var second = new SolomonI1().Solve(instance, Options).Solution;

        Assert.Equal(first.Routes.Select(r => r.Visits), second.Routes.Select(r => r.Visits));
    }

    [Theory]
    [InlineData(SeedRule.Farthest)]
    [InlineData(SeedRule.EarliestDeadline)]
    public void Every_I1_parameter_set_builds_a_feasible_solution(SeedRule seed)
    {
        var instance = TestData.Solomon("r101");

        foreach (var p in I1Parameters.DefaultGrid.Where(p => p.Seed == seed))
            Assert.True(SolutionValidator.Validate(instance, SolomonI1.Build(instance, p)).IsFeasible, p.ToString());
    }

    [Fact]
    public void Local_search_merges_single_customer_routes()
    {
        var instance = TestData.Solomon("c101", new SolomonOptions { MaxCustomers = 25 });
        var singletons = new Solution(instance.Customers.Select(c => new Route(0, [c])));
        var search = new LocalSearch(instance);

        var improved = search.Improve(singletons);
        var report = SolutionValidator.Validate(instance, improved);

        Assert.True(report.IsFeasible);
        Assert.True(report.Stats!.VehiclesUsed <= 5, $"{report.Stats.VehiclesUsed} vehicles left");
        Assert.True(search.Stats.Relocate > 0);
    }

    [Fact]
    public void Local_optimum_has_no_improving_move()
    {
        var instance = TestData.Solomon("r201");
        var search = new LocalSearch(instance);
        var optimum = search.Improve(new SolomonI1().Solve(instance, Options).Solution);

        var again = search.Improve(optimum);

        Assert.Equal(0, search.Stats.Total);
        Assert.Equal(Evaluator.Evaluate(instance, optimum).Cost, Evaluator.Evaluate(instance, again).Cost, 9);
    }

    [Fact]
    public void Local_search_rejects_infeasible_start()
    {
        var instance = TestData.Solomon("c101");
        var oneRoute = new Solution([new Route(0, instance.Customers)]);

        Assert.Throws<ArgumentException>(() => new LocalSearch(instance).Improve(oneRoute));
    }

    [Fact]
    public void Catalog_creates_known_solvers()
    {
        Assert.Equal("I1", SolverCatalog.Create("i1").Name);
        Assert.Equal("I1+LS", SolverCatalog.Create("I1-LS").Name);
        Assert.Throws<ArgumentException>(() => SolverCatalog.Create("unknown"));
    }
}
