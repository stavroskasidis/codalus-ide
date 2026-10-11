# 0045. Profiler views

- **Status:** accepted (M18a, 2026-10-11; the maintainer's standing go-ahead for the milestones)
- **Date:** 2026-10-11
- **Deciders:** maintainer

## Context

ADR 0044 puts the traces in `daedalus-diaghost`. This ADR settles what the user sees: how a profiling session starts and is kept, the report's views and how they are computed and paged, the Diagnostic Tools and .NET Counters windows, hot lines in the editor, and how the custom controls are made accessible (the roadmap asks every profiler view's ADR for its screen-reader design).

## Decisions

**The Performance Profiler** (Debug → Performance Profiler…, Alt+F2) is a document page: the tools (CPU Usage, .NET Counters), the target (the startup project, built and launched held before `Main` so its startup is profiled, or a running .NET process found by id or name), Start and Stop Collection (also on the status bar while collecting), and the saved sessions to reopen or delete. A session ends when the user stops it or the program exits; a launched program stops with it.

**Sessions** are folders under the solution's `.daedalus/profiler/` (the state folder's `profiler/` without a solution): `session.json` (name, start, duration, target, project, process, tools), `cpu.nettrace` and `counters.json` (the .NET Counters export's JSON). They are reopened from the hub or with Debug → Open Profiling Session…, which also opens any `.nettrace` file. Comparing two sessions comes with memory diffs in M18b.

**The report's model** (`CpuProfile` in the host) has the functions; a **call tree of functions**, a node per distinct call path, numbered so a parent precedes its children; the **address-level stacks** the samples point at, each frame keeping its code address for source lines; and the samples (time, thread, stack, managed or not). A filter (a time range, threads, native samples) is applied by one pass over the samples into per-node counts, summed up the tree in one backward pass; the last four filters are cached, so expanding and paging are cheap.

**Views** (`CpuViews`), each a request:
- **Call Tree** (top-down): a node's children, the widest first, 200 at a time; the report opens the hot path by itself (the widest child while it holds half of its parent).
- **Bottom-Up**: the functions with self time, the hottest first; a node's children are the callers of its path, read from the leaf up.
- **Functions** with **Caller/Callee**: self and total time (a recursive function's time counted once, at its outermost call); the callers of the outermost calls and the callees of every call except itself; Go to Source opens the function's hottest line.
- **Flame Graph**: frames laid out depth-first, the widest first, under a zoom root; frames narrower than 0.05% of the root are left out and the threshold doubles until at most 6,000 frames remain. Double-click or Enter asks for a frame's subtree; Back returns. Within the frames it has, the control zooms (wheel, +/-) and pans (drag, Shift+wheel) locally.
- **Hot Files** and **hot lines**: per source line, the samples with the line on the stack (total) and running on it (self), counted once per sample. The report sends its files' lines to the editor gutter (`HotLineMargin`, `HotLineStore`) for its filter; closing the report clears them; Debug → Show Hot Lines toggles them.
- **Timeline**: samples in 200 buckets for the filter's threads; dragging on it selects the time range that filters every view, Escape or Clear Range removes it.
- **Counters**: the session's counters with their average, minimum, maximum and last value.

**The Diagnostic Tools window** (Debug → Windows, Ctrl+Alt+F2) follows the newest local program run (Ctrl+F5) or debugged (F5) while it is open: CPU, GC heap and working set over the last minute (`TimeSeriesChart`), GCs and exceptions marked and listed, and the live `System.Runtime` counters; Record CPU Profile records a trace of the program and opens its report. It opens by itself when debugging starts only with the setting `diagnostics.showDiagnosticToolsOnDebug` (off by default: every F5 would start the host and an EventPipe session on the debuggee).

**The .NET Counters window** (View → Other Windows) attaches to any .NET process by id or name and watches the EventSources and meters named (`Name[counter;counter]` picks counters), with Export as CSV (one row per value: seconds, provider, counter, tags, unit, kind, value) or JSON (`counters.json`'s format).

**Accessibility.** The flame graph is focusable and keyboard-driven (arrows move to the parent, the first child and the neighbours; Enter zooms; Backspace goes back; Home resets), its selected frame is described in a polite live region under it, and every frame's numbers are also in the Call Tree, which screen readers read as a tree with each node's name and shares. The timeline's range is also stated in text (and cleared with a button); the charts have names saying what they show, and their values are in the Counters list.

## Consequences

- The UI never holds a trace: a view is at most a page, a pruned flame graph or one file's lines, whatever the trace's size.
- Hot lines come from the PDBs next to the program, so a rebuilt program's lines may drift until the next session (the margin doesn't mark them stale yet; `TODO(issue)`).
- Session diffs, the events viewer and Speedscope/Chromium export are M18b.
