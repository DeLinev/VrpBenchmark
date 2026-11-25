using System.Globalization;
using System.Text.RegularExpressions;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Instances;

/// <summary>
/// Plain-text solution format used by SINTEF for the published Solomon solutions:
/// header lines followed by "Route k : id id id ..." lines with external customer ids.
/// Crew tasks additionally identify the assigned crew in square brackets before the colon.
/// </summary>
public static partial class SolutionFile
{
    [GeneratedRegex(@"^\s*Route\s+\d+\s*(?:\[(?<crew>[^\]]+)\]\s*)?:(?<ids>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex RouteLine();

    [GeneratedRegex(@"^\s*Unassigned\s*:(?<ids>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex UnassignedLine();

    public static Solution Read(string path, ProblemInstance instance)
    {
        using var reader = new StreamReader(path);
        return Parse(reader, instance);
    }

    public static Solution Parse(TextReader reader, ProblemInstance instance)
    {
        var solution = new Solution();
        var crewTypes = instance.VehicleTypes.Select((v, index) => (v.Id, index)).ToDictionary(v => v.Id, v => v.index);
        var nodes = instance.Nodes.ToDictionary(n => n.Id, n => n.Index);

        while (reader.ReadLine() is { } line)
        {
            var unassigned = UnassignedLine().Match(line);
            if (unassigned.Success)
            {
                solution.Unassigned.AddRange(ReadCustomers(unassigned.Groups["ids"].Value));
                continue;
            }
            var match = RouteLine().Match(line);
            if (!match.Success)
            {
                if (line.TrimStart().StartsWith("Route ", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException($"Invalid solution route: {line}");
                if (line.TrimStart().StartsWith("Unassigned", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException($"Invalid unassigned order list: {line}");
                continue;
            }

            int type = 0;
            if (match.Groups["crew"].Success)
            {
                string crew = Uri.UnescapeDataString(match.Groups["crew"].Value);
                if (!crewTypes.TryGetValue(crew, out type))
                    throw new FormatException($"Unknown crew '{crew}' in solution.");
            }
            else if (crewTypes.Count != 1)
                throw new FormatException("A route in a multi-crew task needs a crew ID in square brackets.");
            solution.Routes.Add(new Route(type, ReadCustomers(match.Groups["ids"].Value)));
        }

        return solution;

        IEnumerable<int> ReadCustomers(string ids) =>
            ids.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(id =>
                nodes.TryGetValue(id, out int index) ? index : instance.IndexOf(NormalizeId(id)));
    }

    // Some published files pad numeric ids with zeros ("092").
    private static string NormalizeId(string id) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : id;

    public static void Write(string path, ProblemInstance instance, Solution solution, string author, bool includeCrew = false)
    {
        using var writer = new StreamWriter(path);
        Write(writer, instance, solution, author, includeCrew);
    }

    public static void Write(TextWriter writer, ProblemInstance instance, Solution solution, string author, bool includeCrew = false)
    {
        writer.WriteLine($"Instance name : {instance.Name}");
        writer.WriteLine($"Authors       : {author}");
        writer.WriteLine($"Date          : {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        writer.WriteLine("Solution");

        int number = 1;
        foreach (var route in solution.UsedRoutes)
        {
            var ids = route.Visits.Select(v => instance.Nodes[v].Id);
            string crew = includeCrew || instance.VehicleTypes.Count > 1
                ? $" [{Uri.EscapeDataString(instance.VehicleTypes[route.VehicleType].Id)}]" : "";
            writer.WriteLine($"Route {number++}{crew} : {string.Join(' ', ids)}");
        }
        if (!solution.IsComplete)
            writer.WriteLine($"Unassigned : {string.Join(' ', solution.Unassigned.Select(i => instance.Nodes[i].Id))}");
    }
}
