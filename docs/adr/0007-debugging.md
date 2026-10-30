# 0007. Debugging

- **Status:** accepted (M5, continuing autonomously toward the MVP)
- **Date:** 2026-10-09
- **Deciders:** maintainer (goal: "keep going until MVP is reached")
- **Amended by:** [ADR 0086](0086-a-managed-coreclr-debugger.md) (accepted 2026-10-28: a managed CoreCLR adapter of our own replaces netcoredbg in phases; phase 1 built in M54, 2026-10-29: the Codalus debugger behind `debug.adapter`, netcoredbg still the default)

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

**Client.** `DaedalusStudio.Debugging` holds:
- `DapConnection`: framing shared with `DaedalusStudio.Rpc`, source-generated JSON (AOT-safe), reverse requests refused.
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
- netcoredbg waits a fixed 5 seconds for a launched program's runtime to report; the IDE waits up to 60 seconds more for one that reports later (`DebugAdapter.ConfigurationDoneAsync`). netcoredbg starts the program or attaches on the later of the launch or attach and `configurationDone`, and fails that request: for a program started in the terminal or a console window, attached to once it reaches the startup hook, that's usually the `attach`, which gets the same wait (`DebugAdapter.AttachAsync`; PR #35's CI). netcoredbg also answers any request that runs past a fixed 15 seconds with "Command execution timed out." while the request goes on (its worker finishes it before the next); an evaluation of a name or of its members is asked once more then (`DebugAdapter.EvaluateAsync`), any other isn't, as it could do what it does twice. That answer to the request that carries the start (`configurationDone`, or a later `attach`) counts as the startup timeout too (`DebugAdapter.IsStartupTimeout`), so the start gets the same 60 seconds: on Windows ICorDebug's attach waits without a limit for the process's loader breakpoint, and netcoredbg goes on attaching after the 15 seconds (PR #97's CI, the packaged WinUI app). On another machine (an SSH, container or WSL transport) nothing can wait for the program, so there that answer fails the start as before: only the 5-second answer makes a start again (`DebugService.IsStartupTimeout`), as one made again after the command timeout would leave behind the program netcoredbg is still starting. The session shows running from `configurationDone` on, but netcoredbg fails `threads` with E_FAIL until it has handled the process's CreateProcess callback, which comes after the attach's thread and module events: Break All asks again every 250 ms while the program runs, for up to those 60 seconds (`DebugAdapter.PauseAsync`). It's the only request sent while the program runs that needs the process, so the start itself doesn't wait for `threads`. In a session of several programs, Break All leaves out a program still joining (`CompoundDebugAdapter.PauseAsync`), as listing the threads does: waiting for it would hold up the next programs' pauses and stop it once attached, after the session may have gone on. A program has joined once its `configurationDone` and its launch or attach are answered (a program in the terminal or a console window is attached to after `configurationDone`); the first program starts with the session, and Break All waits for it as above. On macOS netcoredbg takes such a runtime up only once a request arrives (its CreateProcess callback waits for the stdin lock its main thread holds while reading; PR #24's CI), so while waiting the IDE sends netcoredbg a request it answers with "not implemented" (`codalus/wake`) every 250 ms. TODO(issue): report upstream (Samsung/netcoredbg). On Linux two more ways a start waited out those 60 seconds are handled (PR #32's CI): the .NET 11 runtime's debugger transport can deadlock at its start (it creates its `clr-debug-pipe-<pid>-<key>-in`/`-out` FIFOs in `DbgTransportSession::Init`, and its transport thread unlinks and recreates them when it first runs; a debugger that connects in between opens the old `-in` while the runtime waits on the new one, both blocked in `open` for good). Once the program's `-in` FIFO has been there 15 seconds without netcoredbg taking the runtime up, a launched program is ended and started again (`DebugAdapter.StuckRuntimeWait`, up to `DebugService.StartupAttempts`). And netcoredbg doesn't reap a program that ends before its runtime reports: it stays a zombie, which `Process.HasExited` reads as running, so its state is read from `/proc`. TODO(issue): report upstream (dotnet/runtime); macOS has the same runtime race, not yet detected there.
