# VrpBenchmark

An experimental comparison of algorithms for the Vehicle Routing Problem with Time Windows (VRPTW) with capacity constraints, on the 56 Solomon instances with 100 customers. The optimization priority is the number of vehicles first, then total distance.

The compared methods are Solomon I1 with local search (`i1-ls`), simulated annealing (`sa`), adaptive large neighborhood search (`alns`), and Google OR-Tools (`ortools`). The solver and experiment-harness code is written in C#, and the analysis of the results in Python.

## Data and results

- [data/solomon](data/solomon): instances, best known solutions (BKS), and [data sources](data/solomon/SOURCES.md).
- [results/main-60s](results/main-60s): the main experiment: 60 s, 10 repetitions each of SA, ALNS, and OR-Tools; one I1+LS run per instance. 1,736 runs in total.
- [results/conv-60s](results/conv-60s): a separate series for convergence curves: 60 s, 3 repetitions each of SA, ALNS, and OR-Tools on every instance, 504 runs in total.
- [results/main-60s/analysis](results/main-60s/analysis): CSV tables, PNG figures, and a [text summary](results/main-60s/analysis/tables.md).
- [src](src), [tests](tests), [analysis](analysis): source code, tests, and analysis scripts.

In each series, `runs.csv` contains the final metrics of the runs, `history.csv` the improvement histories, `solutions/` the routes, and `run-info.json` the parameters, environment, and code version.

## Running

You need the .NET SDK 10 and Python with the dependencies from [analysis/requirements.txt](analysis/requirements.txt). Commands are run from the repository root.

Build, check the BKS, and solve a single instance:

```powershell
dotnet build -c Release
dotnet run -c Release --no-build --project src/VrpBenchmark.Runner -- check-bks
dotnet run -c Release --no-build --project src/VrpBenchmark.Runner -- solve alns data/solomon/instances/c101.txt --time 60 --seed 1
```

New benchmark series with the parameters of the main and the additional experiments:

```powershell
dotnet run -c Release --no-build --project src/VrpBenchmark.Runner -- benchmark --name repeat-main-60s --time 60 --seeds 10 --jobs 5
dotnet run -c Release --no-build --project src/VrpBenchmark.Runner -- benchmark --name repeat-conv-60s --time 60 --seeds 3 --algorithms sa,alns,ortools --jobs 5
```

New folder names preserve the published results. Re-running a command with the same name resumes an unfinished experiment, skipping runs that have already been recorded. `--jobs` sets the number of concurrent runs; `--instances c101,r101` restricts the set of instances for a quick check.

## Analyzing the saved results

An example for PowerShell with a Python virtual environment:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r analysis/requirements.txt
.\.venv\Scripts\python.exe analysis/vrp_analysis.py results/main-60s --convergence results/conv-60s
```

The command updates the tables and figures in `results/main-60s/analysis` without running the routing algorithms. To analyze your own repeat run, replace the names of both series. A [Jupyter notebook](analysis/main-60s.ipynb) is also available; it requires Jupyter, and the `CONVERGENCE` variable must be set to `../results/conv-60s`.