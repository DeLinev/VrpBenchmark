"""
Analysis of a benchmark experiment: tables, statistical tests and figures for the article.

    python analysis/vrp_analysis.py results/main-60s

Everything is written to results/<name>/analysis/: tables as CSV (machine-readable) and tables.md
(Ukrainian headers, decimal comma, ready to copy into the article), figures as 300 dpi PNG.
"""
from __future__ import annotations

import argparse
import json
import math
from dataclasses import dataclass
from pathlib import Path

import matplotlib
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from matplotlib.lines import Line2D
from matplotlib.ticker import FuncFormatter
from scipy import stats

ALGORITHMS = ["i1-ls", "sa", "alns", "ortools"]
METAHEURISTICS = ["sa", "alns", "ortools"]
LABELS = {"i1-ls": "I1 + LS", "sa": "SA", "alns": "ALNS", "ortools": "OR-Tools", "bks": "BKS"}
CLASSES = ["C1", "C2", "R1", "R2", "RC1", "RC2"]
KEYS = ["algorithm", "instance", "seed"]

# Categorical slots 1-3 of the validated palette (all-pairs CVD-safe); the deterministic baseline is neutral.
COLORS = {"alns": "#2a78d6", "sa": "#eb6834", "ortools": "#1baf7a", "i1-ls": "#898781", "bks": "#0b0b0b"}
MARKERS = {"alns": "o", "sa": "s", "ortools": "^", "i1-ls": "D"}
LINESTYLES = {"alns": "-", "sa": "--", "ortools": "-.", "i1-ls": ":"}
TEXT, TEXT_2, GRID, AXIS = "#0b0b0b", "#52514e", "#e1e0d9", "#c3c2b7"

# Nemenyi critical values q(0.05) for k algorithms (Demšar, 2006).
NEMENYI_Q05 = {2: 1.960, 3: 2.343, 4: 2.569, 5: 2.728, 6: 2.850}


# ---------------------------------------------------------------- data

@dataclass
class Experiment:
    path: Path
    data_dir: Path
    runs: pd.DataFrame
    history: pd.DataFrame

    @property
    def algorithms(self) -> list[str]:
        present = set(self.runs.algorithm)
        return [a for a in ALGORITHMS if a in present]

    @property
    def bks(self) -> pd.DataFrame:
        """Class and best known solution of every instance."""
        return (self.runs.drop_duplicates("instance").set_index("instance")
                [["class", "bks_vehicles", "bks_distance"]].sort_index())


def load(experiment_dir: str | Path, data_dir: str | Path | None = None) -> Experiment:
    path = Path(experiment_dir)
    runs = pd.read_csv(path / "runs.csv")
    history = pd.read_csv(path / "history.csv").sort_values(KEYS + ["time_s"], kind="stable")
    _check(runs, history)
    data = Path(data_dir) if data_dir else path.parent.parent / "data" / "solomon"
    return Experiment(path, data, runs, history)


def _check(runs: pd.DataFrame, history: pd.DataFrame) -> None:
    """Refuses inconsistent input instead of silently analysing it."""
    if runs.duplicated(KEYS).any():
        raise ValueError("runs.csv contains duplicate runs")
    if (runs.feasible != 1).any():
        raise ValueError(f"{(runs.feasible != 1).sum()} infeasible runs")
    last = history.groupby(KEYS).cost.last().rename("last")
    joined = runs.set_index(KEYS)[["cost"]].join(last)
    if joined["last"].isna().any():
        raise ValueError("some runs have no convergence history")
    if (joined.cost - joined["last"]).abs().max() > 1e-6:
        raise ValueError("history.csv does not end with the final cost of every run")


def per_instance(runs: pd.DataFrame, best: bool = False) -> pd.DataFrame:
    """Vehicles, distance and cost per (algorithm, instance): mean over seeds, or the run with the lowest cost."""
    columns = ["vehicles", "distance", "cost"]
    if best:
        return runs.loc[runs.groupby(["algorithm", "instance"]).cost.idxmin()].set_index(["algorithm", "instance"])[columns]
    return runs.groupby(["algorithm", "instance"])[columns].mean()


def common_instances(runs: pd.DataFrame) -> list[str]:
    """Instances where every run of every algorithm uses as many vehicles as the BKS: distances are comparable there."""
    worst = runs.groupby("instance").vehicle_diff.max()
    return sorted(worst[worst == 0].index)


# ---------------------------------------------------------------- tables

def class_table(exp: Experiment, best: bool = False) -> pd.DataFrame:
    """Mean vehicles (NV) and distance (TD) per class, plus CNV/CTD over all instances - the usual Solomon table."""
    bks = exp.bks
    g = per_instance(exp.runs, best).join(bks["class"], on="instance")
    frames = {}
    for a in exp.algorithms:
        x = g.loc[a]
        frames[LABELS[a]] = _class_rows(x.vehicles, x.distance, x["class"])
    frames["BKS"] = _class_rows(bks.bks_vehicles, bks.bks_distance, bks["class"])
    return pd.concat(frames, axis=1)


def _class_rows(vehicles: pd.Series, distance: pd.Series, classes: pd.Series) -> pd.DataFrame:
    df = pd.DataFrame({"NV": vehicles, "TD": distance, "class": classes})
    rows = df.groupby("class")[["NV", "TD"]].mean().reindex(CLASSES)
    rows.loc["CNV / CTD"] = [df.NV.sum(), df.TD.sum()]
    return rows


def summary_table(exp: Experiment) -> pd.DataFrame:
    runs, bks = exp.runs, exp.bks
    common = common_instances(runs)
    mean, best = per_instance(runs), per_instance(runs, best=True)
    rows = {}
    for a in exp.algorithms:
        r = runs[runs.algorithm == a]
        matched = r[r.vehicle_diff == 0]
        rows[LABELS[a]] = {
            "CNV (середнє за seed)": mean.loc[a].vehicles.sum(),
            "CNV (найкращий з seed)": best.loc[a].vehicles.sum(),
            "CTD (середнє за seed)": mean.loc[a].distance.sum(),
            "Задач, де найкращий запуск має к-сть машин BKS": int((best.loc[a].vehicles == bks.bks_vehicles).sum()),
            "Запусків з к-стю машин BKS, %": 100 * (r.vehicle_diff == 0).mean(),
            "Геп на цих запусках, % (середнє)": matched.gap_pct.mean(),
            "Геп на цих запусках, % (медіана)": matched.gap_pct.median(),
            f"Геп на {len(common)} спільних задачах, %": r[r.instance.isin(common)].gap_pct.mean(),
            "Ітерацій за запуск (медіана)": r.iterations.median(),
        }
    rows["BKS"] = {"CNV (середнє за seed)": bks.bks_vehicles.sum(), "CNV (найкращий з seed)": bks.bks_vehicles.sum(),
                   "CTD (середнє за seed)": bks.bks_distance.sum(), "Задач, де найкращий запуск має к-сть машин BKS": len(bks)}
    return pd.DataFrame(rows).T


def gap_by_class(exp: Experiment) -> pd.DataFrame:
    """Mean distance gap per class on the common instances (where every run has the BKS fleet size)."""
    r = exp.runs[exp.runs.instance.isin(common_instances(exp.runs))]
    t = r.groupby(["class", "algorithm"]).gap_pct.mean().unstack()
    t = t.reindex(index=[c for c in CLASSES if c in t.index], columns=exp.algorithms)
    t.loc["Усі"] = r.groupby("algorithm").gap_pct.mean().reindex(exp.algorithms)
    t["задач"] = r.drop_duplicates("instance").groupby("class").size().reindex(t.index)
    t.loc["Усі", "задач"] = r.instance.nunique()
    return t.rename(columns=LABELS)


def seed_spread(exp: Experiment) -> pd.DataFrame:
    """How much results depend on the seed: per instance spread over seeds, summarised over instances."""
    rows = {}
    for a in [a for a in exp.algorithms if a != "i1-ls"]:
        r = exp.runs[exp.runs.algorithm == a].groupby("instance")
        rows[LABELS[a]] = {
            "Задач, де всі seed дали однаковий розв'язок": int((r.cost.agg(lambda x: x.round(4).nunique()) == 1).sum()),
            "Задач, де к-сть машин залежить від seed": int((r.vehicles.agg(lambda x: x.max() - x.min()) > 0).sum()),
            "Стандартне відхилення відстані (медіана по задачах)": r.distance.std().median(),
            "Розмах відстані, % від BKS (медіана по задачах)": (r.distance.agg(lambda x: x.max() - x.min())
                                                                / r.bks_distance.first() * 100).median(),
        }
    return pd.DataFrame(rows).T


def load_effect(exp: Experiment) -> pd.DataFrame:
    """
    Threat to validity: runs of the same algorithm on the same instance got different amounts of CPU
    (co-running jobs, turbo, other load). Measures how much that changed the result.
    """
    rows = {}
    for a in ["sa", "alns"]:
        r = exp.runs[exp.runs.algorithm == a].copy()
        if r.empty:
            continue
        r["speed"] = r.iterations / r.groupby("instance").iterations.transform("median")
        rho = [stats.spearmanr(x.iterations, x.cost).statistic
               for _, x in r.groupby("instance") if x.cost.nunique() > 1]
        matched = r[r.vehicle_diff == 0]
        rows[LABELS[a]] = {
            "Ітерацій за запуск (медіана)": r.iterations.median(),
            "Відносна швидкість: мінімум": r.speed.min(),
            "Відносна швидкість: 5-й перцентиль": r.speed.quantile(0.05),
            "Відносна швидкість: 95-й перцентиль": r.speed.quantile(0.95),
            "Відносна швидкість: максимум": r.speed.max(),
            "Кореляція Спірмена (ітерації, вартість), медіана по задачах": float(np.nanmedian(rho)),
            "Геп повільніших запусків, %": matched[matched.speed < 1].gap_pct.mean(),
            "Геп швидших запусків, %": matched[matched.speed >= 1].gap_pct.mean(),
        }
    return pd.DataFrame(rows).T


# ---------------------------------------------------------------- statistics

@dataclass
class RankTest:
    criterion: str
    n: int
    mean_ranks: pd.Series
    friedman_stat: float
    friedman_p: float
    critical_difference: float
    pairs: pd.DataFrame


def rank_test(scores: pd.DataFrame, criterion: str) -> RankTest:
    """
    Friedman test over instances (rows) for algorithms (columns, lower is better), Nemenyi critical
    difference of mean ranks, and pairwise Wilcoxon signed-rank tests with Holm's correction.
    """
    # Means of identical values may differ in the last bits; rounding turns them into exact ties.
    scores = scores.dropna().round(6)
    n, k = scores.shape
    ranks = scores.rank(axis=1, method="average")
    stat, p = stats.friedmanchisquare(*(scores[c] for c in scores.columns))
    cd = NEMENYI_Q05[k] * math.sqrt(k * (k + 1) / (6 * n))

    rows = []
    columns = list(scores.columns)
    for i, a in enumerate(columns):
        for b in columns[i + 1:]:
            diff = scores[a] - scores[b]
            wins, losses = int((diff < 0).sum()), int((diff > 0).sum())
            pw = 1.0 if wins + losses == 0 else stats.wilcoxon(scores[a], scores[b], zero_method="wilcox").pvalue
            rows.append({"A": LABELS[a], "B": LABELS[b], "A краще": wins, "нічия": n - wins - losses,
                         "B краще": losses, "медіана A − B": diff.median(), "p": pw})
    pairs = pd.DataFrame(rows)
    pairs["p (Голм)"] = holm(pairs.p.to_numpy())
    return RankTest(criterion, n, ranks.mean().rename(index=LABELS), stat, p, cd, pairs)


def holm(p: np.ndarray) -> np.ndarray:
    """Holm-Bonferroni adjusted p-values: the i-th smallest is multiplied by (m − i + 1), kept monotone."""
    m = len(p)
    adjusted = np.empty(m)
    running = 0.0
    for position, i in enumerate(np.argsort(p)):
        running = max(running, min(1.0, (m - position) * p[i]))
        adjusted[i] = running
    return adjusted


def rank_tests(exp: Experiment) -> list[RankTest]:
    mean = per_instance(exp.runs)
    algs = exp.algorithms
    common = common_instances(exp.runs)
    return [
        rank_test(mean.cost.unstack(0)[algs], "Вартість (машини, потім відстань), усі задачі"),
        rank_test(mean.vehicles.unstack(0)[algs], "Кількість машин, усі задачі"),
        rank_test(mean.distance.unstack(0).loc[common, algs], "Відстань, спільні задачі"),
    ]


def rank_summary(tests: list[RankTest]) -> pd.DataFrame:
    rows = {}
    for t in tests:
        row = t.mean_ranks.to_dict()
        row.update({"задач": t.n, "χ² Фрідмана": t.friedman_stat, "p": t.friedman_p, "CD (Немені, α = 0.05)": t.critical_difference})
        rows[t.criterion] = row
    return pd.DataFrame(rows).T


def pairwise_table(tests: list[RankTest]) -> pd.DataFrame:
    return pd.concat([t.pairs.assign(**{"критерій": t.criterion}) for t in tests]).set_index(["критерій", "A", "B"])


# ---------------------------------------------------------------- convergence

def convergence(exp: Experiment, common: list[str] | None = None,
                grid: np.ndarray | None = None) -> tuple[np.ndarray, pd.DataFrame, pd.DataFrame]:
    """
    Best-so-far quality over time, split into its two levels:
    - vehicles: total excess over BKS on all instances (mean over seeds);
    - gap: mean distance gap on the common instances, where every run ends with the BKS fleet size
      (by default those of this experiment), from the moment every run has reached that fleet.
    """
    # The grid ends at the last recorded point: the final (polished) solution comes a few ms after the limit.
    grid = np.geomspace(first_solution_time(exp), exp.history.time_s.max(), 200) if grid is None else grid
    bks = exp.bks
    common = set(common_instances(exp.runs) if common is None else common)
    excess, gaps = {}, {}

    for a in [a for a in exp.algorithms if a != "i1-ls"]:
        h = exp.history[exp.history.algorithm == a]
        per_seed: dict[int, np.ndarray] = {}
        gap_rows = []
        for (instance, seed), run in h.groupby(["instance", "seed"]):
            t = run.time_s.to_numpy()
            idx = np.searchsorted(t, grid, side="right") - 1
            known = idx >= 0
            idx = np.clip(idx, 0, None)
            vehicles = np.where(known, run.vehicles.to_numpy()[idx], np.nan)
            per_seed[seed] = per_seed.get(seed, 0) + vehicles - bks.bks_vehicles[instance]
            if instance in common:
                distance = run.distance.to_numpy()[idx]
                gap = (distance - bks.bks_distance[instance]) / bks.bks_distance[instance] * 100
                gap_rows.append(np.where(known & (vehicles == bks.bks_vehicles[instance]), gap, np.nan))
        excess[a] = np.mean(list(per_seed.values()), axis=0)
        # The mean is shown only once every run has the BKS fleet: averaging over a growing subset of runs
        # would make the curve jump up whenever a late run joins with a long distance.
        g = np.array(gap_rows)
        gaps[a] = np.where(np.isnan(g).any(axis=0), np.nan, g.mean(axis=0))

    return grid, pd.DataFrame(excess, index=grid), pd.DataFrame(gaps, index=grid)


# ---------------------------------------------------------------- figures

def _style() -> None:
    plt.rcParams.update({
        "font.family": "Times New Roman",
        "figure.dpi": 100, "savefig.dpi": 300, "savefig.bbox": "tight", "font.size": 9,
        "axes.edgecolor": AXIS, "axes.labelcolor": TEXT_2, "axes.titlesize": 10, "axes.titlecolor": TEXT,
        "axes.spines.top": False, "axes.spines.right": False, "axes.grid": True, "axes.axisbelow": True,
        "grid.color": GRID, "grid.linewidth": 0.6, "xtick.color": TEXT_2, "ytick.color": TEXT_2,
        "legend.frameon": False, "lines.linewidth": 2, "text.color": TEXT,
    })


COMMA = FuncFormatter(lambda v, _: f"{v:g}".replace(".", ","))


def fig_vehicles_by_class(exp: Experiment) -> plt.Figure:
    _style()
    g = per_instance(exp.runs).join(exp.bks, on="instance")
    g["excess"] = g.vehicles - g.bks_vehicles
    excess = g.groupby(["algorithm", "class"]).excess.mean().unstack(0).reindex(CLASSES)
    algs = exp.algorithms
    fig, ax = plt.subplots(figsize=(6.3, 3.0))
    width = 0.8 / len(algs)
    x = np.arange(len(CLASSES))
    for i, a in enumerate(algs):
        ax.bar(x + (i - (len(algs) - 1) / 2) * width, excess[a], width, color=COLORS[a], label=LABELS[a],
               edgecolor="white", linewidth=1)
        for xi, value in zip(x + (i - (len(algs) - 1) / 2) * width, excess[a]):
            # Zero bars are invisible, so every bar gets its value; "0" means exactly the BKS fleet.
            zero = round(value, 2) == 0
            text = "0" if zero else f"{value:.2f}".replace(".", ",")
            ax.annotate(text, (xi, value), xytext=(0, 2), textcoords="offset points", ha="center", va="bottom",
                        rotation=0 if zero else 90, fontsize=7, color=TEXT_2)
    ax.set_xticks(x, CLASSES)
    ax.yaxis.set_major_formatter(COMMA)
    ax.set_ylabel("Машин понад BKS\n(середнє на задачу)")
    ax.grid(axis="x", visible=False)
    ax.legend(ncols=len(algs), loc="upper left")
    ax.set_ylim(0, max(excess.max().max() * 1.3, 0.1))
    return fig


def fig_gap_by_instance(exp: Experiment) -> plt.Figure:
    """
    Distance gap of every common instance (mean over seeds), metaheuristics only: one dot per instance,
    spread sideways within the algorithm's slot, and a dash at the class mean (the gap_by_class table).
    """
    _style()
    r = exp.runs[exp.runs.instance.isin(common_instances(exp.runs))]
    gap = r.groupby(["algorithm", "class", "instance"]).gap_pct.mean().clip(lower=0)
    algs = [a for a in METAHEURISTICS if a in exp.algorithms]
    classes = [c for c in CLASSES if c in set(r["class"])]
    fig, ax = plt.subplots(figsize=(6.3, 3.2))
    width = 0.8 / len(algs)
    for i, a in enumerate(algs):
        for j, c in enumerate(classes):
            values = gap.loc[a, c].to_numpy()
            centre = j + (i - (len(algs) - 1) / 2) * width
            spread = np.linspace(-0.35, 0.35, len(values)) * width if len(values) > 1 else np.zeros(1)
            ax.scatter(centre + spread, values, s=14, marker=MARKERS[a], color=COLORS[a], linewidths=0.4,
                       edgecolors="white", zorder=3, label=LABELS[a] if j == 0 else None)
            ax.plot([centre - width * 0.45, centre + width * 0.45], [values.mean()] * 2, color=TEXT,
                    linewidth=1.2, zorder=4)
    ax.plot([], [], color=TEXT, linewidth=1.2, label="середнє по класу")
    n = r.drop_duplicates("instance").groupby("class").size()
    ax.set_xticks(np.arange(len(classes)), [f"{c}\n({n[c]} {_tasks(n[c])})" for c in classes])
    for j in range(1, len(classes)):
        ax.axvline(j - 0.5, color=GRID, linewidth=0.6)
    ax.set_xlim(-0.5, len(classes) - 0.5)
    ax.yaxis.set_major_formatter(COMMA)
    ax.set_ylabel("Відхилення відстані від BKS, %\n(середнє за seed)")
    ax.grid(axis="x", visible=False)
    ax.legend(ncols=len(algs) + 1, loc="lower left", bbox_to_anchor=(0, 1.0), markerscale=1.5)
    return fig


def fig_convergence(exp: Experiment, reference: Experiment | None = None) -> plt.Figure:
    """
    Vehicles on a log time axis (they drop within the first seconds), distance on a linear one (it improves late,
    and the flat stretches keep their true length). reference: experiment that defines the common instances and
    the I1 + LS level (by default the same one).
    """
    _style()
    reference = reference or exp
    common = common_instances(reference.runs)
    start, end = first_solution_time(exp), exp.history.time_s.max()
    log_grid, excess, _ = convergence(exp, common, np.geomspace(start, end, 300))
    linear_grid, _, gaps = convergence(exp, common, np.linspace(start, end, 600))
    i1 = reference.runs[reference.runs.algorithm == "i1-ls"]
    fig, (top, bottom) = plt.subplots(2, 1, figsize=(6.3, 6.0))

    handles = []
    for a in excess.columns:
        style = {"color": COLORS[a], "linestyle": LINESTYLES[a]}
        handles += top.plot(log_grid, excess[a], **style, label=LABELS[a])
        bottom.plot(linear_grid, gaps[a], **style)
        # A curve that starts late (not every run had the BKS fleet yet) gets a dot where it begins.
        shown = gaps[a].dropna()
        if len(shown) and shown.index[0] > linear_grid[0]:
            bottom.plot(shown.index[0], shown.iloc[0], marker="o", markersize=5, color=COLORS[a])
    if not i1.empty:
        style = {"color": COLORS["i1-ls"], "linestyle": LINESTYLES["i1-ls"], "linewidth": 1.5}
        handles.append(top.axhline(i1.vehicle_diff.sum(), **style, label="I1 + LS (старт SA і ALNS)"))
        bottom.axhline(i1[i1.instance.isin(common)].gap_pct.mean(), **style)
    switch = alns_phase_switch(exp)
    if switch:
        for ax in (top, bottom):
            _phase_mark(ax, switch)
        handles.append(Line2D([], [], color=COLORS["alns"], linewidth=1, alpha=0.6, marker="v", markersize=7,
                              markeredgewidth=0, label="ALNS: перехід до фази 2"))

    seeds = exp.runs.seed.nunique()
    top.set_ylabel(f"Машин понад BKS\n(сума по {exp.runs.instance.nunique()} задачах,\n"
                   f"середнє за {seeds} {_runs(seeds)})")
    bottom.set_ylabel(f"Відхилення відстані від BKS, %\n({len(common)} спільних задач)")
    for ax, values in ((top, excess), (bottom, gaps)):
        ax.set_ylim(bottom=0)
        ax.yaxis.set_major_formatter(COMMA)
        _end_labels(ax, {a: values[a].dropna() for a in values.columns})

    top.set_xscale("log")
    top.set_xlim(start * 0.9, end * 1.15)
    top.set_xticks([t for t in (0.1, 0.3, 1, 3, 10, 30, 60) if start <= t <= end * 1.01])
    top.xaxis.set_major_formatter(COMMA)
    top.xaxis.set_minor_formatter(FuncFormatter(lambda *_: ""))
    top.set_xlabel("Час, с (логарифмічна шкала)")
    bottom.set_xlim(0, end * 1.02)
    bottom.xaxis.set_major_formatter(COMMA)
    bottom.set_xlabel("Час, с")
    # Filled column by column: algorithms in the first row, reference lines in the second.
    order = [i for pair in zip(range(3), range(3, 6)) for i in pair if i < len(handles)]
    fig.legend(handles=[handles[i] for i in order], loc="lower left", bbox_to_anchor=(0.1, 1.0), ncols=3)
    fig.tight_layout()
    return fig


def first_solution_time(exp: Experiment) -> float:
    """Moment (s) by which every run of every algorithm has its first solution: curves start there."""
    return float(exp.history.groupby(KEYS).time_s.min().max())


def _phase_mark(ax: plt.Axes, x: float) -> None:
    """Thin line with a triangle on the top edge, so it cannot be mistaken for a data line."""
    ax.axvline(x, color=COLORS["alns"], linewidth=1, alpha=0.45, zorder=1)
    ax.plot([x], [1], marker="v", markersize=7, color=COLORS["alns"], transform=ax.get_xaxis_transform(),
            clip_on=False, zorder=5)


def alns_phase_switch(exp: Experiment) -> float | None:
    """Moment (s) when ALNS moves from vehicle to distance minimisation, from the parameters in run-info.json."""
    info = exp.path / "run-info.json"
    if "alns" not in exp.algorithms or not info.exists():
        return None
    meta = json.loads(info.read_text(encoding="utf-8"))
    parameters = meta["parameters"]["alns"]
    if parameters.get("StopAtCapacityBound", False):
        return None  # Adaptive runs have individual switch times.
    return meta["timeLimitSeconds"] * parameters["VehicleMinimizationShare"]


def _end_labels(ax: plt.Axes, series: dict[str, pd.Series]) -> None:
    """Final value of each line next to its end, nudged apart vertically so labels never overlap."""
    ends = sorted((s.iloc[-1], a, s.index[-1]) for a, s in series.items() if len(s))
    low, high = ax.get_ylim()
    gap = (high - low) * 0.07
    placed: list[float] = []
    for value, a, x in ends:
        y = max(value, placed[-1] + gap) if placed else value
        placed.append(y)
        ax.annotate(f"{value:.1f}".replace(".", ","), (x, value), xytext=(x * 1.03, y), textcoords="data",
                    va="center", fontsize=8, color=TEXT_2)


def fig_ranks(tests: list[RankTest]) -> plt.Figure:
    """
    Mean Friedman ranks per criterion (rank 1 = best). The bracket spans the Nemenyi critical difference from
    the best algorithm: algorithms whose rank falls inside it are not significantly worse by that test.
    """
    _style()
    by_label = {v: k for k, v in LABELS.items()}
    labels = list(tests[0].mean_ranks.index)
    k = len(labels)
    fig, axes = plt.subplots(len(tests), 1, figsize=(6.3, 1.45 * len(tests)), sharex=True)
    for ax, t in zip(np.atleast_1d(axes), tests):
        best = t.mean_ranks.min()
        ax.axvspan(best, best + t.critical_difference, color=GRID, alpha=0.6, linewidth=0)
        for row, label in enumerate(labels):
            a, rank = by_label[label], t.mean_ranks[label]
            ax.plot(rank, row, marker=MARKERS[a], markersize=7, color=COLORS[a], linestyle="none")
            ax.annotate(f"{rank:.2f}".replace(".", ","), (rank, row), xytext=(7, 0), textcoords="offset points",
                        va="center", fontsize=8, color=TEXT_2)
        ax.set_yticks(range(k), labels)
        ax.set_ylim(k - 0.4, -0.6)
        ax.tick_params(axis="y", length=0)
        ax.spines["left"].set_visible(False)
        ax.grid(axis="y", visible=False)
        ax.set_title(f"{t.criterion} (n = {t.n}; CD = {t.critical_difference:.2f})".replace(".", ","),
                     loc="left", fontsize=9)
    axes[-1].set_xlim(0.8, k + 0.3)
    axes[-1].set_xticks(range(1, k + 1))
    axes[-1].set_xlabel("Середній ранг (1 — найкращий); сіра смуга — критична різниця Немені від найкращого")
    fig.tight_layout()
    return fig


def fig_routes(exp: Experiment, instance: str) -> plt.Figure:
    """Routes of the BKS and of the best run of each metaheuristic on one instance."""
    _style()
    nodes = read_instance(exp.data_dir / "instances" / f"{instance.lower()}.txt")
    panels = [("bks", read_routes(exp.data_dir / "bks-solutions" / f"{instance.lower()}.sol"))]
    best = per_instance(exp.runs, best=True)
    for a in [a for a in METAHEURISTICS if a in exp.algorithms]:
        run = exp.runs[(exp.runs.algorithm == a) & (exp.runs.instance == instance)].sort_values("cost").iloc[0]
        panels.append((a, read_routes(exp.path / "solutions" / a / f"{instance.lower()}_s{int(run.seed)}.sol")))

    fig, axes = plt.subplots(2, 2, figsize=(6.3, 6.5))
    depot = nodes.loc[0]
    for ax, (a, routes) in zip(axes.flat, panels):
        ax.scatter(nodes.x[1:], nodes.y[1:], s=6, color=AXIS, zorder=1)
        distance = 0.0
        for route in routes:
            path = nodes.loc[[0, *route, 0]]
            distance += np.hypot(np.diff(path.x), np.diff(path.y)).sum()
            ax.plot(path.x, path.y, color=COLORS[a], linewidth=1, zorder=2)
        ax.plot(depot.x, depot.y, marker="s", markersize=7, color=TEXT, zorder=3)
        vehicles = len(routes)
        ax.set_title(f"{LABELS[a]}: {vehicles} {_machines(vehicles)}, {distance:.2f}".replace(".", ","), loc="left")
        ax.set_aspect("equal")
        ax.set_xticks([])
        ax.set_yticks([])
        ax.grid(False)
        for side in ("left", "bottom"):
            ax.spines[side].set_visible(False)
    fig.suptitle(f"Задача {instance}", x=0.02, ha="left", fontsize=10)
    fig.tight_layout()
    return fig


def _plural(n: int, one: str, few: str, many: str) -> str:
    if n % 10 == 1 and n % 100 != 11:
        return one
    return few if n % 10 in (2, 3, 4) and n % 100 not in (12, 13, 14) else many


def _machines(n: int) -> str:
    return _plural(n, "машина", "машини", "машин")


def _tasks(n: int) -> str:
    return _plural(n, "задача", "задачі", "задач")


def _runs(n: int) -> str:
    return _plural(n, "запуск", "запуски", "запусків")


def fig_load_effect(exp: Experiment) -> plt.Figure:
    """Relative speed (iterations / instance median) of every run in the order the runs finished."""
    _style()
    runs = exp.runs.assign(order=np.arange(len(exp.runs)))
    fig, ax = plt.subplots(figsize=(6.3, 2.8))
    for a in ["sa", "alns"]:
        r = runs[runs.algorithm == a]
        if r.empty:
            continue
        speed = r.iterations / r.groupby("instance").iterations.transform("median")
        ax.scatter(r.order, speed, s=8, marker=MARKERS[a], color=COLORS[a], alpha=0.6, linewidths=0, label=LABELS[a])
    ax.axhline(1, color=TEXT_2, linewidth=1)
    ax.yaxis.set_major_formatter(COMMA)
    ax.set_xlabel("Порядковий номер завершеного запуску")
    ax.set_ylabel("Ітерацій відносно медіани\nдля цієї задачі")
    ax.legend(loc="upper right", markerscale=2)
    return fig


# ---------------------------------------------------------------- Solomon files

def read_instance(path: Path) -> pd.DataFrame:
    rows = []
    for line in path.read_text().splitlines():
        parts = line.split()
        if len(parts) == 7 and all(p.replace(".", "", 1).isdigit() for p in parts):
            rows.append([float(p) for p in parts])
    df = pd.DataFrame(rows, columns=["id", "x", "y", "demand", "ready", "due", "service"])
    return df.astype({"id": int}).set_index("id")


def read_routes(path: Path) -> list[list[int]]:
    return [[int(c) for c in line.split(":", 1)[1].split()]
            for line in path.read_text().splitlines() if line.startswith("Route")]


# ---------------------------------------------------------------- output

def to_markdown(df: pd.DataFrame, decimals: int | dict[str, int] = 2, index_name: str = "") -> str:
    """Markdown table with decimal comma and grouped thousands, as used in Ukrainian texts."""
    columns = [" / ".join(map(str, c)) if isinstance(c, tuple) else str(c) for c in df.columns]
    # Columns holding only whole numbers (counts) are printed without decimals unless set explicitly.
    whole = {c: (pd.to_numeric(df[k], errors="coerce").dropna() % 1 == 0).all()
             for c, k in zip(columns, df.columns)}

    def fmt(value, column) -> str:
        if isinstance(value, str):
            return value
        if value is None or (isinstance(value, float) and math.isnan(value)):
            return "—"
        explicit = isinstance(decimals, dict) and column in decimals
        d = decimals[column] if explicit else 0 if whole[column] else decimals if isinstance(decimals, int) else 2
        if column.startswith("p") and value != 0 and abs(value) < 10 ** -d:
            return f"{value:.1e}".replace(".", ",")
        if round(value, d) == 0:
            value = 0.0  # no "-0,00"
        return f"{value:,.{d}f}".replace(",", " ").replace(".", ",")

    index = [" / ".join(map(str, i)) if isinstance(i, tuple) else str(i) for i in df.index]
    lines = ["| " + " | ".join([index_name] + columns) + " |", "|" + "---|" * (len(columns) + 1)]
    for name, (_, row) in zip(index, df.iterrows()):
        lines.append("| " + " | ".join([name] + [fmt(v, c) for v, c in zip(row, columns)]) + " |")
    return "\n".join(lines)


def run(experiment_dir: str, data_dir: str | None = None, map_instances: tuple[str, ...] = ("R202",),
        convergence_dir: str | None = None) -> Path:
    """convergence_dir: take the convergence curves from another experiment (by default the same one)."""
    exp = load(experiment_dir, data_dir)
    curves = load(convergence_dir, data_dir) if convergence_dir else exp
    out = exp.path / "analysis"
    out.mkdir(exist_ok=True)

    tests = rank_tests(exp)
    tables = {
        "summary": (summary_table(exp), "Підсумок", {"Ітерацій за запуск (медіана)": 0}),
        "classes_mean": (class_table(exp), "Середнє за класами (середнє за 10 seed)", 2),
        "classes_best": (class_table(exp, best=True), "Середнє за класами (найкращий з 10 seed)", 2),
        "gap_by_class": (gap_by_class(exp), "Відхилення відстані від BKS на спільних задачах, %", {"задач": 0}),
        "seed_spread": (seed_spread(exp), "Залежність від seed", 2),
        "ranks": (rank_summary(tests), "Тест Фрідмана: середні ранги", {"p": 4, "задач": 0}),
        "pairwise": (pairwise_table(tests), "Попарні тести Вілкоксона з поправкою Голма", {"p": 4, "p (Голм)": 4}),
        "load_effect": (load_effect(exp), "Вплив навантаження на систему", {"Ітерацій за запуск (медіана)": 0}),
    }
    md = [f"# Аналіз експерименту {exp.path.name}", ""]
    for name, (table, title, decimals) in tables.items():
        table.to_csv(out / f"{name}.csv", encoding="utf-8-sig")
        md += [f"## {title}", "", to_markdown(table, decimals), ""]
    (out / "tables.md").write_text("\n".join(md), encoding="utf-8")

    figures = {
        "fig_vehicles_by_class": fig_vehicles_by_class(exp),
        "fig_gap_by_instance": fig_gap_by_instance(exp),
        "fig_convergence": fig_convergence(curves, reference=exp),
        "fig_ranks": fig_ranks(tests),
        "fig_load_effect": fig_load_effect(exp),
    }
    for instance in map_instances:
        figures[f"fig_routes_{instance.lower()}"] = fig_routes(exp, instance)
    for name, fig in figures.items():
        fig.savefig(out / f"{name}.png")
        plt.close(fig)
    return out


def main() -> None:
    matplotlib.use("Agg")  # files only; in a notebook the inline backend stays
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("experiment", help="experiment folder, e.g. results/main-60s")
    parser.add_argument("--data", help="Solomon data folder (default: <repo>/data/solomon)")
    parser.add_argument("--maps", default="R202", help="comma-separated instances for route maps")
    parser.add_argument("--convergence", help="experiment folder to take the convergence curves from")
    args = parser.parse_args()
    maps = tuple(i.strip().upper() for i in args.maps.split(",") if i.strip())
    out = run(args.experiment, args.data, maps, args.convergence)
    print(f"Written to {out}")


if __name__ == "__main__":
    main()
