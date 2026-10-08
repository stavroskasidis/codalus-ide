# Benchmarks

`daedalus-bench` measures the UI prototypes and the reference IDEs (VS Code, JetBrains Rider and Visual Studio) on the same scenarios. Results are JSON files under `results/<os>/<target>/`, and `report` turns the latest result per target and scenario into the Markdown tables used in `docs/adr/0002-performance-budgets.md`.

## Quick start

```bash
dotnet run --project benchmarks/Daedalus.Benchmarks.FixtureGen -- all samples
```

```bash
dotnet run --project benchmarks/Daedalus.Benchmarks.Harness -c Release -- list
```

```bash
dotnet run --project benchmarks/Daedalus.Benchmarks.Harness -c Release -- run --targets avalonia,gpui --scenarios cold-start,self-driven,binary-size
```

```bash
dotnet run --project benchmarks/Daedalus.Benchmarks.Harness -c Release -- report --out benchmarks/results/summary.md
```

Before running, publish the prototypes into `artifacts/spike-a` and `artifacts/spike-b`, each with a published language service host in `lshost/`. CLAUDE.md has the commands, and `.github/workflows/benchmarks.yml` does all of it on CI. On Windows, download [PresentMon](https://github.com/GameTechDev/PresentMon/releases) (MIT) to `benchmarks/tools/presentmon/PresentMon.exe`. It is gitignored. It needs administrator rights or membership in *Performance Log Users*.

## Scenarios

| Scenario | What happens | Metrics |
|---|---|---|
| `cold-start` | Launch with nothing open, N times. Run 0 is reported separately as `*_first`. | `startup_ready_ms` (black-box), `startup_interactive_ms` (prototypes, in-app), `startup_selfreported_ms` (VS Code's own `--prof-append-timers`), `memory_idle_*` after 5 s |
| `editor-100k` | Open a copy of the 100k-line C# file, wait until ready plus 10 s, then (with `--allow-input`) type 200 characters at the end of the file every 60 ms and press PageDown 150 times every 50 ms. | `file_ready_ms`, `file_highlight_ms` (prototypes), `memory_file_*`, `typing_input_to_present_ms`, `scroll_input_to_present_ms`, `scroll_present_interval_ms`, `scroll_frames_over_50ms`, `typing_in_app_ms` (prototypes) |
| `open-solution-50` | Open a restored copy of the 50-project solution. VS Code is excluded: without the proprietary C# Dev Kit it has no solution explorer. | `solution_ready_ms` (black-box), `solution_loaded_ms` (prototypes, in-app), `memory_solution_private_mb` |
| `self-driven` | Prototypes only, on any OS including CI. The app drives typing (300 keys, 40 ms apart, mid-file) and scrolling (230 lines/s for 10 s, then 5 go-to-line jumps) itself. | `selfdriven_typing_ms`, `selfdriven_scroll_frame_ms`, `selfdriven_jump_ms`, `startup_interactive_ms`, `file_highlight_ms` |
| `binary-size` | Static measurement. | prototypes: `shell_mb`, `shell_zip_mb`, `lshost_mb`; IDEs: `install_mb` (informative) |

## Methods

- **Black-box (Windows):** the method is the same for every target.
  - *Ready:* the main window of the process tree is visible and answers `WM_NULL` within 100 ms. Its **own** rendered content (captured with `PrintWindow`, never a screen grab) must also have been visually stable for 1 s (fewer than 0.5% of thumbnail pixels changed). The reported time is when it *became* stable.
  - *Input → present:* `SendInput` timestamps (QPC) are paired with the first PresentMon present of the target whose frame began after the input. This is input-to-present, not photon-to-photon: compositor and display latency (about 1–2 refresh intervals) are excluded for every target alike.
  - *Memory:* summed over the whole process tree (shell, language host, Electron helpers, JVM, ServiceHub…), as private and working-set bytes. `*_shell_*` is the root process alone.
- **In-app (prototypes):** the prototypes write timestamped events (`DAEDALUS_BENCH_EVENTS`). These are comparable between the prototypes, but not with black-box numbers.
- **Statistics:** p50/p95/p99 use nearest rank. Every sample is kept in the JSON.

## Safety rules

- Scenarios that inject keystrokes need `--allow-input`, and **nobody may use the computer during the run**. Target windows are brought to the foreground with `AttachThreadInput`, without synthesizing keys. Each keystroke is sent only after verifying that the target window owns the foreground. If focus moves anywhere else, the run stops and is discarded, so input never lands in another application.
- Every run works on a throwaway copy of the fixture under `benchmarks/.work/`, so IDE autosave can never modify the repository.
- VS Code runs with a throwaway profile. Visual Studio runs as an isolated experimental instance (`/rootsuffix DaedalusBench`). Rider uses the user's configuration, because a license is required.

## One-time setup for reference IDEs

- **Visual Studio:** launch `devenv /rootsuffix DaedalusBench` once by hand and complete the first-run dialogs (sign-in, theme). Close it before benchmarking.
- **Rider:** in *Settings → Appearance & Behavior → System Settings*, turn off *Reopen projects on startup*. Close all Rider instances before benchmarking, because a running instance would receive the launch instead.
- **VS Code:** nothing to do. The harness uses a fresh profile with extensions disabled.

## CI

`.github/workflows/benchmarks.yml` runs `cold-start`, `editor-100k` (no input), `self-driven` and `binary-size` on GitHub-hosted Windows, macOS and Linux runners (Linux uses Xvfb and Mesa lavapipe). The runners are GPU-less VMs, so treat those numbers as indicative and comparable only with each other. The budgets in ADR 0002 are enforced on reference hardware.
