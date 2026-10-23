namespace VrpBenchmark.Core.Model;

/// <param name="Count">How many vehicles of this type are available.</param>
/// <param name="Shift">Working hours: the vehicle leaves the start depot no earlier than Start and must be back by End.</param>
/// <param name="FixedCost">Cost of using one vehicle (driver, leasing, insurance).</param>
/// <param name="CostPerDistance">Variable cost per distance unit (fuel, wear).</param>
/// <param name="CostPerTime">Variable cost per time unit of the route duration (hourly wages).</param>
public sealed record VehicleType(
    string Id,
    double Capacity,
    int Count,
    int StartDepot,
    int EndDepot,
    TimeWindow Shift,
    double FixedCost,
    double CostPerDistance,
    double CostPerTime = 0);
