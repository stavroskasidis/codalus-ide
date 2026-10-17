# The 0.3.0 benchmark gate on .NET 11: what regressed

## Decision

2026-10-17, the maintainer: **accept the 0.3.0 sizes and fix the gate** (no host is made smaller). As done ([ADR 0002, amendment 2026-10-17](../adr/0002-performance-budgets.md#amendment-2026-10-17-accepted-values-and-runner-aware-comparisons)):

- `benchmarks/accepted.json` holds the sizes in [the recommendation](#recommendation), at the 0.3.0 run's measured values (`lshost_mb` 93.519 / 92.645 / 102.326 MiB on Linux / Windows / macOS, `markuphost_mb` 3.576 / 3.589 / 4.046, `pluginhost_mb` 4.139 / 4.140 / 4.679). `compare` uses an accepted value in place of the median until runs newer than it exist.
- The baseline takes runs on `master` whose benchmarks job passed on that OS, whatever their other jobs did.
- Latency and memory fail only against at least 3 earlier runs on the same runner CPU model. With fewer, an increase is a warning, with latency divided by VS Code's startup in the same run. Size stays strict, and one run (or an accepted value) is enough. VS Code's own numbers never fail.
- Each result records the runner's L3 cache next to its CPU, and the summary shows it.

Replayed against the results of this note's runs (the first measurement, then the confirmation run as the gate would): the 0.3.0 run passes on every OS against the baseline it had (0.2.1). The language host 6% over its accepted value still fails. Against the baseline the new selection finds (five Linux and macOS runs, two Windows), Linux and Windows pass with warnings, but the Mac's `file_highlight_ms` fails against its three earlier runs on the same CPU, confirmation included (`editor-100k` 6037 ms, then 4200, against a 3610 ms median; `self-driven` 3302, then 3496, against 2120). That metric rose on Linux on the same CPU too ([Latency on Linux](#latency-on-linux)). It waits for the maintainer: it can be accepted (an entry with the Mac's CPU) or investigated.

Date: 2026-10-16. The stable release run [Release 0.3.0](https://github.com/stavroskasidis/codalus-ide/actions/runs/38933321849) (`f72b78e8`, the first full CI run on .NET 11 RC2) failed the regression gate of the `benchmarks` job ([ADR 0002](../adr/0002-performance-budgets.md)) on all three runner OSes. This note separates what is real from what is the runner, and recommends new baseline values. Nothing here changes the budgets, the baseline or the gate; those wait for the maintainer.

**In short:**

- **Size:** real and deterministic, but almost all of it is **M35 (Visual Basic)**, not .NET 11. The gate's only baseline run was 0.2.1, which predates M35. .NET 11 itself adds 1.4 MB (+1.5%) to the language host.
- **Linux latency:** the runner. The 0.3.0 run landed on a slower runner CPU, and VS Code on the same runner slowed by about the same ratio. Against the one earlier run on the same CPU model, Codalus is flat.
- **Windows memory (`editor-100k` +12%):** it follows the runner's L3 cache size, not the release. Linux is flat or lower on .NET 11, here and on CI.
- **Found on the way:** on this machine the Native AOT shell starts about 35 ms later on .NET 11 RC2 (main → first frame). CI doesn't show it, and it is far inside the budget, but it's real here.
- **The gate itself:** its baseline only takes runs whose whole workflow succeeded, so it had one run. A regression that is accepted can never get into the baseline, because the run that would carry it fails the gate. Size will fail every stable release from now on until that changes (see [The gate](#the-gate)).

## What was measured

- **CI:** the `bench-results-<OS>` artifacts of every run that still has them: `benchmarks.yml` runs 38404658061, 38473864501, 38506099399, 38512282488 and 38517724914; `release.yml` runs 38519575251 and 38616576687 (0.2.1, the gate's baseline); and 0.3.0. Each result file records the runner's CPU.
- **Locally:** a 4-vCPU Intel Xeon VM (260 MB L3), Ubuntu 24.04, Xvfb with software rendering. The machine was shared with other agents' builds, so each comparison alternates its builds round by round, and the load average is noted. Builds:
  - **.NET 10:** `f5938012`, develop just before M30b (the parent of the M30b merge `314a7a96`).
  - **.NET 11:** `314a7a96`, the M30b merge, for the shell comparisons; develop at `1e361924` for the language host options.
  - **0.2.1:** `c28a56c8`, the gate's baseline, for sizes.
- Each shell was published as the pipeline does (`dotnet publish src/Codalus.Shell -c Release -r linux-x64`; the self-contained remote servers were left out, which affects none of the numbers here). The hosts were published as `CodalusBundleForPublish` does: framework-dependent, `PublishReadyToRun=true`. Sizes are `codalus-bench`'s: every file but `*.pdb`, in MiB.

## Size

### Where it came from

| Folder | 0.2.1 (`c28a56c8`, .NET 10) | before M30b (`f5938012`, .NET 10) | M30b (`314a7a96`, .NET 11) | develop (`1e361924`) | CI 0.3.0 (Linux) |
|---|---|---|---|---|---|
| `lshost` | 73.91 | 92.13 | 93.52 | 93.62 | 93.5 |
| `markuphost` | 3.33 | 3.50 | 3.58 | 3.66 | 3.6 |
| `pluginhost` | 3.81 | 4.07 | 4.14 | 4.14 | 4.1 |

- **0.2.1 → before M30b, +18.2 MB in `lshost`:** M35 added the Visual Basic Roslyn assemblies: `Microsoft.CodeAnalysis.VisualBasic.dll` 13.20 MB, `.VisualBasic.Features` 2.59 MB and `.VisualBasic.Workspaces` 2.13 MB after ReadyToRun (17.9 MB). `codalus-lshost.dll` and `Codalus.LanguageServices.Protocol.dll` grew by 0.3 MB with features. The M35 checkpoint didn't measure the size, and no benchmark run happened between 0.2.1 and 0.3.0.
- **.NET 11, +1.4 MB (+1.5%) in `lshost`:** the ReadyToRun code of Roslyn 5.12 and .NET 11's crossgen2 (`CSharp` +0.15, `Workspaces` +0.14, `CSharp.Workspaces` +0.19 MB, a few hundredths elsewhere, the unchanged decompiler included), `Microsoft.DiaSymReader.dll` at Roslyn 5.12's version (0.12 → 0.73 MB), the protocol assembly +0.12 MB, and the .NET 11 apphost (−0.04 MB). No assembly was added, and nothing is composite.
- **`markuphost` and `pluginhost`, +0.3 MB each:** three quarters features (the protocol assemblies grew before M30b), a quarter .NET 11 (+0.08 and +0.07 MB, the protocol assemblies' compiled code).
- The same holds on Windows (73.3 → 92.6 MB) and the Mac (81.7 → 102.3 MB).

### IL and ReadyToRun, per assembly (develop, .NET 11)

| Assembly | IL | ReadyToRun | Used for |
|---|---|---|---|
| `Microsoft.CodeAnalysis.CSharp` | 6.77 | 18.98 | C# |
| `Microsoft.CodeAnalysis.VisualBasic` | 4.64 | 13.27 | VB projects only (loaded for MEF in every session, not run) |
| `Microsoft.CodeAnalysis.Features` | 5.42 | 13.21 | completion, fixes, refactorings |
| `Microsoft.CodeAnalysis.Workspaces` | 4.15 | 11.44 | everything |
| `Microsoft.CodeAnalysis` | 2.97 | 8.10 | everything |
| `ICSharpCode.Decompiler` | 2.98 | 7.16 | Go to Definition into metadata |
| `Microsoft.CodeAnalysis.CSharp.Features` | 2.23 | 5.82 | C# |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | 1.00 | 2.70 | C# |
| `Microsoft.CodeAnalysis.VisualBasic.Features` | 1.10 | 2.62 | VB projects only |
| `codalus-lshost` | 0.81 | 2.18 | everything |
| `Microsoft.CodeAnalysis.VisualBasic.Workspaces` | 0.77 | 2.17 | VB projects only |
| `Codalus.LanguageServices.Protocol` | 0.87 | 2.05 | everything |
| the rest (16 files) | 1.82 | 3.92 | |
| **Total** | **35.53** | **93.62** | |

ReadyToRun multiplies an assembly by 2.4–2.9.

### Options to make it smaller

Each is the language host of develop published with the option. The latency columns come from the `.NET 11` shell with that host swapped in: three alternating rounds of `editor-100k` (2 runs), `completion-50` (cold and warm) and a VB completion run on `samples/VisualBasic` (2 runs), load average 2–12. One-time costs come from two microbenchmarks in a fresh process, 8 alternating runs each: the first decompilation of `System.Text.StringBuilder` (as Go to Definition into metadata does), and the first compilation of a 300-class VB file.

| Option | `lshost` MB | C# `file_highlight_ms` | C# first completion (cold / warm) | C# completion while ready, p50 | VB first completion | One-time cost it adds |
|---|---|---|---|---|---|---|
| As shipped: everything ReadyToRun | 93.62 | 3867 | 2761 / 3140 ms | 16.9 ms | 4786 ms | – |
| `PublishReadyToRunComposite` | 98.34 (+4.7) | not measured | | | | bigger: rejected |
| crossgen2 `--Os` | 92.45 (−1.2) | not measured | | | | too little to matter |
| VB assemblies not ReadyToRun (`PublishReadyToRunExclude`) | 82.06 (−11.6) | 3547 | 3132 / 2378 ms | 19.9 ms | 4529 ms | **the first VB compilation of a session: 647 → 1275 ms (+0.63 s)** |
| and `ICSharpCode.Decompiler` | 77.88 (−15.7) | not measured separately | | | | **the first decompilation: 3.60 → 4.49 s (+0.9 s)** |
| and the rarely used dependencies (Humanizer, Elfie, Scripting, DiaSymReader, ConfigurationManager, EventLog, ProtectedData) | 76.05 (−17.6) | 3619 | 2609 / 2675 ms | 20.0 ms | 4287 ms | small, when first used |
| Partial ReadyToRun from a profile (`PublishReadyToRunPartialAssemblies` with a `.mibc`) | – | | | | | needs `dotnet-pgo`, which isn't published as a tool; not tried |

- **C# latency doesn't move** with any of these: the differences are inside the round-to-round spread (the per-round medians of `file_highlight_ms` span 3367–4599 ms for the shipped host alone).
- **The VB end-to-end run doesn't show the VB cost** (the design-time build dominates its first completion, and the sample is small), but the microbenchmark does: the VB compiler jitted from IL doubles the first compilation of a session. In a large VB project that is the first diagnostics and colors.
- **None of them wins clearly.** Getting near 74 MB means jitting VB (+0.6 s once per session for VB users) or the decompiler (+0.9 s on the first Go to Definition into metadata). C# users would gain 15–18 MB of download and lose nothing measurable. That trade is the maintainer's, so nothing is committed. If VB is to go: `<PublishReadyToRunExclude Include="Microsoft.CodeAnalysis.VisualBasic.dll;Microsoft.CodeAnalysis.VisualBasic.Workspaces.dll;Microsoft.CodeAnalysis.VisualBasic.Features.dll" />` in `src/Codalus.LanguageServices.Host/Codalus.LanguageServices.Host.csproj`.

## Latency on Linux

### CI: the runner

The `ubuntu-latest` runners come in three CPU models. Codalus and VS Code (p50; VS Code's own startup timer) follow the model:

| Run | Commit | .NET | Runner CPU | VS Code startup | Codalus startup | typing | jump | `file_highlight_ms` (editor / self-driven) |
|---|---|---|---|---|---|---|---|---|
| 38404658061 | `5192ccdb` | 10 | EPYC 9V45 | 1165 | 146 | 21.2 | 36.5 | 1809 / 1607 |
| 38517724914 | `f0a55dab` | 10 | EPYC 9V45 | 1297 | 221 | 20.9 | 26.2 | 1736 / 1722 |
| 38519575251 | `f0a55dab` | 10 | EPYC 9V45 | 1238 | 212 | 20.9 | 26.1 | 670 / 1307 |
| **38616576687 (0.2.1, the baseline)** | `c28a56c8` | 10 | EPYC 9V45 | 1144 | 222 | 20.9 | 26.4 | 1607 / 1342 |
| 38506099399 | `18c57277` | 10 | EPYC 7763 | 1821 | 316 | 35.7 | 41.1 | 2550 / 2514 |
| 38512282488 | `e051dad7` | 10 | EPYC 7763 | 1884 | 325 | 42.2 | 41.2 | 2634 / 2515 |
| 38473864501 | `27a95f79` | 10 | EPYC 9V74 | 1489 | 280 | 25.5 | 39.0 | 1859 / 1849 |
| **38933321849 (0.3.0)** | `f72b78e8` | 11 | EPYC 9V74 | 1474 | 262 | 27.1 | 39.4 | 2467 / 2309 |

- **The baseline ran on the fastest model, 0.3.0 on a middle one.** On the same model (9V74), .NET 10 → 0.3.0: VS Code −1%, startup −6%, typing +6%, jumps +1%; `file_highlight_ms` +33% / +25% (the confirmation run: 2745 / 1950).
- The ratio of Codalus's startup to VS Code's is 0.13–0.19 on every run; 0.3.0's is 0.18.
- `file_highlight_ms` is the one metric that rose on the same model. It covers the language host's start and its first semantic tokens of the 100k-line file. 0.3.0 has M35 (the VB assemblies are loaded for MEF in every session) and three days of other features; the local comparison below, on the same code with only the runtime changed, shows no difference beyond noise.
- **Windows** ran its baseline on an EPYC 7763 and 0.3.0 on a Xeon 6973P-C: every latency improved, by 21–56%, VS Code's by 26%. The Mac's `file_highlight_ms` (6037 ms in one of three runs, 3971 ms limit on confirmation) is the same kind of noise as its history (1307–4315 ms).

### Locally: .NET 10 against .NET 11 on the same machine

p50 (n = samples) [range of the per-round medians]:

| Metric | .NET 10 (`f5938012`) | .NET 11 (`314a7a96`) | |
|---|---|---|---|
| `startup_interactive_ms`, cold start, 6 alternating rounds, load 0.2–1.5 | 248 (n=18) [243–253] | 309 (n=18) [279–340] | **+25%** |
| startup phases (16 launches each, load < 4): main entered / first frame / interactive | 34 / 259 / 261 ms | 33 / 291 / 297 ms | +33 ms between main and the first frame |
| the same with `DOTNET_GCgen0size` fixed at 64 MB (5 each) | interactive 272 ms | 322 ms | not the GC's budget |
| `selfdriven_typing_ms` (load 7–15) | 43.2 / 571 (n=2700) [36–48] | 35.7 / 558 (n=2700) [34–40] | noise |
| `selfdriven_scroll_frame_ms` | 16.2 / 139 | 16.2 / 79 | same |
| `selfdriven_jump_ms` | 168 (n=45) [114–242] | 60 (n=45) [58–64] | noise (load) |
| `file_highlight_ms`, `editor-100k` | 4506 (n=9) [3276–4725] | 4742 (n=9) [4558–5463] | inside the spread |
| `file_highlight_ms`, `self-driven` | 3760 (n=9) [2908–4236] | 3268 (n=9) [2471–3495] | inside the spread |
| `completion_first_ms` (cold / warm, n=3) | 5917 / 2481 | 2891 / 6091 | too noisy to read |

- **Typing, scrolling, highlighting and completion:** no difference that rises above this machine's noise.
- **Startup is slower on .NET 11 here**, consistently: every .NET 11 round was slower than every .NET 10 round at low load, and the time goes between `main` and the first frame (building the window). The M30b checkpoint measured +5% on this machine. It doesn't show on CI (−6% on the same runner model, −21% on Windows' faster runner), and 309 ms is far inside budget 1 (800 ms). `TODO(issue)`: a startup profile of the AOT shell on .NET 11 against .NET 10 on reference hardware, and again on the GA runtime.

## Memory

### Windows `editor-100k`: the runner's L3 cache

| Run | .NET | Runner CPU (L3) | `memory_file_private_mb` | of which the shell |
|---|---|---|---|---|
| 38519575251 | 10 | EPYC 7763 (32 MB per CCX) | 809 | 506 |
| 38616576687 (0.2.1, the baseline) | 10 | EPYC 7763 | 835 | 551 |
| 38517724914 | 10 | Xeon Platinum 8573C (260 MB) | 880 | 549 |
| 38933321849 (0.3.0) | 11 | Xeon 6973P-C (about 500 MB) | 936 (confirmation 941) | 597 (604) |

- Memory rises with the runner's L3 size on .NET 10 already. The GC sizes the generation-0 budget from the largest cache: this machine (260 MB L3) reports `GCGen0MaxBudget` = 130 MB for a workstation, concurrent GC process on both .NET 10 and .NET 11 (`GC.GetConfigurationVariables()`), where a 32 MB-L3 EPYC gets a fraction of that. Every process of the tree (the shell, the language host, the plugin host) has its own.
- **On Linux .NET 11 uses no more memory.** CI: 930–961 MB on every run, 0.3.0 942. Locally (`editor-100k`, 6 runs per build, 2 alternating rounds):

| | .NET 10 | .NET 11 |
|---|---|---|
| default (gen0 budget up to 130 MB here) | 1104 (shell 563) | 1048 (shell 565) |
| `DOTNET_GCgen0MaxBudget` capped at 16 MB | 1109 (shell 573) | 1004 (shell 528) |
| idle, empty window (cold start, n=18) | 250 | 224 |

- .NET 11's memory responds to the budget cap (−44 MB) where .NET 10's doesn't, so on a runner with about 500 MB of L3 .NET 11 can plausibly hold more generation-0 memory per process than it does here. That is the likely cause of Windows' +12%, but it isn't proven: it needs the scenario on Windows with and without `DOTNET_GCgen0MaxBudget` (a dispatch with the variable set in the job, or the reference machine). `TODO(issue)`.
- **The hosts' GC settings didn't change:** the language host's `runtimeconfig.json` is the same on both (`System.GC.Server` false, `System.GC.Concurrent` true, `TieredPGO` true). DATAS applies only to Server GC, which no host uses.

## The gate

- **One baseline run.** The baseline is the `bench-results-<OS>` artifacts of the last 10 *successful* `pipeline.yml`, `release.yml` and `benchmarks.yml` runs on `master`. Three `benchmarks.yml` runs on `master` (38404658061, 38473864501, 38506099399) and `release.yml` 38519575251 have results but failed overall, and the successful 38517724914 ran on `hotfix-0.2.1`, so only 0.2.1 counted.
- **An accepted change can't enter the baseline.** A run that exceeds the thresholds fails, and a failed run is never a baseline. The language host is 26% over a 5% threshold and is deterministic, so every stable release (and every dispatch with benchmarks on `master`) fails from now on, whatever is decided about the size.
- **`--confirm` doesn't help against the runner:** it repeats the scenario on the same VM, so a slow runner confirms its own slowness (all 12 Linux failures confirmed, VS Code's startup among them).
- **Different runner models move every latency by up to 2× and memory by up to 15%**, more than the thresholds (10% / 20% / 10%).

## Recommendation

**Accept as the new baseline** (the 0.3.0 run's values):

| OS | `lshost_mb` | `markuphost_mb` | `pluginhost_mb` |
|---|---|---|---|
| Linux | 93.5 | 3.6 | 4.1 |
| Windows | 92.6 | 3.6 | 4.1 |
| macOS | 102.3 | 4.0 | 4.7 |

They are deterministic and explained: Visual Basic (M35) by design, .NET 11 within the 5% policy. Linux latency and Windows memory need no change of their own; they are the runner. Only the startup difference found locally is a real .NET 11 regression, and it doesn't reach CI's numbers.

**For the gate** (each needs the maintainer's approval; nothing is changed here):

1. **A way to accept a change:** `benchmarks/accepted.json` (or a section of `budgets.json`) with per-OS, per-metric values, the commit and the reason. `compare` uses it in place of the median for that metric until newer runs have a median of their own. It is reviewed in the pull request like any other change, which is what accepting a regression should be.
2. **Baseline runs by their benchmarks job, not their workflow:** take the artifacts of runs whose `benchmarks` job passed, whatever an unrelated job did.
3. **Compare latency and memory only between runs on the same runner CPU** (each result records it), or divide Codalus's latency by VS Code's on the same run; with fewer than 3 such runs, report without failing. Size needs neither: one run is enough.
4. **Record the L3 size** in the result's machine block, next to the CPU.
5. **Measure a host's size whenever its dependencies change** (a milestone that adds a package to a host), so a jump like M35's is seen in its own milestone, not at the next release.

**Follow-ups:** the AOT shell's startup on .NET 11 (profile, then GA); Windows memory with the generation-0 budget capped; whether VB's ReadyToRun code is worth its 11.6 MB (the trade above); partial ReadyToRun from a profile, if `dotnet-pgo` becomes available.
