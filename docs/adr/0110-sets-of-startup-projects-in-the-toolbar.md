# 0110. Sets of startup projects in the toolbar, and the classic IDE's launch files

- **Status:** accepted (2026-11-25; the maintainer's answers of 2026-11-25)
- **Date:** 2026-11-25
- **Deciders:** maintainer
- **Builds on:** [ADR 0039](0039-debugger-sessions.md) (multiple startup projects, M14c), [ADR 0070](0070-per-solution-state.md) (per-solution state), M29 (the standard toolbar), M65 (the Start button's launch profile drop-down)

## Context

A user's request (#543): after setting up multiple startup projects on the Startup Projects page and closing it, the toolbar's startup project box has nothing to choose them by, so switching between one project and several means opening the page each time. The classic IDE lists its multi-project launch profiles in that box and keeps them in the solution's launch file. M14c had left this gap with a `TODO(issue)` ("the toolbar's startup project box shows one project", [m14c](../checkpoints/m14c.md#deviations-and-gaps)).

The code showed more than the request:

- With multiple startup projects on, the box and the Start button still showed one project (`RunService.StartupProject`, which ignores the setting) while F5 started several.
- Picking a project in the box turned multiple off (`SetStartupProject`), and only the page could turn it back on. The Start drop-down was empty meanwhile.
- A row's launch profile on the page was the project's one choice (`RunService` kept one profile per project), so a project couldn't start with one profile alone and another with its partners.
- ADR 0070 noted that the classic IDE keeps no shared startup configuration. Since 17.11 it does: named multi-project launch profiles in a JSON file next to the solution, shared through source control or kept to the user.

What the classic IDE's files are, from its documentation and public sources: a JSON array of `{ "Name", "Projects": [{ "Path", "Action", "DebugTarget" }] }`, paths relative to the solution with backslashes, actions `Start` and `StartWithoutDebugging`, the launch profile in `DebugTarget`. Shared profiles are in `<solution>.slnLaunch`, the user's own in `<solution>.slnLaunch.user`. For a `.slnx` the sources disagree: dotnet/sdk#48014 says new profiles go in `<solution>.slnxLaunch`, and a Microsoft answer says an existing `.slnLaunch` keeps working after a migration.

## Decision

### Sets in the toolbar

- **The startup project box lists the executable projects, then the sets**: `<Multiple Startup Projects>`, the unnamed set (the page's own, M14c), once it has a project to start or while it's the active one; then the named sets of the solution's launch files. Choosing one makes Start start it; choosing a project goes back to one startup project. The choices are pseudo-paths (`startupSet:<name>`), as folder mode's launch configurations are (`launch:`).
- **The Start button says the set's name**, and its drop-down holds Start and Configure Startup Projects…, since each project's profile is the set's.
- **Select Startup Project** (`run.selectStartupProject`, the palette) lists the sets after the projects, and the status bar names the set while it runs.

### The unnamed set stays

The maintainer's choice, over migrating it into a named set and dropping it. It keeps its place in `.codalus/state/session.json` (ADR 0070), so a configuration from before carries over unchanged and someone who never names a set sees one entry, as before.

### Each entry has its own launch profile

The maintainer's choice, over a profile that follows the project's choice when the entry has none, and over the one choice per project.

- `StartupEntry` has a `Profile` (the file's `DebugTarget`). A set's projects start with their entries' profiles (`RunService.ResolveProfile(StartupEntry, …)`, used by `StartStartupProjectsAsync` and `RunService.StartAsync`); a single startup project keeps the project's chosen profile.
- An entry without one starts the project's first runnable profile, and the page keeps it without one unless the user picks another, so a launch file's entry without a `DebugTarget` is written back without one.
- A session from before has no profile per entry: on load, each entry takes the project's chosen profile once, so the projects start as they did.

### The launch files

- **Shared sets** go in the solution's launch file, unshared ones in its `.user` file (the maintainer's choice, over `.codalus/state/` and over reading `.user` and writing Codalus's own state), so the classic IDE and Codalus see the same sets. The page's **Share profile** checkbox moves a set between the two.
- **Names** (the maintainer's choice, over confirming on Windows first and over `.slnLaunch` everywhere): for `Contoso.slnx`, `Contoso.slnxLaunch` is read first, then `Contoso.slnLaunch`; for `Contoso.sln`, `Contoso.slnLaunch`; each with its `.user` file after the shared ones. A set is rewritten in the file it came from. A new one goes in the first existing file of its kind, else a new `.slnxLaunch` for a `.slnx` and `.slnLaunch` for a `.sln`. A folder or a project opened alone has no launch files, and the page hides the set picker there.
- **Reading** (`SolutionLaunchFiles.Read`): either slash, paths relative to the solution's folder; an action Codalus doesn't know is None; a name already read hides a later set of that name (within a pair, `.slnxLaunch` wins; across, the shared file wins), and that is logged with a file that isn't a list of profiles. Entries whose project isn't in the solution are skipped when starting.
- **Writing** (`SolutionLaunchFiles.Save`, `Delete`): only the set changed is replaced, in place; the other sets, and every property Codalus doesn't know (in the set and in each project's object), stay as they are. A project keeps its path as the file wrote it; a new one is written relative, with backslashes. None projects are left out, unless their object had an action Codalus doesn't know, which is kept. The file keeps its line endings; it's written through a temporary file and a rename. A file left with no set is deleted.
- **On disk** changes (a checkout, the classic IDE saving) are read again when the build host reports a change in the solution's folder (`SolutionService.FilesChanged`), so no watcher of its own. The files are read off the UI thread.
- **The active set** is kept per user in `session.json` (`LaunchSet`). A set gone from the files (a branch without it) is kept as the choice while Start starts the single startup project, and comes back with the file.

### Trust

A launch file comes with the repository, but it only names projects and actions: nothing starts without Start, and Start needs a trusted folder (ADR 0004). The page, as before, opens only in a trusted solution.

### The page

The Startup Projects page's Multiple startup projects gets a set picker: `<Multiple Startup Projects>` and the named sets, **New** (a set from the rows shown, "Profile 1"), **Delete**, the set's **Name** (a rename on Apply) and **Share profile**. Apply saves the chosen set and makes it the one Start starts. Each row's launch profile is the entry's.

## Consequences

- Switching between one project and a set, or between sets, is one choice in the toolbar, as in the classic IDE, and a team can commit its sets.
- A project's profile in a set no longer changes the profile F5 uses for it alone, and the other way round.
- `*.user` in the `.gitignore` Codalus writes (ADR 0031) already ignores the `.user` files.
- To confirm: the `.slnx` file names, and the classic IDE's spelling of None, against files the classic IDE writes for a `.sln` and a `.slnx`. The reader takes either name and any case, so a difference changes only which name a new file gets.
- Not done: a set's per-project target framework or device, and the classic IDE's other per-project options in the file, which are kept but not shown.
- Tests: `LaunchSetsTests` (the files, the active set, per-entry profiles), `LaunchSetsShellTests` (the toolbar, the drop-down, the page, a change on disk, the session), and the smoke step `debug.compound` (a shared set saved in `Sessions.slnxLaunch` and chosen in the toolbar).
