using System.Diagnostics;
using VrpBenchmark.Algorithms.Improvement;
using VrpBenchmark.Algorithms.Routing;
using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Algorithms.Metaheuristics.Alns;

public sealed record AlnsParameters
{
    /// <summary>Maximum share of the budget spent on removing vehicles before minimising distance.</summary>
    public double VehicleMinimizationShare { get; init; } = 0.5;
    public bool StopAtCapacityBound { get; init; } = true;

    /// <summary>Removed customers per iteration: uniformly from [MinRemoved, MaxRemovedShare · n].</summary>
    public int MinRemoved { get; init; } = 4;
    public double MaxRemovedShare { get; init; } = 0.4;

    /// <summary>A solution this much worse than the start (by distance) is accepted with probability 0.5 at the beginning.</summary>
    public double InitialWorsening { get; init; } = 0.05;
    public double FinalTemperatureRatio { get; init; } = 0.002;

    /// <summary>Scores: new global best / better than current / accepted although worse.</summary>
    public double ScoreBest { get; init; } = 33;
    public double ScoreBetter { get; init; } = 9;
    public double ScoreAccepted { get; init; } = 13;

    /// <summary>Weights are updated every SegmentLength iterations: w = (1 − r)·w + r·(score / uses).</summary>
    public int SegmentLength { get; init; } = 100;
    public double ReactionFactor { get; init; } = 0.1;
}

/// <summary>
/// Adaptive Large Neighbourhood Search (Ropke &amp; Pisinger, 2006; Pisinger &amp; Ropke, 2007).
/// Each iteration destroys part of the current solution and repairs it with operators chosen by roulette
/// wheel; operator weights adapt to how successful they were. New solutions are accepted with the simulated
/// annealing criterion.
///
/// Two phases follow the hierarchical objective:
/// 1. Vehicle minimisation: remove the smallest route, cap the fleet at one vehicle less and search until all
///    customers fit again (unserved customers are penalised); repeat with the next route.
/// 2. Distance minimisation with the fleet found in phase 1.
/// </summary>
public sealed class Alns : ISolver
{
    private readonly AlnsParameters _parameters;
    private IDestroyOperator[] _destroy = [];
    private IRepairOperator[] _repair = [];
    private double[] _destroyWeights = [], _repairWeights = [];
    private double[] _destroyScores = [], _repairScores = [];
    private int[] _destroyUses = [], _repairUses = [];
    private Random _random = new();

    public Alns(AlnsParameters? parameters = null) => _parameters = parameters ?? new AlnsParameters();

    public string Name => "ALNS";

    public long LastIterations { get; private set; }
    public long LastVehicleMinimizationIterations { get; private set; }
    public TimeSpan LastDistancePhaseStart { get; private set; }

    /// <summary>Final operator weights of the last run, for analysis.</summary>
    public IReadOnlyDictionary<string, double> LastWeights { get; private set; } = new Dictionary<string, double>();

    public SolverResult Solve(ProblemInstance instance, SolverOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        _random = new Random(options.Seed);
        InitOperators(instance);

        var start = new I1LocalSearch().Solve(instance, options).Solution;
        double penalty = 2 * MaxDistance(instance) + 1;
        int fleet = instance.VehicleTypes[0].Count;
        var best = new PartialSolution(instance, start.UsedRoutes.Select(r => new RouteState(instance, r.VehicleType, r.Visits)), [], fleet, penalty);
        best.MaxRoutes = best.Routes.Count;
        var history = new List<ProgressPoint> { new(stopwatch.Elapsed, best.Cost) };

        double t0 = -_parameters.InitialWorsening * best.Distance / Math.Log(0.5);
        double share = _parameters.VehicleMinimizationShare;
        long iteration = 0;

        // Phase 1: vehicle minimisation.
        // Each route can carry up to Capacity + Epsilon under the solver's feasibility checks.
        double minimumRoutes = 1;
        if (_parameters.StopAtCapacityBound)
        {
            double demand = instance.Customers.Sum(i => instance.Nodes[i].Demand);
            double capacity = instance.VehicleTypes[0].Capacity + Evaluator.Epsilon;
            minimumRoutes = Math.Max(1, Math.Ceiling(demand / capacity));
        }
        var reduced = WithoutSmallestRoute(best, minimumRoutes);
        double phaseBest = reduced?.Cost ?? 0;
        while (reduced is not null)
        {
            double progress = options.Progress(stopwatch.Elapsed, iteration);
            if (progress >= share)
                break;

            iteration++;
            double temperature = t0 * Math.Pow(_parameters.FinalTemperatureRatio, progress / share);
            reduced = Iterate(reduced, ref phaseBest, temperature);

            if (reduced.IsComplete)
            {
                best = reduced.Clone();
                history.Add(new ProgressPoint(stopwatch.Elapsed, best.Cost));
                reduced = WithoutSmallestRoute(best, minimumRoutes);
                phaseBest = reduced?.Cost ?? 0;
            }
        }

        // Phase 2: distance minimisation with the smallest fleet found.
        var current = best.Clone();
        current.MaxRoutes = best.Routes.Count;
        phaseBest = current.Cost;
        LastVehicleMinimizationIterations = iteration;
        LastDistancePhaseStart = stopwatch.Elapsed;
        var distanceBudget = options with
        {
            TimeLimit = options.TimeLimit - LastDistancePhaseStart,
            MaxIterations = options.MaxIterations - iteration,
        };

        while (true)
        {
            var elapsed = stopwatch.Elapsed;
            if (options.Progress(elapsed, iteration) >= 1)
                break;

            double progress = distanceBudget.Progress(elapsed - LastDistancePhaseStart, iteration - LastVehicleMinimizationIterations);
            iteration++;
            double temperature = t0 * Math.Pow(_parameters.FinalTemperatureRatio, progress);
            current = Iterate(current, ref phaseBest, temperature);

            if (current.IsComplete && current.Cost < best.Cost - 1e-9)
            {
                best = current.Clone();
                history.Add(new ProgressPoint(stopwatch.Elapsed, best.Cost));
            }
        }

        LastIterations = iteration;
        LastWeights = _destroy.Select((d, i) => (d.Name, _destroyWeights[i]))
            .Concat(_repair.Select((r, i) => (r.Name, _repairWeights[i])))
            .ToDictionary(x => x.Name, x => x.Item2);

        var polished = new LocalSearch(instance).Improve(best.ToSolution());
        history.Add(new ProgressPoint(stopwatch.Elapsed, Evaluator.Evaluate(instance, polished).Cost));
        return new SolverResult(polished, stopwatch.Elapsed, history, iteration);
    }

    private PartialSolution Iterate(PartialSolution current, ref double phaseBest, double temperature)
    {
        int d = Roulette(_destroyWeights);
        int r = Roulette(_repairWeights);

        int customers = current.Instance.Customers.Count;
        int min = Math.Min(_parameters.MinRemoved, customers);
        int max = Math.Max(min, (int)(_parameters.MaxRemovedShare * customers));

        var candidate = current.Clone();
        _destroy[d].Destroy(candidate, _random.Next(min, max + 1), _random);
        _repair[r].Repair(candidate, _random);

        double score = 0;
        double candidateCost = candidate.Cost, currentCost = current.Cost;
        bool accepted = candidateCost < currentCost
            || _random.NextDouble() < Math.Exp(-(candidateCost - currentCost) / temperature);

        if (candidateCost < phaseBest - 1e-9)
        {
            phaseBest = candidateCost;
            score = _parameters.ScoreBest;
        }
        else if (candidateCost < currentCost - 1e-9)
        {
            score = _parameters.ScoreBetter;
        }
        else if (accepted)
        {
            score = _parameters.ScoreAccepted;
        }

        _destroyScores[d] += score;
        _repairScores[r] += score;
        _destroyUses[d]++;
        _repairUses[r]++;
        if (_destroyUses.Sum() % _parameters.SegmentLength == 0)
            UpdateWeights();

        return accepted ? candidate : current;
    }

    private void InitOperators(ProblemInstance instance)
    {
        _destroy = [new RandomRemoval(), new WorstRemoval(), new ShawRemoval(instance), new RouteRemoval()];
        _repair = [new InsertionRepair(1), new InsertionRepair(2), new InsertionRepair(3)];
        _destroyWeights = [.. _destroy.Select(_ => 1.0)];
        _repairWeights = [.. _repair.Select(_ => 1.0)];
        _destroyScores = new double[_destroy.Length];
        _repairScores = new double[_repair.Length];
        _destroyUses = new int[_destroy.Length];
        _repairUses = new int[_repair.Length];
    }

    private void UpdateWeights()
    {
        Update(_destroyWeights, _destroyScores, _destroyUses);
        Update(_repairWeights, _repairScores, _repairUses);

        void Update(double[] weights, double[] scores, int[] uses)
        {
            double rf = _parameters.ReactionFactor;
            for (int i = 0; i < weights.Length; i++)
            {
                if (uses[i] > 0)
                    weights[i] = (1 - rf) * weights[i] + rf * scores[i] / uses[i];

                // A small floor keeps every operator selectable.
                weights[i] = Math.Max(weights[i], 0.05);
                scores[i] = 0;
                uses[i] = 0;
            }
        }
    }

    private int Roulette(double[] weights)
    {
        double pick = _random.NextDouble() * weights.Sum();
        for (int i = 0; i < weights.Length; i++)
        {
            pick -= weights[i];
            if (pick <= 0)
                return i;
        }
        return weights.Length - 1;
    }

    /// <summary>Tries a smaller fleet only while another reduction is possible.</summary>
    private static PartialSolution? WithoutSmallestRoute(PartialSolution solution, double minimumRoutes)
    {
        if (solution.Routes.Count <= minimumRoutes)
            return null;

        var copy = solution.Clone();
        var smallest = copy.Routes.MinBy(r => r.Count)!;
        copy.Remove(smallest.Visits.ToArray());
        copy.MaxRoutes = copy.Routes.Count;
        return copy;
    }

    private static double MaxDistance(ProblemInstance instance)
    {
        double max = 0;
        for (int i = 0; i < instance.Matrix.Size; i++)
            for (int j = 0; j < instance.Matrix.Size; j++)
                max = Math.Max(max, instance.Matrix.Distance(i, j));
        return max;
    }
}
