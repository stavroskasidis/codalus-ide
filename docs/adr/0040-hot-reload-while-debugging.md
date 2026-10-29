# 0040. Hot Reload while debugging (research)

- **Status:** accepted as research (M14c, 2026-10-11); implementation deferred
- **Date:** 2026-10-11
- **Deciders:** maintainer
- **Amended by:** [ADR 0086](0086-a-managed-coreclr-debugger.md) (accepted 2026-10-28; its phase 2, built 2026-10-29 as M55: under the Codalus debugger, edits apply to the program being debugged, a Blazor Web App's server included; netcoredbg keeps this ADR's limits)

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

## Update 2026-10-20: Blazor WebAssembly while debugging

The maintainer asked for Hot Reload while debugging a Blazor app (0.5). A Blazor WebAssembly app's components run in the browser's .NET runtime, debugged through the dev server's debugging proxy and vscode-js-debug (ADR 0026), not under netcoredbg, so the refusal above doesn't reach them.

- **What was tried:** the dev server of a WebAssembly debug session under `dotnet watch run` instead of `dotnet run`, an edit to a method the page runs every half second, then a breakpoint in it. `dotnet watch` reported "C# and Razor changes applied", the page's console "Applying managed code updates.", and the breakpoint stopped in the changed method with the changed value (`BlazorWasmTests.Hot_reload_applies_an_edit_to_the_app_while_it_is_debugged`).
- **Decision:** Start Debugging runs a Blazor WebAssembly app's dev server under `dotnet watch` when the setting `debug.hotReload` is on (the default) and the project supports Hot Reload; Apply Code Changes and the toolbar's Hot Reload button work during the session, and the status bar follows it as for Start with Hot Reload.
- **Unchanged:** programs netcoredbg launches or attaches to, a Blazor Web App's server among them, still can't take Hot Reload's updates; the path through the debugger above stays the way to them.

## Update 2026-10-22: a Blazor Web App's WebAssembly components while debugging (0.6)

The maintainer reported (0.6, #75) that on a Blazor Web App (a server and its `.Client` project, interactive WebAssembly) Start with Hot Reload applies edits but Start Debugging with `debug.hotReload` on doesn't: `F5` launched the server under netcoredbg (ADR 0027), with no `dotnet watch`, so nothing sent the page an update and Apply Code Changes was disabled.

**What dotnet watch does with a Blazor Web App** (read in dotnet/sdk at 10.0.401 and 11.0.100-rc.2): it has two Hot Reload clients, the browser (through its refresh server) and the server (a named pipe to the agent in it), and sends each update to both. The server's runtime refuses it while a debugger is attached ("The runtime failed to applying the change: Assembly updates cannot be applied while a debugger is attached." then "Further changes won't be applied to this process."); dotnet watch logs that, stops sending that process updates and doesn't restart it, and the browser applies its own. Under `--non-interactive`, rude edits and edits that only take effect at the next start restart the whole app. SDK 11 RC2 prints "changes applied" only when every client applied the update; 10.0.401 prints it anyway.

**Spike** (Linux, the maintainer's app upgraded to the Web App layout and `samples/BlazorHosted`, netcoredbg 3.2.0-1092, SDK 11 RC2 and 10.0.401):
- (a) With netcoredbg attached to the app dotnet watch started, an edit to a WebAssembly component's markup showed in the page 1.5 s after the save, in the same document; dotnet watch logged the server's refusal and no restart. Both SDKs.
- (b) Through the IDE (`BlazorWebAppHotReloadDebugTests`): a breakpoint in the edited method stops in the browser with the changed value.
- (c) netcoredbg attached after "Now listening on" stops at a breakpoint in a server controller, and in a component while the server prerenders.
- (d) A rude edit while the server was stopped at a breakpoint: without detaching, the old server outlived the restart (`dotnet run` forwards SIGTERM and waits for it; a process held by the debugger can't handle it), kept the port, and the new server failed with "address already in use", after which dotnet watch waited for a file change. Detaching on "Restart is needed to apply the changes." let the old server exit, and the new one bound the port and served (HTTP 200). Both SDKs.
- The agent says the app's process id ("Loaded into process 6568: …") from SDK 11 on when `HOTRELOAD_DELTA_CLIENT_LOG_MESSAGES` is set; SDK 10.0.401's line has no id ("Loaded into process: …"), so the app is found in dotnet watch's process tree (dotnet watch → `dotnet run` → the apphost).
- SDK 11 RC2's `dotnet watch` writes a sibling folder's file as "./.Client/Pages/Counter.razor" (it cuts its working directory off as text); the IDE puts the path back together.

**Decision.** Start Debugging of a Blazor Web App's server (a project that hosts a WebAssembly client project) takes a new path when `debug.hotReload` is on, the project supports Hot Reload, the solution's SDK is 10.0.400 or later (or 11), and no enabled breakpoint is in the server's `Program.cs` or `Startup.cs`:
- the server runs through Start with Hot Reload's plan (`dotnet watch run --non-interactive`), with the agent asked to say the app's process;
- once the app serves ("Now listening on", or its address accepting connections), netcoredbg attaches to it as the first adapter of the compound session: an *owned attach* (Stop ends the server and Restart is allowed, as for a launch; a failed start detaches and never ends the server); the page then joins under js-debug;
- `WatchedServerSession` (Workbench) follows the server: on "Restart is needed to apply the changes." the debugger detaches (the page's browser closes), the server exits on the signal dotnet watch forwards, and the session attaches to the next server and opens the page again. An end of the debug session that wasn't asked for (the restart line missed, the debugger lost) waits for a new server, or ends when the app exited or dotnet watch did. A new server while the last one still runs ends the orphan. Stop ends dotnet watch and any server the debugger held past it;
- the IDE doesn't end the server itself when the restart is announced: dotnet watch logs the line before it commits to the restart, and a server that exits first makes it wait for a file change instead of starting again;
- each file dotnet watch reports updated (edits outside the IDE included) is kept out of the server's adapter until the next restart: its breakpoints still bind in the page, or say "The server runs the code it started with until a restart";
- the server's refusal is said once per session, with Restart; a restart for an edit says debugging goes on in a new browser window; the server's output is mirrored into the Debug Console; the status bar says "Hot Reload: sent to the browser" (the browser's application isn't reported back);
- fallbacks: no browser debugs the server alone (launched, as before); a server that can't be found or attached to, or a platform listed in `HotReloadDebugChoice.UnreliablePlatforms` (empty), debugs the page alone with Hot Reload and says why; an older SDK, a startup breakpoint, or Hot Reload unsupported keep the launched session with a Debug Console line.

**Limits:** the server's startup code isn't debugged in this mode (breakpoints there choose the launched session); edits to code the server runs, including components it prerenders or runs interactively (InteractiveServer, InteractiveAuto before WebAssembly loads), wait for Restart; an edit made while stopped in the browser applies on Continue; each restart opens a new browser window; .NET 11 templates have no `inspectUri` (the IDE is expected to start the WebAssembly debugging proxy), so their components' breakpoints don't bind yet.

**Validated:** Linux (the spike, the shell tests, the maintainer's app through the shell on SDK 11 RC2 and 10.0.401). Windows and macOS: CI's shell tests (`BlazorWebAppHotReloadDebugTests`) and the smoke test's `blazor.compoundHotReload` step must prove them; a platform where the attach isn't reliable goes on the fallback list with a TODO(issue).

- `TODO(issue)`: dotnet/sdk: a supported way to learn the app's process id, and to add a startup hook beside the agent (so the server's startup code could be debugged in this mode).
- `TODO(issue)`: dotnet/sdk: `dotnet run` could time out its wait after forwarding SIGTERM, so a stopped app isn't orphaned on Linux and macOS.
- `TODO(issue)`: dotnet/sdk: `dotnet watch` reports a sibling folder's file as "./.Client/…".
- `TODO(issue)`: start the WebAssembly debugging proxy for .NET 11 Blazor apps that have no `inspectUri`.
- `TODO(issue)`: validate SDK 9 and 10.0.1xx-3xx and widen `HotReloadDebugChoice.IsValidatedSdk`.
- The way to the server's edits stays the debugger's (above): Samsung/netcoredbg's DAP apply-deltas request.
