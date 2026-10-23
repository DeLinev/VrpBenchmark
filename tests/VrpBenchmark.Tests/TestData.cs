using VrpBenchmark.Core.Model;
using VrpBenchmark.Instances;

namespace VrpBenchmark.Tests;

internal static class TestData
{
    public static readonly string Dir = DataDirectory.Find();

    public static ProblemInstance Solomon(string name, SolomonOptions? options = null) =>
        SolomonReader.Read(DataDirectory.SolomonInstance(Dir, name), options);

    public static Solution BksSolution(string name, ProblemInstance instance) =>
        SolutionFile.Read(DataDirectory.SolomonBksSolution(Dir, name), instance);

    public static BksTable Bks() => BksTable.Load(DataDirectory.SolomonBksTable(Dir));

    public static IEnumerable<object[]> AllSolomonNames() =>
        Bks().Entries.Select(e => new object[] { e.Instance });
}
