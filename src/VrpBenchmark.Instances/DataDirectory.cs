namespace VrpBenchmark.Instances;

/// <summary>Locates the repository's "data" folder so tools and tests work from any build output directory.</summary>
public static class DataDirectory
{
    public static string Find()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "data", "solomon");
                if (Directory.Exists(candidate))
                    return Path.Combine(dir.FullName, "data");
            }
        }

        throw new DirectoryNotFoundException("Could not find the 'data/solomon' folder in any parent directory.");
    }

    public static string SolomonInstance(string dataDir, string name) =>
        Path.Combine(dataDir, "solomon", "instances", name.ToLowerInvariant() + ".txt");

    public static string SolomonBksSolution(string dataDir, string name) =>
        Path.Combine(dataDir, "solomon", "bks-solutions", name.ToLowerInvariant() + ".sol");

    public static string SolomonBksTable(string dataDir) =>
        Path.Combine(dataDir, "solomon", "bks.csv");
}
