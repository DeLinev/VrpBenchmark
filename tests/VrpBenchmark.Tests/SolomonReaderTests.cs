using VrpBenchmark.Core.Model;
using VrpBenchmark.Instances;

namespace VrpBenchmark.Tests;

public class SolomonReaderTests
{
    [Fact]
    public void Reads_C101_header_depot_and_customers()
    {
        var instance = TestData.Solomon("c101");

        Assert.Equal("C101", instance.Name);
        Assert.Equal(101, instance.Nodes.Count);
        Assert.Equal([0], instance.Depots);
        Assert.Equal(100, instance.Customers.Count);

        var vehicle = Assert.Single(instance.VehicleTypes);
        Assert.Equal(25, vehicle.Count);
        Assert.Equal(200, vehicle.Capacity);
        Assert.Equal(new TimeWindow(0, 1236), vehicle.Shift);

        var first = instance.Nodes[1];
        Assert.Equal(("1", 45.0, 68.0, 10.0, 90.0), (first.Id, first.X, first.Y, first.Demand, first.ServiceTime));
        Assert.Equal(new TimeWindow(912, 967), first.Window);
    }

    [Fact]
    public void Euclidean_matrix_is_symmetric_with_zero_diagonal()
    {
        var m = TestData.Solomon("c101").Matrix;

        // Depot (40, 50) -> customer 1 (45, 68).
        Assert.Equal(Math.Sqrt(5 * 5 + 18 * 18), m.Distance(0, 1), 12);
        for (int i = 0; i < m.Size; i++)
        {
            Assert.Equal(0, m.Distance(i, i));
            for (int j = 0; j < m.Size; j++)
                Assert.Equal(m.Distance(i, j), m.Distance(j, i));
        }
    }

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    public void Truncated_variant_keeps_first_customers(int customers)
    {
        var instance = TestData.Solomon("rc201", new SolomonOptions { MaxCustomers = customers });

        Assert.Equal($"RC201_{customers}", instance.Name);
        Assert.Equal(customers, instance.Customers.Count);
        Assert.Equal(customers.ToString(), instance.Nodes[^1].Id);
    }

    [Fact]
    public void Vehicle_fixed_cost_is_configurable()
    {
        var instance = TestData.Solomon("c101", new SolomonOptions { VehicleFixedCost = 0 });
        Assert.Equal(0, instance.VehicleTypes[0].FixedCost);
        Assert.Equal(SolomonReader.HierarchicalVehicleCost, TestData.Solomon("c101").VehicleTypes[0].FixedCost);
    }
}
