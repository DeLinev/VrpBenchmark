using VrpBenchmark.Core.Model;

namespace VrpBenchmark.Core.Evaluation;

public static class UnassignedOrders
{
    /// <summary>Explains the constraints that can be checked for an individual unassigned customer.</summary>
    public static string Explain(ProblemInstance instance, int customer)
    {
        var order = instance.Nodes[customer];
        var available = instance.VehicleTypes.Where(v => v.Count > 0).ToList();
        if (available.Count == 0)
            return "No crew is available.";
        if (available.All(v => order.Demand > v.Capacity + Evaluator.Epsilon))
            return "Load exceeds the capacity of every available crew.";
        if (available.All(v => order.Volume > v.VolumeCapacity + Evaluator.Epsilon))
            return "Volume exceeds the capacity of every available crew.";

        bool fits = false;
        for (int type = 0; type < instance.VehicleTypes.Count; type++)
        {
            var crew = instance.VehicleTypes[type];
            if (crew.Count <= 0 || order.Demand > crew.Capacity + Evaluator.Epsilon ||
                order.Volume > crew.VolumeCapacity + Evaluator.Epsilon)
                continue;
            fits = true;
            if (Evaluator.Evaluate(instance, new Route(type, [customer])).IsFeasible)
                return "No place found in this plan; the order fits a crew on a dedicated trip.";
        }
        return fits
            ? "No capacity-compatible crew can serve a direct depot round trip within its shift and the time windows."
            : "No available crew meets both the load and volume requirements.";
    }
}
