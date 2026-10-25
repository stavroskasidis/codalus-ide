# Benchmarks

`codalus-bench` measures the shell (`codalus`) and the reference IDEs (VS Code, JetBrains Rider and the classic IDE) on the same scenarios. Results are JSON files under `results/<os>/<target>/`, and `report` turns the latest result per target and scenario into the Markdown tables used in `docs/adr/0002-performance-budgets.md`.

## Quick start

```bash
dotnet run --project benchmarks/Codalus.Benchmarks.FixtureGen -- all samples   # Large100k, Solution50, Tests5k
```

```bash
dotnet run --project benchmarks/Codalus.Benchmarks.Harness -c Release -- list
```

```bash
dotnet publish src/Codalus.Shell -c Release -r win-x64 -o artifacts/shell
```

```bash
dotnet run --project benchmarks/Codalus.Benchmarks.Harness -c Release -- run --targets shell --scenarios cold-start,self-driven,binary-size
```

```bash
dotnet run --project benchmarks/Codalus.Benchmarks.Harness -c Release -- check
```

```bash
dotnet run --project benchmarks/Codalus.Benchmarks.Harness -c Release -- report --out benchmarks/results/summary.md
```

The `shell` target is the published product shell in `artifacts/shell` (`CODALUS_BENCH_SHELL_DIR` points at another one, for before/after numbers) (the publish puts the plugin host, build host, language service host, C# Interactive host and built-in plugins next to it). It runs every scenario; for `open-solution-50` the fixture's folder is trusted and restore-on-open is off in its fresh user data, since the harness restores the copy itself. Since M3 the editor scenarios run in the shell (`--open`, `--bench`); the results of the retired M0 prototypes remain under `results/windows/avalonia/` and `results/windows/gpui/` for reference. CLAUDE.md has the commands, and the `benchmarks` job of `.github/workflows/pipeline.yml` does all of it on CI.

## Scenarios

| Scenario | What happens | Metrics |
|---|---|---|
| `cold-start` | Launch with nothing open, N times. Run 0 is reported separately as `*_first`. The shell starts with `CODALUS_SKIP_SETUP=1`: a fresh user data folder would otherwise show the first-run setup over the idle IDE ([ADR 0002, amendment 2026-10-20](../docs/adr/0002-performance-budgets.md#amendment-2026-10-20-cold-start-without-the-first-run-setup)). | `startup_ready_ms` (black-box), `startup_interactive_ms` (prototypes, in-app), `startup_selfreported_ms` (VS Code's own `--prof-append-timers`), `memory_idle_*` after 5 s |
| `editor-100k` | Open a copy of the 100k-line C# file, wait until ready plus 10 s, then (with `--allow-input`) type 200 characters at the end of the file every 60 ms and press PageDown 150 times every 50 ms. | `file_ready_ms`, `file_highlight_ms` (prototypes), `memory_file_*`, `typing_input_to_display_ms`, `scroll_input_to_display_ms`, `scroll_responses_over_50ms`, `scroll_responses_missing`, `typing_in_app_ms` (prototypes) |
| `open-solution-50` | Open a restored copy of the 50-project solution (cold: no design-time build cache), then, for the shell, open the same copy again (warm, as after a restart; M34b). VS Code is excluded: without the proprietary C# Dev Kit it has no solution explorer. | `solution_ready_ms` (black-box), `solution_structure_ms` (shell: tree from the solution file), `solution_loaded_ms` (in-app: every project evaluated), `designtime_done_ms` (shell: every project's compiler inputs known, built or from the cache), `language_ready_ms` (shell: the C# language service compiled every project once the solution loaded), `memory_solution_private_mb`, `designtime_cache_hits` and `designtime_cache_misses` (the design-time build cache, M34c; the warm open's reasons for misses are in the notes); the warm open's are `warm_*` |
| `self-driven` | Instrumented targets (the shell), on any OS including CI. The app drives typing (300 keys, 40 ms apart, mid-file) and scrolling (230 lines/s for 10 s, then 5 go-to-line jumps) itself. | `selfdriven_typing_ms`, `selfdriven_scroll_frame_ms`, `selfdriven_jump_ms`, `startup_interactive_ms`, `file_highlight_ms` |
| `test-explorer` | The shell only (in-app events): open a restored copy of `samples/Tests5k` (FixtureGen `tests`: 20 xUnit projects, 5,000 tests), wait until the Test Explorer lists every test from source, apply filters, then run one class. Rider and the classic IDE are compared by hand. | `test_tree_ms` (solution structure → every test listed, before any build), `test_filter_frame_ms` (filter → rendered frame), `test_first_result_ms` (Run → first result, build included; informational) |
| `markup-100k` | The shell only (in-app events): opens a 100k-line `Large.json`, then `Large.xml` (`samples/LargeMarkup100k`), and types in the middle of each. | `json_highlight_ms`, `xml_highlight_ms` (launch → grammar colors rendered), `json_typing_ms`, `xml_typing_ms`, `*_memory_private_mb` |
| `terminal` | The shell only (in-app events, no keystroke injection): a terminal prints a 1M-line file (`cat`, `type` on Windows), then a keystroke is written to the default shell 60 times. | `terminal_frame_ms` (frame intervals while printing), `terminal_cat_ms` and `terminal_cat_lines_per_s`, `terminal_memory_mb` (managed memory the terminal keeps, 10k lines of scrollback), `terminal_echo_ms` (write → echo rendered) |
| `binary-size` | Static measurement. | `shell_mb`, `shell_zip_mb` and one `<helper>_mb` per helper folder (`pluginhost`, `buildhost`, `plugins`, `lshost`, `replhost`); IDEs: `install_mb` (informative) |

## Methods

- **Black-box (Windows):** the method is the same for every target.
  - *Ready:* the main window of the process tree is visible and answers `WM_NULL` within 100 ms. Its **own** rendered content (captured with `PrintWindow`, never a screen grab) must also have been visually stable for 1 s (fewer than 0.5% of thumbnail pixels changed). The reported time is when it *became* stable.
  - *Input → display:* `SendInput` timestamps (QPC) are paired with the first **composed desktop frame** (DXGI Desktop Duplication, `LastPresentTime`) whose dirty or move rectangles touch the target window. Only rectangle metadata is read; pixels are never copied. This works identically for every rendering technology. It includes DWM composition but excludes scan-out (about half a refresh interval on average, the same for every target). PresentMon was evaluated first but rejected: Avalonia composes through DirectComposition and never presents a DXGI swap chain of its own, so per-process present pairing cannot measure it.
  - *Memory:* summed over the whole process tree (shell, language host, Electron helpers, JVM, ServiceHub…), as private and working-set bytes. `*_shell_*` is the root process alone.
- **In-app (prototypes):** the prototypes write timestamped events (`CODALUS_BENCH_EVENTS`). These are comparable between the prototypes, but not with black-box numbers.
- **`file_highlight_ms`:** from the launch to the first rendered frame with semantic tokens of the file (`BenchDriver`). An answer with no tokens (a language service that hasn't loaded the file yet) doesn't count; the editor asks again and the next answer's colors do. Until 2026-10-20 it counted, which gave the Mac's CI runners a second, faster mode of the metric.
- **Statistics:** p50/p95/p99 use nearest rank. Every sample is kept in the JSON.

## Safety rules

- Scenarios that inject keystrokes need `--allow-input`, and **nobody may use the computer during the run**. Target windows are brought to the foreground with `AttachThreadInput`, without synthesizing keys. Each keystroke is sent only after verifying that the target window owns the foreground. If focus moves anywhere else, the run stops and is discarded, so input never lands in another application.
- Every run works on a throwaway copy of the fixture under `benchmarks/.work/`, so IDE autosave can never modify the repository.
- VS Code runs with a throwaway profile. The classic IDE runs as an isolated experimental instance (`/rootsuffix CodalusBench`). Rider uses the user's configuration, because a license is required.

## One-time setup for reference IDEs

- **The classic IDE:** launch `devenv /rootsuffix CodalusBench` once by hand and complete the first-run dialogs (sign-in, theme). Close it before benchmarking.
- **Rider:** in *Settings → Appearance & Behavior → System Settings*, turn off *Reopen projects on startup*. Close all Rider instances before benchmarking, because a running instance would receive the launch instead. **Run the Rider targets from a regular terminal:** a Rider launched from an automation process tree (for example, an agent's sandboxed shell) cannot take over the AF_UNIX `.port` socket left by a previous session, so every launch after the first fails with *Start Failed*.
- **VS Code:** nothing to do. The harness uses a fresh profile with extensions disabled.

## Budgets and regression gates

`budgets.json` is ADR 0002 in machine-readable form.

- `check` applies the absolute budgets to the latest results (reference hardware). Budgets marked `pending` are reported but don't fail.
- `compare --baseline DIR [--accepted FILE] [--confirm]` compares the latest results with the median of up to 10 earlier runs of the same OS (CI runners only with CI runners; one result per run, so a confirmation doesn't count twice): latency p50 +10%, p99 +20% (from 20 samples), memory +10%, binary size +5%, ignoring changes below 5 ms / 5 MB / 0.1 MB. With `--confirm`, each regressed scenario is run again and only fails if the regression repeats. Since 2026-10-17 ([ADR 0002, amendment](../docs/adr/0002-performance-budgets.md#amendment-2026-10-17-accepted-values-and-runner-aware-comparisons)):
  - **Binary size** is deterministic and strict: one earlier run is enough.
  - **Latency and memory** depend on the runner, so they're compared only with earlier runs on the same CPU model (each result records the CPU and its L3 cache), and fail only against at least 3 of them (`minComparableRuns`). With fewer, an increase is a warning (`warn` in the table, an annotation on the run): latency is then divided by the reference application's time in each run (VS Code's `startup_selfreported_ms` in `cold-start`), and memory is compared with the runs there are. `failNormalized: true` in `budgets.json` would let a normalized comparison fail from 3 runs; it is off because on CI the ratio of Codalus's startup to VS Code's ranges from 0.13 to 0.19 between runs.
  - **VS Code's own numbers** are compared and reported, never failed: they measure the runner and VS Code's latest release.
  - **Accepted values:** `accepted.json` holds values the maintainer accepted, per OS, target, scenario, metric and stat, each with its reason, date, commit and CI run (`cpu` too, for latency or memory). Runs from before an entry's date don't count for that metric, and the comparison uses the entry's value until newer runs exist; their median takes over from then on. Accepting a regression is a reviewed change to that file. The 0.3.0 sizes of the language, markup and plugin hosts are the first entries ([why](../docs/benchmarks/net11-release-gate.md)).

## CI

The `benchmarks` job of `.github/workflows/pipeline.yml` (every stable release, as a gate, and a manual dispatch with `benchmarks` checked) runs `cold-start`, `editor-100k` (no input), `self-driven` and `binary-size` on GitHub-hosted Windows, macOS and Linux runners (Linux uses Xvfb and Mesa lavapipe). The runners are GPU-less VMs, so treat those numbers as indicative and comparable only with each other. The budgets in ADR 0002 are enforced on reference hardware. On `master`, the job downloads the results of the last 10 runs on `master` whose benchmarks job passed on the same OS (whatever their other jobs did) and runs the regression gate against them with `accepted.json`; its table, warnings included, goes to the job summary whether it passes or not. The summary's machine line names the runner's CPU and L3 cache.
