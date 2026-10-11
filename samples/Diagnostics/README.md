# Diagnostics fixtures (M18)

Programs for the profiler and the diagnostics tools ([ADR 0044](../../docs/adr/0044-diagnostics-host-and-trace-processing.md), [ADR 0045](../../docs/adr/0045-profiler-views.md)). Each takes the seconds to run as its first argument.

| Project | What it does | Used by |
|---|---|---|
| `CpuBound` | Counts primes (the hot line is marked `// [hot]` in `Primes.IsPrime`) while a second thread hashes; publishes the EventCounter `Daedalus-Samples-CpuBound/work-items` and the meter `Daedalus.Samples.CpuBound` (`items.processed`, `batch.duration`). Its launch profile runs it for 3 seconds. | CPU profiling, counters, the Diagnostic Tools window, the smoke test |
| `Allocations` | Allocates many short-lived strings and some large arrays. | GC events; allocation sampling (M18b) |
| `Leaky` | Grows a static cache whose entries are kept alive by event handlers. | Memory snapshots and diffs (M18b) |
| `AsyncHeavy` | Fans out tasks that await timers and each other, with a slow chain and a contended lock. | .NET Async, threads and contention (M18b) |

The BenchmarkDotNet project the roadmap lists comes with the BenchmarkDotNet integration (M18b).
