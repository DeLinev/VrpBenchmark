using VrpBenchmark.Core.Evaluation;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Algorithms.Routing;

/// <summary>
/// A route together with cached prefix and suffix segments, so that any move that cuts the route
/// into a prefix and a suffix and inserts something between them is evaluated in O(1).
/// </summary>
public sealed class RouteState
{
    private readonly ProblemInstance _instance;
    private Segment[] _prefix = [];
    private Segment[] _suffix = [];

    public int VehicleTypeIndex { get; }
    public VehicleType Vehicle { get; }
    public List<int> Visits { get; }
    public Segment StartDepot { get; }
    public Segment EndDepot { get; }
    public double Cost { get; private set; }

    public int Count => Visits.Count;
    public bool IsEmpty => Visits.Count == 0;
    public double Load => _prefix[^1].Load;
    public Segment Whole => Segment.Concat(_instance.Matrix, _prefix[^1], EndDepot);

    public RouteState(ProblemInstance instance, int vehicleTypeIndex, IEnumerable<int> visits)
    {
        _instance = instance;
        VehicleTypeIndex = vehicleTypeIndex;
        Vehicle = instance.VehicleTypes[vehicleTypeIndex];
        Visits = [.. visits];
        StartDepot = Segment.ForDepot(instance.Nodes[Vehicle.StartDepot], Vehicle.Shift);
        EndDepot = Segment.ForDepot(instance.Nodes[Vehicle.EndDepot], Vehicle.Shift);
        Update();
    }

    private RouteState(RouteState other)
    {
        _instance = other._instance;
        _prefix = (Segment[])other._prefix.Clone();
        _suffix = (Segment[])other._suffix.Clone();
        VehicleTypeIndex = other.VehicleTypeIndex;
        Vehicle = other.Vehicle;
        Visits = [.. other.Visits];
        StartDepot = other.StartDepot;
        EndDepot = other.EndDepot;
        Cost = other.Cost;
    }

    /// <summary>Copies the cached segments instead of recomputing them.</summary>
    public RouteState Clone() => new(this);

    /// <summary>Start depot followed by the first <paramref name="count"/> visits.</summary>
    public ref readonly Segment Prefix(int count) => ref _prefix[count];

    /// <summary>Visits from position <paramref name="position"/> to the end, followed by the end depot.</summary>
    public ref readonly Segment Suffix(int position) => ref _suffix[position];

    public Segment NodeAt(int position) => Segment.ForNode(_instance.Nodes[Visits[position]]);

    public TravelMatrix Matrix => _instance.Matrix;

    /// <summary>Visits in positions [from, to) in their current order; the range must not be empty.</summary>
    public Segment Fold(int from, int to)
    {
        var segment = NodeAt(from);
        for (int i = from + 1; i < to; i++)
            segment = Segment.Concat(_instance.Matrix, segment, NodeAt(i));
        return segment;
    }

    /// <summary>Visits in positions [from, to) in reverse order; the range must not be empty.</summary>
    public Segment FoldReversed(int from, int to)
    {
        var segment = NodeAt(to - 1);
        for (int i = to - 2; i >= from; i--)
            segment = Segment.Concat(_instance.Matrix, segment, NodeAt(i));
        return segment;
    }

    /// <summary>Cost this route would have if its visits formed <paramref name="route"/> (depots included).</summary>
    public double CostIf(in Segment route, int visitCount) => CostOf(Vehicle, route, visitCount);

    /// <summary>Must be called after <see cref="Visits"/> has been modified.</summary>
    public void Update()
    {
        var matrix = _instance.Matrix;
        int n = Visits.Count;

        if (_prefix.Length != n + 1)
        {
            _prefix = new Segment[n + 1];
            _suffix = new Segment[n + 1];
        }

        _prefix[0] = StartDepot;
        for (int i = 0; i < n; i++)
            _prefix[i + 1] = Segment.Concat(matrix, _prefix[i], NodeAt(i));

        _suffix[n] = EndDepot;
        for (int i = n - 1; i >= 0; i--)
            _suffix[i] = Segment.Concat(matrix, NodeAt(i), _suffix[i + 1]);

        Cost = CostOf(Vehicle, Segment.Concat(matrix, _prefix[n], EndDepot), n);
    }

    /// <summary>Route cost for the search; infeasible routes get infinite cost.</summary>
    public static double CostOf(VehicleType vehicle, in Segment route, int visitCount)
    {
        if (visitCount == 0)
            return 0;
        if (route.TimeWarp > Evaluator.Epsilon || route.Load > vehicle.Capacity + Evaluator.Epsilon)
            return double.PositiveInfinity;
        return vehicle.FixedCost + vehicle.CostPerDistance * route.Distance;
    }

    public Route ToRoute() => new(VehicleTypeIndex, Visits);
}
