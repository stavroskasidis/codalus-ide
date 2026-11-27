# 0109. Start New Instance, and programs joining a running debug session

- **Status:** accepted (2026-11-25; the maintainer's answers of 2026-11-25)
- **Date:** 2026-11-25
- **Deciders:** maintainer
- **Builds on:** [ADR 0039](0039-debugger-sessions.md) (sessions of independent programs, the Processes window), [ADR 0043](0043-aspire-integration.md) (programs added to a running session), [ADR 0094](0094-the-codalus-debugger-by-default.md) (the Codalus debugger, a stop pausing the other programs)

## Context

A user's request (#547): the classic IDE's Solution Explorer has a **Debug** submenu on a project (Start New Instance, Start Without Debugging, Step Into New Instance), and Start New Instance adds the project to the running debug session. You can F5 an API, then start its client under the debugger without stopping it or setting up multiple startup projects first.

Codalus had neither. `debug.start` was disabled while a session ran, and `ShellHost.StartDebuggingAsync` returned when one was active. `DebugService.AddLaunch` could add a program to a running session, for an AppHost's projects and compose's services, but only to a session of independent programs (`CompoundDebugAdapter` in its independent mode). A one-project F5 was a session of a single adapter, which can't take another one.

The classic IDE's debugger holds processes of every kind in one session: launched, attached, remote, in containers, in the browser. Codalus's sessions are built by about 25 start paths, each owning the whole session.

## Decision

The maintainer chose full parity with the classic IDE, delivered in steps, each a complete pull request:
1. **This step:** the submenu, every one-project F5 session joinable, and programs on this machine (the Debug Console, the integrated terminal, a console window) and in WSL joining. The kinds not yet joinable say so.
2. Web apps: a Blazor WebAssembly app, and a web app debugged with its page (a server with its page: a session of its own today, to be nested in the session as a group).
3. An attach, a remote window, a container profile, an AppHost, mobile and packaged apps, and plugin and folder debug adapters.

### The submenu

- `explorerItemStartable` (the Solution Explorer's selection): an Exe or WinExe project, or a library whose launch settings have a `DebugRoslynComponent` profile (read off the UI thread, set once read if still selected). The submenu (`solutionExplorer/context/debug`) shows on those only, after Set as Startup Project. The classic IDE shows it on every project and refuses a library at the click; the maintainer chose to hide it.
- `debug.startNewInstance`, `debug.stepIntoNewInstance` and `run.startProjectWithoutDebugging`, in the classic IDE's order and names. Each takes an optional project path, defaulting to `ShellHost.CurrentProject` (the selection when Solution Explorer has the focus). `run.startWithoutDebugging` keeps defaulting to the startup project.

### Joining

- **Every one-project F5 is a session of independent programs of one** (`DebugService.LaunchAsync`'s `joinable`; also multiple startup projects with one project to debug, and one framework of a multi-targeted project). It looks like a one-program session until a second program joins: `CompoundDebugAdapter.NamesSoleProgram` is false, so its threads and its exit don't carry the program's name until then (`NamesPrograms`), and from then on they do. AppHosts, compose and multiple startup projects keep naming from the start.
- `DebugService.CanAddLaunch`: such a session with room (64 programs). Start New Instance with no session starts the project as F5 starts it (any kind) and remembers it as the session's first (`_sessionProject`); with one, it checks the session and then the project, builds the project, plans it with its launch profile and adds it (`AddLaunch`). A web app's browser opens once it says it's listening, as for multiple startup projects. A session that ended during the build makes the project the next session's first program.
- Not yet joinable, each said in a notification naming why and what to do (stop the session, or start without debugging): a session attached to a process, a web app's server with its page, a program running alone (in a browser, on a device, on another machine, through a plugin's adapter); a project in a remote window, a mobile app, an AppHost, a packaged WinUI app, a container profile, a Blazor WebAssembly app, a web app whose page is debugged too.
- **Stop** (Shift+F5 while debugging, `ShellHost.StopDebuggingAsync`) also cancels a Start New Instance still building.

### Step Into New Instance

`DebugLaunch.StopAtEntry` sets the launch's `stopAtEntry`. netcoredbg has its own. The Codalus debugger's (`NetFxDebugArguments.StopAtEntry`) binds a breakpoint of its own, which the client isn't told about, when the first module with a managed entry point loads (the framework's assemblies have none). It goes at the first visible line of the PDB's entry point (the user's `Main`, not the compiler's `<Main>`), or else the image's; for an async `Main` and top-level statements that await, at their state machine's `MoveNext`. Without the PDB's entry point (a Windows PDB), an image entry point named `<Name>` leads to the method `Name` of its type. A plain user breakpoint already bound on that line is the stop instead, so the program stops there once. The stop's reason is `entry`.

A program netcoredbg attaches to in the terminal or a console window isn't launched by netcoredbg, so `stopAtEntry` doesn't apply: the Debug Console says so (`TODO(issue)`: a breakpoint at the entry method's first line, read from its PDB).

### Restart and the Processes window

- **Restart** brings back every program: what the session began with (F5's startup projects, or the project Start New Instance began it with), then each project Start New Instance added, in order, built and planned anew, without stopping at entry.
- The Processes window's rows get **Terminate Process** and **Detach Process** (one attached to, or with a console of its own), through `DebugService.EndProcessAsync`. In a session of independent programs one program ends, or is detached from, alone (`CompoundDebugAdapter.EndPartAsync`'s `terminateDebuggee`); in another session the session ends (Stop, or Detach All). A program netcoredbg attached to in the terminal is ended by the IDE after its disconnect, as Stop does. A program terminated or detached from that way is left out of Restart; one that ended by itself comes back.

## Step 2: web apps (2026-11-25)

- **Start or join.** `DebugService.StartOrJoinAsync` starts one program of any kind (any adapter): in the running session of independent programs as one more, or in a session of its own that others can join (one program, named as before). A browser's page (`LaunchBrowserAsync`) and a web app's server with its page (`LaunchCompoundAsync`, `AttachOwnedCompoundAsync`) go through it, and return their index in `Processes`. A server debugged alone (`LaunchAsync(LaunchPlan)`) is joinable too. F5's web sessions can take other programs this way.
- **A web app is one program, nested.** Its server and its page stay the compound session ADR 0027 made them (the page joins once the server listens, the page ends with the server, `dotnet watch`'s release and exclusions), as one program of the session. Its ids are multiplied by both sessions, so a group holds `CompoundDebugAdapter.GroupSlots` (4) adapters, and its ids are 4 times its adapters'. A thread's id is its number in its adapter (the order the session first saw it in), times those, not the OS's id (2026-11-26): a macOS thread id is system-wide and rises with uptime (24 million on a Mac up for 13 days), and times 256 it wrapped an int, so a web app's stops never showed on macOS. The session's multiplier is per instance (`Slots`). `DebugService.Group` finds a group by its index; `AddBrowserAsync` takes it (F5's is the first).
- **Function breakpoints** have the session's ids, which each adapter's own map to, as a file's breakpoints have: an adapter binds one as the module with its method loads, and that event, under the adapter's id, was dropped in a session of programs, so a function breakpoint bound after the start stayed unverified (multiple startup projects, and one-project F5 since step 1).
- **Disposal.** The session disposes every program's adapter, those that ended before it too: a program ends as its adapter closes (Stop's disconnect), and js-debug's DAP server, which outlives its session, stayed running once a page was one program of a session.
- **Mixed sessions.** Each program knows whether it was attached to (`Part.Attached`): Stop ends the launched ones and detaches from the attached ones; `IsAttached` is true when every program was attached to.
- **Joining a web app** (`ShellHost.JoinWebAppAsync`): a Blazor WebAssembly app's page joins as one more program, its dev server running beside it (stopped with its page, or the session; its exit ends its page). A web app debugged with its page joins as one program, its page opening once its address answers (the Debug Console has every program's output, so its "Now listening on" isn't waited for). Step Into New Instance stops its server at its entry point.
- **Not yet:** Hot Reload for a web app that joined (the session's Hot Reload, `DebugEdits`, is its first program's; `TODO(issue)`). *Amended 2026-11-26:* that Hot Reload counts as running only the projects of the programs the session began with (`DebugLaunch.Project`, `DebugProcess.Project`) and the projects they reference; a joined program's projects are built after its baseline, and a rude edit Roslyn saw in one asked the first program to restart, so Continue waited on the question (macOS CI, run 45872232919). A rude edit in a project no running project references asks nothing: the Debug Console says it takes effect when the project is built again (`HotReloadUpdateResult.NotRunning`), and the update is committed. A session with no project known (an attach, an AppHost's) counts every project, as before; a session whose web app runs under `dotnet watch` (netcoredbg's Hot Reload while debugging, #75) takes no other program, since `dotnet watch`'s restarts end the session and start it again.

## Step 3: every other kind (2026-11-25)

- **Every start joins.** An attach (here, on a remote target or in a container, a .NET Framework process), a plugin's or a folder's debug adapter and the Mono debugger go through `StartOrJoinAsync`; a remote window's, a container's and an AppHost's launch through `LaunchOrJoinAsync` (it waits until the program has joined). So every session takes other programs but one whose web app runs under `dotnet watch` (netcoredbg's Hot Reload while debugging, #75: its restarts end the session and start it again).
- **Start New Instance on every kind** goes through F5's own start for it, which now joins the running session: a mobile app on its device, a packaged WinUI app (registered, started by Windows, attached to), a remote window's project, an AppHost (its projects join as F5's do; one AppHost runs at a time), a container profile (its container is removed when the session ends). A start that joins leaves the session's Debug Console (`ClearDebugLogForNewSession`) and its remote companion as they were.
- **Attach to Process while debugging**, as the classic IDE: Attach to Process, Reattach, Attach to Process on a Remote Machine, in a Container and Debug a Web Page are enabled while a session that takes other programs runs (`debugCanAddProgram`), and add to it; a process the session debugs already isn't attached to twice.
- **Thread ids.** A session of programs encodes its ids with the program's place (64 times the program's own, plus its index), and every session is one since step 1, so the Threads window showed 64 times a thread's id, and a breakpoint filter on `ThreadId` under netcoredbg compared another id than the one it names. `IDebugAdapter.DisplayThreadId` gives the program's own (the OS thread's), through a nested web app's group too; the Threads window shows it, and the IDE's filters compare it. The session's ids still route requests.
- **Step Into New Instance with netcoredbg in the terminal**: netcoredbg attaches to that program, so its `stopAtEntry` doesn't apply; a temporary breakpoint at the entry point's first line (`EntryPoints.FirstLine`, from its portable PDB, an async `Main`'s state machine too) stops it there once, unless a breakpoint is there already.

## Consequences

- F5 of one project now runs through `CompoundDebugAdapter` for every program on this machine. Its features pass through to the one adapter (capabilities by thread, Set Next Statement, Hot Reload's adapters, Detach All, Restart), as they did for multiple startup projects. Break All on a stop applies once a second program joins.
- Joined programs share the session's breakpoints, exception settings and Break All rule (`debug.breakAllProcesses`).
- Two instances of one web app can collide on its port, as in the classic IDE; that's the app's to settle.
- The later steps change which sessions and projects can join (`ShellHost.SessionCannotJoinReason`, `ProjectCannotJoinReason`), listed under the roadmap's Deferred work.
