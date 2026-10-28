# Spikes for ADR 0086: CoreCLR debugging through dbgshim and ClrDebug

Throwaway console programs behind [ADR 0086](../../docs/adr/0086-a-managed-coreclr-debugger.md). They are not part of the solution, they don't inherit the repository's build settings (`Directory.Build.props`, `Directory.Packages.props` and `global.json` here), and they aren't tested or shipped. Packages: ClrDebug 0.4.2, `Microsoft.Diagnostics.DbgShim.{linux-x64,osx-arm64,osx-x64}` 10.0.750501, Microsoft.CodeAnalysis.CSharp 5.9.0 (all MIT, nuget.org).

`bash run.sh [rid]` builds the three projects and prints a summary (the RID defaults to the machine's; macOS has no `timeout`, so the script falls back to perl).

## Spike: debugging

`dotnet Spike/bin/Debug/net10.0/Spike.dll <libdbgshim> Debuggee/bin/Debug/<tfm>/Debuggee <line> [--attach|--hold]`

- *(no flag)* launch: dbgshim's `CreateProcessForLaunch` (suspended), `RegisterForRuntimeStartup`, resume. Works, but the exit code is lost: mscordbi reaps the child.
- `--attach`: `Process.Start`, then `EnumerateCLRs`, `CreateVersionStringFromModule`, `CreateDebuggingInterfaceFromVersion`, `DebugActiveProcess`.
- `--hold`: `Process.Start` with `DOTNET_DefaultDiagnosticPortSuspend=1`, `RegisterForRuntimeStartup`, then the diagnostics IPC's `Process/ResumeRuntime`. Catches `Main`'s first line and keeps the exit code.

Each session binds a breakpoint (line 6 is the loop body, line 1 `Main`'s first statement), reads locals and fields, calls the `Label` getter through func-eval, steps over twice and runs to the end. `SPIKE_HIT_ONLY=1` stops only at the breakpoint; `SPIKE_TRACE=1` logs every callback. The debuggee targets net9.0, net10.0 and net11.0; drop the ones the installed SDK can't build.

## HotReload: edits through ICorDebugModule2::ApplyChanges

`dotnet HotReload/bin/Debug/net10.0/HotReload.dll <libdbgshim> [--late] [--not-modifiable] [--no-enc-flag]`

Compiles a small app with Roslyn, debugs it, applies two Roslyn deltas (`EmitDifference`) and binds a breakpoint on a line only the edited version has (from the PDB delta). `--late` attaches after `Main` started (as to `dotnet watch`'s server), `--not-modifiable` leaves `DOTNET_MODIFIABLE_ASSEMBLIES` unset, `--no-enc-flag` doesn't set `CORDEBUG_JIT_ENABLE_ENC` as the module loads.

## Results (2026-10-28)

| | Linux x64 | macOS arm64 | macOS x64 (Rosetta) | Windows | Linux arm64 |
|---|---|---|---|---|---|
| Spike, .NET 9 | pass | pass | not run | not run | not run |
| Spike, .NET 10 | pass | pass | pass | not run | not run |
| Spike, .NET 11 | pass | not run | not run | not run | not run |
| HotReload | as expected | as expected | as expected | not run | not run |

"Pass" is 5/5 sessions in each mode, exit code 7 with `--attach` and `--hold` and lost (-1) with launch. "As expected" is `ApplyChanges` S_OK and "every call ran the latest edit" for the first three variants, `CORDBG_E_ENC_MODULE_NOT_ENC_ENABLED` for the last two. On Windows, `run.sh` needs Git Bash, and `Spike`'s `waitpid` and the diagnostic socket lookup are Unix-only (Windows uses the pipe `\\.\pipe\dotnet-diagnostic-<pid>`).
