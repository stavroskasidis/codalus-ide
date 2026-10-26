# 0021. Debugger depth on netcoredbg

- **Status:** accepted (M14 plan, 2026-10-10; the maintainer's standing go-ahead for M9–M16)
- **Date:** 2026-10-10
- **Deciders:** maintainer

## Context

M5 gave the MVP a debugger on netcoredbg over DAP (ADR 0007): launch and attach, line breakpoints with conditions (hit counts and logpoints emulated in the client), stepping, Locals, Autos, Watch, Call Stack, the Debug Console and read-only data tips. M14 closes the gaps with a classic IDE's debugger. It is split:
- **M14a:** what netcoredbg already allows, plus features the client can build on top of it. This ADR.
- **M14b:** the debug companion host (`daedalus-debughost`): `DebuggerDisplay` and type proxies, Make Object ID, the visualizer plugin API with grid and image visualizers, Parallel Stacks and Parallel Watch, dumps (ClrMD), symbol settings, and pinned data tips.
- **M14c:** sessions: compound launches, attach over SSH, into Docker and WSL, the WSL launch profile, Roslyn components, just-in-time debugging, and the Hot Reload research.

What netcoredbg 3.2.0-1092 does, probed over DAP:
- `exceptionInfo` returns the inner-exception chain (`details.innerException`), and `$exception` evaluates.
- `setExceptionBreakpoints` takes `filterOptions` with a condition: a comma-separated list of exact exception type names, negated as a whole by a leading `!`.
- `setVariable` and `setExpression` change locals, fields and properties.
- `evaluate` returns strings whole (5,000 characters and more), so a visualizer can show the full value.
- `module` events carry the name, path and symbol status. There is no `modules` request.
- No `gotoTargets` (Set Next Statement), `stepInTargets` (Step Into Specific), `readMemory`, `disassemble` or data breakpoints: each fails with `E_NOTIMPL`.
- `DebuggerDisplay` is not applied (an object shows as `{Type}`).

## Decisions

**Exception settings: per-type conditions on the `all` filter**
- An exception settings list: a set of types to break on when thrown, and a switch for every other type. They become one `filterOptions` entry on the `all` filter: `A,B` when only listed types break, `!A,B` when every type except the listed ones does. With nothing to break on, the `all` filter is left out. `user-unhandled` stays on.
- Matching is by exact type name, as netcoredbg matches: a base type doesn't cover derived ones. The settings are saved with the solution's session.

**Execution features built from breakpoints**
- **Run to Cursor** adds a hidden breakpoint at the target line (sent with the file's breakpoints) and continues. Any stop removes it. **Force Run to Cursor** also continues past every other breakpoint until it reaches the target. With no session, Run to Cursor starts debugging with a temporary breakpoint on the line.
- **Run to Click** is Ctrl+click on a line in the breakpoint margin while the program is paused. (the classic IDE shows a glyph next to the line on hover; the margin keeps one click target per line.)
- **Temporary breakpoints** are removed after their first stop. **Dependent breakpoints** stay inactive until the breakpoint they depend on is hit in the session. Both are handled in the client, like hit counts.
- **Function breakpoints** use netcoredbg's `setFunctionBreakpoints`.
- Breakpoints get labels. The Breakpoints window filters by label or text, and imports or exports breakpoints as JSON.

**Breakpoint states don't trigger a resend**
- The adapter's answers (verified, pending, the message) now raise `BreakpointStore.StatesChanged`, separate from `Changed`. Until now one event carried both, so every `setBreakpoints` response made the session send the file's breakpoints again.

**Persistence**
- Temporary and dependent breakpoints, labels, function breakpoints and the exception settings are saved with the solution's session, like the M5 breakpoints. Export and import use the same format, as a JSON file.

**Inspection in the shell**
- **The exception helper** opens at the current statement when the program stops on an exception. It shows the type, the message and the inner-exception chain, with View Details (`$exception` in QuickWatch), Copy Details, and "Break when this exception type is thrown" (the exception settings above).
- **QuickWatch** (Shift+F9) evaluates one expression as a tree and adds it to Watch. It is a tool window rather than a modal dialog: the shell has no secondary windows, and a docked QuickWatch stays usable while stepping.
- **Editing values:** Locals and Autos use `setVariable`; Watch roots and members with an evaluate name use `setExpression`.
- **Data tips** become a tree that expands and edits values (the M5 `TODO`).
- **Visualizers** for string values: Text, JSON, XML and HTML, in a read-only document colored by the file type's grammar (ADR 0016). The string is evaluated again in full (netcoredbg doesn't truncate it), unescaped, and JSON and XML are indented; text that doesn't parse is shown as it is. The HTML visualizer shows the source, not a rendering.
- **PerfTips:** the time from resuming the program (a step, Continue, Run to Cursor) to the next stop, shown at the end of the current line. It is measured in the shell, so it includes the adapter's overhead; it is meant for comparing steps, not as a profiler.
- **Threads window:** id, name and top frame for each thread, flags, and switching the active thread. **Modules window:** the modules from `module` events with their symbol status.
- **Reattach to Process** (Shift+Alt+P) attaches to the last process again, by id while it lives, otherwise by name.

## Consequences

- Run to Cursor, temporary and dependent breakpoints cost a stop in the debuggee when they are hit, as hit counts already do. They are invisible to the user, but they make timing-sensitive code behave differently than without the debugger, as the classic IDE's own breakpoints do.
- Known gaps unless netcoredbg gains them: Set Next Statement, Step Into Specific, the Memory, Disassembly and Registers windows, and data breakpoints. They stay listed in the user guide.
- `DebuggerDisplay` waits for M14b's companion host, which can evaluate the attribute's expressions. `TODO(issue)`.
- Revisit the client-side breakpoint features if netcoredbg adds hit counts or temporary breakpoints.
