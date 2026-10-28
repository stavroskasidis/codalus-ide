# 0086. A managed CoreCLR debugger adapter, and Hot Reload while debugging

- **Status:** proposed (2026-10-28), after two spikes; nothing is built. The maintainer decides it as D48 in the [roadmap](../roadmap.md#decision-register).
- **Date:** 2026-10-28
- **Deciders:** maintainer
- **Would amend:** [ADR 0007](0007-debugging.md) (netcoredbg as the .NET debugger), [ADR 0040](0040-hot-reload-while-debugging.md) (Hot Reload while debugging), [ADR 0076](0076-debugging-the-desktop-clr.md) (ClrDebug only on Windows)

## Context

Every .NET Core and .NET program is debugged by netcoredbg (Samsung, MIT; ADR 0007). Most of what it can't do, and most of what had to be built around it, comes from netcoredbg itself, not from the runtime:

- **Gaps** (ADR 0007, 0021, 0032): no hit counts or logpoints (emulated in the client, a stop per hit), no exception settings per type, no Set Next Statement, no `[DebuggerTypeProxy]` (`new` can't be evaluated), no `new`, casts or lambdas in expressions, every exit code 0 on macOS, and no macOS x64 build since 3.2.0: x64 programs on Apple silicon (.NET 5 and earlier, under Rosetta) get its last one, 3.1.3, frozen (ADR 0030).
- **Workarounds** (ADR 0007): its fixed 5-second wait for a launched runtime and 15-second "Command execution timed out." answers (`DebugAdapter.ConfigurationDoneAsync`, `IsStartupTimeout`, `EvaluateAsync`'s second ask), Break All retried while `threads` fails, `codalus/wake` every 250 ms on macOS (its CreateProcess callback waits on a stdin lock), a zombie read from `/proc`.
- **Upstream fixes wait on its releases:** the Debug Console loses a program's last lines now and then until the pinned version moves to the release with [Samsung/netcoredbg#258](https://github.com/Samsung/netcoredbg/pull/258) (`docs/ci-flakes.md`).
- **Hot Reload while debugging** (ADR 0040): the runtime refuses `MetadataUpdater.ApplyUpdate` while a debugger is attached; the debugger has to apply the change through `ICorDebugModule2::ApplyChanges`. netcoredbg does that only on its MI interpreter, not on the DAP one Codalus uses, so programs under netcoredbg can't take Hot Reload, and a Blazor Web App's server is detached from and attached to again around every restart (`WatchedServerSession`, ADR 0040's 2026-10-22 update).

Since M43b Codalus has its own managed DAP adapter on ICorDebug, for .NET Framework (`codalus-debughost --netfx-dap`, ADR 0076): ClrDebug (MIT) over source-generated COM interop, symbols through System.Reflection.Metadata, our own C# expression evaluator (literals, members, `?.`, indexers, calls through func-eval, operators with C#'s promotion, `?:`, `??`, casts), stepping with Just My Code, exceptions with M14a's settings. Only its start and attach path (`mscoree`'s `ICLRMetaHost`, `CorDebugTarget.cs`) is specific to the desktop CLR. CoreCLR exposes the same ICorDebug through `dbgshim`, which Microsoft ships as `Microsoft.Diagnostics.DbgShim.<rid>` packages (MIT) for Windows, Linux and macOS on x64 and Arm64; ICorDebug itself (`mscordbi`) comes with every runtime.

## The spikes (2026-10-28)

Two throwaway programs, not in the repository, on ClrDebug 0.4.2 and dbgshim 10.0.750501:

**1. Debugging.** A console debuggee (a loop, a local object with auto-properties and a computed `Label` property) on .NET 9, 10 and 11, with three ways to start a session:
- *launch*: dbgshim's `CreateProcessForLaunch` (suspended), `RegisterForRuntimeStartup`, resume; the startup callback hands over the debuggee's own runtime's `ICorDebug` (wrapped with `Extensions.GetObjectForIUnknown<ICorDebug>`), then `DebugActiveProcess`;
- *attach*: to a program already running, through `EnumerateCLRs`, `CreateVersionStringFromModule` and `CreateDebuggingInterfaceFromVersion`;
- *held launch*: the program started by us with `DOTNET_DefaultDiagnosticPortSuspend=1`, `RegisterForRuntimeStartup`, then the diagnostics IPC's `Process/ResumeRuntime` command.

Each session bound a line breakpoint when the module loaded (the PDB's sequence point by System.Reflection.Metadata, `ICorDebugCode::CreateBreakpoint`), read the locals by the PDB's names and the object's fields, called the `Label` getter through `ICorDebugEval` (`"#42 for Ada"`), stepped over twice with a stepper over the line's IL range, ran to the end and read the exit code. The callback continues every event itself (`e.Continue = false` and `Controller.Continue`, as `CorDebugTarget` does).

| | Linux x64 | macOS arm64 | macOS x64 (Rosetta) |
|---|---|---|---|
| .NET 9 | 5/5 in each mode | 5/5 in each mode | not run (no .NET 9 x64 install) |
| .NET 10 | 5/5 in each mode | 5/5 in each mode | 5/5 in each mode |
| .NET 11 | 5/5 in each mode | not run (no .NET 11 SDK there) | not run |
| Exit code: launch / attach / held launch | lost / correct / correct | lost / correct / correct | lost / correct / correct |
| A breakpoint on `Main`'s first line, held launch | stops | stops | stops |

On Linux: 110 sessions in the first round, including 25 held launches each on .NET 10 and 11 (.NET 11's runtime deadlocks at its start under netcoredbg now and then, ADR 0007; never here), then the scripted rounds. One attach run printed nothing at all, not even the spike's first line, written before anything is debugged; it didn't come back in 90 more. The breakpoint is hit about 90 ms after the start; nothing like netcoredbg's 5-second wait, 15-second answers or macOS wake requests was needed.

What the spike found:
- **The launch path loses the exit code** on every OS: after `DebugActiveProcess`, mscordbi's own runtime layer reaps its child, and `waitpid` finds nothing (`ECHILD`; dbgshim alone doesn't reap). netcoredbg survives that by interposing `waitpid` (`src/debugger/waitpid.cpp`), which macOS's two-level namespace doesn't allow: hence its exit code 0 there.
- **The held launch keeps it**, as the program is our own child, and catches the first line of `Main`. Its costs: the runtime prints a fixed "configured to pause" notice on the program's stdout, and the variable is inherited by the program's own .NET children, which would wait to be resumed. Attaching *while* it is held times out (`CORDBG_E_TIMEOUT`): the hold comes before the runtime's debugger side starts, so the order is register, resume, attach.

**2. Hot Reload through the debugger.** A program compiled by Roslyn (Debug, portable PDB with its Edit and Continue information), debugged, stopped at a breakpoint before it calls `Message()`. Roslyn's `EmitDifference` against `EmitBaseline.CreateInitialBaseline(compilation, the module, the PDB's EnC maps, the methods' local signatures)` made the delta for a new body with a new local and one more line; `ICorDebugModule2::ApplyChanges` applied it; a breakpoint on a line only the new version has, placed from the PDB delta's sequence points (its EncMap gives the method), stopped with the new local's value; a second edit on the first one's baseline applied too, and every later call ran the latest code.

| How the program runs | `ApplyChanges` |
|---|---|
| debugged from before its module loads, the debugger setting `CORDEBUG_JIT_ENABLE_ENC` there | applied (with or without `DOTNET_MODIFIABLE_ASSEMBLIES`) |
| started with `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, debugged from the start without the flag | applied |
| **started with `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, attached to later** (setting the flag then fails with `CORDBG_E_CANNOT_BE_ON_ATTACH`) | **applied** |
| neither | `CORDBG_E_ENC_MODULE_NOT_ENC_ENABLED`; the old code goes on |

Identical on Linux x64, macOS arm64 and macOS x64. ADR 0040's "can't be enabled for an attached process" is netcoredbg's rule, not the runtime's. `dotnet watch` (SDK 10.0.401 and 11 RC3) starts the app with `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, confirmed by an app printing it, so the Blazor Web App's server, which the IDE attaches to after `dotnet watch` starts it, could take edits through the debugger.

**Not tried:** Windows and Linux arm64; exceptions, threads, async methods, Just My Code on CoreCLR, remote targets, the integrated terminal and compound sessions; deltas from the SDK's build and the language host's Hot Reload service (with rude edits), edits to methods on the stack, added members and types.

## Options

| | (a) Stay on netcoredbg | (b) Contribute upstream | (c) Our adapter for CoreCLR too |
|---|---|---|---|
| Hot Reload while debugging | not possible | after a DAP apply-deltas request is accepted and released (`TODO(issue)` in ADR 0040) | ours to build: the runtime side works (spike 2) |
| Gaps and workarounds | stay | each one a PR and a release away | the gaps become ours to close; netcoredbg's workarounds go with it |
| macOS | exit code 0; x64 programs on netcoredbg 3.1.3, frozen | needs a fix they can't make by interposing, and an x64 build again | the held launch's exit code; x64 programs through dbgshim's osx-x64 package, with the adapter run as x64 (the spike's Rosetta run) |
| Expressions | no `new`, casts or lambdas | as today | ADR 0076's evaluator (casts, operators, `?.`, `??`); `new` and lambdas are gaps in both |
| Code to keep | netcoredbg's DAP quirks (`DebugAdapter`) | the same, plus PRs | one adapter for both CLRs, about 6,000 lines today; ICorDebug's semantics are ours |
| Shipping | a downloaded, pinned native binary per platform (`eng/get-netcoredbg.sh`, `RemoteDebuggers`) | the same | ClrDebug back into the Linux and macOS packages (8 MB, left out since 0.3.0) and dbgshim from NuGet per platform |
| Risk | none new | slow; not ours to schedule | parity with what netcoredbg does today, on three OSes, remote targets included |

## Decision (proposed)

**(c), in phases, with netcoredbg the default until the new adapter matches it.**

1. **The adapter.** `codalus-debughost --clr-dap`: ADR 0076's session (`NetFxDebugSession`, the evaluator, `ClrModuleInfo`) over a second target beside `CorDebugTarget`, started through dbgshim. ClrDebug ships again on every OS; dbgshim comes from `Microsoft.Diagnostics.DbgShim.<rid>` in the debug companion's output for its runtime identifier.
   - **Launch:** the program must stay our child for its exit code, and its children must not inherit a hold. To be decided by a spike: a small launcher process that starts the program through dbgshim (suspended), reports its id, resumes it once the adapter has registered, and reports the exit code (no variable, no notice), against the held launch with the variable cleared for the program's children by the startup hook ADR 0039 already ships.
   - **Opt-in:** a setting `debug.adapter` (`netcoredbg` by default, `codalus`). x64 programs on Apple silicon keep netcoredbg 3.1.3 until the companion can run as x64 there. Session routing stays where ADR 0076 put it (`DebugService` picks the adapter from the program).
   - **Parity tests:** the existing debugger suites (`DebugService`, data tips, compound sessions, the terminal, test debugging, Blazor) run against both adapters on every CI OS. Remote targets keep netcoredbg in this phase.
2. **Apply Code Changes while debugging.** A DAP request of ours (`codalus/applyChanges`); deltas and rude edits from the language host's Hot Reload service (Roslyn Features, already referenced); every module of a launched program gets `CORDEBUG_JIT_ENABLE_ENC` as it loads; programs the IDE starts to attach to later (the terminal, a console window, `dotnet watch`'s server) get `DOTNET_MODIFIABLE_ASSEMBLIES=debug`. Breakpoints in updated methods move with the PDB delta. For a Blazor Web App's server, edits the runtime can apply are applied in place instead of waiting for Restart: open question for this phase, since `dotnet watch` still sends the server its own update, which the runtime refuses.
3. **The default, then netcoredbg's removal.** When the parity suites pass on Windows, Linux and macOS, the new adapter's CI failure rate over some weeks of nightlies is no worse than netcoredbg's, remote targets run it (`codalus-server` carries the adapter and dbgshim for its platform), and the user guide's gaps list is rewritten: the default changes, and a later release removes netcoredbg, `eng/get-netcoredbg.sh`, its copy in `RemoteDebuggers` and its workarounds in `DebugAdapter`.

## Consequences

- If accepted: Hot Reload while debugging becomes possible for launched programs, the terminal, and the Blazor Web App's server; macOS exit codes become correct; x64 programs on Apple silicon can leave netcoredbg 3.1.3 for a maintained dbgshim; the gaps netcoredbg can't close (per-type exception settings, Set Next Statement through `ICorDebugILFrame::SetIP`, type proxies through func-eval of `new`, hit counts and logpoints without a stop per hit) become work we can schedule.
- Every fix to the debugger becomes ours, on three OSes. ICorDebug's behavior off Windows is known only from these spikes until phase 1's suites run on CI.
- The Linux and macOS packages grow by ClrDebug (about 8 MB) and dbgshim (0.3 to 0.4 MB per platform) during phases 1 and 2, while netcoredbg still ships; the size gate counts both.
- `TODO(issue)`: run both spikes on Windows and Linux arm64 (through Remote Control sessions on those machines, as on the Mac) before phase 1 starts.
- `TODO(issue)`: the launch spike above (launcher process against the held launch).
- `TODO(issue)`: the one silent attach failure on Linux, if it comes back under the phase 1 suites.
- Worth revisiting if netcoredbg gains a DAP request for Hot Reload before phase 1 is built, which would leave (a) or (b) the cheaper way to most of this.
