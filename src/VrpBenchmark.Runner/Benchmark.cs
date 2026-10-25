using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VrpBenchmark.Algorithms;
using VrpBenchmark.Algorithms.Construction;
using VrpBenchmark.Algorithms.Metaheuristics;
using VrpBenchmark.Algorithms.Metaheuristics.Alns;
using VrpBenchmark.Core.Metrics;
using VrpBenchmark.Core.Validation;
using VrpBenchmark.Instances;
using VrpBenchmark.OrTools;

namespace VrpBenchmark.Runner;

public sealed record BenchmarkConfig(
    string Name,
    IReadOnlyList<string> Algorithms,
    int Seeds,
    double TimeLimitSeconds,
    int Jobs,
    IReadOnlyList<string> Instances);

public readonly record struct RunKey(string Algorithm, string Instance, int Seed);

/// <summary>
/// Runs every algorithm on every instance with every seed and appends the results to CSV files in
/// results/&lt;name&gt;/. Runs already present in runs.csv are skipped, so an interrupted experiment
/// continues where it stopped when started again with the same name.
/// </summary>
public static class Benchmark
{
    public const string RunsHeader =
        "algorithm,instance,class,seed,time_limit_s,elapsed_s,iterations,feasible,vehicles,distance,cost,bks_vehicles,bks_distance,vehicle_diff,gap_pct";

    public const string HistoryHeader = "algorithm,instance,seed,time_s,cost,vehicles,distance";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>Algorithms without randomness are run once, with seed 1.</summary>
    public static bool IsDeterministic(string algorithm) => algorithm is "i1" or "i1-ls";

    public static IEnumerable<RunKey> Plan(BenchmarkConfig config)
    {
        // Seeds go first so that an experiment stopped early still has complete seeds for every algorithm.
        for (int seed = 1; seed <= config.Seeds; seed++)
            foreach (string algorithm in config.Algorithms)
                if (seed == 1 || !IsDeterministic(algorithm))
                    foreach (string instance in config.Instances)
                        yield return new RunKey(algorithm, instance, seed);
    }

    public static HashSet<RunKey> ReadCompleted(string runsPath)
    {
        var done = new HashSet<RunKey>();
        if (!File.Exists(runsPath))
            return done;

        foreach (string line in File.ReadLines(runsPath).Skip(1))
        {
            var f = line.Split(',');
            if (f.Length >= 4)
                done.Add(new RunKey(f[0], f[1], int.Parse(f[3], Inv)));
        }

        return done;
    }

    public static int Run(string dataDir, string resultsRoot, BenchmarkConfig config)
    {
        string dir = Path.Combine(resultsRoot, config.Name);
        Directory.CreateDirectory(dir);
        string runsPath = Path.Combine(dir, "runs.csv");
        string historyPath = Path.Combine(dir, "history.csv");
        string infoPath = Path.Combine(dir, "run-info.json");

        if (!RecordSession(infoPath, dataDir, config, out string? error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var bks = BksTable.Load(DataDirectory.SolomonBksTable(dataDir));
        var completed = ReadCompleted(runsPath);
        var pending = Plan(config).Where(k => !completed.Contains(k)).ToList();
        int total = completed.Count + pending.Count;

        Console.Error.WriteLine($"Experiment '{config.Name}': {completed.Count} runs done, {pending.Count} to go, {config.Jobs} in parallel.");

        // Long experiments must not be cut by idle sleep; the request ends with the process.
        if (OperatingSystem.IsWindows())
            SetThreadExecutionState(EsContinuous | EsSystemRequired);

        var runs = OpenAppend(runsPath, RunsHeader);
        var history = OpenAppend(historyPath, HistoryHeader);
        var writeLock = new object();
        var clock = Stopwatch.StartNew();
        int finished = 0;

        Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = config.Jobs }, key =>
        {
            string line;
            try
            {
                line = Execute(key, dataDir, dir, bks, config, history, writeLock);
            }
            catch (Exception ex)
            {
                lock (writeLock)
                    File.AppendAllText(Path.Combine(dir, "errors.log"), $"{DateTime.Now:s} {key}: {ex}{Environment.NewLine}");
                line = $"{key.Algorithm} {key.Instance} s{key.Seed}: ERROR {ex.Message}";
            }

            lock (writeLock)
            {
                int n = ++finished;
                var eta = TimeSpan.FromSeconds(clock.Elapsed.TotalSeconds / n * (pending.Count - n));
                Console.Error.WriteLine($"[{completed.Count + n}/{total}] {line}  (ETA {eta:hh\\:mm\\:ss})");
            }
        });

        runs.Dispose();
        history.Dispose();
        Console.Error.WriteLine($"Finished in {clock.Elapsed:hh\\:mm\\:ss}. Results: {dir}");
        MarkSessionFinished(infoPath, finished);
        PrintSummary(runsPath);
        return 0;

        // Writers are shared between threads; every write happens under writeLock.
        string Execute(RunKey key, string dataDir, string dir, BksTable bks, BenchmarkConfig config, StreamWriter history, object writeLock)
        {
            var instance = SolomonReader.Read(DataDirectory.SolomonInstance(dataDir, key.Instance));
            var result = SolverCatalog.Create(key.Algorithm).Solve(instance, new SolverOptions(TimeSpan.FromSeconds(config.TimeLimitSeconds), key.Seed));
            var report = SolutionValidator.Validate(instance, result.Solution);
            var s = report.Stats!;
            bks.TryGet(instance.Name, out var best);

            string solutionDir = Path.Combine(dir, "solutions", key.Algorithm);
            Directory.CreateDirectory(solutionDir);
            SolutionFile.Write(Path.Combine(solutionDir, $"{key.Instance.ToLowerInvariant()}_s{key.Seed}.sol"), instance, result.Solution, $"{key.Algorithm}, seed {key.Seed}");

            double gap = best is null ? double.NaN : Gap.Percent(s.Distance, best.Distance);
            string row = string.Join(',',
                key.Algorithm, key.Instance, key.Instance[..^2], key.Seed.ToString(Inv),
                config.TimeLimitSeconds.ToString(Inv), result.Elapsed.TotalSeconds.ToString("0.###", Inv),
                result.Iterations?.ToString(Inv) ?? "", report.IsFeasible ? "1" : "0",
                s.VehiclesUsed.ToString(Inv), s.Distance.ToString("0.####", Inv), s.Cost.ToString("0.####", Inv),
                best?.Vehicles.ToString(Inv) ?? "", best?.Distance.ToString(Inv) ?? "",
                best is null ? "" : (s.VehiclesUsed - best.Vehicles).ToString(Inv), gap.ToString("0.####", Inv));

            double fixedCost = instance.VehicleTypes[0].FixedCost;
            var points = Thin(result.History, fixedCost);

            lock (writeLock)
            {
                foreach (var p in points)
                {
                    var (vehicles, distance) = Decode(p.Cost, fixedCost);
                    history.WriteLine(string.Join(',', key.Algorithm, key.Instance, key.Seed.ToString(Inv),
                        p.Time.TotalSeconds.ToString("0.####", Inv), p.Cost.ToString("0.####", Inv),
                        vehicles?.ToString(Inv) ?? "", distance?.ToString("0.####", Inv) ?? ""));
                }
                history.Flush();

                // The run counts as done only once its row is written, after its history.
                runs.WriteLine(row);
                runs.Flush();
            }

            return $"{key.Algorithm} {key.Instance} s{key.Seed}: {s.VehiclesUsed} veh, {s.Distance:0.00} " +
                   $"(BKS {best?.Vehicles} / {best?.Distance:0.00}){(report.IsFeasible ? "" : " INFEASIBLE")}";
        }
    }

    /// <summary>
    /// Keeps the convergence curve compact: a point is kept when the vehicle count changes or at least 5% more
    /// time has passed since the last kept point (log-spaced in time); the final point is always kept.
    /// </summary>
    public static List<ProgressPoint> Thin(IReadOnlyList<ProgressPoint> history, double fixedCost)
    {
        var kept = new List<ProgressPoint>();
        for (int i = 0; i < history.Count; i++)
        {
            var p = history[i];
            bool last = i == history.Count - 1;
            if (kept.Count == 0 || last
                || Decode(p.Cost, fixedCost).Vehicles != Decode(kept[^1].Cost, fixedCost).Vehicles
                || p.Time.TotalSeconds >= kept[^1].Time.TotalSeconds * 1.05 + 0.001)
            {
                kept.Add(p);
            }
        }

        return kept;
    }

    /// <summary>
    /// Splits a cost into vehicles and distance. Valid for a homogeneous fleet whose fixed cost exceeds any
    /// route set's distance, as with the hierarchical Solomon setup (fixed cost 10 000).
    /// </summary>
    public static (int? Vehicles, double? Distance) Decode(double cost, double fixedCost)
    {
        if (fixedCost <= 0)
            return (null, null);

        int vehicles = (int)Math.Floor((cost + 1e-6) / fixedCost);
        return (vehicles, cost - vehicles * fixedCost);
    }

    private static StreamWriter OpenAppend(string path, string header)
    {
        bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
        var writer = new StreamWriter(path, append: true);
        if (isNew)
            writer.WriteLine(header);
        return writer;
    }

    /// <summary>
    /// Stores the experiment set-up and appends a session (machine, code version, start time) to run-info.json.
    /// Refuses to continue an experiment created with a different time limit.
    /// </summary>
    private static bool RecordSession(string infoPath, string dataDir, BenchmarkConfig config, out string? error)
    {
        error = null;
        var info = File.Exists(infoPath) ? JsonNode.Parse(File.ReadAllText(infoPath))!.AsObject() : new JsonObject
        {
            ["name"] = config.Name,
            ["timeLimitSeconds"] = config.TimeLimitSeconds,
            ["created"] = DateTime.Now.ToString("s", Inv),
            ["sessions"] = new JsonArray(),
        };

        double existingLimit = info["timeLimitSeconds"]!.GetValue<double>();
        if (Math.Abs(existingLimit - config.TimeLimitSeconds) > 1e-9)
        {
            error = $"Experiment '{config.Name}' was created with a {existingLimit} s time limit, not {config.TimeLimitSeconds} s. Use another --name.";
            return false;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        info["seeds"] = config.Seeds;
        info["algorithms"] = new JsonArray([.. config.Algorithms.Select(a => (JsonNode)a)]);
        info["instances"] = config.Instances.Count;
        info["parameters"] = new JsonObject
        {
            ["i1"] = new JsonObject { ["grid"] = I1Parameters.DefaultGrid.Count },
            ["sa"] = JsonSerializer.SerializeToNode(new SimulatedAnnealingParameters()),
            ["alns"] = JsonSerializer.SerializeToNode(new AlnsParameters()),
            ["ortools"] = new JsonObject
            {
                ["version"] = OrToolsSolver.LibraryVersion,
                ["scale"] = OrToolsSolver.Scale,
                ["firstSolution"] = "PARALLEL_CHEAPEST_INSERTION",
                ["metaheuristic"] = "GUIDED_LOCAL_SEARCH",
            },
        };

        info["sessions"]!.AsArray().Add(new JsonObject
        {
            ["started"] = DateTime.Now.ToString("s", Inv),
            ["jobs"] = config.Jobs,
            ["cpu"] = CpuName(),
            ["logicalCores"] = Environment.ProcessorCount,
            ["os"] = RuntimeInformation.OSDescription,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["gitCommit"] = Git(dataDir, "rev-parse HEAD"),
            ["gitDirty"] = Git(dataDir, "status --porcelain") is { Length: > 0 },
        });

        File.WriteAllText(infoPath, info.ToJsonString(options));
        return true;
    }

    private static void MarkSessionFinished(string infoPath, int runs)
    {
        var info = JsonNode.Parse(File.ReadAllText(infoPath))!.AsObject();
        var session = info["sessions"]!.AsArray()[^1]!.AsObject();
        session["finished"] = DateTime.Now.ToString("s", Inv);
        session["runsCompleted"] = runs;
        File.WriteAllText(infoPath, info.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void PrintSummary(string runsPath)
    {
        var rows = File.ReadLines(runsPath).Skip(1).Select(l => l.Split(',')).ToList();
        Console.WriteLine($"{"Algorithm",-10} {"Runs",5} {"Infeas.",8} {"Avg CNV",8} {"Avg gap %",10}");

        foreach (var group in rows.GroupBy(r => r[0]))
        {
            int seeds = group.Select(r => r[3]).Distinct().Count();
            double cnv = group.Sum(r => int.Parse(r[8], Inv)) / (double)seeds;
            double gap = group.Average(r => double.Parse(r[14], Inv));
            Console.WriteLine($"{group.Key,-10} {group.Count(),5} {group.Count(r => r[7] != "1"),8} {cnv,8:0.0} {gap,10:0.00}");
        }
    }

    private static string? CpuName()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) is string name ? name.Trim() : null;
    }

    private static string? Git(string workingDir, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
