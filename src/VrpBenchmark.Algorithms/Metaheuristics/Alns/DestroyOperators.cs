using VrpBenchmark.Algorithms.Routing;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Algorithms.Metaheuristics.Alns;

public interface IDestroyOperator
{
    string Name { get; }

    /// <summary>Moves about <paramref name="count"/> customers from the routes into the request bank.</summary>
    void Destroy(PartialSolution solution, int count, Random random);
}

/// <summary>Removes customers chosen uniformly at random.</summary>
public sealed class RandomRemoval : IDestroyOperator
{
    public string Name => "random";

    public void Destroy(PartialSolution solution, int count, Random random)
    {
        var assigned = solution.AssignedCustomers().ToArray();
        random.Shuffle(assigned);
        solution.Remove(assigned.Take(count).ToArray());
    }
}

/// <summary>
/// Removes customers whose removal saves the most, i.e. those that are "badly placed" (Ropke &amp; Pisinger, 2006).
/// The ranking is randomised: the customer at rank ⌊y^p · n⌋ is taken, y ~ U(0, 1).
/// </summary>
public sealed class WorstRemoval(double randomness = 3) : IDestroyOperator
{
    public string Name => "worst";

    public void Destroy(PartialSolution solution, int count, Random random)
    {
        var matrix = solution.Instance.Matrix;

        for (int removed = 0; removed < count && solution.Routes.Count > 0; removed++)
        {
            var candidates = new List<(int Customer, double Saving)>();
            foreach (var route in solution.Routes)
            {
                for (int i = 0; i < route.Count; i++)
                {
                    var without = Segment.Concat(matrix, route.Prefix(i), route.Suffix(i + 1));
                    candidates.Add((route.Visits[i], route.Cost - route.CostIf(without, route.Count - 1)));
                }
            }

            candidates.Sort((x, y) => y.Saving.CompareTo(x.Saving));
            int rank = (int)(Math.Pow(random.NextDouble(), randomness) * candidates.Count);
            solution.Remove([candidates[rank].Customer]);
        }
    }
}

/// <summary>
/// Shaw (relatedness) removal: removes customers that are similar to each other, so that repair can reshuffle them.
/// Relatedness R(i, j) = 9·d(i,j)/dmax + 3·|e_i − e_j|/emax + 2·|q_i − q_j|/qmax, smaller means more related,
/// where d is distance, e the window start and q the demand (weights from Ropke &amp; Pisinger, 2006).
/// </summary>
public sealed class ShawRemoval : IDestroyOperator
{
    private readonly ProblemInstance _instance;
    private readonly double _maxDistance, _maxStart, _maxDemand;
    private readonly double _randomness;

    public ShawRemoval(ProblemInstance instance, double randomness = 6)
    {
        _instance = instance;
        _randomness = randomness;
        var customers = instance.Customers.Select(c => instance.Nodes[c]).ToList();
        _maxDistance = Math.Max(1e-9, customers.Max(a => customers.Max(b => instance.Matrix.Distance(a.Index, b.Index))));
        _maxStart = Math.Max(1e-9, customers.Max(c => c.Window.Start));
        _maxDemand = Math.Max(1e-9, customers.Max(c => c.Demand));
    }

    public string Name => "shaw";

    public double Relatedness(int i, int j)
    {
        var a = _instance.Nodes[i];
        var b = _instance.Nodes[j];
        return 9 * _instance.Matrix.Distance(i, j) / _maxDistance
             + 3 * Math.Abs(a.Window.Start - b.Window.Start) / _maxStart
             + 2 * Math.Abs(a.Demand - b.Demand) / _maxDemand;
    }

    public void Destroy(PartialSolution solution, int count, Random random)
    {
        var remaining = solution.AssignedCustomers().ToList();
        if (remaining.Count == 0)
            return;

        int first = remaining[random.Next(remaining.Count)];
        var removed = new List<int> { first };
        remaining.Remove(first);

        while (removed.Count < count && remaining.Count > 0)
        {
            int reference = removed[random.Next(removed.Count)];
            remaining.Sort((x, y) => Relatedness(reference, x).CompareTo(Relatedness(reference, y)));
            int rank = (int)(Math.Pow(random.NextDouble(), _randomness) * remaining.Count);
            removed.Add(remaining[rank]);
            remaining.RemoveAt(rank);
        }

        solution.Remove(removed);
    }
}

/// <summary>Removes one whole random route regardless of the requested count; helps to get rid of vehicles.</summary>
public sealed class RouteRemoval : IDestroyOperator
{
    public string Name => "route";

    public void Destroy(PartialSolution solution, int count, Random random)
    {
        if (solution.Routes.Count == 0)
            return;

        var route = solution.Routes[random.Next(solution.Routes.Count)];
        solution.Remove(route.Visits.ToArray());
    }
}
