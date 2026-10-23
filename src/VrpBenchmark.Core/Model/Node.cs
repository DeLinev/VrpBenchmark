namespace VrpBenchmark.Core.Model;

public enum NodeKind
{
    Depot,
    Customer,
}

/// <param name="Index">Position of the node in <see cref="ProblemInstance.Nodes"/> and in the travel matrix.</param>
/// <param name="Id">External identifier from the source data (e.g. Solomon customer number).</param>
public sealed record Node(
    int Index,
    string Id,
    NodeKind Kind,
    double X,
    double Y,
    double Demand,
    TimeWindow Window,
    double ServiceTime);
