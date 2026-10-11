# Containers fixture (M17)

- `Web/`: an ASP.NET Core app with a Dockerfile (as Add Docker Support writes it) and a "Container (Dockerfile)" launch profile. Fast mode runs its build output on the Dockerfile's `base` stage.
- `docker-compose.yml` (with its override) and `docker-compose.dcproj`: a compose app, and the compose project file Visual Studio adds for it. Daedalus Studio loads the `.dcproj` from its XML and never builds it.

The container tests copy this folder, build `Web`, and use the user's docker or podman (the image is `mcr.microsoft.com/dotnet/aspnet:10.0`). They remove every container and image they create.
