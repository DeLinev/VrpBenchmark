using System.Globalization;
using System.Text.RegularExpressions;
using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Instances;

/// <summary>
/// Plain-text solution format used by SINTEF for the published Solomon solutions:
/// header lines followed by "Route k : id id id ..." lines with external customer ids.
/// All routes are assigned to vehicle type 0, which is enough for homogeneous-fleet benchmarks.
/// </summary>
public static partial class SolutionFile
{
    [GeneratedRegex(@"^\s*Route\s+\d+\s*:(?<ids>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex RouteLine();

    public static Solution Read(string path, ProblemInstance instance)
    {
        using var reader = new StreamReader(path);
        return Parse(reader, instance);
    }

    public static Solution Parse(TextReader reader, ProblemInstance instance)
    {
        var solution = new Solution();

        while (reader.ReadLine() is { } line)
        {
            var match = RouteLine().Match(line);
            if (!match.Success)
                continue;

            var ids = match.Groups["ids"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            solution.Routes.Add(new Route(0, ids.Select(id => instance.IndexOf(NormalizeId(id)))));
        }

        return solution;
    }

    // Some published files pad numeric ids with zeros ("092").
    private static string NormalizeId(string id) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : id;

    public static void Write(string path, ProblemInstance instance, Solution solution, string author)
    {
        using var writer = new StreamWriter(path);
        Write(writer, instance, solution, author);
    }

    public static void Write(TextWriter writer, ProblemInstance instance, Solution solution, string author)
    {
        writer.WriteLine($"Instance name : {instance.Name}");
        writer.WriteLine($"Authors       : {author}");
        writer.WriteLine($"Date          : {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        writer.WriteLine("Solution");

        int number = 1;
        foreach (var route in solution.UsedRoutes)
        {
            var ids = route.Visits.Select(v => instance.Nodes[v].Id);
            writer.WriteLine($"Route {number++} : {string.Join(' ', ids)}");
        }
    }
}
