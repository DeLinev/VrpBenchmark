using System.Diagnostics;
using VrpBenchmark.Algorithms.Construction;
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
    /// <summary>Preserve the Solomon fleet-first objective. Disable to compare complete plans by operating cost alone.</summary>
    public bool MinimizeFleetFirst { get; init; } = true;

    /// <summary>Removed customers per iteration: uniformly from [MinRemoved, MaxRemovedShare · n].</summary>
    public int MinRemoved { get; init; } = 4;
    public double MaxRemovedShare { get; init; } = 0.4;

    /// <summary>A solution this much worse than the start (by distance) is accepted with probability 0.5 at the beginning.</summary>
    public double InitialWorsening { get; init; } = 0.05;
    public double FinalTemperatureRatio { get; init; } = 0.002;

    /// <summary>Scores for a changed plan: new phase best / better than current / otherwise accepted.</summary>
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
/// 2. Operating cost minimisation with the fleet size found in phase 1.
/// In cost-only mode the entire available fleet remains eligible during the search.
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
    public long LastAcceptedRepeats { get; private set; }
    public long LastVehicleMinimizationIterations { get; private set; }
    public TimeSpan LastDistancePhaseStart { get; private set; }

    /// <summary>Final operator weights of the last run, for analysis.</summary>
    public IReadOnlyDictionary<string, double> LastWeights { get; private set; } = new Dictionary<string, double>();

    public SolverResult Solve(ProblemInstance instance, SolverOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        _random = new Random(options.Seed);
        LastAcceptedRepeats = 0;
        if (instance.Customers.Count == 0)
        {
            LastIterations = LastVehicleMinimizationIterations = 0;
            LastDistancePhaseStart = TimeSpan.Zero;
            LastWeights = new Dictionary<string, double>();
            return new SolverResult(new Solution(), stopwatch.Elapsed, [new ProgressPoint(stopwatch.Elapsed, 0)], 0);
        }
        var polish = new LocalSearch(instance);
        InitOperators(instance);
        int fleet = instance.VehicleTypes.Sum(v => v.Count);
        if (fleet == 0)
            throw new InvalidOperationException("No crew is available to serve the orders.");
        double maxDistance = MaxDistance(instance);
        var best = CreateStart(instance, options, fleet, maxDistance, polish);
        long iteration = 0;
        double t0 = -_parameters.InitialWorsening * Math.Max(1, best.Distance) / Math.Log(0.5);
        double phaseBest = best.Cost;
        while (!best.IsComplete && options.Progress(stopwatch.Elapsed, iteration) < 1)
        {
            iteration++;
            best = Iterate(best, ref phaseBest, t0);
        }
        if (!best.IsComplete)
            throw new InvalidOperationException("ALNS did not find a complete plan with the available crews within the search budget.");

        bool fleetFirst = _parameters.MinimizeFleetFirst;
        best.MaxRoutes = fleetFirst ? best.Routes.Count : fleet;
        var history = new List<ProgressPoint> { new(stopwatch.Elapsed, best.Cost) };

        double share = _parameters.VehicleMinimizationShare;
        long constructionIterations = iteration;

        // Phase 1: vehicle minimisation.
        // Each route can carry up to Capacity + Epsilon under the solver's feasibility checks.
        double minimumRoutes = 1;
        if (_parameters.StopAtCapacityBound)
        {
            double demand = instance.Customers.Sum(i => instance.Nodes[i].Demand);
            double volume = instance.Customers.Sum(i => instance.Nodes[i].Volume);
            minimumRoutes = Math.Max(CapacityBound(instance, demand, v => v.Capacity),
                CapacityBound(instance, volume, v => v.VolumeCapacity));
        }
        var reduced = fleetFirst ? WithoutSmallestRoute(best, minimumRoutes) : null;
        phaseBest = reduced?.Cost ?? 0;
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
        current.MaxRoutes = fleetFirst ? best.Routes.Count : fleet;
        phaseBest = current.Cost;
        long distanceStartIteration = iteration;
        LastVehicleMinimizationIterations = iteration - constructionIterations;
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

            double progress = distanceBudget.Progress(elapsed - LastDistancePhaseStart, iteration - distanceStartIteration);
            iteration++;
            double temperature = t0 * Math.Pow(_parameters.FinalTemperatureRatio, progress);
            current = Iterate(current, ref phaseBest, temperature);

            if (current.IsComplete && (fleetFirst && current.Routes.Count < best.Routes.Count ||
                (!fleetFirst || current.Routes.Count == best.Routes.Count) && current.Cost < best.Cost - 1e-9))
            {
                best = current.Clone();
                if (fleetFirst)
                    current.MaxRoutes = best.MaxRoutes = best.Routes.Count;
                history.Add(new ProgressPoint(stopwatch.Elapsed, best.Cost));
            }
        }

        LastIterations = iteration;
        LastWeights = _destroy.Select((d, i) => (d.Name, _destroyWeights[i]))
            .Concat(_repair.Select((r, i) => (r.Name, _repairWeights[i])))
            .ToDictionary(x => x.Name, x => x.Item2);

        var polished = polish.Improve(best.ToSolution());
        if (instance.VehicleTypes.Count > 1)
        {
            var final = new PartialSolution(instance, polished.UsedRoutes.Select(r => new RouteState(instance, r.VehicleType, r.Visits)), [], fleet, best.UnassignedPenalty);
            CrewAssignment.Improve(final);
            polished = final.ToSolution();
        }
        history.Add(new ProgressPoint(stopwatch.Elapsed, Evaluator.Evaluate(instance, polished).Cost));
        return new SolverResult(polished, stopwatch.Elapsed, history, iteration);
    }

    private static int CapacityBound(ProblemInstance instance, double demand, Func<VehicleType, double> capacity)
    {
        int used = 0;
        foreach (var type in instance.VehicleTypes.Where(v => v.Count > 0).OrderByDescending(capacity))
        {
            double perCrew = capacity(type) + Evaluator.Epsilon;
            if (demand <= perCrew * type.Count)
                return Math.Max(1, used + (int)Math.Ceiling(demand / perCrew));
            demand -= perCrew * type.Count;
            used += type.Count;
        }
        return used;
    }

    private PartialSolution CreateStart(ProblemInstance instance, SolverOptions options, int fleet, double maxDistance, LocalSearch polish)
    {
        if (instance.VehicleTypes.Count == 1)
        {
            var start = new SolomonI1().Solve(instance, options).Solution;
            if (start.UsedRoutes.All(r => Evaluator.Evaluate(instance, r).IsFeasible))
            {
                var routes = polish.Improve(start).UsedRoutes.Select(r => new RouteState(instance, r.VehicleType, r.Visits)).ToList();
                if (routes.Count <= fleet)
                    return new PartialSolution(instance, routes, [], fleet, 2 * maxDistance + 1);
            }
        }

        // Any complete plan uses at most two travel legs and one fixed charge per customer.
        double penalty = 1 + instance.Customers.Count * instance.VehicleTypes.Max(v => v.FixedCost + 2 * maxDistance * v.CostPerDistance);
        var plan = new PartialSolution(instance, [], instance.Customers, fleet, penalty);
        new InsertionRepair(2).Repair(plan, _random);
        CrewAssignment.Improve(plan);
        return plan;
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
        CrewAssignment.Improve(candidate);

        double score = 0;
        double candidateCost = candidate.Cost, currentCost = current.Cost;
        bool accepted = candidateCost < currentCost
            || _random.NextDouble() < Math.Exp(-(candidateCost - currentCost) / temperature);

        if (accepted && current.HasSameRoutesAs(candidate))
            LastAcceptedRepeats++;
        else if (candidateCost < phaseBest - 1e-9)
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
