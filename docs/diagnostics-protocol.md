# Shell ↔ diagnostics host protocol

`daedalus-diaghost` ([ADR 0044](adr/0044-diagnostics-host-and-trace-processing.md)) runs EventPipe sessions on .NET processes (Microsoft.Diagnostics.NETCore.Client) and reads their events and recorded traces (TraceEvent). It speaks JSON-RPC 2.0 with LSP framing (`DaedalusStudio.Rpc`) over stdio. The shell starts it on the first profiling action (`DiagnosticsService` in `src/DaedalusStudio.Workbench/Diagnostics`) and supervises it with `SupervisedProcess`: after a crash it restarts, live sessions end, and opened traces are opened again on their next request. `DAEDALUS_DIAGHOST` points at another build (an apphost or a `.dll`); otherwise `diaghost/` next to the shell is used. Contracts are in `src/DaedalusStudio.Diagnostics.Protocol/DiagnosticsMessages.cs`; the views are described in [ADR 0045](adr/0045-profiler-views.md), and the tools added in M18b (allocations, memory, .NET Async, threads, exceptions, file I/O, activities, the events viewer, exports) in [ADR 0046](adr/0046-profiler-tools.md).

## Conventions

- **Sessions** are named by the shell (`session`, any unique string). Live sessions push notifications; a CPU trace is recorded to a file the shell names.
- **Traces** stay in the host once opened, named by the id `diag/trace/open` returns. Views are asked for a page or a level at a time; nothing sends a whole tree.
- **Filters** (`CpuFilter`): `{startMilliseconds?, endMilliseconds?, threads?, includeExternal, profile?}`. Times are from the trace's start. Without `includeExternal`, samples taken outside managed code (native code, or waiting) don't count. `profile` picks the stack profile the `diag/cpu/*` views count (`cpu` when left out, `allocations`, `contention`, `exceptions`).
- **Stack profiles**: a trace holds up to four, each viewed like the CPU's. A sample has a weight: 1 for a CPU sample or an exception, the KB an allocation tick stands for, the microseconds a contention waited; totals and widths are sums of weights. Allocations and exceptions add their type as a leaf frame (module `[type]`, `[exception]`), contention a `Lock contention` frame.
- **Samples**: EventPipe's sample profiler sees every managed thread once a millisecond; a sample's "total" is having a function (or line) on its stack, "self" running in it. Percentages are of the filter's samples (`total`).
- **Cancellation**: `$/cancelRequest` cancels a session start, a stop, or the opening of a trace.

## Lifecycle

| Method | Params | Result | Notes |
|---|---|---|---|
| `initialize` | `{processId, protocolVersion}` | `{name, version, protocolVersion}` | The first request after every (re)start. `processId` (the shell) is left out of process lists. |
| `shutdown` (request), `exit` (notification) | | | |

## Processes

| Method | Params | Result | Notes |
|---|---|---|---|
| `diag/processes` | `{}` | `{processes: [{id, name, commandLine?}]}` | Processes with a .NET diagnostics port, except this host, the shell and the IDE's other hosts. `commandLine` on Linux only. |
| `diag/resume` | `{processId}` | `{}` | Resumes a program started with `DOTNET_DefaultDiagnosticPortSuspend=1` (profiling from launch), after its sessions have started. Retries while the port isn't open yet. |

## Live sessions

| Method | Params | Result | Notes |
|---|---|---|---|
| `diag/counters/start` | `{session, processId, providers: [{name, kind (eventCounters \| meter), counters?}], intervalSeconds, runtimeEvents}` | `{processName}` | EventCounters of each EventSource (`EventCounterIntervalSec`); meters through `System.Diagnostics.Metrics` (`counters` names instruments); with `runtimeEvents`, the runtime's GC and first-chance exception events. |
| `diag/counters/stop` | `{session}` | `{}` | |
| `diag/counters/update` | notification (host → shell) | `{session, at, values: [{provider, name, displayName, unit?, value, kind (gauge \| rate \| percentile), tags?}]}` | Four times a second at most. `at`: seconds since the session started. A histogram gives one value per percentile, named `<instrument> P50` and so on. |
| `diag/runtimeEvents` | notification (host → shell) | `{session, events: [{at, kind (gc \| exception), text, generation?, durationMilliseconds?, type?}]}` | A GC when it ends, with its generation, reason and duration; an exception with its type and message. |
| `diag/sessionEnded` | notification (host → shell) | `{session, error?}` | The process exited (no error), or the session failed. Also sent for a CPU recording whose process exited before it was stopped. |

## CPU traces

| Method | Params | Result | Notes |
|---|---|---|---|
| `diag/trace/start` | `{session, processId, outputPath, tools?, providers?: [{name, keywords, level, arguments?}]}` | `{processName}` | The `tools` (`cpu` (the default), `allocations`, `async`, `threads`, `exceptions`, `fileio`, `activities`) with the runtime's loader, JIT, ReadyToRun and IL-to-native-map events, plus any extra EventSource or EventPipe `providers`, in one session streamed to `outputPath` (`.nettrace`). With `fileio`, the process's I/O counters go next to it (`name.io.json`). |
| `diag/trace/stop` | `{session}` | `{path, bytes, durationSeconds}` | Stops the session (the runtime writes rundown) and waits for the file. |
| `diag/trace/open` | `{path}` | `{trace, path, processName?, processId?, durationMilliseconds, samples, externalSamples, functions, threads: [{id, name, samples}], profiles: [{kind, unit, total, events}], events, tasks, requests, commands, spans, threadPoolEvents, fileIo}` | Reads the file in one pass (a `.etlx` conversion in the temp folder); source lines are resolved in the background. `profiles` lists the stack profiles with samples; the counts say what the other views have. |
| `diag/trace/close` | `{trace}` | `{}` | |
| `diag/cpu/tree` | `{trace, filter, node, offset, count}` | `{total, nodes: [{id, function, name, module?, inclusive, exclusive, hasChildren}], more}` | Top-down: the children of `node` (-1: the roots), the widest first. |
| `diag/cpu/callers` | `{trace, filter, path, offset, count}` | as `diag/cpu/tree` | Bottom-up: with an empty `path`, the functions with self time; otherwise the callers of the path (function ids from the hot function up). A node's `id` is its function; `exclusive` counts the stacks that end there. |
| `diag/cpu/functions` | `{trace, filter, search?, offset, count}` | `{total, functions: [{id, name, module?, inclusive, exclusive, file?, line?}], more}` | By self time. A recursive function's total counts each sample once. `file` and `line`: its hottest line, once lines are resolved. |
| `diag/cpu/callerCallee` | `{trace, filter, function}` | `{total, function, callers, callees}` | Callers of its outermost calls; callees of every call but itself. |
| `diag/cpu/flame` | `{trace, filter, root, minFraction, maxFrames}` | `{total, frames: [{node, function, name, depth, start, width, self}], path, truncated}` | The flame graph under `root` (-1: everything): `start` and `width` in samples from the root's left edge, `depth` from the root. Frames narrower than `minFraction` of the root are left out, and the threshold doubles until at most `maxFrames` remain (`truncated`). `path`: the nodes from the top down to `root`. |
| `diag/cpu/timeline` | `{trace, filter, buckets}` | `{durationMilliseconds, bucketMilliseconds, samples}` | Samples per bucket for the filter's threads (its time range ignored). |
| `diag/cpu/files` | `{trace, filter}` | `{total, files: [{path, inclusive}], resolved}` | Source files with samples. Waits up to 30 s for symbols; `resolved` is false if they aren't read yet. |
| `diag/cpu/lines` | `{trace, filter, file}` | `{total, lines: [{line, inclusive, exclusive}]}` | One file's lines with samples (paths compared with `/` and `\` alike, case-insensitively on Windows). |

## Comparing traces

| Method | Params | Result | Notes |
|---|---|---|---|
| `diag/cpu/compare` | `{baseline, trace, filter, count}` | `{baselineTotal, total, functions: [{name, module?, baselineSelf, baselineTotal, self, total, delta}]}` | Two open traces' functions matched by name and module: each one's share (0 to 1) of its own trace, the largest changes of the total share first. Only `filter.includeExternal` and `filter.profile` apply. |

## The other tools

| Method | Params | Result | Notes |
|---|---|---|---|
| `diag/async/tasks` | `{trace, filter, search?, offset, count}` | `{total, durationMilliseconds, tasks: [{id, name, method?, start, duration, thread, status?, stack}], more}` | The TPL's operations (`TraceOperation` start and stop), the longest first; `method` names an async method from its state machine's frame; `stack`: the first frames that created it. A missing `status`: still running at the end. |
| `diag/threads` | `{trace, buckets}` | `{durationMilliseconds, bucketMilliseconds, threads: [{id, name, states, running, waiting, contention}], pool: [{at, kind, workers, reason?}]}` | `states` has a character per bucket: `R` running managed code, `W` waiting or in native code, `C` blocked on a lock, a space when not seen; the totals are milliseconds. `pool`: `adjustment` (with the runtime's reason), `start`, `retire`. |
| `diag/exceptions` | `{trace, filter}` | `{total, groups: [{type, throwSite, count, message?, file?, line?}], recent: [{at, type, message?, thread}]}` | Grouped by type and throw site (the function that threw), with its line once symbols are read (waits up to 30 s for them); `recent`: the last 500. |
| `diag/activities` | `{trace, kind (http \| database \| span), filter, byTime, offset, count}` | `{total, durationMilliseconds, items: [{start, duration, name, detail?, status?, source?, id?, parent?, depth, failed}], more}` | HTTP requests (`System.Net.Http`), database commands (DiagnosticSource's SqlClient and EF Core events, and spans with `db.*` tags) or `ActivitySource` spans (nested by `parent`, `depth` in their trace); the longest first unless `byTime`. |
| `diag/fileio` | `{trace}` | `{available, platform, source, samples: [{at, readBytes, writeBytes, readOperations?, writeOperations?}], readBytes, writeBytes, readOperations?, writeOperations?}` | The I/O counters recorded with the trace; `source` says what the OS counts. |
| `diag/events/stats` | `{trace}` | `{total, events: [{provider, name, count}]}` | Every event of the trace by provider and name. |
| `diag/events/list` | `{trace, filter: {providers?, names?, text?, startMilliseconds?, endMilliseconds?, threads?}, offset, count}` | `{total, events: [{index, at, provider, name, thread, payload}], more}` | A page of events; `names` match `Name` or `Provider/Name`; `text` searches payloads (cancellable). |
| `diag/trace/export` | `{trace, filter, format (speedscope \| chromium), outputPath}` | `{path, bytes}` | The filter's profile as Speedscope JSON or Chromium trace events (with the spans, requests and commands). |

## Memory

| Method | Params | Result | Notes |
|---|---|---|---|
| `diag/heap/snapshot` | `{processId, outputPath}` | `{path, bytes}` | A GC heap snapshot (an induced full GC that reports the heap, as dotnet-gcdump takes one), kept as a `.nettrace`. |
| `diag/heap/open` | `{path}` | `{snapshot, path, objects, bytes, types, roots}` | |
| `diag/heap/close` | `{snapshot}` | `{}` | |
| `diag/heap/types` | `{snapshot, baseline?, search?, offset, count}` | `{objects, bytes, types: [{id, name, count, bytes, countDelta?, bytesDelta?}], more}` | By bytes; with a `baseline` (another open snapshot), the changes by name, the most growth first (gone types have `id` -1). |
| `diag/heap/roots` | `{snapshot, path, offset, count}` | `{referrers: [{id, name, count, bytes, hasChildren}], more}` | `path`: a type id, then referrer type ids up. The objects whose first-found referrers match it, grouped by the next referrer: a type (`id` ≥ 0), a root kind (`id` < 0: stack, finalizer queue, GC handle…) or unreached (`id` = int.MinValue). |

## Errors

| Code | Meaning |
|---|---|
| -33300 | No process with that id, or it exited. |
| -33301 | The process has no diagnostics port, or refused the session (not .NET, .NET Core before 3.0, the port disabled). |
| -33302 | No such session. |
| -33303 | The trace or snapshot isn't open (the shell opens it again and retries). |
| -33304 | The trace couldn't be read or recorded; the message says why. |
