using VrpBenchmark.Core.Metrics;

namespace VrpBenchmark.Tests;

public class GapTests
{
    [Theory]
    [InlineData(850.00, 828.94, 2.5406)]
    [InlineData(828.94, 828.94, 0)]
    [InlineData(800.00, 1000.00, -20)]
    public void Computes_relative_deviation_in_percent(double value, double reference, double expected)
    {
        Assert.Equal(expected, Gap.Percent(value, reference), 4);
    }
}
