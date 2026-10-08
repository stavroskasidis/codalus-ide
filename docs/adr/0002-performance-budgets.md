# 0002. Performance budgets

- **Status:** proposed (awaiting maintainer approval at the M0 checkpoint; once approved, CI enforces the budgets)
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

- **Reference hardware (Windows):** Intel Core i7 13th gen, NVIDIA GeForce GPU, Windows 11 Pro. The display refresh rate is recorded in every result file. The machine was unattended during the run, and every target ran at the same 1600×1000 window size.
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

<!-- REFERENCE:BEGIN -->
_Filled in from the benchmark run._
<!-- REFERENCE:END -->

macOS and Linux: pending. VS Code and the prototypes are measured by `.github/workflows/benchmarks.yml` on GitHub-hosted runners after the first push. Rider and Visual Studio cannot be automated there (Rider needs a license; VS is Windows-only).

## Proposed budgets

<!-- BUDGETS:BEGIN -->
_Filled in from the benchmark run._
<!-- BUDGETS:END -->

## Enforcement in CI

Two kinds of gate are proposed, because GitHub-hosted runners are GPU-less VMs with noisy timing:

1. **Absolute budgets on reference hardware.** `daedalus-bench` runs on the reference machine (or a self-hosted runner with a GPU, to be decided in M7) before each milestone checkpoint and release. A result over budget blocks the release.
2. **Relative regression gates on CI runners.** On every PR to `main` (3 OSes) and nightly, the `self-driven`, `cold-start` and `binary-size` scenarios are compared with the rolling median of the last 10 nightly runs of `main` on the same runner image. The PR fails if p50 regresses by more than 10% or p99 by more than 20% (memory: +10%, binary size: +5%). Two consecutive confirmations are needed, to absorb runner noise. `TODO(issue)`: implement the baseline store and the comparison step (M1; the harness already emits the JSON these gates need).
