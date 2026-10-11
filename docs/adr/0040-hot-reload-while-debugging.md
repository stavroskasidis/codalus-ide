# 0040. Hot Reload while debugging (research)

- **Status:** accepted as research (M14c, 2026-10-11); implementation deferred
- **Date:** 2026-10-11
- **Deciders:** maintainer

## Context

M15 (ADR 0022) applies Hot Reload through `dotnet watch`. Its agent, a startup hook in the program, applies each metadata delta with `System.Reflection.Metadata.MetadataUpdater.ApplyUpdate`. The program runs without the debugger.

The roadmap asks M14 to find out whether those runtime metadata updates can be applied while netcoredbg is attached, and to record the answer. Edit and Continue parity with other IDEs is not promised.

## What was tried

**1. dotnet watch with netcoredbg attached.** This is what users would try first. `HotReloadResearchTests` is an opt-in test (`DAEDALUS_RESEARCH=1`), run on Windows with .NET SDK 10.0.401 and netcoredbg 3.2.0-1092:
1. A console program prints a message every 500 ms under `dotnet watch run`.
2. `DebugService` attaches netcoredbg to the program.
3. The test saves an edit to the message.

dotnet watch reported:

> The runtime failed to applying the change: Assembly updates cannot be applied while a debugger is attached. Further changes won't be applied to this process.

The program kept printing the old message. The runtime itself refuses `ApplyUpdate` while a managed debugger is attached: the debugger is expected to apply the change. The refusal doesn't depend on netcoredbg.

**2. Updates through the debugger.** The runtime's other way to take a delta is the debugging API's Edit and Continue: `ICorDebugModule2::ApplyChanges`, with the metadata and IL deltas. netcoredbg 3.2.0 has it, found in the binary and probed over its interpreters:
- a `HotReloadApplyDeltas(dll, deltaMD, deltaIL, deltaPDB, lineUpdates)` path that calls `ApplyChanges`, loads the PDB delta, updates line information, and moves breakpoints in updated methods;
- on its **MI** interpreter (`--interpreter=mi`): `-gdb-set enable-hot-reload 1` (accepted), and the `-apply-deltas` command, which exists and fails without a session;
- a message that Hot Reload **can't be enabled for an attached process**: it needs a launch, so that the runtime starts with `DOTNET_MODIFIABLE_ASSEMBLIES=debug`;
- nothing on its **DAP** interpreter (`--interpreter=vscode`), which Daedalus uses: no request applies deltas.

The deltas themselves come from Roslyn: `Compilation.EmitDifference` against a baseline, which is what `dotnet watch` and other IDEs use through Roslyn's Hot Reload service (in `Microsoft.CodeAnalysis.Features`, which the language host already references).

## Decision

- **Not possible today**: Hot Reload (`dotnet watch`, M15) and netcoredbg can't be used on the same program. With netcoredbg attached, `MetadataUpdater.ApplyUpdate` is refused by the runtime.
- **Possible later, through the debugger, for launched programs only.** The parts:
  1. The language host computes deltas with Roslyn's Hot Reload service, from the open documents against the build that was launched.
  2. netcoredbg applies them with `ApplyChanges`. That needs a DAP request upstream (a custom request on the vscode interpreter mirroring MI's `-apply-deltas`), or Daedalus driving netcoredbg's MI interpreter, which would mean a second client.
  3. The launch sets `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, and the session reports rude edits (changes the runtime can't apply) as other IDEs do.
- Until then, Start with Hot Reload (M15) and Start Debugging stay separate. The debugging guide says so.
- `TODO(issue)`: propose the DAP request to Samsung/netcoredbg (MIT), then build Apply Code Changes while debugging on it.

## Consequences

- Users choose between Hot Reload and the debugger for a run, as today. An edit made while debugging is not applied, and the editor doesn't pretend it is.
- The future implementation is bounded: Roslyn's delta service is the same one `dotnet watch` uses. netcoredbg already contains the runtime side. The missing piece is a protocol request.
- Programs started in the integrated terminal (ADR 0039) are attached to, so they are covered by netcoredbg's "attached process" limit. Hot Reload while debugging would apply to programs launched with the Debug Console.
