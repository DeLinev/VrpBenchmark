using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Algorithms;

/// <param name="TimeLimit">Budget for metaheuristics; deterministic heuristics finish earlier and ignore it.</param>
public sealed record SolverOptions(TimeSpan TimeLimit, int Seed = 1);

/// <summary>A point on the convergence curve: the best cost known at the given moment.</summary>
public readonly record struct ProgressPoint(TimeSpan Time, double Cost);

public sealed record SolverResult(Solution Solution, TimeSpan Elapsed, IReadOnlyList<ProgressPoint> History);

public interface ISolver
{
    string Name { get; }

    SolverResult Solve(ProblemInstance instance, SolverOptions options);
}
