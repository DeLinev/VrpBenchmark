namespace VrpBenchmark.Core.Model;

public sealed class Route
{
    public int VehicleType { get; set; }

    /// <summary>Customer node indices in visiting order. Start and end depots are implied by the vehicle type.</summary>
    public List<int> Visits { get; }

    public bool IsEmpty => Visits.Count == 0;

    public Route(int vehicleType, IEnumerable<int>? visits = null)
    {
        VehicleType = vehicleType;
        Visits = visits is null ? [] : [.. visits];
    }

    public Route Clone() => new(VehicleType, Visits);
}

public sealed class Solution
{
    public List<Route> Routes { get; }
    /// <summary>Customer node indices explicitly left for the dispatcher.</summary>
    public List<int> Unassigned { get; }
    public bool IsComplete => Unassigned.Count == 0;

    public Solution(IEnumerable<Route>? routes = null, IEnumerable<int>? unassigned = null)
    {
        Routes = routes is null ? [] : [.. routes];
        Unassigned = unassigned is null ? [] : [.. unassigned];
    }

    public IEnumerable<Route> UsedRoutes => Routes.Where(r => !r.IsEmpty);

    public Solution Clone() => new(Routes.Select(r => r.Clone()), Unassigned);
}
