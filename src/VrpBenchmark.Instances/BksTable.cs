using System.Globalization;

namespace VrpBenchmark.Instances;

public sealed record BksEntry(string Instance, int Vehicles, double Distance, string Reference);

/// <summary>Best known solutions loaded from a CSV file with header: instance,vehicles,distance,reference.</summary>
public sealed class BksTable
{
    private readonly Dictionary<string, BksEntry> _entries;

    private BksTable(Dictionary<string, BksEntry> entries) => _entries = entries;

    public IReadOnlyCollection<BksEntry> Entries => _entries.Values;

    public static BksTable Load(string path)
    {
        var entries = new Dictionary<string, BksEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var f = line.Split(',');
            if (f.Length < 3)
                throw new FormatException($"Invalid BKS line: '{line}'.");

            var entry = new BksEntry(
                Instance: f[0].Trim(),
                Vehicles: int.Parse(f[1], CultureInfo.InvariantCulture),
                Distance: double.Parse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture),
                Reference: f.Length > 3 ? f[3].Trim() : "");

            entries[entry.Instance] = entry;
        }

        return new BksTable(entries);
    }

    public bool TryGet(string instance, out BksEntry entry) => _entries.TryGetValue(instance, out entry!);
}
