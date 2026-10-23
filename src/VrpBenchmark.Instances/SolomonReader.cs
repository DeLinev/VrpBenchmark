using System.Globalization;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Instances;

public sealed record SolomonOptions
{
    /// <summary>Keep only the first N customers (the classic 25- and 50-customer Solomon variants).</summary>
    public int? MaxCustomers { get; init; }

    public double VehicleFixedCost { get; init; } = SolomonReader.HierarchicalVehicleCost;
}

public static class SolomonReader
{
    /// <summary>
    /// Fixed cost per vehicle that dominates any realistic distance change on Solomon instances, so minimising
    /// the total cost reproduces the hierarchical objective: fewer vehicles first, then shorter distance.
    /// </summary>
    public const double HierarchicalVehicleCost = 10_000;

    public static ProblemInstance Read(string path, SolomonOptions? options = null)
    {
        using var reader = new StreamReader(path);
        return Parse(reader, options);
    }

    public static ProblemInstance Parse(TextReader reader, SolomonOptions? options = null)
    {
        options ??= new SolomonOptions();

        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line.Trim());

        string name = lines.FirstOrDefault(l => l.Length > 0)
            ?? throw new FormatException("Solomon file is empty.");

        int vehicleHeader = lines.FindIndex(l => l.StartsWith("NUMBER", StringComparison.OrdinalIgnoreCase));
        if (vehicleHeader < 0)
            throw new FormatException("Vehicle section (NUMBER CAPACITY) not found.");

        var vehicleLine = ParseNumbers(lines.Skip(vehicleHeader + 1).First(l => l.Length > 0));
        int vehicleCount = (int)vehicleLine[0];
        double capacity = vehicleLine[1];

        int customerHeader = lines.FindIndex(l => l.StartsWith("CUST NO", StringComparison.OrdinalIgnoreCase));
        if (customerHeader < 0)
            throw new FormatException("Customer section (CUST NO.) not found.");

        var nodes = new List<Node>();
        foreach (string line in lines.Skip(customerHeader + 1).Where(l => l.Length > 0))
        {
            var f = ParseNumbers(line);
            if (f.Length != 7)
                throw new FormatException($"Expected 7 values in customer line, got {f.Length}: '{line}'.");

            var kind = nodes.Count == 0 ? NodeKind.Depot : NodeKind.Customer;
            if (kind == NodeKind.Customer && nodes.Count > options.MaxCustomers)
                break;

            nodes.Add(new Node(
                Index: nodes.Count,
                Id: ((int)f[0]).ToString(CultureInfo.InvariantCulture),
                Kind: kind,
                X: f[1],
                Y: f[2],
                Demand: f[3],
                Window: new TimeWindow(f[4], f[5]),
                ServiceTime: f[6]));
        }

        if (nodes.Count < 2)
            throw new FormatException("Solomon file must contain a depot and at least one customer.");

        var depot = nodes[0];
        var vehicle = new VehicleType(
            Id: "vehicle",
            Capacity: capacity,
            Count: vehicleCount,
            StartDepot: depot.Index,
            EndDepot: depot.Index,
            Shift: depot.Window,
            FixedCost: options.VehicleFixedCost,
            CostPerDistance: 1.0);

        if (options.MaxCustomers is int max)
            name = $"{name}_{max}";

        return new ProblemInstance(name, nodes, [vehicle], TravelMatrix.Euclidean(nodes));
    }

    private static double[] ParseNumbers(string line) =>
        line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture))
            .ToArray();
}
