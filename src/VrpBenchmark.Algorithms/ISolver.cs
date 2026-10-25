using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Algorithms;

/// <param name="TimeLimit">Budget for metaheuristics; deterministic heuristics finish earlier and ignore it.</param>
/// <param name="MaxIterations">
/// If set, metaheuristics measure their progress (e.g. the cooling schedule) in iterations instead of time,
/// which makes a run fully reproducible for a given seed. The time limit still applies.
/// </param>
public sealed record SolverOptions(TimeSpan TimeLimit, int Seed = 1, long? MaxIterations = null)
{
    /// <summary>Share of the budget used so far, from 0 to 1.</summary>
    public double Progress(TimeSpan elapsed, long iterations)
    {
        double byTime = elapsed / TimeLimit;
        return MaxIterations is long max ? Math.Max(byTime, (double)iterations / max) : byTime;
    }
}

/// <summary>A point on the convergence curve: the best cost known at the given moment.</summary>
public readonly record struct ProgressPoint(TimeSpan Time, double Cost);

/// <param name="Iterations">Main-loop iterations of a metaheuristic; null for algorithms without such a loop.</param>
public sealed record SolverResult(Solution Solution, TimeSpan Elapsed, IReadOnlyList<ProgressPoint> History, long? Iterations = null);

public interface ISolver
{
    string Name { get; }

    SolverResult Solve(ProblemInstance instance, SolverOptions options);
}
