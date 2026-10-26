# 0006. Build and run

- **Status:** accepted (M4, continuing autonomously toward the MVP)
- **Date:** 2026-10-09
- **Deciders:** maintainer (goal: "keep going until MVP is reached")

## Context

The project brief's M4: build, rebuild and clean at solution and project level with incremental builds; structured output in the Output window; errors and warnings in the Error List with navigation; cancelling a build; startup project selection; Run Without Debugging; launch profiles from `launchSettings.json`.

ADR 0004 put MSBuild in the build host (evaluation, restore, design-time builds since M3), and only for trusted folders.

## Decision

**Builds run in the build host** with an MSBuild `BuildManager` of their own and a fresh `ProjectCollection` per build, `MaxNodeCount` = the processor count, and the active solution configuration and platform. All build work runs in MSBuild's out-of-process worker nodes (`DisableInProcNode`, no node reuse), so task assemblies never load into the host and the nodes exit with the build. Design-time builds share a gate with builds: when a build starts, design-time builds in flight are cancelled and queued again for after it.
- *Solution* builds build the solution file (`.sln`/`.slnx`), so MSBuild's solution metaproject decides project order and configuration mapping; *project* builds build the project with its mapped configuration.
- Targets: `Build`, `Rebuild`, `Clean`. A `Restore` runs first in its own evaluation (as `msbuild -restore` does), except for Clean.
- Incremental builds are MSBuild's own (up-to-date checks of targets); Daedalus adds no fast-up-to-date check yet. `TODO(issue)`: a fast up-to-date check like the classic IDE's.
- A logger turns MSBuild events into `build/output` lines (minimal verbosity: warnings and errors in MSBuild's `file(line,col): error CODE: message [project]` form, high-importance messages such as `App -> bin/...`, and a summary) and structured `build/diagnostic` notifications (severity, code, message, file, line, column, project). The `build/run` request completes after the last of them with the result, counts and elapsed time. `build/cancel` cancels all submissions; the result then says so.
- If the build's restore changed a project's assets file, that project is evaluated (and design-time built) again.
- Builds are trusted-only, like everything else MSBuild does.

**In the shell** (`BuildService` in the workbench): one build at a time; Save All before building (setting `build.saveAllBeforeBuild`); the Build output channel; build diagnostics in the Error List next to the language service's (a Source column and a Build + IntelliSense / Build Only / IntelliSense Only filter; a diagnostic both report is shown once; build entries stay until the next build starts); the Error List opens when a build fails with errors; status bar progress and result; the Build and Debug menus, the Solution Explorer's context menu, and the familiar key bindings (Ctrl+Shift+B, Ctrl+Break, Ctrl+F5, Shift+F5).

**Startup project**: chosen in the Solution Explorer (Set as Startup Project, shown in bold) or with Debug → Select Startup Project, remembered per solution; by default the first project with `OutputType` `Exe` or `WinExe`.

**Run Without Debugging** (`RunService` in the workbench): builds the startup project, then starts it according to its launch profile from `Properties/launchSettings.json` (the chosen one, else the first `Project` or `Executable` profile). The build host's `project/runInfo` evaluates the SDK's `RunCommand`, `RunArguments` and `RunWorkingDirectory` (what `dotnet run` uses) for the active configuration and target framework.
- `Project`: `RunCommand RunArguments` plus the profile's `commandLineArgs`, in its `workingDirectory` (default: the project folder), with its `environmentVariables`, `DOTNET_LAUNCH_PROFILE`, and `applicationUrl` as `ASPNETCORE_URLS`; `launchBrowser` opens `launchUrl` when the app logs "Now listening on" (or after 10 seconds).
- `Executable`: the profile's `executablePath` (a bare name is found on the `PATH`).
- Other commands (`IISExpress`, `Docker`…) are refused with a message.
- Without a profile: the program with no arguments.
The process's output (stdout and stderr) goes to a "Run: <project>" Output channel, and its standard input is closed, so a read returns end-of-input instead of hanging. Stop (Shift+F5, or the status bar) ends it and its process tree. On Windows, `run.externalConsole` starts console apps in a console window of their own instead, where input works. `TODO(issue)`: an integrated terminal for input on every OS (post-MVP roadmap). Programs keep running when the shell exits.

The launch profile is chosen per project from the status bar (the startup item) or Debug → Select Launch Profile.

## Consequences

- One MSBuild in one process, as before; worker nodes are child processes of the build host and exit with the build.
- Console apps that read standard input only work in the external console (Windows) until the integrated terminal.
- M5 (debugging) reuses the launch profile resolution and the startup project.
