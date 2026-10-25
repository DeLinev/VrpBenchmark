using VrpBenchmark.Algorithms;
using VrpBenchmark.Algorithms.Construction;
using VrpBenchmark.Algorithms.Metaheuristics;
using VrpBenchmark.Algorithms.Metaheuristics.Alns;
using VrpBenchmark.OrTools;

namespace VrpBenchmark.Runner;

public static class SolverCatalog
{
    private static readonly Dictionary<string, Func<ISolver>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["i1"] = () => new SolomonI1(),
        ["i1-ls"] = () => new I1LocalSearch(),
        ["sa"] = () => new SimulatedAnnealing(),
        ["alns"] = () => new Alns(),
        ["ortools"] = () => new OrToolsSolver(),
    };

    public static IEnumerable<string> Names => Factories.Keys;

    public static ISolver Create(string name) =>
        Factories.TryGetValue(name, out var factory)
            ? factory()
            : throw new ArgumentException($"Unknown algorithm '{name}'. Available: {string.Join(", ", Names)}.");
}
