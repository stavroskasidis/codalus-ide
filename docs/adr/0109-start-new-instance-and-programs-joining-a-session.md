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

## Consequences

- F5 of one project now runs through `CompoundDebugAdapter` for every program on this machine. Its features pass through to the one adapter (capabilities by thread, Set Next Statement, Hot Reload's adapters, Detach All, Restart), as they did for multiple startup projects. Break All on a stop applies once a second program joins.
- Joined programs share the session's breakpoints, exception settings and Break All rule (`debug.breakAllProcesses`).
- Two instances of one web app can collide on its port, as in the classic IDE; that's the app's to settle.
- The later steps change which sessions and projects can join (`ShellHost.SessionCannotJoinReason`, `ProjectCannotJoinReason`), listed under the roadmap's Deferred work.
