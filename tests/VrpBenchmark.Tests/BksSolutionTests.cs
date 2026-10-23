using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Validation;
using VrpBenchmark.Instances;

namespace VrpBenchmark.Tests;

public class BksSolutionTests
{
    [Fact]
    public void C101_best_known_solution_has_10_vehicles_and_distance_828_94()
    {
        var instance = TestData.Solomon("c101");
        var report = SolutionValidator.Validate(instance, TestData.BksSolution("c101", instance));

        Assert.True(report.IsFeasible);
        Assert.Equal(10, report.Stats!.VehiclesUsed);
        Assert.Equal(828.94, Math.Round(report.Stats.Distance, 2));
        Assert.Equal(10 * SolomonReader.HierarchicalVehicleCost + report.Stats.Distance, report.Stats.Cost, 9);
    }

    [Theory]
    [MemberData(nameof(TestData.AllSolomonNames), MemberType = typeof(TestData))]
    public void Published_solution_is_feasible_and_matches_bks(string name)
    {
        Assert.True(TestData.Bks().TryGet(name, out var bks));
        var instance = TestData.Solomon(name);
        var report = SolutionValidator.Validate(instance, TestData.BksSolution(name, instance));

        Assert.Empty(report.Violations);
        Assert.Equal(bks.Vehicles, report.Stats!.VehiclesUsed);
        Assert.Equal(bks.Distance, Math.Round(report.Stats.Distance, 2));
    }

    [Theory]
    [MemberData(nameof(TestData.AllSolomonNames), MemberType = typeof(TestData))]
    public void Evaluator_agrees_with_validator_on_published_solutions(string name)
    {
        var instance = TestData.Solomon(name);
        var solution = TestData.BksSolution(name, instance);

        Assert.All(solution.Routes, r => Assert.True(Evaluator.Evaluate(instance, r).IsFeasible));
        var stats = Evaluator.Evaluate(instance, solution);
        Assert.Equal(0, stats.LateNodes);
        Assert.Equal(0, stats.Overload);
    }

    [Fact]
    public void Solution_file_round_trip_preserves_routes()
    {
        var instance = TestData.Solomon("r101");
        var original = TestData.BksSolution("r101", instance);

        var writer = new StringWriter();
        SolutionFile.Write(writer, instance, original, "test");
        var restored = SolutionFile.Parse(new StringReader(writer.ToString()), instance);

        Assert.Equal(original.Routes.Select(r => r.Visits), restored.Routes.Select(r => r.Visits));
    }
}
