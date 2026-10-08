# 0002. Performance budgets

- **Status:** accepted (2026-10-08). Budgets 1–3 and 5 are final; budget 4 (solution) stays provisional until Rider has been measured.
- **Date:** 2026-10-08
- **Deciders:** maintainer

## Context

`ide-prompt.md` (principle 5) gives provisional targets and asks M0 to measure the reference IDEs on the same scenarios. It then asks for final budgets that are **at least as good as VS Code for the editor** and **at least as good as Rider for solution and language features**.

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
- **Reference IDEs:** VS Code 1.140.0 (fresh profile, no extensions, so the editor runs on TextMate grammars without a C# language service), JetBrains Rider 2026.2.1 (user configuration, warm caches) and Visual Studio 2026 18.10 Enterprise (isolated experimental instance).
- **Black-box metrics** use the same procedure for every target, including our prototypes:
  - *Ready:* the window is responsive and its own content has been visually stable for 1 s.
  - *Input → display:* `SendInput` timestamps are paired with the first composed desktop frame that changes the target window (DXGI Desktop Duplication).
  - *Memory:* private bytes of the whole process tree.
- **In-app metrics** are emitted by the prototypes only. They are used for cross-checking, and on CI where window probes are unavailable.
- **Limitations:**
  - Input → display includes DWM composition but not scan-out or panel response (about half a refresh interval on average, the same for every target).
  - The "ready" heuristic treats a still splash or welcome screen as ready.
  - Warm starts only: OS file caches are not flushed. Run 0 of each target is reported separately.
  - Rider and VS run with real-world configurations, while VS Code runs with no extensions at all.

## Reference measurements (Windows)

The values are p50 / p99 unless noted, all measured with the same black-box procedure. Raw data is in `benchmarks/results/windows/`, and the full table is in `benchmarks/results/summary-windows.md`.

| Scenario | VS Code 1.140 | Visual Studio 2026 (18.10) | JetBrains Rider 2026.2.1 | Prototype A (Avalonia) | Prototype B (GPUI) |
|---|---|---|---|---|---|
| Cold start → ready, empty window | 1517 / 1573 ms (self-reported: 1072 ms) | 921 / 1683 ms (start window) | **pending** | 346 / 404 ms | 373 / 400 ms |
| Idle memory (process tree, private) | 1293 MB (8 processes) | 716 MB | **pending** | 194 MB | 185 MB |
| 100k-line file → ready | 1821 / 2379 ms | 2060 / 2636 ms | **pending** | 1396 ms | 1570 ms |
| Memory with the 100k file | 1608 MB | 1394 MB | **pending** | 390 MB | 382 MB |
| Typing, keystroke → displayed frame | 13.2 / 20.0 ms | 17.0 / 201 ms | **pending** | 13.6 / 24.9 ms | 11.3 / 18.8 ms |
| PageDown → displayed frame | 10.9 / 25.4 ms | 25.2 / 53.4 ms | **pending** | 11.8 / 22.7 ms | 12.2 / 18.7 ms |
| Responses > 50 ms while scrolling | 0 | 2–3 per run | **pending** | 0 | 0 |
| 50-project solution → usable Solution Explorer | n/a (needs C# Dev Kit) | not measurable black-box¹ | **pending** | 8.5 s² | 8.1 s² |

¹ VS shows its main window after about 1.0 s and loads the solution asynchronously. A visual-stability probe can't tell when Solution Explorer becomes usable. `TODO(issue)`: detect populated tree items through UI Automation.
² This is bound by the M0 language host, which runs a full MSBuildWorkspace design-time load of all 50 projects before showing the tree. The UI stack doesn't affect it.

**Rider is pending.** JetBrains IDEs keep an AF_UNIX `.port` socket in their system directory. A Rider launched from the harness process tree cannot take over that socket from a previous session, so every launch after the first fails with "Start Failed". Launching Rider normally works. The Rider portion therefore has to be run from a regular terminal:

```bash
dotnet run --project benchmarks/Daedalus.Benchmarks.Harness -c Release -- run --targets rider --scenarios cold-start,editor-100k,open-solution-50 --allow-input
```

The editor and typing budgets below depend only on VS Code, as the mission requires, so they are final. The solution and language-service budgets stay provisional until Rider has been measured.

macOS and Linux: pending. VS Code and the prototypes are measured by `.github/workflows/benchmarks.yml` on GitHub-hosted runners after the first push. Rider and Visual Studio cannot be automated there (Rider needs a license; VS is Windows-only).

## Proposed budgets

The budgets apply to **reference hardware** (the machine above, or an equivalent self-hosted runner) and use the harness's black-box definitions unless marked "in-app". The rule from the mission: editor budgets must be at least as good as VS Code, and solution and language budgets at least as good as Rider.

| # | Budget | Proposed | Provisional | Basis |
|---|---|---|---|---|
| 1 | Cold start → ready, empty window | **p50 ≤ 800 ms, max ≤ 1.5 s** | < 1.5 s | VS Code 1.52 s and VS 0.92 s. The prototypes take 0.35 s, which leaves room for M1's real shell. |
| 2 | Typing, keystroke → displayed frame (100k-line file, semantic highlighting on) | **p50 ≤ 13 ms, p99 ≤ 20 ms** | p99 < 16 ms | Matches VS Code (13.2 / 20.0) on a 165 Hz display, including DWM composition. Prototype A is at 13.6 / 24.9 today, so **A must improve in M3**. |
| 2b | Typing, input → rendered (in-app) | **p99 ≤ 16 ms** | (same intent) | Keeps the provisional "render within one 60 Hz frame" intent without compositor noise. A is at 18.0 today (M3), and B at 5.6. |
| 3a | Open the 100k-line file → ready | **p50 ≤ 1.8 s** | – | VS Code 1.82 s. A is at 1.40 s. |
| 3b | Scrolling the 100k-line file | **PageDown → display p99 ≤ 25 ms, no response > 50 ms; self-driven scroll at 230 lines/s with no frame > 50 ms and p99 ≤ 1 refresh interval + 2 ms** | no frame > 50 ms | VS Code 25.4 ms p99. Both prototypes are at about 7 ms per frame. |
| 4 | 50-project solution → Solution Explorer | **Provisional until Rider has been measured:** tree from the solution file ≤ 1.5 s; project nodes and dependencies evaluated ≤ 5 s; language services ready in the background, with a budget set in M3 | < 5 s | The M0 host takes 8.1–8.6 s because it evaluates everything before showing anything. M2's build host (ADR 0004) shows the structure from the solution file first and evaluates in parallel: structure 0.47 s, all projects evaluated 1.19 s (p50 from launch, reference machine), 208 MB for the process tree. |
| 5 | Idle memory, empty window (process tree, private) | **≤ 250 MB** | < 300 MB | VS Code 1.29 GB and VS 0.72 GB. The prototypes use 185–194 MB, including the language host. |
| 5b | Memory with the 100k-line file open | **≤ 500 MB** | – | VS Code 1.61 GB. A uses 390 MB. |
| 6 | Shell binary size (zipped, excluding the shared host) | regression gate only (+5%) | – | A is 18.7 MB zipped. |

**Interpretation rules.**
- Budgets 1–5b are checked from N ≥ 3 runs (typing: ≥ 600 keystrokes, scrolling: ≥ 450 presses).
- A budget fails when its p50 or p99 limit is exceeded on two consecutive runs on reference hardware.
- When the reference hardware changes, all reference IDEs are re-measured, and the budgets are re-derived with the same rules.

## Enforcement in CI

Two kinds of gate are proposed, because GitHub-hosted runners are GPU-less VMs with noisy timing:

1. **Absolute budgets on reference hardware.** `daedalus-bench` runs on the reference machine (or a self-hosted runner with a GPU, to be decided in M7) before each milestone checkpoint and release. A result over budget blocks the release.
2. **Relative regression gates on CI runners.** Nightly (3 OSes) and on manual dispatch, the `self-driven`, `cold-start` and `binary-size` scenarios are compared with the rolling median of the last 10 nightly runs of `master` on the same runner image. The run fails if p50 regresses by more than 10% or p99 by more than 20% (memory: +10%, binary size: +5%). Two consecutive confirmations are needed, to absorb runner noise.

**Implementation (M1).** `benchmarks/budgets.json` holds these budgets and the regression policy. `daedalus-bench check` applies the absolute budgets; editor budgets are measured on the spike and marked pending until M3 moves the editor into the shell. The nightly workflow downloads the results of the last 10 successful nightly runs (GitHub artifacts are the baseline store) and runs `daedalus-bench compare --baseline … --confirm`: a regression fails the run only if an immediate re-run of the same scenario confirms it. Changes under small absolute floors (5 ms, 5 MB, 0.1 MB) are ignored, and p99 is only compared from 20 samples.
