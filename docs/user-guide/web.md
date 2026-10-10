# Web development

Daedalus Studio runs ASP.NET Core projects with Kestrel, using their launch profiles: `applicationUrl` becomes the app's URLs, and `launchBrowser` opens your browser. Start with `F5` (debugging) or `Ctrl+F5`.

## Hot Reload

Debug → **Start with Hot Reload** runs the startup project under `dotnet watch`, with its launch profile. Save a file, or press `Alt+F10` (Apply Code Changes, which saves every modified file), and the change is applied to the running app:
- **Web apps:** the browser refreshes, including after CSS changes.
- **Edits that can't be applied** to a running app (a *rude edit*, such as a changed method signature): the app restarts, and a notification says so.
- **Edits that don't compile:** they wait until they do.

The status bar shows the state (watching, applied in N ms, compile errors, restarting); click it to see the output. The **Hot Reload on Start Without Debugging** setting (`run.hotReload`) makes `Ctrl+F5` start with Hot Reload. Hot Reload isn't available while debugging: the debugger can't apply changes to a paused program, so restart the session instead.

## Razor and Blazor

`.razor` and `.cshtml` files are served by the C# language service, through the same Razor compiler the build uses:
- **C# in a Razor file** (expressions like `@count`, event handlers like `@onclick="Increment"`, `@code` blocks): completion, hover, parameter info, Go to Definition, Find All References and highlighting. Errors appear in the editor and the Error List as you type.
- **Markup:**
  - after `<`: component names (your own and the framework's), with HTML elements;
  - inside a component's tag: its parameters (`[Parameter]` properties) and directive attributes such as `@onclick` and `@bind`;
  - after `@` at the start of a line: directives (`@page`, `@inject`, `@code`…).
- **Hover and Go to Definition** work on a component's tag and on its parameters.
- **Navigating to a component** from a `.cs` file opens its `.razor` file.

Run, Debug and Hot Reload work as for other ASP.NET Core apps: with Hot Reload, saving a component applies markup and code changes to the running app.

Not available in Razor files yet: rename, quick actions, formatting and folding. Errors that the Razor compiler itself reports (`RZ…`) appear after a build.

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
