# Profiling and diagnostics

Codalus IDE profiles .NET programs with the runtime's own **EventPipe**, on Windows, macOS and Linux. The work happens in a separate process, `codalus-diaghost`, started the first time you use a profiling tool: a trace being read never slows the IDE down, and a large trace stays there while you look at it a view at a time.

What you can profile: programs on .NET Core 3.0 or later (profiling from launch needs .NET 5 or later) running on this machine. Programs in containers, in WSL or over SSH aren't reachable yet.

## The Performance Profiler

**Debug → Performance Profiler…** (`Alt+F2`) opens the profiler's page.

1. Choose the **tools**:
   - **CPU Usage**: where the time goes, sampled every millisecond on every managed thread.
   - **.NET Counters**: CPU, memory, garbage collections and exceptions over time.
   - **.NET Object Allocation**: the types allocated and the stacks that allocate them (the runtime samples an allocation about every 100 KB).
   - **Memory Usage**: heap snapshots you take while collecting (**Take Snapshot**), and a full dump to inspect objects (**Create Full Dump**).
   - **.NET Async**: async methods and tasks over time, the longest first.
   - **Threads and Contention**: who waits for which lock, the thread pool's injections, each thread's state over time.
   - **Exceptions**: first-chance exceptions over time, grouped by type and where they were thrown.
   - **File I/O**: bytes (and, except on macOS, operations) read and written over time.
   - **Activities**: HTTP requests, database commands and `Activity` spans.
2. Choose the **target**:
   - **The startup project**: it is built and started with its launch profile, and waits before its first line until the tools are attached, so its startup is in the profile too. It stops when you stop collecting.
   - **A running .NET process**: pick it from the list (type part of its name or its process id to filter).
3. **Start**. While collecting, the status bar says so; click it, or **Stop Collection** on the page or the Debug menu, to stop. A program that ends stops the collection too.

The session is saved in your solution's `.codalus/profiler` folder (one folder per session: `session.json`, `trace.nettrace` with the tools' events, `counters.json`, `heap-1.nettrace`… for snapshots, `full.dmp`), and its report opens. The page lists the saved sessions to open again or delete, and **Compare** shows the selected session against another one (the functions whose share changed most, and the heap's growth when both have snapshots); **Debug → Open Profiling Session…** opens one from anywhere, or any `.nettrace` file recorded by another tool (such as `dotnet-trace`). You may want to add `.codalus/profiler/` to your `.gitignore`.

## The report

At the top, the **timeline** shows the CPU samples over the session: drag across it to look at part of the time only (Escape or **Clear Range** shows all of it). Choose a **thread**, or include **native code and waiting** (EventPipe can't tell a thread waiting from one running native code, so these samples are left out unless you ask). Every view follows these filters.

- **Call Tree**: the calls from the top down, each with its **total** (the samples with the call on the stack) and **self** (the samples running in it) shares. The hot path is expanded for you.
- **Bottom-Up**: the functions that use the CPU themselves, the hottest first; expand one to see who called it.
- **Functions**: every function by self time, with a search box. Select one to see **who calls it** and **what it calls**; double-click a caller or callee to move to it. **Go to Source** (or double-click, or Enter) opens the function at its hottest line.
- **Flame Graph**: each call is a bar as wide as its time, its callees below it. Hover a bar for its numbers, scroll the mouse wheel to zoom and drag to pan (Shift+wheel pans too). Double-click a bar (or select it and press Enter) to show only its calls; **Back** (Backspace) returns. With the keyboard: the arrow keys move between a bar's caller, its first callee and its neighbours; `+` and `-` zoom; Home shows it all. Your code is drawn in warm colours, the framework's in cool ones.
- **Hot Files**: the source files with samples; open one to see its **hot lines**.
- **Counters**: each counter's average, minimum, maximum and last value over the session.

When the session recorded allocations, contention or exceptions, **Stacks of** above the views switches all of them to those: the call tree, bottom-up view, functions and flame graph then count kilobytes allocated (with the allocated type as the last frame), microseconds waited for locks, or exceptions thrown (with their type as the last frame). The other tools have their own tabs:

- **Allocations**: the allocated types by kilobytes; expand one for the stacks that allocate it.
- **Memory**: pick a snapshot and what to compare it with. Without a baseline, its types by size; with one (by default the snapshot before), what grew, the most first. Select a type for its **paths to roots**: which types hold its objects, up to the root (a static, a stack, a GC handle…) that keeps them alive. **Inspect Objects in the Full Dump** opens the dump to look at objects and their fields.
- **.NET Async**: tasks and async methods (named after the method, such as `Worker.FetchAsync`) with their duration on the session's time axis; select one for the stack that started it. Search by name.
- **Threads**: each thread over time (green: running managed code, grey: waiting or in native code, red: blocked on a lock), the stacks that waited for locks, and the thread pool's injections (in bold when it was starved or blocked).
- **Exceptions**: exceptions over time, then grouped by type and throw site with a message; double-click (or Enter) opens the line that threw.
- **File I/O**: read and write rates over the session. What the numbers count depends on the OS: on Windows all of the process's I/O (files, network, devices) with operation counts; on Linux the bytes through read and write calls (files, pipes and sockets) and the calls; on macOS the bytes read from and written to disk (cached reads don't count). Per-file detail needs kernel tracing, which isn't available.
- **Activities**: HTTP requests (the URL, status and time), database commands (from SqlClient, Entity Framework Core, and libraries that tag spans with `db.*`), or `Activity` spans nested under their parents, the longest first or in time order.
- **Events**: every event in the trace by provider and name (double-click one to list those), and the events themselves with their payloads, filtered by provider, name, text and the timeline's range. **Export for Speedscope** and **Export Chromium Trace** write the current stack profile for [speedscope.app](https://www.speedscope.app) or Perfetto and the browsers' trace viewers (the Chromium export includes the spans, requests and commands); **Save Trace As** copies the `.nettrace`.

**Hot lines**: while a report is open, the editor shows, in the gutter of each file with samples, the share of the samples with that line on the stack, over a bar that is redder the more of them ran on the line itself. Hover it for the numbers. **Debug → Show Hot Lines in the Editor** (or the report's check box) hides or shows them; closing the report removes them. Lines come from the program's symbols (`.pdb`) next to it, so they match the build you profiled.

## The Diagnostic Tools window

**Debug → Windows → Diagnostic Tools** (`Ctrl+Alt+F2`). While it is open, it follows the program you run (`Ctrl+F5`) or debug (`F5`):
- **CPU**, **GC Heap** and **Working Set** over the last minute, with exceptions marked on the CPU chart and garbage collections on the heap chart;
- **Events**: each garbage collection (its generation, reason and duration) and each exception (type and message), the newest first;
- **Counters**: the runtime's counters, live.

**Record CPU Profile** records a CPU profile of the program until you press it again (or the program ends), then opens its report. To have the window open when debugging starts, turn on **Debugging → Show Diagnostic Tools When Debugging Starts**.

## The .NET Counters window

**View → Other Windows → .NET Counters** watches the counters of any .NET process, like `dotnet-counters`:
1. Enter the process id or (part of) its name, or pick it from the list (**Refresh** updates it).
2. Name the **EventSources** whose counters to watch (`System.Runtime` by default), and any **meters** (`System.Diagnostics.Metrics`), comma-separated. `Name[a;b]` watches only the counters or instruments `a` and `b`, as in `System.Runtime[cpu-usage;working-set], MyApp[orders.placed]`.
3. Set the interval in seconds and **Start**.

Meters show counters as their change per interval, gauges as their value, and histograms as their 50th, 95th and 99th percentiles. **Export CSV** writes one row per value (seconds, provider, counter, tags, unit, kind, value); **Export JSON** writes each counter's values.

## Good to know

- Profiling the startup project builds it, so it needs a trusted solution; attaching to a running process doesn't.
- Stacks are managed code only: time spent in the runtime itself or in native libraries shows up under the managed code that called it.
- A heap snapshot runs a full garbage collection that pauses the program while the heap is walked.
- Snapshots are kept as `.nettrace` files of the runtime's heap events; `dotnet-gcdump`'s `.gcdump` files can't be opened yet.

## Benchmarks

Projects that reference **BenchmarkDotNet** get a stopwatch in the gutter of each `[Benchmark]` method and of each class with benchmarks. Click it to run that benchmark (or the class's): the project is built in Release and run with BenchmarkDotNet, its output goes to the **Benchmarks** output, and the results open in **View → Other Windows → Benchmarks** (mean, error, standard deviation, allocations). The window lists the solution's benchmarks (double-click one to open it), runs all of them or the selected one, chooses BenchmarkDotNet's **job** (Dry for a quick check, Short by default, Medium or Default for numbers to trust) and whether to run **in process** (faster, no separate build). Select a result for its **history**: every earlier run of that benchmark, with the change from the run before, kept in `.codalus/benchmarks/history.json`. Running benchmarks builds and runs your code, so it needs a trusted solution.
