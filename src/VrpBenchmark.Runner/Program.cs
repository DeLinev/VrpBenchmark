using System.Globalization;
using VrpBenchmark.Core.Metrics;
using VrpBenchmark.Core.Model;
using VrpBenchmark.Core.Validation;
using VrpBenchmark.Instances;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

return args switch
{
    ["info", var instancePath] => Info(instancePath),
    ["validate", var instancePath, var solutionPath] => Validate(instancePath, solutionPath),
    ["check-bks"] => CheckBks(DataDirectory.Find()),
    ["check-bks", var dataDir] => CheckBks(dataDir),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("""
        Usage:
          info <instance.txt>                     Print a summary of a Solomon instance
          validate <instance.txt> <solution.sol>  Check a solution against all constraints
          check-bks [data-dir]                    Validate all published best known solutions
        """);
    return 1;
}

static int Info(string instancePath)
{
    var instance = SolomonReader.Read(instancePath);
    var vehicle = instance.VehicleTypes[0];
    var customers = instance.Customers.Select(c => instance.Nodes[c]).ToList();

    Console.WriteLine($"Instance        : {instance.Name}");
    Console.WriteLine($"Customers       : {customers.Count}");
    Console.WriteLine($"Vehicles        : {vehicle.Count} x capacity {vehicle.Capacity}");
    Console.WriteLine($"Planning horizon: [{vehicle.Shift.Start}, {vehicle.Shift.End}]");
    Console.WriteLine($"Total demand    : {customers.Sum(c => c.Demand)} (lower bound {Math.Ceiling(customers.Sum(c => c.Demand) / vehicle.Capacity)} vehicles)");
    Console.WriteLine($"Avg window width: {customers.Average(c => c.Window.End - c.Window.Start):0.##}");
    return 0;
}

static int Validate(string instancePath, string solutionPath)
{
    var instance = SolomonReader.Read(instancePath);
    var solution = SolutionFile.Read(solutionPath, instance);
    var report = SolutionValidator.Validate(instance, solution);

    PrintReport(instance, report);
    return report.IsFeasible ? 0 : 2;
}

static void PrintReport(ProblemInstance instance, ValidationReport report)
{
    Console.WriteLine($"Instance : {instance.Name}");
    Console.WriteLine($"Feasible : {(report.IsFeasible ? "yes" : "NO")}");

    if (report.Stats is { } s)
    {
        Console.WriteLine($"Vehicles : {s.VehiclesUsed}");
        Console.WriteLine($"Distance : {s.Distance:0.####}");
        Console.WriteLine($"Duration : {s.Duration:0.##}");
        Console.WriteLine($"Cost     : {s.Cost:0.##}");
    }

    foreach (var v in report.Violations)
        Console.WriteLine($"  [{v.Kind}] {v.Message}");
}

static int CheckBks(string dataDir)
{
    var bks = BksTable.Load(DataDirectory.SolomonBksTable(dataDir));
    int failures = 0;

    Console.WriteLine($"{"Instance",-8} {"Veh",4} {"BKS veh",8} {"Distance",12} {"BKS dist",10} {"Gap %",8}  Status");

    foreach (var entry in bks.Entries.OrderBy(e => e.Instance, StringComparer.Ordinal))
    {
        var instance = SolomonReader.Read(DataDirectory.SolomonInstance(dataDir, entry.Instance));
        var solution = SolutionFile.Read(DataDirectory.SolomonBksSolution(dataDir, entry.Instance), instance);
        var report = SolutionValidator.Validate(instance, solution);
        var stats = report.Stats!;

        // Published distances are rounded to 2 decimals.
        bool matches = stats.VehiclesUsed == entry.Vehicles && Math.Abs(stats.Distance - entry.Distance) <= 0.005 + 1e-9;
        bool ok = report.IsFeasible && matches;
        if (!ok)
            failures++;

        string status = ok ? "OK" : !report.IsFeasible ? $"INFEASIBLE ({report.Violations.Count} violations)" : "MISMATCH";
        Console.WriteLine($"{entry.Instance,-8} {stats.VehiclesUsed,4} {entry.Vehicles,8} {stats.Distance,12:0.0000} {entry.Distance,10:0.00} {Gap.Percent(stats.Distance, entry.Distance),8:0.000}  {status}");
    }

    Console.WriteLine();
    Console.WriteLine(failures == 0 ? $"All {bks.Entries.Count} best known solutions verified." : $"{failures} of {bks.Entries.Count} solutions failed.");
    return failures == 0 ? 0 : 2;
}
