# 0022. The ASP.NET Core loop: endpoints, .http files and Hot Reload

- **Status:** accepted (M15 plan, 2026-10-10; the maintainer's standing go-ahead for M9–M16)
- **Date:** 2026-10-10
- **Deciders:** maintainer

## Context

M4 runs web projects with their launch profiles (`applicationUrl` becomes `ASPNETCORE_URLS`, `launchBrowser` opens the browser), and M11 completes `appsettings.json` from a bundled schema. M15 makes the ASP.NET Core loop productive: find endpoints, call them, set up HTTPS and secrets, and see code changes without a restart. It is split:
- **M15a:** the Endpoints Explorer, `.http` files and their runner, the environment selector, the HTTPS development certificate and user secrets.
- **M15b:** Hot Reload, with browser refresh.
- **M15c:** scaffolding and service references (OpenAPI, gRPC).

The decisions to make:
- where endpoints are discovered;
- which `.http` dialect to support, and where requests run;
- how the development certificate and user secrets are handled;
- how Hot Reload works, given that Roslyn's hot-reload API (`Microsoft.CodeAnalysis.ExternalAccess.Watch`/`HotReload`) isn't published on nuget.org.

## Decisions

**Endpoints: discovered in the C# language host, with the semantic model**
- `daedalus/aspnet/endpoints` (`docs/protocol.md`) runs in `daedalus-lshost`, where the compilations already are. Only projects whose compilation has `IEndpointRouteBuilder` are scanned.
- **Minimal APIs:** `Map*` calls whose method is an ASP.NET Core extension method (the semantic model, not just the name), with the route from a constant argument. Group prefixes follow the receiver: chained calls (`WithTags`, `RequireAuthorization`) return the same builder, locals and fields go to their initializer, and `MapGroup` adds its prefix.
- **Controllers:** classes that derive from `ControllerBase` or carry `[ApiController]`/`[Controller]`. Routes come from `[Route]` and `[HttpGet]`… templates, with `[controller]` and `[action]` replaced and `/`/`~/` templates absolute; actions without attribute routes use the default conventional route.
- **Not found** (documented): routes built at run time, Razor Pages, and endpoints added by libraries (Identity, health checks), as Visual Studio's own Endpoints Explorer.

**`.http` files: the Visual Studio and REST Client dialect, run by the shell**
- **Format:** requests separated by `###`; `#` and `//` comments; `# @name` names a request; `@variable = value`; a request line (`METHOD URL [HTTP/1.1]`, or a URL for GET) with `?`/`&` query lines; headers; a body (or `< file`).
- **Variables:** file variables (which may use each other), environments from `http-client.env.json` (`$shared` plus the chosen environment, `http-client.env.json.user` overriding), the results of named requests (`{{name.response.body.$.path}}`, `{{name.response.headers.X}}`), and `$guid`, `$randomInt`, `$timestamp`, `$datetime`, `$localDatetime`, `$processEnv`. An unknown variable stops the request with a message instead of sending a literal `{{x}}`.
- **The runner** is `HttpService` in the workbench: `HttpClient`, redirects followed, responses up to 4 MB kept as text, a history of 50. It runs in the shell process because requests go to the user's own servers, need no isolation, and their results feed the UI directly.
- **Certificates:** a localhost server's untrusted certificate is accepted (the development certificate before it is trusted); other hosts are verified.
- **The editor:** our own TextMate grammar (`source.http`), and a send glyph on each request line instead of Visual Studio's CodeLens: our CodeLens comes from language servers, and `.http` has none. `Ctrl+Alt+R` sends the request at the caret.
- **The response** shows in an HTTP Response tool window: the status, timing and size; the body colored and formatted by content type (JSON and XML indented); the headers; the request as sent; and the history.

**Development certificate and user secrets: the SDK's own commands**
- `dotnet dev-certs https --check --trust` tells trusted, untrusted or missing. Before the first run or debug session of a profile with an https URL, the shell offers to trust it; `dotnet dev-certs https --trust` does it, with the OS's own confirmation on Windows and macOS. On Linux, trust depends on the distribution and browser, so the shell adds guidance (export and import, or `update-ca-certificates`) instead of touching system stores.
- **User secrets:** `dotnet user-secrets init` adds the `UserSecretsId`; the shell opens `secrets.json` from the SDK's folder (`%APPDATA%\Microsoft\UserSecrets` on Windows, `~/.microsoft/usersecrets` elsewhere), where `appsettings.json`'s schema already applies.

**The environment selector**
- Debug → Select the ASP.NET Core Environment sets `ASPNETCORE_ENVIRONMENT` and `DOTNET_ENVIRONMENT` for every run and debug session, over the launch profile's, until it is reset. It is saved with the solution.

**Hot Reload (M15b): `dotnet watch`, not Roslyn's internal API**
- The roadmap planned delta computation in the language host with Roslyn's hot-reload service. Those packages are only on the .NET engineering feeds, and their API changes with every SDK (the roadmap's top risk).
- `dotnet watch` ships in every SDK (MIT), computes the deltas with the matching Roslyn, applies them through the startup hook, refreshes browsers for web apps, and restarts on rude edits (`--non-interactive`). It reports in its output ("changes applied in N ms", "Restarting application").
- **Decision:** "Start with Hot Reload" runs the project under `dotnet watch run` with its launch profile, and the IDE follows the output: a status bar item and a notification on rude edits. Saving a file is the trigger, as in Visual Studio's "Hot Reload on file save". This keeps the SDK and the deltas in lockstep and needs no unpublished dependency.
- **Not covered:** Hot Reload while debugging. netcoredbg can't apply metadata updates, so a debug session restarts instead (ADR 0021 lists Edit and Continue as a gap).

## Consequences

- Endpoint discovery costs a compilation per web project. It runs when the Endpoints Explorer is visible, after the language service is ready and after a C# file is saved (debounced).
- The `.http` runner has no proxy or client-certificate settings yet. `TODO(issue)`.
- If Roslyn's hot-reload packages appear on nuget.org with a stable API, revisit M15b: computing deltas in the language host would allow Hot Reload while debugging, once a debugger supports it.
