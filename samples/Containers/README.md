# Containers fixture (M17)

- `Web/`: an ASP.NET Core app with a Dockerfile (as Add Docker Support writes it) and a "Container (Dockerfile)" launch profile. Fast mode runs its build output on the Dockerfile's `base` stage; `wwwroot/hello.txt` is a static web asset that fast mode serves from the project folder.
- `docker-compose.yml` (with its override) and `docker-compose.dcproj`: a compose app, and the compose project file the classic IDE adds for it. Codalus IDE loads the `.dcproj` from its XML and never builds it.
- `Compose/` (`Compose.slnx`, M63): a compose app of two .NET services and one that isn't. `front` calls `back` by its service name; each has a Dockerfile with a `base` stage, so fast mode runs its Debug build there. `idle` (`mcr.microsoft.com/dotnet/runtime-deps:10.0` sleeping) runs as `compose.yaml` says.

The container tests copy this folder, build `Web`, and use the user's docker or podman (the images are `mcr.microsoft.com/dotnet/aspnet:10.0`, and `runtime-deps:10.0` for `Compose/`). They remove every container and image they create.
