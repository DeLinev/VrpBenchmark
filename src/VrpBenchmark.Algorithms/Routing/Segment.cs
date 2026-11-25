using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Algorithms.Routing;

/// <summary>
/// Summary of a fixed sequence of nodes that allows evaluating the concatenation of two sequences in O(1)
/// (Vidal et al., 2013, "A hybrid genetic algorithm with adaptive diversity management...").
/// Time warp measures by how much the vehicle would have to "travel back in time" to respect all windows;
/// the sequence is time-feasible exactly when it is zero.
/// </summary>
public readonly struct Segment
{
    public int First { get; }
    public int Last { get; }

    /// <summary>Minimal total duration: travel + service + unavoidable waiting.</summary>
    public double Duration { get; }

    public double TimeWarp { get; }

    /// <summary>Earliest service start at the first node that leads to the minimal duration.</summary>
    public double Earliest { get; }

    /// <summary>Latest service start at the first node that does not add time warp.</summary>
    public double Latest { get; }

    public double Load { get; }
    public double Volume { get; }
    public double Distance { get; }

    private Segment(int first, int last, double duration, double timeWarp, double earliest, double latest, double load, double volume, double distance)
    {
        First = first;
        Last = last;
        Duration = duration;
        TimeWarp = timeWarp;
        Earliest = earliest;
        Latest = latest;
        Load = load;
        Volume = volume;
        Distance = distance;
    }

    public static Segment ForNode(Node node) =>
        new(node.Index, node.Index, node.ServiceTime, 0, node.Window.Start, node.Window.End, node.Demand, node.Volume, 0);

    /// <summary>A depot visit restricted to the intersection of the depot opening hours and the vehicle shift.</summary>
    public static Segment ForDepot(Node depot, TimeWindow shift)
    {
        double start = Math.Max(depot.Window.Start, shift.Start);
        double end = Math.Min(depot.Window.End, shift.End);
        return new(depot.Index, depot.Index, 0, Math.Max(0, start - end), start, end, 0, 0, 0);
    }

    public static Segment Concat(TravelMatrix matrix, in Segment a, in Segment b)
    {
        double travel = matrix.Time(a.Last, b.First);
        double delta = a.Duration - a.TimeWarp + travel;
        double waiting = Math.Max(b.Earliest - delta - a.Latest, 0);
        double warp = Math.Max(a.Earliest + delta - b.Latest, 0);

        return new Segment(
            a.First,
            b.Last,
            a.Duration + b.Duration + travel + waiting,
            a.TimeWarp + b.TimeWarp + warp,
            Math.Max(b.Earliest - delta, a.Earliest) - waiting,
            Math.Min(b.Latest - delta, a.Latest) + warp,
            a.Load + b.Load,
            a.Volume + b.Volume,
            a.Distance + b.Distance + matrix.Distance(a.Last, b.First));
    }

    public static Segment Concat(TravelMatrix matrix, in Segment a, in Segment b, in Segment c) =>
        Concat(matrix, Concat(matrix, a, b), c);

    public static Segment Concat(TravelMatrix matrix, in Segment a, in Segment b, in Segment c, in Segment d) =>
        Concat(matrix, Concat(matrix, Concat(matrix, a, b), c), d);
}
