# 0055. Remote test environments

- **Status:** accepted (M21b, 2026-10-13)
- **Date:** 2026-10-13
- **Deciders:** maintainer

## Context

Code built on one OS often has to be tested on another: a Windows developer ships to Linux servers, or a library has platform-specific paths. Other IDEs read a `testenvironments.json` next to the solution that lists environments (`wsl`, `docker`, `ssh`), and the test explorer runs tests in the one picked. Repositories that have the file expect it to work.

The Test Explorer's .NET tests come from the `DotNetTest` plugin (ADR 0010). It runs VSTest through vstest.console's translation layer and Microsoft.Testing.Platform apps in server mode, both on this machine: test hosts connect back to a local port. Remote windows (ADR 0050) don't help: they move the whole workspace, and a local solution's tests should run elsewhere without that.

## Decision

**Build here, run there, results back as TRX.**
- `TestEnvironments` (workbench `Testing/`) reads `testenvironments.json`: in the solution's folder, or a folder above it up to the repository's root, with comments. It reads `name`, `type`, `wslDistribution`, `dockerImage`, `dockerFile` and `remoteUri`. An environment it can't use keeps a `Problem` (an unknown type, a missing image, WSL on a non-Windows machine), shown when it's run.
- The Test Explorer's toolbar gets a target environment list (Local and the file's environments) when the file exists. With an environment picked, Run All, Run on a selection, a playlist or the tests at the cursor go to `ShellHost.RunTestsInEnvironmentAsync`:
  1. the test projects of the selection are built here (the normal build);
  2. `EnvironmentTestRunner` runs each project's assembly there with `dotnet test <assembly> --logger trx --filter FullyQualifiedName=…` (the filter's reserved characters escaped; no filter for a whole project);
  3. the TRX documents go through `Trx.Import` and `TestService.ApplyImported`, as an imported results file does. The run is named after the environment, and `TestService.BeginExternalRun` makes it a running run that Cancel stops.
- Each type:
  - **WSL** (`WslTarget`): the assembly through `/mnt/<drive>`; results written next to the solution and read here.
  - **Docker**: a container per solution and environment (`daedalus-tests-<hash>`) from the image or the built Dockerfile. It's started through `DevContainers.UpAsync` with the solution's folder mounted at `/workspaces/<folder>`, kept alive and reused; commands run through `docker exec` (`ContainerTarget`). Outputs and results are under the mount.
  - **SSH** (`SshTarget`, the user's ssh): each project's output folder is sent as a `.tar.gz` through ssh's stdin to `~/.daedalus-tests/<solution hash>/<project>/`, replacing the last copy. The results are read back with `cat`.
- The command runs from `/tmp`: the solution's `global.json` pins this machine's SDK, which the environment may not have. `~/.dotnet` comes first on PATH when it exists.

**Why VSTest's `dotnet test <assembly>`.** Assemblies built on Windows run anywhere .NET does. `dotnet test` on an assembly needs only the SDK in the environment, writes TRX, and filters by name. Microsoft.Testing.Platform projects run through it too when they reference the VSTest bridge, as the common templates do.

## Alternatives

- **Running the plugin's runners remotely**: the translation layer and the server-mode protocol connect test hosts back to this machine's ports; through ssh that means tunnels per run, and Docker and WSL networking differ. TRX needs nothing back but a file.
- **Building in the environment**: the solution's `obj/` is shared with the local build through WSL's and Docker's mounts, and a restore there would rewrite `project.assets.json` with the other OS's paths and break the local build. Building here keeps one build.
- **A remote window per environment**: heavier (the whole server and hosts) and it moves the workspace, while only the tests should move.

## Consequences

- Tests appear from local discovery. A test that exists only on the other OS (`#if`, a platform-specific project) shows its result only if discovered here; unmatched results are listed in the Tests output.
- Debugging and coverage in an environment aren't supported yet: they say so and point to Local. Run Failed, Run Not Run and Repeat Last still run locally.
- Docker environments need the outputs under the solution's folder; the container stays, labelled with the project label of ADR 0042, for the Containers window to remove.
- The environment needs an SDK with `dotnet test` and the test framework's runtime.

**Tests**:
- `TestEnvironmentTests`: parsing and problems, the filter's escaping, container paths and names, finding the file up to the repository; and samples/Testing's xUnit v2 project built here and run in the stand-in (WSL on Windows, a fake `ssh` on Linux and macOS), with passed, failed and skipped results back.
- `TestEnvironmentShellTests`: through the Test Explorer, with WSL.
