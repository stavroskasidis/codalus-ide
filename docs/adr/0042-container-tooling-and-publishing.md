# 0042. Container tooling and publishing

- **Status:** accepted (M17a and M17c, 2026-10-11; the maintainer's standing go-ahead for the milestones)
- **Date:** 2026-10-11
- **Deciders:** maintainer

## Context

M17 gets an app from the IDE to a deployable artefact or container. It is split (roadmap rule 5):
- **M17a, containers:** Dockerfile and compose editing, Add Docker Support, running and debugging a project in a container, compose files and Visual Studio's compose projects (`.dcproj`), the Containers window, Docker and Podman.
- **M17b, Aspire:** ADR 0043.
- **M17c, publishing:** publish profiles (`.pubxml`) with a UI; folder, framework-dependent or self-contained, single-file, trimmed, ReadyToRun and Native AOT publishing; container images through the SDK.

The questions: how to talk to the container engine; how a project runs and is debugged in a container; what to do with `.dcproj` files, whose SDK (`Microsoft.Docker.Sdk`) ships only with Visual Studio; and where publishing runs.

## Decisions

**The engine: the user's `docker` or `podman` CLI, not the Engine API**
- `ContainerEngine` (workbench `Containers/`) runs one CLI process per operation and parses its output: Go templates with tab-separated fields (`ps`, `images`), and `inspect`'s JSON, which Docker and Podman both write in Docker's shape. Docker.DotNet would mean a socket per OS (named pipe, Unix socket, Podman's machine socket) and a second code path for Podman; the CLI already knows the user's context, machine, credentials and compose plugin.
- The engine is the `containers.tool` setting (a command line, so `wsl -d Ubuntu --exec podman` works), else M14c's `debug.containerTool`, else `docker` when it is on the PATH, else `podman`. Podman's `docker` shim's stderr note is ignored.
- Every command is cancellable (its process is ended), and nothing blocks the UI thread.
- Containers Daedalus Studio creates carry the label `dev.daedalus.project` (the project path), so the Containers window lists them first and tests find their own.

**Run and debug: the "Container (Dockerfile)" launch profile, with fast mode**
- `"commandName": "Docker"` (Visual Studio's) runs the project in a container; `httpPort`, `sslPort`, `publishAllPorts`, `launchUrl` with `{Scheme}://{ServiceHost}:{ServicePort}`, and `containerRunArguments` are read.
- **Fast mode** (`containers.fastMode`, on by default, Visual Studio's Debug behavior): `build --target base` builds only the Dockerfile's `base` stage; the container idles (`tail -f /dev/null`) with the project's build output mounted at `/app`. Run starts `dotnet /app/App.dll` through `exec -i` (its output is the Run channel's); Debug copies the pinned Linux netcoredbg into the container (M14c's `RemoteDebuggers`, without asking: the container is ours) and launches the app under it through `exec -i` as a pipe transport, so breakpoints bind from the first line. A rebuild needs no image build. Debug builds are portable, so a Windows or macOS build runs in the Linux container.
- **Regular mode** (fast mode off): the whole Dockerfile is built and the container runs its entry point; debugging attaches to its .NET process.
- The container's ports are published on `127.0.0.1` (the profile's ports, else free ones), the profile's environment applies without its `applicationUrl`, and the container is removed when the program or the debug session ends.
- HTTPS in containers (the development certificate mounted in) isn't done: `TODO(issue)`. Static web assets of the project folder aren't mounted in fast mode: `TODO(issue)`.

**Add Docker Support: the .NET templates' files**
- A multi-stage Dockerfile on `mcr.microsoft.com/dotnet/{aspnet|runtime}:<version>` and `sdk`, built from the solution's folder (the templates' context), restoring each project file first; `.dockerignore` there; and the "Container (Dockerfile)" profile. The fixture's Dockerfile is what it writes. No package reference is added (Visual Studio's `Microsoft.VisualStudio.Azure.Containers.Tools.Targets` is proprietary and not needed).

**Compose: the engine's own `compose`, and `.dcproj` as a solution item**
- Compose Up (`compose -f file [-f file.override] -p name up -d --build`), Down, and Debug Compose Service (up, then attach to the service's .NET process; the `.dcproj`'s `DockerServiceName` picks the service).
- **`.dcproj`** is never evaluated: the build host reads its items from the XML (`ComposeProject`), it loads as a project without frameworks, gets no design-time build, and solution builds and restores use a solution filter without it (as for unloaded projects). Solutions created in Visual Studio open and build without errors.
- Compose files are validated with the Compose Specification's schema (Apache-2.0), bundled in the markup host; Dockerfiles keep TextMate's grammar. The Dockerfile language server (D3) isn't added: `TODO(issue)`.

**The Containers window**
- Containers (state, image, ports) and images; for the selected container its logs, environment, ports and mounts, labels, and files (`exec ls -1Ap`, BusyBox too; a file opens as a local copy). Actions: start, stop, restart, remove, open in browser, a terminal in the container (`exec -it`, the integrated terminal), attach the debugger.

**Publishing (M17c): the SDK's `Publish` and `PublishContainer` targets in the build host**
- Publish runs as a build (`BuildTargets.Publish`, `PublishContainer`) with the profile as `PublishProfile` and `_IsPublishing=true`, so MSBuild stays in the build host (ADR 0004), restore sees the profile's runtime identifier, and trim and AOT warnings reach the Error List like any build warning.
- Profiles are `Properties/PublishProfiles/*.pubxml`, read and written by the shell as plain XML (unknown properties kept).
- Container images use the SDK's container support (MIT), to the local engine (`LocalRegistry` Docker or Podman) or a registry the user names; cloud targets stay plugins.

## Consequences

- Container features need a running engine; without one the window says why, and the tests skip (they fail on CI's Linux runner, the only GitHub runner with Linux containers).
- Fast mode needs the Dockerfile's `base` stage, as the templates have it; without one, regular mode is used.
- Windows containers aren't supported (the scripts are `sh`, as M14c's remote targets): `TODO(issue)`.
