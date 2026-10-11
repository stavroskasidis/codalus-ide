# Containers, Aspire and publishing

Daedalus Studio builds, runs and debugs your apps in containers with **your own Docker or Podman**, through its command line. Nothing runs in a daemon of ours.

## The container engine

The engine is the **Containers → Container Engine** setting (`containers.tool`): `docker`, `podman`, or a command line such as `wsl -d Ubuntu --exec podman`. When it is empty, Daedalus Studio uses `docker` if it is on your `PATH`, else `podman`. On Windows and macOS, Docker Desktop or a running Podman machine (`podman machine start`) is needed; the containers are Linux containers.

## Add Docker Support

Right-click a project → **Add Docker Support** (also on the Project menu). It writes:
- a `Dockerfile` in the project's folder, as the .NET container templates write it: a `base` stage on `mcr.microsoft.com/dotnet/aspnet` (or `runtime` for console apps), `build` and `publish` stages on the SDK image, and a `final` stage;
- a `.dockerignore` in the solution's folder, which is the build context;
- a **Container (Dockerfile)** launch profile in `Properties/launchSettings.json`.

## Running and debugging in a container

Choose the **Container (Dockerfile)** launch profile (the toolbar's profile box or Project Properties), then press `Ctrl+F5` or `F5`.

- **Fast mode** (the default, the **Containers → Fast Mode** setting): only the Dockerfile's `base` stage is built. Your Debug build's output is mounted into the container at `/app`, so rebuilding needs no image build. `Ctrl+F5` starts the app in the container, with its output in the Output window's **Run** channel. `F5` copies the Linux debugger into the container and starts the app under it: breakpoints, stepping and every debugger window work as for a local program.
- With fast mode off, the whole Dockerfile is built and the image's entry point runs; `F5` attaches to the app once it has started.
- The container's ports are published on `127.0.0.1`: the profile's `httpPort` and `sslPort`, else free ports. With `launchBrowser`, the browser opens on `launchUrl` (`{Scheme}://{ServiceHost}:{ServicePort}` is filled in).
- Stopping the program, or the debug session, removes the container. Image builds go to the **Container Tools** output.

## Compose

Right-click a `compose.yaml` or `docker-compose.yml` (or a `.dcproj`) → **Compose Up** builds and starts its services (with `docker-compose.override.yml` when there is one), **Compose Down** stops and removes them, and **Debug Compose Service** starts them and attaches the debugger to the service you pick.

Solutions made in Visual Studio with a Docker Compose project (`.dcproj`) open without errors: the compose project shows its files, and builds leave it out (its SDK ships only with Visual Studio). Its `DockerServiceName` is the service Debug Compose Service debugs.

Compose files are checked against the Compose Specification's schema as you type, with descriptions on hover. Dockerfiles are colored.

## The Containers window

**View → Other Windows → Containers** lists your containers (Daedalus Studio's own first) and images. For the selected container:
- **Logs**, **Environment**, **Ports** (and mounts), **Labels**;
- **Files**: browse its folders; a file opens as a local copy (changes aren't written back).

Toolbar: Start, Stop, Restart, Remove, Open in Browser (its first published port), **Open Terminal** (a shell in the container, in the integrated terminal), and **Attach Debugger** (to a .NET process in it). Debug → **Attach to a Process in a Container…** does the same from the Debug menu.

## Limits

- Linux containers only.
- HTTPS inside the container (the development certificate) isn't set up; use HTTP ports.
- In fast mode, static files of the project folder (`wwwroot`) aren't mounted.
