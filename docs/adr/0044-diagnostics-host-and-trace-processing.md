# 0044. The diagnostics host and trace processing

- **Status:** accepted (M18a, 2026-10-11; the maintainer's standing go-ahead for the milestones)
- **Date:** 2026-10-11
- **Deciders:** maintainer

## Context

M18 replaces the classic IDE's Diagnostic Tools window, its performance profiler and its memory tools with open, EventPipe-based equivalents that work on Windows, Linux and macOS. M18a does the foundation: live counters and runtime events, CPU sampling, the Performance Profiler hub, the Diagnostic Tools and .NET Counters windows, and hot lines in the editor. M18b adds memory snapshots and diffs, .NET Async, threads and contention, exceptions, file I/O, the events viewer and exports, activity views and BenchmarkDotNet on the same host.

The constraints:
- **The shell is Native AOT.** TraceEvent (the library that reads EventPipe streams and `.nettrace` files) is large, uses reflection and needs JIT; it can't live in the shell.
- **Heavy work is out of process and supervised**, and **no new host starts with the IDE** (the idle budget, ADR 0002).
- **Large traces stay where they are read.** A trace of a minute of a busy server is hundreds of thousands of samples and tens of thousands of distinct stacks; a 1 GB `.nettrace` must open without the UI waiting and without its call tree crossing the wire.
- **Every dependency is OSI-licensed and MIT-compatible** (the roadmap's licence rule), including what a package ships besides its managed code.

## Options

| Question | Options | Verdict |
|---|---|---|
| Where tracing runs | The shell; the debug companion host (ClrMD, M14b); a new host | **A new supervised, lazy `daedalus-diaghost`.** The debug companion is tied to debug sessions and reads dumps; profiling also happens without a debugger, keeps sessions open for minutes and holds opened traces. Started on the first profiling action. |
| Talking to the runtime | Our own diagnostics IPC client; Microsoft.Diagnostics.NETCore.Client | **Microsoft.Diagnostics.NETCore.Client** (MIT; already used by the debug companion): process discovery, EventPipe sessions (with `requestStackwalk: false` for counters on .NET 6 and later), resuming a suspended runtime. |
| Reading events and traces | Our own nettrace parser; TraceEvent | **TraceEvent** (MIT): `EventPipeEventSource` for live sessions (EventCounters, `System.Diagnostics.Metrics`, the runtime's GC and exception events) and `TraceLog` for recorded traces (method names from JIT and rundown events, ReadyToRun methods, IL-to-native maps for source lines). Its native Windows binaries are **excluded** (below). |
| Profiling from launch | A reverse diagnostics server the program connects to; `DOTNET_DefaultDiagnosticPortSuspend` | **`DOTNET_DefaultDiagnosticPortSuspend=1`**: the program is started by the shell (its launch profile, as Run does) and waits before `Main`; the host starts its sessions on the program's own diagnostics port and then sends `ResumeRuntime`. Everything is public API, and nothing listens on a socket of ours. |
| What counts as CPU | Every EventPipe sample; managed-code samples only | **Managed-code samples by default.** EventPipe's sample profiler visits every managed thread every millisecond and tags each sample as in managed code or "external" (native code, or waiting); it can't tell a wait from native work. The views count managed samples, and a filter adds the external ones. |
| Source lines | Our own IL offset mapping; TraceEvent's `GetSourceLine` | **TraceEvent's**, with a symbol path of the modules' own folders and no symbol server: portable PDBs next to the program (or where they were built) give each frame's line from the runtime's IL-to-native maps. Framework modules without PDBs are skipped after a few misses. |
| Sending trees to the shell | Whole trees; aggregated, paged views | **Paged views.** The host keeps the trace and answers a level of the call tree, a page of functions, a pruned flame graph (frames narrower than a fraction of the root left out, at most a few thousand), a timeline in buckets, or one file's lines (ADR 0045). |

## Decision

**Process.** `daedalus-diaghost` (`src/DaedalusStudio.Diagnostics.Host`) speaks JSON-RPC over stdio (`docs/diagnostics-protocol.md`), started lazily by `DiagnosticsService` (workbench `Diagnostics/`) through `SupervisedProcess`; after a crash it restarts, live sessions end with a message, and opened traces are opened again on their next request (`CpuTrace`). `DAEDALUS_DIAGHOST` points at another build; installed builds use `diaghost/` next to the shell. It runs no workspace code, so it also works for untrusted folders (profiling the startup project needs a trusted solution, because it builds it).

**Sessions.**
- **Counters**: EventCounters of the named EventSources (`EventCounterIntervalSec`), `System.Diagnostics.Metrics` meters through `MetricsEventSource` (its own session id; counters, up-down counters, gauges and histogram percentiles), and optionally the runtime's GC (keyword `0x1`) and exception (`0x8000`) events; read live and pushed to the shell in batches four times a second (`diag/counters/update`, `diag/runtimeEvents`). A session ends with the process (`diag/sessionEnded`).
- **CPU traces**: `Microsoft-DotNETCore-SampleProfiler` and the runtime's loader, JIT, ReadyToRun and IL-to-native-map events at the verbose level, with rundown at the end, streamed to a `.nettrace` file. Stopping triggers rundown; a process that exits first leaves a complete file (its methods were captured as they were compiled).
- **Opening a trace** converts it with `TraceLog` to a temporary `.etlx`, builds the profile (functions, the call tree of functions, the address-level stacks, the samples; ADR 0045) and resolves source lines in the background.

**Licences.** TraceEvent's package carries `KernelTraceControl.dll` (ETW kernel sessions, from the Windows Performance Toolkit) and `msdia140.dll` (Windows PDBs, from the classic IDE's DIA SDK), copied by its build props. Both are Microsoft redistributables under proprietary terms. The host references the package with `ExcludeAssets="build;buildTransitive"`, so neither is copied or shipped; EventPipe and portable PDBs don't need them. Recorded in `THIRD-PARTY-NOTICES.md`.

## Consequences

- Profiling needs .NET Core 3.0 or later with the diagnostics port enabled (the default); profiling from launch needs .NET 5 or later (`DefaultDiagnosticPortSuspend`). Programs in containers, WSL or over SSH aren't reachable yet (`TODO(issue)`, with remote development, M21).
- Native frames (the runtime's own code, P/Invoke targets) don't appear: EventPipe's stacks are managed only. Kernel-level profiling (ETW kernel stacks, `perf_events`) stays a candidate after M18.
- The sampling interval is EventPipe's fixed millisecond; overhead was measured on the CPU fixture (see the checkpoint).
- Allocation sampling, gcdumps, the TPL, contention, exception and file I/O events, and the events viewer use the same host and protocol in M18b.
