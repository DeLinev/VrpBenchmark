using System.Diagnostics;
using VrpBenchmark.Algorithms.Improvement;
using VrpBenchmark.Algorithms.Routing;
using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Algorithms.Metaheuristics;

public sealed record SimulatedAnnealingParameters
{
    /// <summary>Probability of accepting an average worsening move at the start of the search.</summary>
    public double InitialAcceptance { get; init; } = 0.5;

    /// <summary>Final temperature as a fraction of the initial one.</summary>
    public double FinalTemperatureRatio { get; init; } = 0.001;

    /// <summary>Number of random moves sampled to calibrate the initial temperature.</summary>
    public int CalibrationSamples { get; init; } = 1000;

    public int MaxChainLength { get; init; } = 3;
}

/// <summary>
/// Simulated annealing over the four local search neighbourhoods. Every iteration draws one random move;
/// feasible improving moves are always accepted, worsening ones with probability exp(−Δ/T).
/// The temperature decreases geometrically from T0 to T0·FinalTemperatureRatio over the time (or iteration) budget.
/// Starts from I1 + local search and polishes the best solution found with local search at the end.
/// </summary>
public sealed class SimulatedAnnealing : ISolver
{
    private enum MoveKind { Relocate, Swap, TwoOptStar, TwoOpt }

    private readonly record struct Move(MoveKind Kind, int A, int I, int K, int B, int J);

    private readonly SimulatedAnnealingParameters _parameters;
    private List<RouteState> _routes = [];
    private Random _random = new();

    public SimulatedAnnealing(SimulatedAnnealingParameters? parameters = null) =>
        _parameters = parameters ?? new SimulatedAnnealingParameters();

    public string Name => "SA";

    public long LastIterations { get; private set; }

    public SolverResult Solve(ProblemInstance instance, SolverOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        _random = new Random(options.Seed);

        var start = new I1LocalSearch().Solve(instance, options).Solution;
        _routes = [.. start.UsedRoutes.Select(r => new RouteState(instance, r.VehicleType, r.Visits))];

        double current = _routes.Sum(r => r.Cost);
        double bestCost = current;
        var best = Snapshot();
        var history = new List<ProgressPoint> { new(stopwatch.Elapsed, bestCost) };

        double t0 = InitialTemperature();
        double ratio = _parameters.FinalTemperatureRatio;
        double temperature = t0;
        long iteration = 0;

        while (true)
        {
            if ((iteration & 255) == 0)
            {
                double progress = options.Progress(stopwatch.Elapsed, iteration);
                if (progress >= 1)
                    break;
                temperature = t0 * Math.Pow(ratio, progress);
            }

            iteration++;
            if (!TryDrawMove(out var move))
                continue;

            double delta = Evaluate(move);
            if (double.IsInfinity(delta))
                continue;

            if (delta <= 0 || _random.NextDouble() < Math.Exp(-delta / temperature))
            {
                Apply(move);
                current += delta;

                if (current < bestCost - 1e-9)
                {
                    bestCost = current;
                    best = Snapshot();
                    history.Add(new ProgressPoint(stopwatch.Elapsed, bestCost));
                }
            }
        }

        LastIterations = iteration;
        var polished = new LocalSearch(instance).Improve(best);
        history.Add(new ProgressPoint(stopwatch.Elapsed, Evaluator.Evaluate(instance, polished).Cost));
        return new SolverResult(polished, stopwatch.Elapsed, history);
    }

    /// <summary>T0 such that an average worsening move is accepted with probability InitialAcceptance.</summary>
    private double InitialTemperature()
    {
        double sum = 0;
        int count = 0;

        for (int s = 0; s < _parameters.CalibrationSamples; s++)
        {
            if (!TryDrawMove(out var move))
                continue;

            double delta = Evaluate(move);
            if (delta > 0 && !double.IsInfinity(delta))
            {
                sum += delta;
                count++;
            }
        }

        double average = count > 0 ? sum / count : 1.0;
        return -average / Math.Log(_parameters.InitialAcceptance);
    }

    private bool TryDrawMove(out Move move)
    {
        move = default;
        int routes = _routes.Count;
        var kind = (MoveKind)_random.Next(4);

        switch (kind)
        {
            case MoveKind.Relocate:
            {
                int a = _random.Next(routes);
                var A = _routes[a];
                int i = _random.Next(A.Count);
                int k = 1 + _random.Next(Math.Min(_parameters.MaxChainLength, A.Count - i));
                int b = _random.Next(routes);
                int p = _random.Next(_routes[b].Count + 1);
                if (a == b && p >= i && p <= i + k)
                    return false;
                move = new Move(kind, a, i, k, b, p);
                return true;
            }

            case MoveKind.Swap:
            case MoveKind.TwoOptStar:
            {
                if (routes < 2)
                    return false;
                int a = _random.Next(routes);
                int b = _random.Next(routes - 1);
                if (b >= a)
                    b++;
                var A = _routes[a];
                var B = _routes[b];

                if (kind == MoveKind.Swap)
                {
                    move = new Move(kind, a, _random.Next(A.Count), 0, b, _random.Next(B.Count));
                    return true;
                }

                int i = _random.Next(A.Count + 1);
                int j = _random.Next(B.Count + 1);
                if (!Moves.CanExchangeTails(A, B) || (i == 0 && j == 0) || (i == A.Count && j == B.Count))
                    return false;
                move = new Move(kind, a, i, 0, b, j);
                return true;
            }

            default:
            {
                int a = _random.Next(routes);
                var A = _routes[a];
                if (A.Count < 2)
                    return false;
                int i = _random.Next(A.Count - 1);
                int j = i + 1 + _random.Next(A.Count - 1 - i);
                move = new Move(kind, a, i, 0, 0, j);
                return true;
            }
        }
    }

    private double Evaluate(in Move m) => m.Kind switch
    {
        MoveKind.Relocate => Moves.RelocateDelta(_routes[m.A], m.I, m.K, _routes[m.B], m.J),
        MoveKind.Swap => Moves.SwapDelta(_routes[m.A], m.I, _routes[m.B], m.J),
        MoveKind.TwoOptStar => Moves.TwoOptStarDelta(_routes[m.A], m.I, _routes[m.B], m.J),
        _ => Moves.TwoOptDelta(_routes[m.A], m.I, m.J),
    };

    private void Apply(in Move m)
    {
        switch (m.Kind)
        {
            case MoveKind.Relocate: Moves.ApplyRelocate(_routes[m.A], m.I, m.K, _routes[m.B], m.J); break;
            case MoveKind.Swap: Moves.ApplySwap(_routes[m.A], m.I, _routes[m.B], m.J); break;
            case MoveKind.TwoOptStar: Moves.ApplyTwoOptStar(_routes[m.A], m.I, _routes[m.B], m.J); break;
            default: Moves.ApplyTwoOpt(_routes[m.A], m.I, m.J); break;
        }

        // An emptied route means one vehicle less; it is never reopened.
        _routes.RemoveAll(r => r.IsEmpty);
    }

    private Solution Snapshot() => new(_routes.Select(r => r.ToRoute()));
}
