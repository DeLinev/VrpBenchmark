using VrpBenchmark.Algorithms;
using VrpBenchmark.Algorithms.Construction;
using VrpBenchmark.Algorithms.Routing;
using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Tests;

public class MovesTests
{
    /// <summary>
    /// For random moves on a feasible solution: a finite delta must equal the real cost change after applying
    /// the move, and an infinite delta must correspond to a route that really becomes infeasible.
    /// </summary>
    [Theory]
    [InlineData("c101")]
    [InlineData("r201")]
    [InlineData("rc105")]
    public void Delta_matches_the_real_cost_change(string name)
    {
        var instance = TestData.Solomon(name);
        var start = new SolomonI1().Solve(instance, new SolverOptions(TimeSpan.FromSeconds(1))).Solution;
        var routes = start.Routes.Select(r => new RouteState(instance, r.VehicleType, r.Visits)).ToList();
        var random = new Random(3);
        int feasible = 0, infeasible = 0;

        for (int trial = 0; trial < 3000; trial++)
        {
            var copy = routes.Select(r => r.Clone()).ToList();
            double before = copy.Sum(r => r.Cost);
            int a = random.Next(copy.Count), b = random.Next(copy.Count);
            var A = copy[a];
            var B = copy[b];
            double delta;

            switch (trial % 4)
            {
                case 0:
                {
                    int i = random.Next(A.Count);
                    int k = 1 + random.Next(Math.Min(3, A.Count - i));
                    int p = random.Next(B.Count + 1);
                    if (a == b && p >= i && p <= i + k)
                        continue;
                    delta = Moves.RelocateDelta(A, i, k, B, p);
                    Moves.ApplyRelocate(A, i, k, B, p);
                    break;
                }
                case 1:
                {
                    if (a == b)
                        continue;
                    int i = random.Next(A.Count), j = random.Next(B.Count);
                    delta = Moves.SwapDelta(A, i, B, j);
                    Moves.ApplySwap(A, i, B, j);
                    break;
                }
                case 2:
                {
                    if (a == b)
                        continue;
                    int i = random.Next(A.Count + 1), j = random.Next(B.Count + 1);
                    delta = Moves.TwoOptStarDelta(A, i, B, j);
                    Moves.ApplyTwoOptStar(A, i, B, j);
                    break;
                }
                default:
                {
                    if (A.Count < 2)
                        continue;
                    int i = random.Next(A.Count - 1);
                    int j = i + 1 + random.Next(A.Count - 1 - i);
                    delta = Moves.TwoOptDelta(A, i, j);
                    Moves.ApplyTwoOpt(A, i, j);
                    break;
                }
            }

            var after = new Solution(copy.Select(r => r.ToRoute()));
            if (double.IsInfinity(delta))
            {
                Assert.Contains(copy, r => !r.IsEmpty && !Evaluator.Evaluate(instance, r.ToRoute()).IsFeasible);
                infeasible++;
            }
            else
            {
                Assert.All(after.UsedRoutes, r => Assert.True(Evaluator.Evaluate(instance, r).IsFeasible));
                Assert.Equal(Evaluator.Evaluate(instance, after).Cost - before, delta, 6);
                feasible++;
            }
        }

        // Random moves under tight windows are mostly infeasible; both outcomes just need to be exercised.
        Assert.True(feasible >= 10 && infeasible >= 10, $"feasible {feasible}, infeasible {infeasible}");
    }

    [Fact]
    public void Clone_is_independent_of_the_original()
    {
        var instance = TestData.Solomon("c101");
        var original = new RouteState(instance, 0, TestData.BksSolution("c101", instance).Routes[0].Visits);
        double cost = original.Cost;

        var clone = original.Clone();
        clone.Visits.Reverse();
        clone.Update();

        Assert.Equal(cost, original.Cost);
        Assert.NotEqual(original.Visits, clone.Visits);
    }
}
