using System.Text.Json;
using System.Text.Json.Serialization;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Instances;

public static class InstanceFile
{
    public static bool IsCrewFile(string path) =>
        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);

    public static ProblemInstance Read(string path, int? maxCustomers = null)
    {
        if (!IsCrewFile(path))
            return SolomonReader.Read(path, new SolomonOptions { MaxCustomers = maxCustomers });

        using var stream = File.OpenRead(path);
        var data = JsonSerializer.Deserialize<CrewFile>(stream, JsonOptions)
            ?? throw new FormatException("Expected a JSON object containing a depot, crews and orders.");
        if (string.IsNullOrWhiteSpace(data.Name) || data.Orders.Length == 0)
            throw new FormatException("A task needs a name and at least one order.");
        if (!double.IsFinite(data.SpeedKmh) || data.SpeedKmh <= 0 || maxCustomers is <= 0)
            throw new FormatException("speedKmh and the customer limit must be positive.");

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<Node>();
        AddStop(data.Depot, NodeKind.Depot, 0, 0, 0);
        foreach (var order in data.Orders)
        {
            if (order is null)
                throw new FormatException("An order must be an object.");
            AddStop(order, NodeKind.Customer, order.WeightKg, order.VolumeM3, order.ServiceMinutes);
        }
        if (maxCustomers is int limit && limit < nodes.Count - 1)
            nodes.RemoveRange(limit + 1, nodes.Count - 1 - limit);

        var crewIds = new HashSet<string>(StringComparer.Ordinal);
        var crews = new List<VehicleType>();
        foreach (var crew in data.Crews)
        {
            if (crew is null)
                throw new FormatException("A crew must be an object.");
            CheckId(crew.Id, crewIds);
            if (!Nonnegative(crew.CapacityKg) || !Nonnegative(crew.VolumeCapacityM3) ||
                !Nonnegative(crew.FixedCost) || !Nonnegative(crew.CostPerKm))
                throw new FormatException($"Crew '{crew.Id}' needs finite, nonnegative capacities and costs.");
            crews.Add(new VehicleType(crew.Id, crew.CapacityKg, 1, 0, 0, crew.Shift.ToWindow(crew.Id), crew.FixedCost, crew.CostPerKm)
                { VolumeCapacity = crew.VolumeCapacityM3 });
        }

        return new ProblemInstance(data.Name, nodes, crews, TravelMatrix.Euclidean(nodes, data.SpeedKmh / 60));

        void AddStop(Stop stop, NodeKind kind, double weight, double volume, double service)
        {
            CheckId(stop.Id, nodeIds);
            if (!double.IsFinite(stop.X) || !double.IsFinite(stop.Y) || !Nonnegative(weight) || !Nonnegative(volume) || !Nonnegative(service))
                throw new FormatException($"Invalid coordinates, load or service duration for '{stop.Id}'.");
            nodes.Add(new Node(nodes.Count, stop.Id, kind, stop.X, stop.Y, weight, stop.Window.ToWindow(stop.Id), service) { Volume = volume });
        }
    }

    private static bool Nonnegative(double value) => double.IsFinite(value) && value >= 0;

    private sealed record Window(double Start, double End)
    {
        public TimeWindow ToWindow(string id)
        {
            if (!Nonnegative(Start) || !double.IsFinite(End) || End < Start)
                throw new FormatException($"Invalid window for '{id}': expected 0 <= start <= end in minutes.");
            return new TimeWindow(Start, End);
        }
    }

    private static void CheckId(string id, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(char.IsWhiteSpace) || !seen.Add(id))
            throw new FormatException($"Expected a unique identifier without whitespace, got '{id}'.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed record CrewFile(string Name, double SpeedKmh, Stop Depot, Crew[] Crews, Order[] Orders);
    private record Stop(string Id, double X, double Y, Window Window);
    private sealed record Order(string Id, double X, double Y, Window Window, double WeightKg, double VolumeM3, double ServiceMinutes)
        : Stop(Id, X, Y, Window);
    private sealed record Crew(string Id, double CapacityKg, double VolumeCapacityM3, Window Shift, double FixedCost, double CostPerKm);
}
