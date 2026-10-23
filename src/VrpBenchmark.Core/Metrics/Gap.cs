namespace VrpBenchmark.Core.Metrics;

public static class Gap
{
    /// <summary>Relative deviation from the reference (e.g. best known solution), in percent.</summary>
    public static double Percent(double value, double reference) => (value - reference) / reference * 100.0;
}
