# Diagnostics fixtures (M18)

Programs for the profiler and the diagnostics tools ([ADR 0044](../../docs/adr/0044-diagnostics-host-and-trace-processing.md), [ADR 0045](../../docs/adr/0045-profiler-views.md), [ADR 0046](../../docs/adr/0046-profiler-tools.md)). Each takes the seconds to run as its first argument (0: until killed).

| Project | What it does | Used by |
|---|---|---|
| `CpuBound` | Counts primes in batches of the primes below a million, 0.1 to 0.2 s each (the hot line is marked `// [hot]` in `Primes.IsPrime`) while a second thread hashes; publishes the EventCounter `Codalus-Samples-CpuBound/work-items` and the meter `Codalus.Samples.CpuBound` (`items.processed`, `batch.duration`). Its launch profile runs it for 3 seconds. | CPU profiling, counters, the Diagnostic Tools window, comparing sessions, the smoke test |
| `Allocations` | Allocates many short-lived strings and some large arrays. | GC events; allocations (types and stacks) |
| `Leaky` | Grows a static cache of `Order`s whose entries are also kept alive by event handlers. | Memory snapshots, their diffs and paths to roots; full dumps |
| `AsyncHeavy` | Fans out tasks that await timers and each other, with a slow chain (`SlowChainAsync`), a contended lock, a first-chance `InvalidOperationException` for every eighth item (marked `// [throw]` in `Worker.Validate`), and blocking work items at the start that starve the thread pool. | .NET Async, threads and contention, exceptions, the events viewer |
| `Activities` | Sends HTTP requests to a server of its own, runs EF Core queries on an in-memory SQLite database, wraps them in `ActivitySource` spans (`round` with `fetch` and `query`), and writes and reads a 1 MB file each round. References `Microsoft.EntityFrameworkCore.Sqlite`. | HTTP requests, database commands, spans, file I/O, exports |
| `Benchmarks` | BenchmarkDotNet benchmarks (`StringBenchmarks` with a baseline and `[MemoryDiagnoser]`, `HashBenchmarks`). References `BenchmarkDotNet`. | Benchmark glyphs, the Benchmarks window and its history |
