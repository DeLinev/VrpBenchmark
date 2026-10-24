using System.Globalization;
using VrpBenchmark.Algorithms;
using VrpBenchmark.Core.Metrics;
using VrpBenchmark.Core.Model;
using VrpBenchmark.Core.Validation;
using VrpBenchmark.Instances;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var options = CommandOptions.Parse(args, out var positional);

return positional switch
{
    ["info", var instancePath] => Info(instancePath),
    ["validate", var instancePath, var solutionPath] => Validate(instancePath, solutionPath),
    ["check-bks"] => CheckBks(DataDirectory.Find()),
    ["check-bks", var dataDir] => CheckBks(dataDir),
    ["solve", var algorithm, var instancePath] => Solve(algorithm, instancePath, options),
    ["run-all", var algorithm] => RunAll(algorithm, DataDirectory.Find(), options),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine($"""
        Usage:
          info <instance.txt>                     Print a summary of a Solomon instance
          validate <instance.txt> <solution.sol>  Check a solution against all constraints
          check-bks [data-dir]                    Validate all published best known solutions
          solve <algorithm> <instance.txt>        Solve one instance and validate the result
          run-all <algorithm>                     Solve all 56 Solomon instances, compare with BKS

        Options for solve / run-all:
          --time <seconds>      time limit for metaheuristics (default 10)
          --seed <n>            random seed (default 1)
          --customers <n>       solve: use only the first n customers (25/50 variants)
          --out <file.sol>      solve: save the solution

        Algorithms: {string.Join(", ", SolverCatalog.Names)}
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
        Console.WriteLine($"{entry.Instance,-8} {stats.VehiclesUsed,4} {entry.Vehicles,8} {stats.Distance,12:0.0000} {entry.Distance,10:0.00} {ForDisplay(Gap.Percent(stats.Distance, entry.Distance), 3),8:0.000}  {status}");
    }

    Console.WriteLine();
    Console.WriteLine(failures == 0 ? $"All {bks.Entries.Count} best known solutions verified." : $"{failures} of {bks.Entries.Count} solutions failed.");
    return failures == 0 ? 0 : 2;
}

static int Solve(string algorithm, string instancePath, CommandOptions options)
{
    var solver = SolverCatalog.Create(algorithm);
    var instance = SolomonReader.Read(instancePath, new SolomonOptions { MaxCustomers = options.Customers });
    var result = solver.Solve(instance, options.ToSolverOptions());
    var report = SolutionValidator.Validate(instance, result.Solution);

    Console.WriteLine($"Algorithm: {solver.Name}");
    Console.WriteLine($"Time     : {result.Elapsed.TotalMilliseconds:0} ms");
    if (solver is I1LocalSearch { LastStats: { } ls })
        Console.WriteLine($"Moves    : relocate {ls.Relocate}, swap {ls.Swap}, 2-opt* {ls.TwoOptStar}, 2-opt {ls.TwoOpt}");
    PrintReport(instance, report);

    var bks = BksTable.Load(DataDirectory.SolomonBksTable(DataDirectory.Find()));
    if (options.Customers is null && report.Stats is { } s && bks.TryGet(instance.Name, out var entry))
        Console.WriteLine($"BKS      : {entry.Vehicles} vehicles, {entry.Distance:0.00} (gap {ForDisplay(Gap.Percent(s.Distance, entry.Distance), 2):0.00}%)");

    if (options.Output is { } output)
    {
        SolutionFile.Write(output, instance, result.Solution, solver.Name);
        Console.WriteLine($"Saved to : {output}");
    }

    return report.IsFeasible ? 0 : 2;
}

static int RunAll(string algorithm, string dataDir, CommandOptions options)
{
    var bks = BksTable.Load(DataDirectory.SolomonBksTable(dataDir));
    var rows = new List<(string Class, int Vehicles, double Distance, BksEntry Bks, double Ms, bool Feasible)>();

    Console.WriteLine($"{"Instance",-8} {"Veh",4} {"BKS",4} {"Distance",10} {"BKS dist",10} {"Gap %",7} {"ms",7}  Status");

    foreach (var entry in bks.Entries.OrderBy(e => e.Instance, StringComparer.Ordinal))
    {
        var instance = SolomonReader.Read(DataDirectory.SolomonInstance(dataDir, entry.Instance));
        var result = SolverCatalog.Create(algorithm).Solve(instance, options.ToSolverOptions());
        var report = SolutionValidator.Validate(instance, result.Solution);
        var s = report.Stats!;

        rows.Add((InstanceClass(entry.Instance), s.VehiclesUsed, s.Distance, entry, result.Elapsed.TotalMilliseconds, report.IsFeasible));
        Console.WriteLine($"{entry.Instance,-8} {s.VehiclesUsed,4} {entry.Vehicles,4} {s.Distance,10:0.00} {entry.Distance,10:0.00} {ForDisplay(Gap.Percent(s.Distance, entry.Distance), 2),7:0.00} {result.Elapsed.TotalMilliseconds,7:0}  {(report.IsFeasible ? "OK" : "INFEASIBLE")}");
    }

    // CNV / CTD: cumulative number of vehicles and total distance, the usual way to report Solomon results.
    Console.WriteLine();
    Console.WriteLine($"{"Class",-6} {"CNV",5} {"BKS",5} {"CTD",10} {"BKS",10} {"Avg gap %",10} {"Same veh",9}");
    foreach (var group in rows.GroupBy(r => r.Class).Append(rows.GroupBy(_ => "All").Single()))
    {
        Console.WriteLine($"{group.Key,-6} {group.Sum(r => r.Vehicles),5} {group.Sum(r => r.Bks.Vehicles),5} " +
            $"{group.Sum(r => r.Distance),10:0.00} {group.Sum(r => r.Bks.Distance),10:0.00} " +
            $"{ForDisplay(group.Average(r => Gap.Percent(r.Distance, r.Bks.Distance)), 2),10:0.00} " +
            $"{group.Count(r => r.Vehicles == r.Bks.Vehicles),4}/{group.Count(),-4}");
    }

    Console.WriteLine();
    Console.WriteLine($"Total time {rows.Sum(r => r.Ms) / 1000:0.0} s, infeasible: {rows.Count(r => !r.Feasible)}");
    return rows.All(r => r.Feasible) ? 0 : 2;
}

static string InstanceClass(string name) => name[..(name.Length - 2)];

// A tiny negative value such as -0.0004 rounds to "-0.00" in .NET; adding +0.0 turns the rounded -0.0 into +0.0.
static double ForDisplay(double value, int decimals) => Math.Round(value, decimals) + 0.0;

sealed record CommandOptions(double TimeSeconds, int Seed, int? Customers, string? Output)
{
    public SolverOptions ToSolverOptions() => new(TimeSpan.FromSeconds(TimeSeconds), Seed);

    public static CommandOptions Parse(string[] args, out string[] positional)
    {
        var rest = new List<string>();
        double time = 10;
        int seed = 1;
        int? customers = null;
        string? output = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--time": time = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--customers": customers = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--out": output = args[++i]; break;
                default: rest.Add(args[i]); break;
            }
        }

        positional = [.. rest];
        return new CommandOptions(time, seed, customers, output);
    }
}
