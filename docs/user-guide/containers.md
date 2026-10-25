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

Right-click a `compose.yaml` or `docker-compose.yml` (or a `.dcproj`) → **Compose Up** builds and starts its services (with `docker-compose.override.yml` when there is one), **Compose Down** stops and removes them, and **Debug Compose Service** starts them and attaches the debugger to the service you pick.

Solutions made in the classic IDE with a Docker Compose project (`.dcproj`) open without errors: the compose project shows its files, and builds leave it out (its SDK ships only with the classic IDE). Its `DockerServiceName` is the service Debug Compose Service debugs.

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
- `F5` debugs the AppHost and every project in **one debug session**: breakpoints in any project stop, and the Processes window lists each program.

**View → Other Windows → Aspire Resources** (it opens on its own when an AppHost starts) lists the app's resources: projects, containers and executables, with their state and endpoints. For the selected one: its **Console** output, **Environment** and **Details** (endpoints, properties). **Start**, **Stop** and **Restart** are the resource's own commands (a container added with `WithExplicitStart()` starts from here); **Open Endpoint** opens its address. **Open Dashboard** (also on the Debug menu) opens the Aspire dashboard, already signed in; with `launchBrowser` in the AppHost's launch profile it opens when the app starts.

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
- Aspire: the AppHost's orchestrator needs Docker or Podman running, even when no container is started (without one, a project that waits for another with `WaitFor` never starts); only project resources are started by the IDE (containers and executables by Aspire's orchestrator); a debug session holds the AppHost and up to seven projects; in a debug session a project's output goes to the Debug Console, not to the dashboard.
- Cloud publish targets (Azure, AWS, Google Cloud) aren't built in.
