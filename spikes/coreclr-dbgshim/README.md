# Spikes for ADR 0086: CoreCLR debugging through dbgshim and ClrDebug

Throwaway console programs behind [ADR 0086](../../docs/adr/0086-a-managed-coreclr-debugger.md). They are not part of the solution, they don't inherit the repository's build settings (`Directory.Build.props`, `Directory.Packages.props` and `global.json` here), and they aren't tested or shipped. Packages: ClrDebug 0.4.2, `Microsoft.Diagnostics.DbgShim.{linux-x64,linux-arm64,osx-arm64,osx-x64,win-x64,win-arm64}` 10.0.750501, Microsoft.CodeAnalysis.CSharp 5.9.0 (all MIT, nuget.org).

`bash run.sh [rid] [runs] [design runs] [sections]` builds the projects and prints a summary (the RID defaults to the machine's; macOS has no `timeout`, so the script falls back to perl; on Windows, run it in Git Bash). `DEBUGGEE_TFMS="net9.0;net10.0"` builds the debuggee for the frameworks the installed SDK can build. Sections: `spike` (5 runs per mode), `design` (the launch candidates, 25 runs each), `attach` (25 runs each way), `hotreload`.

## Spike: debugging

`dotnet Spike/bin/Debug/net10.0/Spike.dll <dbgshim> Debuggee/bin/Debug/<tfm>/Debuggee <line> [--attach|--hold|--launcher|--hook]`

- *(no flag)* launch: dbgshim's `CreateProcessForLaunch` (suspended), `RegisterForRuntimeStartup`, resume. Works, but the exit code is lost: mscordbi reaps the child.
- `--attach`: `Process.Start`, then `EnumerateCLRs`, `CreateVersionStringFromModule`, `CreateDebuggingInterfaceFromVersion`, `DebugActiveProcess`.
- `--hold`: `Process.Start` with `DOTNET_DefaultDiagnosticPortSuspend=1`, `RegisterForRuntimeStartup`, then the diagnostics IPC's `Process/ResumeRuntime`. Catches `Main`'s first line and keeps the exit code.
- `--launcher` (launch candidate (a)): a launcher process (this program with `--launcher-process`) starts the program through `CreateProcessForLaunch`, suspended, reports its pid over a named pipe, resumes it when told and reports its exit code.
- `--hook` (launch candidate (b)): `--hold` plus `DOTNET_STARTUP_HOOKS` naming `Hook/`, which clears `DOTNET_DefaultDiagnosticPortSuspend` and itself before `Main` (`SPIKE_HOOK_MANAGED_ONLY=1`: through `Environment.SetEnvironmentVariable` only, without libc's `unsetenv`).

The diagnostic port is a socket in `$TMPDIR` off Windows and the pipe `\\.\pipe\dotnet-diagnostic-<pid>` on Windows (`Shared/DiagnosticIpc.cs`); a launched program's exit code comes from `waitpid` off Windows and its process handle on Windows (`Shared/ChildExit.cs`).

Each session binds a breakpoint (line 6 is the loop body, line 1 `Main`'s first statement), reads locals and fields, calls the `Label` getter through func-eval, steps over twice and runs to the end. `SPIKE_HIT_ONLY=1` stops only at the breakpoint; `SPIKE_TRACE=1` logs every callback; `SPIKE_CHILD=1` makes the debuggee start itself again as a child, through `Process.Start` and natively, and report whether that child was held (exit 3: it ran and didn't see the variable); `SPIKE_RESUME_DELAY_MS` delays a held launch's resume; `SPIKE_ATTACH_NO_WAIT=1` lets an attached program go on before the breakpoint is bound. The debuggee targets net9.0, net10.0 and net11.0.

## HotReload: edits through ICorDebugModule2::ApplyChanges

`dotnet HotReload/bin/Debug/net10.0/HotReload.dll <libdbgshim> [--late] [--not-modifiable] [--no-enc-flag]`

Compiles a small app with Roslyn, debugs it, applies two Roslyn deltas (`EmitDifference`) and binds a breakpoint on a line only the edited version has (from the PDB delta). `--late` attaches after `Main` started (as to `dotnet watch`'s server), `--not-modifiable` leaves `DOTNET_MODIFIABLE_ASSEMBLIES` unset, `--no-enc-flag` doesn't set `CORDEBUG_JIT_ENABLE_ENC` as the module loads.

## Results (2026-10-28)

| | Linux x64 | macOS arm64 | macOS x64 (Rosetta) | Windows x64 | Linux arm64 |
|---|---|---|---|---|---|
| Spike, .NET 9 | pass | pass | not run | pass | not run |
| Spike, .NET 10 | pass | pass | pass | pass | not run |
| Spike, .NET 11 | pass | not run | not run | pass (RC3) | not run |
| Launch candidates (a) and (b) | 25/25 each on .NET 9 and 10 | MAC-ARM | MAC-X64 | 25/25 each on .NET 9, 10 and 11 | not run |
| HotReload | as expected | as expected | as expected | as expected | not run |

"Pass" is 5/5 sessions in each mode, exit code 7 with `--attach` and `--hold`, and with launch lost (-1) off Windows and 7 on Windows. A launch candidate's run passes with a full session, exit code 7, both children `exit 3`, no "configured to pause" line, and a stop on `Main`'s first line in a second session. "As expected" is `ApplyChanges` S_OK and "every call ran the latest edit" for the first three variants, `CORDBG_E_ENC_MODULE_NOT_ENC_ENABLED` for the last two. Linux x64 is WSL's Ubuntu 24.04 (.NET 9.0.14, 10.0.13) in this round. Details and the conclusions in ADR 0086.
