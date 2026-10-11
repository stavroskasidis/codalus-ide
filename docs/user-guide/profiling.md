# Profiling and diagnostics

Daedalus Studio profiles .NET programs with the runtime's own **EventPipe**, on Windows, macOS and Linux. The work happens in a separate process, `daedalus-diaghost`, started the first time you use a profiling tool: a trace being read never slows the IDE down, and a large trace stays there while you look at it a view at a time.

What you can profile: programs on .NET Core 3.0 or later (profiling from launch needs .NET 5 or later) running on this machine. Programs in containers, in WSL or over SSH aren't reachable yet.

## The Performance Profiler

**Debug → Performance Profiler…** (`Alt+F2`) opens the profiler's page.

1. Choose the **tools**:
   - **CPU Usage**: where the time goes, sampled every millisecond on every managed thread.
   - **.NET Counters**: CPU, memory, garbage collections and exceptions over time.
2. Choose the **target**:
   - **The startup project**: it is built and started with its launch profile, and waits before its first line until the tools are attached, so its startup is in the profile too. It stops when you stop collecting.
   - **A running .NET process**: pick it from the list (type part of its name or its process id to filter).
3. **Start**. While collecting, the status bar says so; click it, or **Stop Collection** on the page or the Debug menu, to stop. A program that ends stops the collection too.

The session is saved in your solution's `.daedalus/profiler` folder (one folder per session: `session.json`, `cpu.nettrace`, `counters.json`), and its report opens. The page lists the saved sessions to open again or delete; **Debug → Open Profiling Session…** opens one from anywhere, or any `.nettrace` file recorded by another tool (such as `dotnet-trace`). You may want to add `.daedalus/profiler/` to your `.gitignore`.

## The report

At the top, the **timeline** shows the CPU samples over the session: drag across it to look at part of the time only (Escape or **Clear Range** shows all of it). Choose a **thread**, or include **native code and waiting** (EventPipe can't tell a thread waiting from one running native code, so these samples are left out unless you ask). Every view follows these filters.

- **Call Tree**: the calls from the top down, each with its **total** (the samples with the call on the stack) and **self** (the samples running in it) shares. The hot path is expanded for you.
- **Bottom-Up**: the functions that use the CPU themselves, the hottest first; expand one to see who called it.
- **Functions**: every function by self time, with a search box. Select one to see **who calls it** and **what it calls**; double-click a caller or callee to move to it. **Go to Source** (or double-click, or Enter) opens the function at its hottest line.
- **Flame Graph**: each call is a bar as wide as its time, its callees below it. Hover a bar for its numbers, scroll the mouse wheel to zoom and drag to pan (Shift+wheel pans too). Double-click a bar (or select it and press Enter) to show only its calls; **Back** (Backspace) returns. With the keyboard: the arrow keys move between a bar's caller, its first callee and its neighbours; `+` and `-` zoom; Home shows it all. Your code is drawn in warm colours, the framework's in cool ones.
- **Hot Files**: the source files with samples; open one to see its **hot lines**.
- **Counters**: each counter's average, minimum, maximum and last value over the session.

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
- Allocations, memory snapshots and their differences, the .NET Async, threads and contention, exceptions and file I/O tools, the events viewer, activity views and BenchmarkDotNet are coming next.
