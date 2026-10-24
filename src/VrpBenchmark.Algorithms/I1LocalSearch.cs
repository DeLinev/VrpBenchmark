using System.Diagnostics;
using VrpBenchmark.Algorithms.Construction;
using VrpBenchmark.Algorithms.Improvement;
using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Algorithms;

/// <summary>Baseline: best I1 construction improved by local search to a local optimum.</summary>
public sealed class I1LocalSearch : ISolver
{
    public string Name => "I1+LS";

    public LocalSearchStats? LastStats { get; private set; }

    public SolverResult Solve(ProblemInstance instance, SolverOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        var construction = new SolomonI1().Solve(instance, options);

        var search = new LocalSearch(instance);
        var solution = search.Improve(construction.Solution);
        LastStats = search.Stats;

        var history = new List<ProgressPoint>(construction.History)
        {
            new(stopwatch.Elapsed, Evaluator.Evaluate(instance, solution).Cost),
        };

        return new SolverResult(solution, stopwatch.Elapsed, history);
    }
}
