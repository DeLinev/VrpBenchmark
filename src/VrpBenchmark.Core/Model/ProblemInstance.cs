namespace VrpBenchmark.Core.Model;

public sealed class ProblemInstance
{
    private readonly Dictionary<string, int> _indexById;

    public string Name { get; }
    public IReadOnlyList<Node> Nodes { get; }
    public IReadOnlyList<VehicleType> VehicleTypes { get; }
    public TravelMatrix Matrix { get; }
    public IReadOnlyList<int> Depots { get; }
    public IReadOnlyList<int> Customers { get; }

    public ProblemInstance(string name, IReadOnlyList<Node> nodes, IReadOnlyList<VehicleType> vehicleTypes, TravelMatrix matrix)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Index != i)
                throw new ArgumentException($"Node '{nodes[i].Id}' has index {nodes[i].Index}, expected {i}.");
        }

        if (matrix.Size != nodes.Count)
            throw new ArgumentException($"Matrix size {matrix.Size} does not match node count {nodes.Count}.");

        _indexById = new Dictionary<string, int>(nodes.Count);
        foreach (var node in nodes)
        {
            if (!_indexById.TryAdd(node.Id, node.Index))
                throw new ArgumentException($"Duplicate node id '{node.Id}'.");
        }

        foreach (var vehicle in vehicleTypes)
        {
            if (nodes[vehicle.StartDepot].Kind != NodeKind.Depot || nodes[vehicle.EndDepot].Kind != NodeKind.Depot)
                throw new ArgumentException($"Vehicle type '{vehicle.Id}' must start and end at depot nodes.");
        }

        Name = name;
        Nodes = nodes;
        VehicleTypes = vehicleTypes;
        Matrix = matrix;
        Depots = nodes.Where(n => n.Kind == NodeKind.Depot).Select(n => n.Index).ToArray();
        Customers = nodes.Where(n => n.Kind == NodeKind.Customer).Select(n => n.Index).ToArray();
    }

    public int IndexOf(string id) =>
        _indexById.TryGetValue(id, out int index)
            ? index
            : throw new KeyNotFoundException($"Instance '{Name}' has no node with id '{id}'.");
}
