# 0094. The Codalus debugger by default, and its gaps closed

- **Status:** accepted (2026-11-11, D49 in the [roadmap](../roadmap.md#decision-register)); built as [M58](../roadmap.md#m58--the-codalus-debugger-by-default) in two pull requests (M58a: the adapter's features, still opt-in; M58b: remote targets and the default)
- **Date:** 2026-11-11
- **Deciders:** maintainer (the grilling of 2026-11-11: every answer below is theirs)
- **Amends:** [ADR 0086](0086-a-managed-coreclr-debugger.md) (its phase 3: the default changes now, not after weeks of nightlies), [ADR 0007](0007-debugging.md) (netcoredbg as the .NET debugger), [ADR 0021](0021-debugger-depth.md) and [ADR 0032](0032-debug-companion-host.md) (their gaps lists), [ADR 0039](0039-debugger-sessions.md) (remote targets' debugger), [ADR 0040](0040-hot-reload-while-debugging.md) (active frames), [ADR 0076](0076-debugging-the-desktop-clr.md) (x86 and Windows PDBs)

## Context

ADR 0086 replaced netcoredbg with a debug adapter of our own in three phases. Phases 1 and 2 are built (M54, M55): `codalus-debughost --clr-dap` on dbgshim passes the debugger suites on Windows, Linux x64, Linux arm64 and macOS, and applies edits to the program being debugged. It is still a preview behind `debug.adapter`, and on 2026-11-11 this was left:

- **Programs it hands to netcoredbg:** x64 programs on Apple silicon, and every remote target (SSH, containers, WSL, remote windows).
- **netcoredbg's gaps, unchanged under it:** hit counts and logpoints done by the IDE with a stop per hit, no Set Next Statement, no Step Into Specific, no `[DebuggerTypeProxy]` or Results View, no `new`, lambdas or generic methods in expressions, no Make Object ID, no return values, no freezing of threads, no data breakpoints, no Memory, Disassembly or Registers windows, no mixed-mode debugging, no symbol servers.
- **Hot Reload's semantics while debugging:** a method already running finishes in its old version.
- **Around it:** with several startup projects a stop in one program doesn't pause the others; the .NET Framework adapter debugs only 64-bit processes with portable PDBs; remote sessions show no `[DebuggerDisplay]`, icons or grouping; Watch reads only C#.

The maintainer's request of 2026-11-11: make the Codalus debugger the default and close the gaps.

## Decision

| | Decision |
|---|---|
| **netcoredbg** | `debug.adapter` defaults to `codalus`. `netcoredbg` stays one release as an opt-in fallback, then a later release removes it with `eng/get-netcoredbg.sh`, its copy in `RemoteDebuggers` and its workarounds in `DebugAdapter` |
| **Native-level gaps** | Managed side only: Memory (`ICorDebugProcess::ReadMemory`, `WriteMemory`), Registers (`ICorDebugRegisterSet`) and Disassembly of JIT-compiled managed code, decoded by Iced (MIT) on x64 and by a decoder of our own on arm64. **Data breakpoints and mixed-mode debugging stay documented gaps** |
| **Remote targets** | A self-contained, trimmed adapter bundle per platform (`codalus-debughost` with the CoreCLR adapter only), copied with consent as netcoredbg is, and carried by `codalus-server`. Runs on targets that have only the program's runtime |
| **x64 programs on Apple silicon** | The same bundle for `osx-x64`, an on-demand component (the Components window), downloaded with consent the first time such a program is debugged |
| **Expressions** | Our interpreter first, extended with `new`, generic methods, object IDs, type proxies and Results View. What it can't read (lambdas, LINQ, the rest of C#) the C# language host compiles against the stopped frame into a small assembly, which the adapter loads into the program (`Assembly.Load` through func-eval) and calls |
| **Visual Basic** | VB expressions through the compiled path (VB is in the C# host's workspace). F# expressions stay a gap |
| **Several startup projects** | A stop in one program pauses the others, as the classic IDE does; a setting turns it off |
| **.NET Framework** | 32-bit processes through an x86 adapter process, and Windows PDBs through a managed, MIT-licensed reader |
| **Remote sessions' metadata** | The adapter on the target reads the modules itself: `[DebuggerDisplay]`, icons and grouping there too |
| **Edit and Continue** | Active frames are remapped into the new version (`ICorDebugILFrame2::RemapFunction` at the runtime's remap opportunity, from Roslyn's active statements); what Roslyn can't remap is a rude edit |
| **Symbols** | Options › Debugging › Symbols, as the classic IDE: the Microsoft and NuGet.org symbol servers listed and **off**, the user's own URLs and folders, a cache, include and exclude lists, Load Symbols in the Modules window. Nothing leaves the machine until a server is turned on |
| **Left as documented limits** | Dumps from another operating system, Windows remote targets, start-up breakpoints in packaged WinUI apps, and the Mono adapter's gaps (iPhone, CoreCLR on mobile, Hot Reload on devices) |
| **Landing** | Two pull requests into `develop`, each opened once when complete: M58a, the adapter's features, with the adapter still opt-in; M58b, remote targets, the component, the default, the user guide's limits rewritten |

ADR 0086 made the default wait for a failure rate over some weeks of nightlies no worse than netcoredbg's. The maintainer changes it now instead: the fallback setting is the safety net, and every failure of the `debug-codalus` shard recorded in `docs/ci-flakes.md` is worked as a bug of the default debugger.

## Consequences

- The gaps list of the user guide becomes the Codalus debugger's own, much shorter; netcoredbg's is kept only under the fallback setting until it is removed.
- The packages carry the adapter bundle for their own platform's remote targets (Linux x64 and arm64 in every package, as `codalus-server` is), which the size gate counts.
- Compiled expressions load assemblies into the debugged program that stay until it ends (collectible contexts can't be used through func-eval before .NET Core 3.0, and not on .NET Framework); the Modules window hides them.
- The interpreter stays the first reader, so data tips, conditions and attach without a loaded solution don't wait for a compile, and the remote bundle needs no Roslyn.
- Symbol servers are a new way workspace-derived data (module names, their IDs) leaves the machine: off by default, HTTPS only, listed in the user guide's privacy notes.
