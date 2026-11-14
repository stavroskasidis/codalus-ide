# Containers, Aspire and publishing

Codalus IDE builds, runs and debugs your apps in containers with **your own Docker or Podman**, through its command line. Nothing runs in a daemon of ours.

## The container engine

The engine is the **Containers → Container Engine** setting (`containers.tool`): `docker`, `podman`, or a command line such as `wsl -d Ubuntu --exec podman`. When it is empty, Codalus IDE uses `docker` if it is on your `PATH`, else `podman`. Attach to a Process in a Container uses it too, unless you set `debug.containerTool`. On Windows and macOS, Docker Desktop or a running Podman machine (`podman machine start`) is needed; the containers are Linux containers.

## Add Docker Support

Right-click a project → **Add Docker Support** (also on the Project menu). It writes:
- a `Dockerfile` in the project's folder, as the .NET container templates write it: a `base` stage on `mcr.microsoft.com/dotnet/aspnet` (or `runtime` for console apps), `build` and `publish` stages on the SDK image, and a `final` stage;
- a `.dockerignore` in the solution's folder, which is the build context;
- a **Container (Dockerfile)** launch profile in `Properties/launchSettings.json`.

## Running and debugging in a container

Choose the **Container (Dockerfile)** launch profile (the toolbar's profile box or Project Properties), then press `Ctrl+F5` or `F5`.

- **Fast mode** (the default, the **Containers → Fast Mode** setting): only the Dockerfile's `base` stage is built. Your Debug build's output is mounted into the container at `/app`, so rebuilding needs no image build; its static web assets (`wwwroot`, Razor class libraries' files) are mounted read-only too. `Ctrl+F5` starts the app in the container, with its output in the Output window's **Run** channel. `F5` copies the Linux debugger into the container and starts the app under it: breakpoints, stepping and every debugger window work as for a local program.
- With fast mode off, the whole Dockerfile is built and the image's entry point runs; `F5` attaches to the app once it has started.
- The container's ports are published on `127.0.0.1`: the profile's `httpPort` and `sslPort`, else free ports. With `launchBrowser`, the browser opens on `launchUrl` (`{Scheme}://{ServiceHost}:{ServicePort}` is filled in).
- Stopping the program, or the debug session, removes the container. Image builds go to the **Container Tools** output.

## Compose

Right-click a `compose.yaml` or `docker-compose.yml` (or a `.dcproj`) → **Compose Up** builds and starts its services (with `docker-compose.override.yml` when there is one), **Compose Down** stops and removes them, and **Debug Compose** starts them all under the debugger.

### Fast mode

With **Containers → Fast Mode** on (the default), compose runs as the classic IDE's Debug configuration does:
- **Each .NET service runs your Debug build.** A service is .NET when it is built from a Dockerfile next to a project (the one project in the Dockerfile's folder), or when its label `dev.codalus.project` names one (relative to the compose file). Codalus IDE builds the project, builds only the Dockerfile's `base` stage as `<app>-<service>:dev`, and mounts the build output at `/app`; static web assets (`wwwroot`, Razor class libraries' files) are mounted read-only too.
- **A code change needs no image build.** Compose Up again restarts only the services whose build changed (the others go on), and so does any build while the app runs.
- **Services that aren't .NET** (a database, a cache) run as the compose file says, and so does a .NET service whose Dockerfile has no `base` stage.
- **The IDE's override.** Codalus IDE writes these settings to `.codalus/state/<solution>/compose.override.yml`, which compose reads after your own files. It's your user state, never committed, and rewritten on each run. **Show the IDE's Override** (the Containers window's toolbar, or on the compose file's menu) opens it read-only.

With fast mode off, the compose file runs as written (every image built in full), and **Debug Compose** attaches the debugger to one service once it has started: the `.dcproj`'s `DockerServiceName`, the only one, or the one you pick.

### Debugging every service

**Debug Compose** (also on Debug → Other Debug Targets) starts every service and debugs each .NET one as a program of one debug session: the debugger is copied into each container and starts the program there, so breakpoints in start-up code hit. The Processes window lists each service; a breakpoint in one pauses the others, as for multiple startup projects. Paths in the containers map back to the solution's files.

While the app runs, Debug → Other Debug Targets acts on **one service**, the others left running (each asks which service):
- **Restart Compose Service…**: a debugged service's project is built and its program starts again under the debugger; any other service restarts (on its new build).
- **Stop Compose Service…**: the service stops (and leaves the debug session).
- **Debug Compose Service…**: a .NET service running without the debugger joins the session (or starts one).
- **Detach Compose Service…**: the service leaves the debug session and goes on running its build without the debugger (it restarts to do so).

**Apply Code Changes** (`Alt+F10`, a save, or Continue) applies edits to every debugged service under the Codalus debugger, in its container: the container runs your build, so the edits are for its code. After **Stop Debugging**, the debugged services' containers wait idle until Compose Up, Restart Compose Service or Compose Down.

**Profiling a service** (on Linux): each .NET service fast mode runs, debugged or not, can be profiled in its container while it runs. **Profile Compose Service…** (Debug → Other Debug Targets) collects CPU Usage and .NET Counters from the service you pick until you stop collecting, and opens the report; the **Performance Profiler** (`Alt+F2`) lists the services with the running processes (**back (compose service)**) for its other tools: allocations, memory snapshots, .NET Async, threads, exceptions and activities. The profiler attaches while the service runs, so its start-up isn't in the profile. How it gets in: the override mounts the solution's `.codalus/state/<solution>/diag` folder at `/codalus-diag` in each .NET service and sets `DOTNET_DiagnosticPorts=/codalus-diag/<service>.sock,connect,nosuspend`, so the service's runtime connects to the socket the profiler listens on there (and, while nothing listens, tries again without waiting). This needs the engine to run the containers on this machine's Linux kernel, as Docker and Podman do on Linux: Docker Desktop's file sharing (macOS, Windows, and Docker Desktop on Linux) doesn't carry Unix sockets, so there the profiler says no runtime connected, and on macOS and Windows the IDE doesn't add the folder or the variable.

Solutions made in the classic IDE with a Docker Compose project (`.dcproj`) open without errors: the compose project shows its files, and builds leave it out (its SDK ships only with the classic IDE). Its `DockerServiceName` is the service Debug Compose starts first.

Compose files are checked against the Compose Specification's schema as you type, with descriptions on hover. Dockerfiles are colored.

## The Containers window

**View → Other Windows → Containers** lists your containers (Codalus IDE's own first) and images. For the selected container:
- **Logs**, **Environment**, **Ports** (and mounts), **Labels**;
- **Files**: browse its folders; a file opens as a local copy (changes aren't written back).

Toolbar: Start, Stop, Restart, Remove, Open in Browser (its first published port), **Open Terminal** (a shell in the container, in the integrated terminal), and **Attach Debugger** (to a .NET process in it). Debug → **Attach to a Process in a Container…** does the same from the Debug menu.

When no engine answers (Docker or Podman isn't installed, or its daemon or machine isn't running), the window and **Attach to a Process in a Container…** say so, and how to start or install one.

## .NET Aspire

New Project's **Enlist in Aspire orchestration** (on Additional information, for web projects in a trusted location) adds `<Solution>.AppHost` and `<Solution>.ServiceDefaults` projects to a new solution, or adds the new project to the solution's own AppHost, with the references and the AppHost's `AddProject` line ([New projects and solutions](solutions.md#additional-information)).

Set an Aspire **AppHost** project as the startup project, then:
- `Ctrl+F5` runs it. Its orchestrator asks Codalus IDE to start each project of the app: they run like any program, each with its **Run** output, and their output also reaches the dashboard's console logs.
- `F5` debugs the AppHost and every project in **one debug session**, however many projects it has: breakpoints in any project stop, and the Processes window lists each program.

**View → Other Windows → Aspire Resources** opens among the documents (it also opens on its own when an AppHost starts, as a tab behind the file you're in). It lists the app's resources: projects, containers and executables, with their state and endpoints. For the selected one: its **Console** output, **Environment** and **Details** (endpoints, properties). The console is an editor like the Output window: it keeps the program's colors, shows standard error in the error color, holds the last 4 million characters, and stays responsive however fast a resource writes. **Start**, **Stop** and **Restart** are the resource's own commands (a container added with `WithExplicitStart()` starts from here; a project restarted under `F5` joins the debug session again); **Debug** debugs one project alone while the AppHost runs without the debugger (`Ctrl+F5`), attaching to its program (Detach leaves it running); **Open Endpoint** opens its address. **Open Dashboard** (also on the Debug menu) opens the Aspire dashboard, already signed in; with `launchBrowser` in the AppHost's launch profile it opens when the app starts.

Aspire's orchestrator and dashboard come with your AppHost's packages; Codalus IDE talks to them, and ships neither. An AppHost started from the IDE gives the orchestrator two minutes to reach Docker or Podman when it starts (a slow Docker Desktop otherwise fails the start); set `ASPIRE_DEPENDENCY_CHECK_TIMEOUT` (seconds) yourself to choose another limit.

## Publishing

Right-click a project → **Publish…** (also on the Build menu) opens its Publish page. A project's **publish profiles** are the `.pubxml` files in `Properties/PublishProfiles` (the format `dotnet publish -p:PublishProfile=<name>` reads, and other IDEs write); the page makes a folder profile when there is none, and **New Profile…** adds one.

- **Target:** a **folder**, or a **container image** (the .NET SDK's container support: to your local Docker or Podman when the registry is empty, or to the registry you name, signed in with `docker login`).
- **Configuration**, **target framework** and **target runtime** (a runtime identifier such as `linux-x64`; empty means portable).
- **Self-contained**, **single file**, **trimmed**, **ReadyToRun** and **Native AOT** (these need a target runtime; the page says what's missing).

**Publish** saves the profile and publishes through the same build as Build: output in **Output → Build**, and errors and warnings, trimming and AOT ones included, in the Error List. When it finishes, a notification offers to open the folder.

## Limits

- Linux containers only.
- HTTPS inside the container (the development certificate) isn't set up; use HTTP ports.
- Compose: profiling a service works on Linux engines only (Unix sockets don't cross Docker Desktop's file sharing), attached while it runs (not from its start), without File I/O (the service's process isn't one of this machine's) and without a full dump; a service run without the debugger takes a code change by a build and a restart (no Hot Reload); with fast mode off, Debug Compose debugs one service, attached after it started.
- A single project run with the **Container (Dockerfile)** launch profile isn't profiled in its container yet.
- Blazor WebAssembly components aren't debugged in the browser when their server runs in a compose service or under Aspire.
- Aspire: the AppHost's orchestrator needs Docker or Podman running, even when no container is started (without one, a project that waits for another with `WaitFor` never starts); only project resources are started by the IDE (containers and executables by Aspire's orchestrator); a debug session holds up to 64 programs (the AppHost, its projects and their restarts); in a debug session a project's output goes to the Debug Console, not to the dashboard.
- Cloud publish targets (Azure, AWS, Google Cloud) aren't built in.
