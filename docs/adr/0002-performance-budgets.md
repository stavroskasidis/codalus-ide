# 0002. Performance budgets

- **Status:** accepted (2026-10-08), amended 2026-10-10 ([see below](#amendment-2026-10-10-rider-dropped-proposed-budgets-approved)) 2026-10-17 (the regression gate: [accepted values and runner-aware comparisons](#amendment-2026-10-17-accepted-values-and-runner-aware-comparisons)) and 2026-10-20 ([`cold-start` without the first-run setup](#amendment-2026-10-20-cold-start-without-the-first-run-setup)). All budgets are final. Rider is no longer a reference, so budget 4 (solution) is final as it stands, and the budgets proposed at M8–M16 are gates. *Update 2026-10-14:* `benchmarks.yml` is gone; the benchmarks are `pipeline.yml`'s `benchmarks` job, run for stable releases or on a dispatch with `benchmarks` checked, with the regression gate against the last 10 runs on `master`. Hosted macOS and Linux results aren't committed yet (`benchmarks/results/` holds Windows only).
- **Date:** 2026-10-08
- **Deciders:** maintainer

## Context

The project brief (principle 5) gave provisional targets and asks M0 to measure the reference IDEs on the same scenarios. It then asks for final budgets that are **at least as good as VS Code for the editor** and **at least as good as Rider for solution and language features**.

| Provisional target | Value |
|---|---|
| Cold start to an interactive empty window | < 1.5 s |
| Keystroke-to-render latency in the editor | p99 < 16 ms |
| Opening and scrolling a 100k-line file | smooth, with no frame > 50 ms |
| 50-project solution to a usable Solution Explorer | < 5 s, with language services ready in the background |
| Idle memory with an empty solution | < 300 MB |

## Method

The full description is in `benchmarks/README.md`. In short:

- **Reference hardware (Windows):** Intel Core i5-13600KF (20 logical cores), 32 GB RAM, NVIDIA GeForce RTX 4060, 165 Hz display, Windows 11 Pro (build 26300). Machine details are recorded in every result file. The machine was unattended during the run, and every target ran at the same 1600×1000 window size.
- **Reference IDEs:** VS Code 1.140.0 (fresh profile, no extensions, so the editor runs on TextMate grammars without a C# language service), JetBrains Rider 2026.2.1 (user configuration, warm caches) and the classic IDE (2026, 18.10 Enterprise; isolated experimental instance).
- **Black-box metrics** use the same procedure for every target, including our prototypes:
  - *Ready:* the window is responsive and its own content has been visually stable for 1 s.
  - *Input → display:* `SendInput` timestamps are paired with the first composed desktop frame that changes the target window (DXGI Desktop Duplication).
  - *Memory:* private bytes of the whole process tree.
- **In-app metrics** are emitted by the prototypes only. They are used for cross-checking, and on CI where window probes are unavailable.
- **Limitations:**
  - Input → display includes DWM composition but not scan-out or panel response (about half a refresh interval on average, the same for every target).
  - The "ready" heuristic treats a still splash or welcome screen as ready.
  - Warm starts only: OS file caches are not flushed. Run 0 of each target is reported separately.
  - Rider and the classic IDE run with real-world configurations, while VS Code runs with no extensions at all.

## Reference measurements (Windows)

The values are p50 / p99 unless noted, all measured with the same black-box procedure. Raw data is in `benchmarks/results/windows/`, and the full table is in `benchmarks/results/summary-windows.md`.

| Scenario | VS Code 1.140 | Classic IDE 2026 (18.10) | JetBrains Rider 2026.2.1 | Prototype A (Avalonia) | Prototype B (GPUI) |
|---|---|---|---|---|---|
| Cold start → ready, empty window | 1517 / 1573 ms (self-reported: 1072 ms) | 921 / 1683 ms (start window) | not measured | 346 / 404 ms | 373 / 400 ms |
| Idle memory (process tree, private) | 1293 MB (8 processes) | 716 MB | not measured | 194 MB | 185 MB |
| 100k-line file → ready | 1821 / 2379 ms | 2060 / 2636 ms | not measured | 1396 ms | 1570 ms |
| Memory with the 100k file | 1608 MB | 1394 MB | not measured | 390 MB | 382 MB |
| Typing, keystroke → displayed frame | 13.2 / 20.0 ms | 17.0 / 201 ms | not measured | 13.6 / 24.9 ms | 11.3 / 18.8 ms |
| PageDown → displayed frame | 10.9 / 25.4 ms | 25.2 / 53.4 ms | not measured | 11.8 / 22.7 ms | 12.2 / 18.7 ms |
| Responses > 50 ms while scrolling | 0 | 2–3 per run | not measured | 0 | 0 |
| 50-project solution → usable Solution Explorer | n/a (needs C# Dev Kit) | not measurable black-box¹ | not measured | 8.5 s² | 8.1 s² |

¹ The classic IDE shows its main window after about 1.0 s and loads the solution asynchronously. A visual-stability probe can't tell when Solution Explorer becomes usable. `TODO(issue)`: detect populated tree items through UI Automation.
² This is bound by the M0 language host, which runs a full MSBuildWorkspace design-time load of all 50 projects before showing the tree. The UI stack doesn't affect it.

**Rider was never measured.** On 2026-10-10 the maintainer dropped it as a reference instead (see the amendment). The command below still works for anyone who wants the comparison. JetBrains IDEs keep an AF_UNIX `.port` socket in their system directory. A Rider launched from the harness process tree cannot take over that socket from a previous session, so every launch after the first fails with "Start Failed". Launching Rider normally works. The Rider portion therefore has to be run from a regular terminal:

```bash
dotnet run --project benchmarks/DaedalusStudio.Benchmarks.Harness -c Release -- run --targets rider --scenarios cold-start,editor-100k,open-solution-50 --allow-input
```

The editor and typing budgets below depend only on VS Code, as the mission requires, so they are final. The solution and language-service budgets were provisional until Rider had been measured. Since the amendment, they are absolute targets.

macOS and Linux: pending. VS Code and the prototypes are measured by `.github/workflows/benchmarks.yml` on GitHub-hosted runners after the first push. Rider and the classic IDE cannot be automated there (Rider needs a license; the classic IDE is Windows-only).

## Proposed budgets

The budgets apply to **reference hardware** (the machine above, or an equivalent self-hosted runner) and use the harness's black-box definitions unless marked "in-app". The rule from the mission: editor budgets must be at least as good as VS Code, and solution and language budgets at least as good as Rider.

| # | Budget | Proposed | Provisional | Basis |
|---|---|---|---|---|
| 1 | Cold start → ready, empty window | **p50 ≤ 800 ms, max ≤ 1.5 s** | < 1.5 s | VS Code 1.52 s and the classic IDE 0.92 s. The prototypes take 0.35 s, which leaves room for M1's real shell. |
| 2 | Typing, keystroke → displayed frame (100k-line file, semantic highlighting on) | **p50 ≤ 13 ms, p99 ≤ 20 ms** | p99 < 16 ms | Matches VS Code (13.2 / 20.0) on a 165 Hz display, including DWM composition. Prototype A is at 13.6 / 24.9 today, so **A must improve in M3**. |
| 2b | Typing, input → rendered (in-app) | **p99 ≤ 16 ms** | (same intent) | Keeps the provisional "render within one 60 Hz frame" intent without compositor noise. A is at 18.0 today (M3), and B at 5.6. |
| 3a | Open the 100k-line file → ready | **p50 ≤ 1.8 s** | – | VS Code 1.82 s. A is at 1.40 s. |
| 3b | Scrolling the 100k-line file | **PageDown → display p99 ≤ 25 ms, no response > 50 ms; self-driven scroll at 230 lines/s with no frame > 50 ms and p99 ≤ 1 refresh interval + 2 ms** | no frame > 50 ms | VS Code 25.4 ms p99. Both prototypes are at about 7 ms per frame. |
| 4 | 50-project solution → Solution Explorer | **Final since 2026-10-10, without Rider (see the amendment):** tree from the solution file ≤ 1.5 s; project nodes and dependencies evaluated ≤ 5 s; language services ready in the background, with a budget set in M3 | < 5 s | The M0 host takes 8.1–8.6 s because it evaluates everything before showing anything. M2's build host (ADR 0004) shows the structure from the solution file first and evaluates in parallel: structure 0.47 s, all projects evaluated 1.19 s (p50 from launch, reference machine), 208 MB for the process tree. |
| 5 | Idle memory, empty window (process tree, private) | **≤ 250 MB** | < 300 MB | VS Code 1.29 GB and the classic IDE 0.72 GB. The prototypes use 185–194 MB, including the language host. |
| 5b | Memory with the 100k-line file open | **≤ 500 MB** | – | VS Code 1.61 GB. A uses 390 MB. |
| 6 | Shell binary size (zipped, excluding the shared host) | regression gate only (+5%) | – | A is 18.7 MB zipped. |

**Interpretation rules.**
- Budgets 1–5b are checked from N ≥ 3 runs (typing: ≥ 600 keystrokes, scrolling: ≥ 450 presses).
- A budget fails when its p50 or p99 limit is exceeded on two consecutive runs on reference hardware.
- When the reference hardware changes, all reference IDEs are re-measured, and the budgets are re-derived with the same rules.

## Enforcement in CI

Two kinds of gate are proposed, because GitHub-hosted runners are GPU-less VMs with noisy timing:

1. **Absolute budgets on reference hardware.** `daedalus-bench` runs on the reference machine before each milestone checkpoint and release (M7 kept the reference machine rather than a self-hosted runner, ADR 0009). A result over budget blocks the release.
2. **Relative regression gates on CI runners.** On manual dispatch (3 OSes; nightly once the repo is public, see the M3 note below), the `self-driven`, `cold-start` and `binary-size` scenarios are compared with the rolling median of the last 10 nightly runs of `master` on the same runner image. The run fails if p50 regresses by more than 10% or p99 by more than 20% (memory: +10%, binary size: +5%). Two consecutive confirmations are needed, to absorb runner noise.

**Implementation (M1).** `benchmarks/budgets.json` holds these budgets and the regression policy. `daedalus-bench check` applies the absolute budgets; editor budgets are measured on the spike and marked pending until M3 moves the editor into the shell. The workflow downloads the results of the last 10 successful runs (GitHub artifacts are the baseline store) and runs `daedalus-bench compare --baseline … --confirm`: a regression fails the run only if an immediate re-run of the same scenario confirms it. Changes under small absolute floors (5 ms, 5 MB, 0.1 MB) are ignored, and p99 is only compared from 20 samples.

**Editor budgets in the shell (M3a).** The editor budgets moved from the M0 spike to the shell (`budgets.json`, target `shell`). On the reference machine (Windows, 2026-10-09): file ready 1.72 s (≤ 1.8 s), memory with the 100k-line file 492 MB (≤ 500 MB; the shell 245 MB, the C# language service with live diagnostics most of the rest), no scroll frames over 50 ms, cold start and budget 4 unchanged. The in-app typing p99 (#2b, self-driven) is 21–23 ms against 16 ms; the spike itself measured 19.5 ms on the same machine, so this budget was never met in the self-driven measurement (p50 7.9 ms vs 7.2 ms). The black-box typing and PageDown budgets (#2, #3b) need `--allow-input` on an unattended machine and were not re-measured. The 50-project solution reaches "language services ready" (every project compiled once) after about 13 s; informational (`language_ready_ms`) until a budget is set.

**CI cost (M3).** The repo is private on GitHub's free plan, so the nightly schedules were removed: the benchmark workflow (and the 3-OS test matrix) run on manual dispatch, once per milestone checkpoint, and the regression baseline is the last 10 such runs. Nightly runs come back when the repo goes public (after M32, roadmap D29) and minutes are free.

## Amendment (2026-10-10): Rider dropped, proposed budgets approved

The maintainer decided this on 2026-10-10 (roadmap decision D28).

- **Rider is no longer a reference.** Its measurements were never taken (the socket problem above), and every milestone from M8 on deferred them.
  - Solution and language-service budgets are now absolute targets, not "at least as good as Rider".
  - Each milestone proposes them at its checkpoint, and the maintainer approves them.
  - Editor budgets don't change: they still follow VS Code.
- **Budget 4 is final** as it stands, at p50 on the reference machine:
  - the tree from the solution file ≤ 1.5 s
  - the projects evaluated ≤ 5 s
- **The budgets proposed at M8–M16 are approved** and gate like the others.
  - Those in `budgets.json` apply now: Test Explorer (M8), packages and properties (M9), the terminal (M10) and the 100k-line markup files (M11).
  - The proposed budgets that have no harness scenario yet become gates when their scenario lands: the M13 history graph, the M14 debugger, and the M15 Endpoints Explorer and Hot Reload. Their checkpoints track them as `TODO(issue)`.

## Amendment (2026-10-17): accepted values and runner-aware comparisons

The 0.3.0 release failed the regression gate on all three runner OSes ([the investigation](../benchmarks/net11-release-gate.md)). The sizes were real: M35 added Visual Basic's Roslyn assemblies to the language host (about 18 MB of ReadyToRun code), and .NET 11 RC2 added 1.4 MB. The latency and memory failures followed the runner's CPU model and L3 cache, not the release. The gate itself had two faults. Its baseline took only runs whose whole workflow had succeeded, so it had one run (0.2.1). And a change, once accepted, could never enter the baseline: the run carrying it failed the gate, and a failed run was never a baseline.

The maintainer decided on 2026-10-17: **accept the 0.3.0 sizes, and fix the gate.** The relative regression gate (*Enforcement in CI*, item 2) now works like this; the thresholds and noise floors don't change.

- **Accepted values.** `benchmarks/accepted.json` holds values the maintainer accepted, per OS, target, scenario, metric and stat. Each entry has a reason, a date, the commit and CI run it was measured in, and, for latency or memory, the runner CPU it holds for. For that metric, baseline runs from before the entry's date don't count, and `codalus-bench compare` uses the entry's value in place of their median until newer runs exist. From then on, the newer runs' median takes over. Accepting a regression is a change to that file, reviewed in its pull request. The first entries are the 0.3.0 sizes of `lshost_mb`, `markuphost_mb` and `pluginhost_mb` on each OS.
- **Baseline runs.** The baseline takes the runs on `master` (this pipeline's, and `release.yml`'s and `benchmarks.yml`'s before it) whose benchmarks job passed on that OS, whatever their other jobs did. It takes the last 10, as before, and counts one result per run.
- **Binary size** is deterministic and stays strict: it fails against the median of the earlier runs, and one run, or an accepted value, is enough.
- **Latency and memory** compare only with earlier runs on the same runner CPU model, and fail only against at least 3 of them (`minComparableRuns`). With fewer, an increase over the limit is a warning in the job summary and an annotation on the run, not a failure:
  - Latency is then divided by the reference application's time in the same run (VS Code's self-reported startup in `cold-start`), and the ratio is compared with the earlier runs' ratios. A normalized comparison only warns, because on CI the ratio of Codalus's startup to VS Code's ranges from 0.13 to 0.19 between runs, which is wider than the thresholds. `failNormalized` in `budgets.json` would make it fail from 3 such runs.
  - Memory has nothing to divide by: the GC sizes its generation-0 budget from the L3 cache, and VS Code's memory doesn't follow that. It is compared with the runs there are.
- **The reference application's own numbers** (VS Code's) are compared and reported, but never fail. They measure the runner and VS Code's latest release, not Codalus.
- **The L3 cache** is recorded in each result's machine block, next to the CPU. The summary and the gate's table show it.
- The gate's table goes to the job summary whether the gate passes or fails.

Replayed on the 0.3.0 results: they pass against the baseline that release had (0.2.1). A language host 6% over its accepted value still fails. Against the runs the new baseline selection finds, the Mac's `file_highlight_ms` fails against three earlier runs on the same CPU, confirmation included. That metric also rose on Linux on the same CPU. It is left to the maintainer, either to accept or to investigate.

## Amendment (2026-10-20): `cold-start` without the first-run setup

The 0.4.0 release (run 39367310356) failed the regression gate on the Mac's `cold-start` `memory_idle_ws_mb`: 276.8 MB against a 266.4 MB limit, confirmation included. The same increase showed on Linux and Windows as warnings, since there were too few runs on the same CPU. The cause isn't the idle IDE. Every harness launch uses a fresh user data folder, so every launch is a first start. `cold-start` opens nothing, so the first-run setup (M46) opened over the IDE and stayed open through the 5 s idle wait. The other scenarios open a file or a solution, or run a bench, and those skip the setup.

- **The setup grew.** Since M34h (`119322e1`), its five theme cards are live miniatures of the shell's chrome in each variant's resources. Measured on Linux (the published AOT shell under Xvfb):
  - With the cards' content stubbed out, idle memory was about 20 MB lower.
  - Without the setup at all, it was about 26 MB lower.
  - At `c6c944c0`, the last baseline run on `master`, the setup was about 7 MB of the idle measurement. The baseline only ever had that much setup in it.
  - The memory is live, not garbage: a forced compacting GC with the setup open doesn't lower it.
- **The decision (the maintainer, 2026-10-20):** `cold-start` measures the idle IDE. The harness starts the shell with `CODALUS_SKIP_SETUP=1` at every launch (`Targets.cs`). That changes only `cold-start`, the one scenario where the setup showed. The first-run setup's own cost is a one-time cost at a new user data folder, and no budget covers it.
- **The baseline needs nothing.** Accepted values (the 2026-10-17 amendment) are for accepting an increase. Here the measurement goes down, by about 7 MB against runs from before 0.4.0, so nothing can fail on it. The earlier runs leave the 10-run window as new releases come in.
  - Until they do, the reference for `memory_idle_*` is a few MB higher than what is measured now, about 3% of it, under the +10% threshold.
  - A regression smaller than that difference would pass in that time.

