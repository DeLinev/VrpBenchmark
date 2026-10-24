using VrpBenchmark.Algorithms.Routing;
using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Tests;

public class SegmentTests
{
    [Theory]
    [MemberData(nameof(TestData.AllSolomonNames), MemberType = typeof(TestData))]
    public void Whole_route_segment_matches_evaluator_on_published_solutions(string name)
    {
        var instance = TestData.Solomon(name);

        foreach (var route in TestData.BksSolution(name, instance).Routes)
        {
            var whole = new RouteState(instance, route.VehicleType, route.Visits).Whole;
            var stats = Evaluator.Evaluate(instance, route);

            Assert.Equal(stats.Distance, whole.Distance, 9);
            Assert.Equal(stats.Load, whole.Load);
            Assert.Equal(0, whole.TimeWarp, 9);
            Assert.True(whole.Duration <= stats.Duration + 1e-9);
        }
    }

    [Theory]
    [InlineData("c204")]
    [InlineData("r202")]
    [InlineData("rc201")]
    public void Time_warp_detects_exactly_the_routes_with_missed_windows(string name)
    {
        var instance = TestData.Solomon(name);
        var random = new Random(42);
        var routes = TestData.BksSolution(name, instance).Routes;
        int infeasible = 0;

        for (int trial = 0; trial < 200; trial++)
        {
            // Swapping two nearby customers gives a mix of feasible and infeasible routes.
            var visits = routes[trial % routes.Count].Visits.ToArray();
            int i = random.Next(visits.Length - 1);
            int j = Math.Min(visits.Length - 1, i + 1 + random.Next(2));
            (visits[i], visits[j]) = (visits[j], visits[i]);
            var route = new Route(0, visits);

            bool lateByEvaluator = Evaluator.Evaluate(instance, route).LateNodes > 0;
            bool lateBySegment = new RouteState(instance, 0, visits).Whole.TimeWarp > Evaluator.Epsilon;

            Assert.Equal(lateByEvaluator, lateBySegment);
            infeasible += lateByEvaluator ? 1 : 0;
        }

        Assert.InRange(infeasible, 1, 199);
    }

    [Fact]
    public void Concatenation_is_associative()
    {
        var instance = TestData.Solomon("r201");
        var m = instance.Matrix;
        var random = new Random(7);

        for (int trial = 0; trial < 100; trial++)
        {
            var nodes = instance.Customers.OrderBy(_ => random.Next()).Take(6).Select(c => Segment.ForNode(instance.Nodes[c])).ToArray();
            var ab = Segment.Concat(m, Segment.Concat(m, nodes[0], nodes[1]), Segment.Concat(m, nodes[2], nodes[3]));
            var cd = Segment.Concat(m, nodes[4], nodes[5]);

            var left = Segment.Concat(m, ab, cd);
            var right = nodes.Aggregate((x, y) => Segment.Concat(m, x, y));

            Assert.Equal(right.Duration, left.Duration, 9);
            Assert.Equal(right.TimeWarp, left.TimeWarp, 9);
            Assert.Equal(right.Earliest, left.Earliest, 9);
            Assert.Equal(right.Latest, left.Latest, 9);
            Assert.Equal(right.Distance, left.Distance, 9);
        }
    }

    [Fact]
    public void Prefix_plus_suffix_gives_the_whole_route_at_every_cut()
    {
        var instance = TestData.Solomon("rc101");
        var route = TestData.BksSolution("rc101", instance).Routes[0];
        var state = new RouteState(instance, 0, route.Visits);

        for (int cut = 0; cut <= state.Count; cut++)
        {
            var joined = Segment.Concat(instance.Matrix, state.Prefix(cut), state.Suffix(cut));
            Assert.Equal(state.Whole.Distance, joined.Distance, 9);
            Assert.Equal(state.Whole.Duration, joined.Duration, 9);
        }
    }
}
