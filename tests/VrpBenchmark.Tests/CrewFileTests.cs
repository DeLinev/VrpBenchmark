using System.Text.Json;
using System.Text.Json.Nodes;
using VrpBenchmark.Algorithms;
using VrpBenchmark.Algorithms.Metaheuristics.Alns;
using VrpBenchmark.Core.Validation;
using VrpBenchmark.Instances;

namespace VrpBenchmark.Tests;

public class CrewFileTests
{
    private static string Example(string name) => Path.Combine(TestData.Dir, "crews", "instances", name + ".json");

    [Theory]
    [InlineData("01-light-deliveries", "van-01")]
    [InlineData("02-bulky-parcels", "truck-02")]
    [InlineData("03-heavy-orders", "truck-02")]
    [InlineData("04-split-shifts", "evening-van,morning-van")]
    [InlineData("05-mixed-depot-day", "box-truck,early-van,late-van")]
    public void Example_round_trips_with_the_assigned_crews(string name, string expectedCrews)
    {
        var task = InstanceFile.Read(Example(name));
        var result = new Alns().Solve(task, new SolverOptions(TimeSpan.FromSeconds(10), MaxIterations: 100));
        using var writer = new StringWriter();
        SolutionFile.Write(writer, task, result.Solution, "test");
        var saved = SolutionFile.Parse(new StringReader(writer.ToString()), task);

        Assert.True(SolutionValidator.Validate(task, saved).IsFeasible);
        Assert.Equal(expectedCrews.Split(','), saved.UsedRoutes.Select(r => task.VehicleTypes[r.VehicleType].Id).Order(StringComparer.Ordinal));
        Assert.Equal(result.Solution.UsedRoutes.Select(r => r.Visits), saved.UsedRoutes.Select(r => r.Visits));
        Assert.Equal(result.Solution.UsedRoutes.Select(r => r.VehicleType), saved.UsedRoutes.Select(r => r.VehicleType));
    }

    [Theory]
    [InlineData("Route 1 : 001")]
    [InlineData("Route 1 [unknown] : 001")]
    public void Multiple_crews_require_a_known_assignment(string line)
    {
        var task = InstanceFile.Read(Example("01-light-deliveries"));
        Assert.Throws<FormatException>(() => SolutionFile.Parse(new StringReader(line), task));
    }

    [Fact]
    public void Speed_is_converted_to_kilometres_per_minute()
    {
        var task = InstanceFile.Read(Example("01-light-deliveries"), maxCustomers: 2);
        Assert.Equal(2, task.Customers.Count);
        Assert.Equal("001", task.Nodes[1].Id);
        Assert.Equal(Math.Sqrt(10) * 60 / 40, task.Matrix.Time(0, 1), 10);
    }

    [Fact]
    public void Missing_window_bound_and_duplicate_crew_are_reported()
    {
        string path = Path.Combine(Path.GetTempPath(), $"crew-{Guid.NewGuid():N}.json");
        var json = JsonNode.Parse(File.ReadAllText(Example("01-light-deliveries")))!;
        var shift = json["crews"]![0]!["shift"]!.AsObject();
        try
        {
            shift.Remove("start");
            File.WriteAllText(path, json.ToJsonString());
            Assert.Throws<JsonException>(() => InstanceFile.Read(path));
            shift["start"] = 480;
            json["crews"]![1]!["id"] = json["crews"]![0]!["id"]!.GetValue<string>();
            File.WriteAllText(path, json.ToJsonString());
            Assert.Throws<FormatException>(() => InstanceFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
