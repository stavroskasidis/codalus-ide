# 0032. The debug companion host

- **Status:** accepted (M14b, 2026-10-11; the maintainer's standing go-ahead for the milestones)
- **Date:** 2026-10-11
- **Deciders:** maintainer

## Context

ADR 0021 split debugger depth into M14a (what netcoredbg allows, plus client-side features), M14b (a debug companion host) and M14c (remote and special sessions). M14b's scope: `DebuggerDisplay` and type proxies, Make Object ID, a visualizer plugin API with grid and image visualizers, Parallel Stacks and Watch, dumps, symbol settings, pinned data tips.

What netcoredbg (3.2.0-1092) does, probed over DAP for M14b:
- `evaluate` calls methods and property getters, including static ones with arguments (`System.Convert.ToBase64String(bytes)`), and reads private fields. It doesn't evaluate `new`, casts or lambdas, and matches overloads exactly.
- It applies neither `[DebuggerDisplay]` nor `[DebuggerTypeProxy]`. Of `[DebuggerBrowsable]`, it honors only `Never`, and only on properties.
- Stack frames carry a `moduleId`; `module` events give its name.
- No `readMemory`, symbol search paths, thread freezing, object handles or return values after a step out. Exception filters are by type only. Breakpoints have no thread filters.
- `disconnect` with `terminateDebuggee: false` detaches from an attached process. A launched program's console is netcoredbg's (it has no `runInTerminal`), so the program dies at its next console write once netcoredbg exits.

## Decisions

**A companion host for what DAP can't answer**
- `daedalus-debughost` (`src/DaedalusStudio.Debugging.Host`) is a .NET host next to the shell (`debughost/`), started on first use and supervised like the other hosts (`DebugCompanion`, `DAEDALUS_DEBUGHOST`). It speaks JSON-RPC: `daedalus/debug/typeDisplay`, `openDump`, `dumpHeap`, `dumpInstances`, `dumpObject`, `closeDump` and `createDump` (`docs/protocol.md`).
- It never attaches to the debuggee: netcoredbg owns the live process. It reads metadata and dumps.

**Debugger attributes from metadata, applied through evaluation**
- `typeDisplay` reads a type's `[DebuggerDisplay]` and `[DebuggerBrowsable]` from the loaded modules' metadata (System.Reflection.Metadata, base types included). The shell evaluates each `{expression}` of the display string on the object (`(target).member`; `,nq` drops quotes) within a 3-second budget, and hides `Never` members, fields included. Values without the attribute keep netcoredbg's.
- **Type proxies are a gap:** showing one means constructing the proxy (`new Proxy(value)`), which netcoredbg can't evaluate. The grid visualizer reads collections instead.

**Visualizers**
- **Grid** for arrays and the common collections (`List<T>`, `Queue<T>`, `Stack<T>`, `HashSet<T>`, `ImmutableArray<T>`, …): the elements are read as an array (`.ToArray()` when it isn't one), up to 500 rows, with an object's members as columns, in a read-only text table. `IEnumerable` in general and `DataTable` aren't covered: extension methods and `new` can't be evaluated.
- **Image** for byte arrays: `System.Convert.ToBase64String(value)` is evaluated in the debuggee (16 MB at most), and PNG, JPEG, GIF, BMP, WebP and ICO open in the image viewer.
- **The visualizer plugin API** (proposed, API 0.2.7): `IDebug.RegisterVisualizer(options, visualize)` offers a visualizer for type names (a trailing `*` is a prefix). The plugin evaluates expressions in the paused frame (`debug/evaluate`) and returns a declarative view: text with a language, or a table. Plugins start with the new `onDebug` activation event. A built-in rich surface (F3) is not needed for this.

**Windows and toolbars**
- **Parallel Stacks:** every thread's stack, merged from the outermost frame, threads sharing callers grouped until their stacks branch; the active thread's node is marked. **Parallel Watch:** one expression evaluated on every thread, in its topmost frame with source.
- **Processes:** the debugged process with its state; Detach All (attached sessions only), Terminate All, Attach to Process. **Debug Location** toolbar: the process, a thread picker and a frame picker while paused.
- **Pinned data tips:** the data tip's pin button pins the expression to its line. The tip floats right of the line over the editor, is evaluated again at each pause and frame change (red when changed, muted when the program isn't paused), keeps a comment, follows edits, and is saved with the solution's session. Clear All DataTips removes them.

**Client-side conditions, as in M14a**
- **Breakpoint filters:** Visual Studio's syntax (`ThreadId`, `ThreadName`, `ProcessId`, `ProcessName`, `MachineName`, with `=`, `!=`, `&`, `||`, `!` and parentheses). netcoredbg stops; the client continues when the filter doesn't match.
- **Exception conditions by module:** an exception type in Exception Settings takes module names (wildcards; `!` excludes). When a thrown exception's top frame is in another module, the client continues.
- **Step over properties and operators** becomes its own setting (`debug.stepOverPropertiesAndOperators`, netcoredbg's `enableStepFiltering`), separate from Just My Code.

**Dumps (ClrMD)**
- Debug → Open Dump File reads a dump through ClrMD (`Microsoft.Diagnostics.Runtime`, MIT): the threads with their managed stacks and exceptions, frames mapped to source lines through the portable PDBs next to the modules, the heap by type, instances, and an object's fields, followed by address. Debug → Create Dump writes a full dump of the debugged process through the diagnostics IPC (`Microsoft.Diagnostics.NETCore.Client`, MIT), the same way `dotnet-dump` does.
- Full dumps, because a heap dump leaves out the JIT's IL-to-native maps, so frames can't be mapped to lines. A frame without a map shows no line rather than a guessed one.
- The DAC must match the dump's runtime and load on the reading OS: dumps open on the OS that wrote them.
- Since ClrMD 4.2 (2026-10-16), which no longer loads the DAC beside the runtime path a dump names, the host passes the DAC from a .NET installation on the machine (its own, `DOTNET_ROOT`'s, the `dotnet` on the PATH, the default install folders) whose shared framework has the dump's runtime version (`Dumps.InstalledDac`). A dump of a self-contained app's runtime that no installation has doesn't open off Windows. `TODO(issue)`

**Symbol settings**
- No symbol servers or search paths: netcoredbg reads the PDBs next to the modules, and ClrMD likewise for dumps. Library sources still come from embedded sources and Source Link (ADR 0017). Recorded as a gap.

## Consequences

- One more host process, started only when a display attribute, a dump or a dump creation needs it; the shell disposes it with the session.
- `[DebuggerDisplay]` costs an evaluation per expression per object shown, cut off after 3 seconds; values keep netcoredbg's text past the budget.
- Gaps, unless netcoredbg gains them or M14c changes the process model: type proxies, Make Object ID (no handles across evaluations; the companion can't hold handles in a live process it doesn't attach to), return values after a step out, freezing threads, the Memory window, symbol servers, reading another OS's dumps, and Detach All for launched programs. They are listed in the user guide.
- The plugin visualizer API is proposed: it may change before API 1.0 (ADR 0003).
