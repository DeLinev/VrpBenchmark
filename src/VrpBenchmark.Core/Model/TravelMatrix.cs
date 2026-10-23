namespace VrpBenchmark.Core.Model;

public sealed class TravelMatrix
{
    private readonly double[] _distance;
    private readonly double[] _time;

    public int Size { get; }

    /// <param name="distance">Row-major matrix: element [from * size + to].</param>
    /// <param name="time">Row-major matrix of travel times, same layout as <paramref name="distance"/>.</param>
    public TravelMatrix(int size, double[] distance, double[] time)
    {
        if (distance.Length != size * size || time.Length != size * size)
            throw new ArgumentException($"Matrix data must contain exactly {size * size} elements.");

        Size = size;
        _distance = distance;
        _time = time;
    }

    public double Distance(int from, int to) => _distance[from * Size + to];

    public double Time(int from, int to) => _time[from * Size + to];

    /// <summary>
    /// Straight-line distances in full double precision, as used by the published Solomon best known solutions.
    /// Travel time equals distance divided by <paramref name="speed"/>.
    /// </summary>
    public static TravelMatrix Euclidean(IReadOnlyList<Node> nodes, double speed = 1.0)
    {
        int n = nodes.Count;
        var distance = new double[n * n];
        var time = new double[n * n];

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double dx = nodes[i].X - nodes[j].X;
                double dy = nodes[i].Y - nodes[j].Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                distance[i * n + j] = d;
                time[i * n + j] = d / speed;
            }
        }

        return new TravelMatrix(n, distance, time);
    }
}
