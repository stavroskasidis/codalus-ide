# 0007. Debugging

- **Status:** accepted (M5, continuing autonomously toward the MVP)
- **Date:** 2026-10-09
- **Deciders:** maintainer (goal: "keep going until MVP is reached")

## Context

The project brief's M5 asks for a DAP client in the shell plus netcoredbg, with:
- launch and attach
- breakpoints: line, conditional, hit count and logpoints
- stepping
- the call stack, Locals, Autos and Watch windows, and a debug console
- exception settings
- data tips
- multiple threads

Features netcoredbg can't support are to be documented as known gaps. vsdbg is off limits because of its license.

**netcoredbg** (Samsung, MIT; release 3.2.0-1092 of 2026-06-25) has builds for Windows x64, Linux x64/arm64 and macOS arm64. Each archive bundles:
- `dbgshim` (MIT, from .NET diagnostics)
- the Roslyn 2.x scripting assemblies it uses to evaluate expressions (Apache-2.0)

Its DAP `initialize` reports:

| Capability | Supported |
|---|---|
| `configurationDone` | yes |
| Conditional breakpoints | yes |
| Function breakpoints | yes |
| `setVariable` and `setExpression` | yes |
| `exceptionInfo` | yes |
| `terminate` | yes |
| `cancel` | yes |
| Exception filters | `all`, `user-unhandled` |
| Hit-count breakpoints | **no** |
| Logpoints | **no** |
| Exception options per type | **no** |

It sends `initialized` immediately after the `initialize` response.

## Decision

**Adapter.**
- netcoredbg runs as a child process of the shell, one per debug session (`--interpreter=vscode`, DAP over stdio). It is not restarted on a crash: the session ends and says why.
- Where it comes from: `DAEDALUS_NETCOREDBG`, then `debugger/` next to the shell (installers put it there, M7), then `.work/netcoredbg` in a source checkout.
- `eng/get-netcoredbg.sh` downloads a pinned release and checks its SHA-256. CI runs it before the tests.
- It is not committed, and not fetched during a build.

**Client.** `Daedalus.Debugging` holds:
- `DapConnection`: framing shared with `Daedalus.Rpc`, source-generated JSON (AOT-safe), reverse requests refused.
- `DebugAdapter`: typed requests.

The workbench's `DebugService` owns sessions and breakpoints, and the shell draws the UI.

**Launch** reuses M4:
1. Build the startup project.
2. Resolve its launch profile into a `LaunchPlan`.
3. Send netcoredbg `launch` with `program` = the plan's file, `args` = its arguments split like a command line, `cwd` and `env`.
   - `justMyCode` is on (setting `debug.justMyCode`).
   - Program output arrives as DAP `output` events and goes to the Debug Console.
   - netcoredbg gives the debuggee no console input.

**Attach**: Debug → Attach to Process lists processes (the .NET ones first) and sends `attach {processId}`. Stop detaches from an attached process and terminates a launched one.

**Breakpoints** live in the workbench and persist per solution. They are sent on `initialized` and whenever they change.
- **Line** and **conditional** breakpoints use netcoredbg directly.
- **Hit count and logpoints are emulated in the client**, since netcoredbg has neither:
  - The client registers a plain breakpoint (with the condition, if any).
  - On a `breakpoint` stop it identifies the breakpoint (`hitBreakpointIds`, else the top frame's file and line) and counts the hit.
  - It continues without showing the stop when the hit condition (`= n`, `>= n`, `% n`, as in other .NET IDEs) isn't met.
  - For a logpoint, it evaluates the message's `{expressions}`, writes the message to the Debug Console and continues.
- A breakpoint can be disabled; the adapter then doesn't get it at all.

**Exceptions**: Debug → Exception Settings picks the filters: `all` (break when thrown) or `user-unhandled` (the default, as with other .NET debuggers). A stop on an exception shows its type and message (`exceptionInfo`).

**Stepping and threads**:
- Continue (F5), Step Over (F10), Step Into (F11), Step Out (Shift+F11), Break All (Ctrl+Alt+Break), Stop (Shift+F5) and Restart (Ctrl+Shift+F5).
- A stop selects the stopping thread. The call stack shows that thread's frames, and the Threads list switches to another thread.

**Windows.** All are tool windows, and a stop refreshes the ones that are open:
- **Call Stack**: double-click a frame to select it; its source opens at the frame's line.
- **Locals**: the selected frame's scopes, expandable.
- **Autos**: the locals and arguments named on the current and previous lines.
- **Watch**: user expressions, kept per solution.
- **Debug Console**: program and adapter output, plus an input line that evaluates in the selected frame (`repl`).

**Editor**:
- A breakpoint margin: click it or press F9 to toggle; disabled breakpoints are hollow; condition, hit count and log message are set from a context menu.
- The current statement is highlighted with an arrow.
- Data tips: hovering an identifier or member access while stopped evaluates it (`hover`) and shows the value, expandable.
- Edits to a running program's source are not applied (no Edit and Continue).

## Consequences

- Known gaps (netcoredbg):
  - no Edit and Continue
  - no hot reload
  - no mixed-mode debugging
  - no historical debugging
  - no exception settings per exception type
  - no data breakpoints
  - no "set next statement"
  - no debugging of dumps
  - no input for console programs while debugging
- Hit counts and logpoints cost a round trip per hit (the program stops briefly), unlike in an adapter that supports them.
- macOS x64 has no netcoredbg build: debugging there is unavailable until there is one.
- On macOS (arm64), netcoredbg reports every exit code as 0 (found by the M5 CI run); the tests skip that assertion there.
