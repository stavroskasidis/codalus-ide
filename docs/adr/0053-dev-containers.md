# 0053. Dev Containers and remote windows in containers

- **Status:** accepted (M21b, 2026-10-13)
- **Date:** 2026-10-13
- **Deciders:** maintainer

## Context

M21a (ADR 0050, 0051) gave remote windows over SSH and in WSL: `daedalus-server` copied to the target and started through the target's shell, with files, processes, terminals, ports and the hosts on the far side. Containers are the third place developers work:
- **A running container**: the one the app runs in, a database's, a service's.
- **A dev container**: a container described by the repository (`.devcontainer/devcontainer.json`), so that everyone builds in the same environment. VS Code and Visual Studio open these.

The Dev Containers specification has a reference CLI (`@devcontainers/cli`, Node.js). It implements everything: features (OCI artifacts that install tools at build time), Docker Compose, every lifecycle hook, image metadata merging.

## Decision

**A running container is a remote target like the others.**
- `container:<name>` is a `RemoteTargetSpec` whose target is M14c's `ContainerTarget`: `docker exec -i` or Podman's, with the container CLI of the settings (`containers.tool`), including one inside WSL (`wsl -d … podman`).
- The server bundle is copied in and started through that shell, exactly as over SSH. It runs on the container's .NET, as the hosts do (ADR 0050).
- The remote picker lists the running containers, `--remote container:<name>` opens one, and the Containers window has Open in Remote Window.

**Dev Containers: a C# subset, not the CLI.** `DevContainers` reads `devcontainer.json` (JSON with comments) and does what most repositories use:
- `image`; or `build` (`dockerfile`, `context`, `args`), and the legacy `dockerFile`;
- `workspaceFolder` (default `/workspaces/<folder>`) with the folder bind-mounted there;
- `containerEnv`, `runArgs`, `mounts` (strings or objects), `remoteUser`/`containerUser`;
- `postCreateCommand` (string, array or object), run once when the container is created;
- `forwardPorts`: covered by the remote session's port detection, which forwards what starts listening;
- the `${localWorkspaceFolder}`, `${localWorkspaceFolderBasename}`, `${containerWorkspaceFolder}` and `${containerWorkspaceFolderBasename}` variables.

The container is kept alive (`sleep` in a loop, the CLI's `overrideCommand`). It's named after the folder (a hash of its path) and labelled with it (`dev.daedalus.devcontainer`, plus the project label of ADR 0042), so opening the folder again reuses it (started if stopped) and `postCreateCommand` doesn't run again. The remote window then opens at the workspace folder, on the open solution's counterpart there.

**Not implemented, and said so**: properties outside the subset (`features`, `dockerComposeFile`, the other lifecycle commands, `customizations`) are listed in the Dev Containers output when the container comes up.

**Why not the CLI.**
- It needs Node.js, which the IDE only downloads on demand for web language servers (ADR 0024).
- It would add a second process model around the container engine, which the IDE drives itself everywhere else (ADR 0042: one process per operation, never the Engine API).
- The subset covers the common `devcontainer.json`.

Features are the main loss; the CLI can come later for them.

## Consequences

- A container needs `sh` and the server's runtime (.NET, like the hosts). Images without .NET open, but projects don't load, as on any remote machine without it.
- The workspace is a bind mount: on Windows with Podman in WSL, the folder is mounted through `/mnt/<drive>/…` (`WslLaunch.ToLinuxPath`).
- Dev containers carry our labels, so the Containers window and cleanups recognize them.

**Tests**:
- the parser, the variables and what's unsupported;
- a real dev container brought up with Podman and the local Ubuntu image (in WSL on Windows): the folder mounted, `postCreateCommand` once, reuse;
- a remote session connected into a running container, with the server copied in.

All never pull.
