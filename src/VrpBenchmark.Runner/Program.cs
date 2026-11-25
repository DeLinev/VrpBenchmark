using System.Globalization;
using VrpBenchmark.Algorithms;
using VrpBenchmark.Algorithms.Metaheuristics;
using VrpBenchmark.Algorithms.Metaheuristics.Alns;
using VrpBenchmark.Core.Metrics;
using VrpBenchmark.Core.Model;
using VrpBenchmark.Core.Validation;
using VrpBenchmark.Instances;
using VrpBenchmark.Runner;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;

var options = CommandOptions.Parse(args, out var positional);

return positional switch
{
    ["info", var instancePath] => Info(instancePath),
    ["validate", var instancePath, var solutionPath] => Validate(instancePath, solutionPath),
    ["check-bks"] => CheckBks(DataDirectory.Find()),
    ["check-bks", var dataDir] => CheckBks(dataDir),
    ["solve", var algorithm, var instancePath] => Solve(algorithm, instancePath, options),
    ["run-all", var algorithm] => RunAll(algorithm, DataDirectory.Find(), options),
    ["benchmark"] => RunBenchmark(DataDirectory.Find(), options),
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
          benchmark --name <name>                 Full experiment: algorithms x instances x seeds -> results/<name>/

        Options for solve / run-all:
          --time <seconds>      time limit for metaheuristics (default 10)
          --seed <n>            random seed (default 1)
          --customers <n>       solve: use only the first n customers (25/50 variants)
          --out <file.sol>      solve: save the solution
          --jobs <n>            run-all / benchmark: runs in parallel (default 1)

        Options for benchmark:
          --name <name>         experiment folder; rerun with the same name to continue an interrupted run
          --algorithms <a,b>    default i1-ls,sa,alns,ortools
          --seeds <n>           seeds 1..n for randomised algorithms (default 10)
          --instances <a,b>     subset of instances, e.g. c101,r101 (default: all 56)

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
    if (solver is SimulatedAnnealing sa)
        Console.WriteLine($"Iterations: {sa.LastIterations:N0}");
    if (solver is Alns alns)
    {
        Console.WriteLine($"Distance phase: {alns.LastDistancePhaseStart.TotalSeconds:0.000} s after start, after {alns.LastVehicleMinimizationIterations:N0} fleet iterations");
        Console.WriteLine($"Accepted repeats of the current plan: {alns.LastAcceptedRepeats:N0} of {alns.LastIterations:N0} iterations");
        Console.WriteLine($"Iterations: {alns.LastIterations:N0}; weights: {string.Join(", ", alns.LastWeights.Select(w => $"{w.Key} {w.Value:0.00}"))}");
    }
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
    var entries = bks.Entries.OrderBy(e => e.Instance, StringComparer.Ordinal).ToArray();
    var rows = new (string Class, int Vehicles, double Distance, BksEntry Bks, double Ms, bool Feasible)[entries.Length];
    int done = 0;

    Parallel.For(0, entries.Length, new ParallelOptions { MaxDegreeOfParallelism = options.Jobs }, k =>
    {
        var entry = entries[k];
        var instance = SolomonReader.Read(DataDirectory.SolomonInstance(dataDir, entry.Instance));
        var result = SolverCatalog.Create(algorithm).Solve(instance, options.ToSolverOptions());
        var report = SolutionValidator.Validate(instance, result.Solution);
        var s = report.Stats!;

        rows[k] = (InstanceClass(entry.Instance), s.VehiclesUsed, s.Distance, entry, result.Elapsed.TotalMilliseconds, report.IsFeasible);
        Console.Error.Write($"\r{Interlocked.Increment(ref done)}/{entries.Length} solved");
    });
    Console.Error.WriteLine();

    Console.WriteLine($"{"Instance",-8} {"Veh",4} {"BKS",4} {"Distance",10} {"BKS dist",10} {"Gap %",7} {"ms",7}  Status");
    foreach (var r in rows)
        Console.WriteLine($"{r.Bks.Instance,-8} {r.Vehicles,4} {r.Bks.Vehicles,4} {r.Distance,10:0.00} {r.Bks.Distance,10:0.00} {ForDisplay(Gap.Percent(r.Distance, r.Bks.Distance), 2),7:0.00} {r.Ms,7:0}  {(r.Feasible ? "OK" : "INFEASIBLE")}");

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
    Console.WriteLine($"Total solver time {rows.Sum(r => r.Ms) / 1000:0.0} s, infeasible: {rows.Count(r => !r.Feasible)}");
    return rows.All(r => r.Feasible) ? 0 : 2;
}

static int RunBenchmark(string dataDir, CommandOptions options)
{
    if (options.Name is null)
    {
        Console.Error.WriteLine("benchmark requires --name <name>.");
        return 1;
    }

    var all = BksTable.Load(DataDirectory.SolomonBksTable(dataDir)).Entries.Select(e => e.Instance).Order(StringComparer.Ordinal).ToList();
    var instances = options.Instances is null
        ? all
        : [.. options.Instances.Split(',').Select(i => all.Single(a => a.Equals(i.Trim(), StringComparison.OrdinalIgnoreCase)))];

    var config = new BenchmarkConfig(
        options.Name,
        options.Algorithms?.Split(',').Select(a => a.Trim().ToLowerInvariant()).ToList() ?? ["i1-ls", "sa", "alns", "ortools"],
        options.Seeds,
        options.TimeSeconds,
        options.Jobs,
        instances);

    foreach (string algorithm in config.Algorithms)
        SolverCatalog.Create(algorithm);

    string resultsRoot = Path.Combine(Directory.GetParent(dataDir)!.FullName, "results");
    return Benchmark.Run(dataDir, resultsRoot, config);
}

static string InstanceClass(string name) => name[..(name.Length - 2)];

// A tiny negative value such as -0.0004 rounds to "-0.00" in .NET; adding +0.0 turns the rounded -0.0 into +0.0.
static double ForDisplay(double value, int decimals) => Math.Round(value, decimals) + 0.0;

sealed record CommandOptions(double TimeSeconds, int Seed, int? Customers, string? Output, int Jobs,
    string? Name, string? Algorithms, int Seeds, string? Instances)
{
    public SolverOptions ToSolverOptions() => new(TimeSpan.FromSeconds(TimeSeconds), Seed);

    public static CommandOptions Parse(string[] args, out string[] positional)
    {
        var rest = new List<string>();
        double time = 10;
        int seed = 1;
        int? customers = null;
        string? output = null;
        int jobs = 1;
        int seeds = 10;
        string? name = null, algorithms = null, instances = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--time": time = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--customers": customers = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--out": output = args[++i]; break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seeds": seeds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--name": name = args[++i]; break;
                case "--algorithms": algorithms = args[++i]; break;
                case "--instances": instances = args[++i]; break;
                default: rest.Add(args[i]); break;
            }
        }

        positional = [.. rest];
        return new CommandOptions(time, seed, customers, output, jobs, name, algorithms, seeds, instances);
    }
}
