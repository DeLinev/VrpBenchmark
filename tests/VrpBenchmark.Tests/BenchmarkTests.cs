using VrpBenchmark.Algorithms;
using VrpBenchmark.Runner;

namespace VrpBenchmark.Tests;

public class BenchmarkTests
{
    [Fact]
    public void Plan_runs_deterministic_algorithms_once_and_orders_by_seed()
    {
        var config = new BenchmarkConfig("t", ["i1-ls", "sa"], 3, 1, 1, ["C101", "R101"]);

        var plan = Benchmark.Plan(config).ToList();

        Assert.Equal(2 + 3 * 2, plan.Count);
        Assert.All(plan.Where(k => k.Algorithm == "i1-ls"), k => Assert.Equal(1, k.Seed));
        Assert.Equal(plan.Select(k => k.Seed).Order(), plan.Select(k => k.Seed));
        Assert.Equal(plan.Count, plan.Distinct().Count());
    }

    [Fact]
    public void Parallel_runs_start_in_plan_order()
    {
        const int jobs = 5;
        var started = new List<int>();

        Benchmark.ForEachInOrder(Enumerable.Range(0, 500), jobs, i =>
        {
            lock (started)
                started.Add(i);
            Thread.SpinWait(1000);
        });

        // Items are taken one by one, so an item can only be overtaken by the ones other workers already hold.
        Assert.Equal(500, started.Distinct().Count());
        for (int position = 0; position < started.Count; position++)
            Assert.True(position >= started[position] - (jobs - 1), $"item {started[position]} started at position {position}");
    }

    [Fact]
    public void Interrupted_experiment_continues_without_repeating_runs()
    {
        string root = Path.Combine(Path.GetTempPath(), "vrp-bench-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new BenchmarkConfig("exp", ["i1-ls"], 1, 1, 2, ["C101"]);
            var extended = first with { Instances = ["C101", "R201"] };

            Assert.Equal(0, Benchmark.Run(TestData.Dir, root, first));
            Assert.Equal(0, Benchmark.Run(TestData.Dir, root, extended));

            string runs = Path.Combine(root, "exp", "runs.csv");
            var done = Benchmark.ReadCompleted(runs);
            Assert.Equal(2, done.Count);
            Assert.Equal(3, File.ReadAllLines(runs).Length);
            Assert.True(File.Exists(Path.Combine(root, "exp", "solutions", "i1-ls", "r201_s1.sol")));

            // A different time limit must not be mixed into the same experiment.
            Assert.Equal(1, Benchmark.Run(TestData.Dir, root, first with { TimeLimitSeconds = 2 }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Thin_keeps_vehicle_changes_and_endpoints_and_spaces_points_in_log_time()
    {
        // 10 000 improvements within one second, one vehicle removed half way.
        var history = Enumerable.Range(1, 10_000)
            .Select(i => new ProgressPoint(TimeSpan.FromSeconds(i / 10_000.0), (i < 5_000 ? 110_000 : 100_000) + 1000 - i * 0.01))
            .ToList();

        var thin = Benchmark.Thin(history, 10_000);

        Assert.True(thin.Count < 200, $"{thin.Count} points kept");
        Assert.Equal(history[0], thin[0]);
        Assert.Equal(history[^1], thin[^1]);
        Assert.Contains(history[4_999], thin);
    }

    [Theory]
    [InlineData(191_650.80, 19, 1650.80)]
    [InlineData(30_000.00, 3, 0.0)]
    public void Decode_splits_cost_into_vehicles_and_distance(double cost, int vehicles, double distance)
    {
        var (v, d) = Benchmark.Decode(cost, 10_000);
        Assert.Equal(vehicles, v);
        Assert.Equal(distance, d!.Value, 6);
    }
}
