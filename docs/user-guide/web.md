# Web development

Daedalus Studio runs ASP.NET Core projects with Kestrel, using their launch profiles: `applicationUrl` becomes the app's URLs, and `launchBrowser` opens your browser. Start with `F5` (debugging) or `Ctrl+F5`.

## The Endpoints Explorer

View → Endpoints Explorer lists every endpoint of the solution's ASP.NET Core projects, by project:
- **Minimal APIs:** `MapGet`, `MapPost`… and `MapMethods`, with the prefixes of the `MapGroup` calls they are made on.
- **Controller actions:** from `[Route]` and `[HttpGet]`… templates, with `[controller]` and `[action]` filled in. Actions without route attributes use the default `{controller}/{action}` route.

Double-click an endpoint to open its handler. Right-click → **Generate Request** adds a request for it to the project's `.http` file. The file is created if needed, with a `@<Project>_HostAddress` variable from the launch profile; route parameters become `{{variables}}` to fill in. The list refreshes when you save a C# file. Endpoints built at run time, Razor Pages and endpoints added by libraries aren't listed.

## .http files

`.http` (and `.rest`) files describe HTTP requests, in the format of Visual Studio and VS Code's REST Client:

```http
@host = http://localhost:5248

GET {{host}}/api/todos/1
Accept: application/json

###

# @name create
POST {{host}}/api/todos
Content-Type: application/json

{ "id": 3, "title": "Write docs" }

###

GET {{host}}/api/todos/{{create.response.body.$.id}}
```

- **Send a request:** click the green ▶ next to it, press `Ctrl+Alt+R` with the caret in it, or right-click → Send Request. The response appears in the **HTTP Response** window: status, time and size; the body, colored and formatted (JSON and XML indented); the headers; the request as sent, variables filled in; and the history of requests.
- **Variables:**
  - `@name = value` lines (values can use other variables);
  - `{{$guid}}`, `{{$randomInt 1 100}}`, `{{$timestamp}}`, `{{$datetime iso8601}}`, `{{$localDatetime rfc1123}}` and `{{$processEnv NAME}}`;
  - the results of named requests (`# @name create`): `{{create.response.body.$.id}}` (a JSONPath) and `{{create.response.headers.Location}}`.

  A request with an unknown variable isn't sent.
- **Environments:** an `http-client.env.json` file next to the `.http` file (or in a parent folder) defines them, with `$shared` values for all. `http-client.env.json.user` overrides it (keep that one out of source control). Right-click → **Select the Environment for .http Requests** picks one; the first is used until then.

  ```json
  { "$shared": { "version": "v1" }, "dev": { "host": "http://localhost:5248" }, "staging": { "host": "https://staging.example.com" } }
  ```
- **Bodies** can come from a file: `< ./payload.json`.
- **Certificates:** a server on localhost is accepted even before its development certificate is trusted. Other hosts must have a valid certificate.

## The environment

Debug → **Select the ASP.NET Core Environment** sets `ASPNETCORE_ENVIRONMENT` (and `DOTNET_ENVIRONMENT`) for every run and debug session, over the launch profile's: Development, Staging, Production or another name. "Use the launch profile's" turns it off. The choice is saved with the solution.

## HTTPS development certificate

Before the first run of a project with an `https` URL, Daedalus Studio checks the ASP.NET Core development certificate and offers to trust it if needed. Debug → **Check the HTTPS Development Certificate** and **Trust the HTTPS Development Certificate** do it at any time. They use `dotnet dev-certs https`:
- **Windows and macOS** ask you to confirm.
- **Linux:** `--trust` covers .NET, and the browsers and OpenSSL where it can. If a browser still warns:
  1. Export the certificate: `dotnet dev-certs https --export-path ~/aspnetcore-dev.crt --format PEM`.
  2. Import it into that browser, or add it to the system store. On Debian and Ubuntu, copy it to `/usr/local/share/ca-certificates/` and run `sudo update-ca-certificates`.

## User secrets

Project → **Manage User Secrets** (also on a project's context menu) opens the project's `secrets.json`. If the project has no `UserSecretsId` yet, `dotnet user-secrets init` adds one to the project file first. The file lives outside the project folder, so it stays out of source control:
- **Windows:** `%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json`
- **Linux and macOS:** `~/.microsoft/usersecrets/<id>/secrets.json`

It is completed with the same schema as `appsettings.json`.
