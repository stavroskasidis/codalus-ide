# Web development

Codalus IDE runs ASP.NET Core projects with Kestrel, using their launch profiles: `applicationUrl` becomes the app's URLs, and `launchBrowser` opens your browser. Start with `F5` (debugging) or `Ctrl+F5`.

## HTML, CSS, JavaScript and TypeScript

Codalus IDE colors these files out of the box. Completion, errors, hover, navigation and formatting come from the same open-source language servers VS Code uses:
- VS Code's HTML and CSS servers, for HTML (with the CSS and JavaScript inside it), CSS, SCSS and Less;
- `typescript-language-server` with TypeScript 5.9, for JavaScript, TypeScript, JSX and TSX.

They run on Node.js. The first time you open such a file, Codalus IDE offers to download Node.js and the servers once, about 70 MB, into its cache folder. It uses a pinned Node.js release, checks its checksum, and installs the servers from a lockfile. Your own Node.js installation isn't used or changed.
- **Install** downloads them now. **Never** turns the offer off.
- The **Web Language Servers** setting (`web.languageServers`) chooses between asking (the default), downloading without asking, and off.
- **Install the Web Language Servers** in the command palette downloads them at any time.

The HTML server also serves the markup of Razor files: see [Razor and Blazor](#razor-and-blazor).

### ESLint

When a JavaScript or TypeScript file is under an ESLint configuration (`eslint.config.js`, `.mjs`, `.cjs` or `.ts`, or an older `.eslintrc*`), Codalus IDE lints it with the project's own ESLint (from its `node_modules`), as you type:
- **Problems** appear in the editor and the Error List next to TypeScript's, with the rule as the code (`no-unused-vars`).
- **Quick actions** (`Ctrl+.`) offer the rule's fix when it has one, and disabling the rule for the line or the file.
- **No ESLint installed** (`npm install` not run yet): nothing is linted, and the Language Services output says why.
- **The ESLint setting** (`web.eslint`) turns it off.

### Prettier

When a project uses Prettier (installed in its `node_modules`, with a `.prettierrc*` or `prettier.config.*` file, or a `"prettier"` key in `package.json`), **Format Document** (`Ctrl+K, Ctrl+D`) and format on save run it, with the project's configuration, for the files it formats: JavaScript, TypeScript, CSS, HTML, JSON, Markdown, YAML and more. Otherwise the language servers format. The **Prettier** setting (`web.prettier`) turns it off.

### npm

**View → Other Windows → Task Runner Explorer** (`Ctrl+Alt+Backspace`) lists the scripts of the solution's `package.json` files (the solution's folder and its projects', not `node_modules`).
- **Run a script:** double-click it. It runs as `npm run <script>` in a terminal of its own. **Run npm Script…** in the command palette picks one too.
- **Restore Packages** (the Task Runner Explorer's button, a `package.json`'s context menu, or Project → Restore npm Packages) runs `npm install`, with its output in the npm output.
- **Packages not installed when a solution opens:** Codalus IDE offers to restore them. The **Restore npm Packages on Open** setting (`npm.restoreOnOpen`) can make it restore without asking, or never.
- **Bindings** in `package.json` run scripts on IDE events, in the format other IDEs' Task Runner Explorer writes:

  ```json
  "-vs-binding": { "BeforeBuild": ["lint"], "AfterBuild": ["copy"], "Clean": ["clean"], "ProjectOpen": ["watch"] }
  ```

  A Before Build script that fails stops the build. The Task Runner Explorer shows a script's bindings.
- **Which npm:** yours, on `PATH`. Without one, or with the **npm** setting (`npm.runtime`) set to `builtIn`, the built-in Node.js's npm runs them (downloaded once, as for the language servers).

Scripts and installs run the project's code, so packages are restored only in trusted folders.

## Hot Reload

Debug → **Start with Hot Reload** runs the startup project under `dotnet watch`, with its launch profile. Save a file, or press `Alt+F10` (Apply Code Changes, which saves every modified file), and the change is applied to the running app:
- **Web apps:** the browser refreshes, including after CSS changes.
- **Edits that can't be applied** to a running app (a *rude edit*, such as a changed method signature): the app restarts, and a notification says so.
- **Edits that don't compile:** they wait until they do.
- **While Hot Reload is still starting** (the status bar says *starting*), `dotnet watch` doesn't see a saved file yet, even when the app already answers. Apply Code Changes waits until it watches, then saves.

The toolbar's **Hot Reload** button (the flame, next to Start) starts with Hot Reload, and while the app runs applies the code changes. Its menu also has Stop Running Programs, the Hot Reload on Start Without Debugging switch and the settings.

The status bar shows the state (watching, applied in N ms, compile errors, restarting); click it to see the output. The **Hot Reload on Start Without Debugging** setting (`run.hotReload`) makes `Ctrl+F5` start with Hot Reload. It needs .NET 6 or later: for an older startup project the command is disabled, and its menu item says so.

**While debugging a Blazor WebAssembly app**, Hot Reload works too: Start Debugging runs its dev server under `dotnet watch`, so saving a file (or `Alt+F10`) applies the edit to the app in the browser while the debugger stays attached, and breakpoints stop in the changed code. **In a Blazor Web App**, edits to WebAssembly components update in the browser while you debug the server and the page; edits to code the server runs wait for Restart, and edits that need a restart restart the app ([Debugging the server and the page together](#debugging-the-server-and-the-page-together)). The **Hot Reload while debugging** setting (`debug.hotReload`, on by default) turns it off. Other apps run under the debugger itself, and the .NET runtime refuses Hot Reload's updates to a program a debugger runs: restart the session to see an edit there, or use Start with Hot Reload.

## Razor and Blazor

`.razor` and `.cshtml` files are served by the C# language service, through the same Razor compiler the build uses:
- **Colors** as Visual Studio gives them, in every theme: components and their parameters (like types and attributes), HTML elements, attributes and values, `@` transitions and directives (`@using`, `@inject`, `@code`), Razor (`@* *@`) and HTML comments, tag helpers in `.cshtml`, and the C# inside the markup as in a `.cs` file. A component is colored as one only when it resolves to a component, so a misspelled tag stays an HTML element. Each kind has its own entry in Fonts and Colors (Razor component element, Markup element…). Until the project has loaded, and for a Razor file outside a project, the Razor grammar colors the file.
- **C# in a Razor file** (expressions like `@count`, event handlers like `@onclick="Increment"`, `@code` blocks): completion, hover, parameter info, Go to Definition, Find All References and highlighting. Errors appear in the editor and the Error List as you type.
- **Markup:**
  - after `<`: component names (your own and the framework's), with HTML elements;
  - inside a component's tag: its parameters (`[Parameter]` properties) and directive attributes such as `@onclick` and `@bind`;
  - after `@` at the start of a line: directives (`@page`, `@inject`, `@code`…).
- **Hover and Go to Definition** work on a component's tag and on its parameters.
- **Navigating to a component** from a `.cs` file opens its `.razor` file.
- **HTML and CSS** (with the web language servers): in markup, the HTML server completes elements, attributes and their values, and describes them on hover. CSS in `<style>` blocks gets CSS completion. The C# and the Razor syntax are left out of what it sees, so these work between them, in `@if` and `@foreach` blocks too.
- **Scoped CSS:** `Counter.razor.css` styles `Counter.razor` only, and Solution Explorer shows it under the component. In its selectors, completion offers the class names and ids of the component's markup, and Blazor's `::deep`.

Run, Debug and Hot Reload work as for other ASP.NET Core apps: with Hot Reload, saving a component applies markup and code changes to the running app.

Not available in Razor files yet: rename, quick actions, formatting and folding. Errors that the Razor compiler itself reports (`RZ…`) appear after a build.

## Blazor WebAssembly

A standalone Blazor WebAssembly app (`dotnet new blazorwasm`) runs on the WebAssembly dev server, and its components run in the browser.
- **Run** (`Ctrl+F5`) starts the dev server; the launch profile's `launchBrowser` opens the page.
- **Debug** (`F5`) debugs the app in the browser:
  - Codalus starts the dev server, then opens the page in Chrome, Edge or Chromium through the dev server's debugging proxy (the profile's `inspectUri`, which `dotnet new` puts there).
  - Breakpoints in `.razor` and `.cs` files stop the component's C# in the browser, with the call stack, locals, watches and stepping, as in any session. Breakpoints in the app's JavaScript files work in the same session.
  - The browser gets a temporary profile, so your own browser, its tabs and its extensions aren't touched. Stopping the session closes it and stops the dev server.
  - The first session downloads the browser debugger, vscode-js-debug (about 1.2 MB), and Node.js if the web language servers haven't already. The Debug Console shows the progress.
  - The setting **Browser** (`debug.browser`) picks the browser: `auto` (Chrome, then Edge, then Chromium), `chrome`, `edge`, `chromium`, or a path.
- **Hot Reload** works once the page is open in a browser, because the components run there: start with Hot Reload, open the page (or let `launchBrowser` open it), then save. The status bar says when the change was applied.

## Debugging the server and the page together

A **Blazor Web App with WebAssembly components** (its launch profile has `inspectUri`, as `dotnet new blazor --interactivity WebAssembly` or `Auto` writes it) debugs as one session: `F5` starts the server under the .NET debugger and, once it listens, opens the page in the browser under the browser debugger.
- **A breakpoint in a component's C#** stops on the server while it prerenders the page, then again in the browser when the component runs there. The Call Stack and Threads windows say which: the browser's threads are named `Browser: …`.
- **Breakpoints in the page's scripts** stop in the browser too.
- **One stop shows at a time.** If the server and the browser both stop, the second shows when you continue the first. **Break All** pauses both.
- **Ending:** stopping the session closes the browser and stops the server. Closing the browser ends the session.
- **The startup project:** a Blazor Web App's server is the default startup project, even when the solution lists its `.Client` project first (on its own, the client's dev host serves nothing at `/`).

**With Hot Reload** (the setting `debug.hotReload`, on by default), `F5` runs the server under `dotnet watch`, as Start with Hot Reload does, and the debugger attaches to it once it serves:
- **Edits to WebAssembly components apply while you debug:** save the file (or `Alt+F10`), and the browser runs the changed code; breakpoints stop in it. The status bar says *Hot Reload: sent to the browser*: whether the browser applied it isn't reported back.
- **Edits to code the server runs** take effect after Restart (`Ctrl+Shift+F5`): the .NET runtime doesn't apply edits to a program a debugger is attached to. That includes components the server prerenders, or runs interactively (InteractiveServer, or InteractiveAuto before WebAssembly loads). Codalus IDE says so once per session, and the server's breakpoints in edited files wait for the restart, with the reason in the margin and the Breakpoints window; the page still stops at them.
- **Edits that need a restart** (a changed signature or field type, `Program.cs`) restart the app, and debugging follows it: the debugger attaches to the new server and the page opens again in a new browser window. An edit made while stopped in the browser applies when you continue.
- **The server's startup code** has already run when the debugger attaches, so its breakpoints don't stop. Set a breakpoint in the server's `Program.cs` (or `Startup.cs`) and `F5` debugs the server from its start instead, without Hot Reload; the Debug Console says why.
- **The server's console output** shows in the Debug Console and in Output, *Run: <name>*.
- **It needs the .NET SDK 10.0.400 or later** (or 11); with an older SDK, `F5` debugs without Hot Reload and the Debug Console says why.
- If the debugger can't attach to the server, only the page is debugged, with Hot Reload, and a notification says so. Turn off `debug.hotReload` to debug the server from its start.
- A Blazor Web App made by the .NET 11 template has no `inspectUri`: .NET 11 expects the IDE to start the WebAssembly debugging proxy, which Codalus IDE doesn't do yet. Its page opens under the browser debugger for scripts, but breakpoints in its WebAssembly components don't bind yet. Add the `inspectUri` line and `app.UseWebAssemblyDebugging()` from the .NET 10 template to debug them.

**Other ASP.NET Core apps** whose launch profile opens a browser (`launchBrowser`) debug their page's scripts the same way. The **JavaScript debugging for ASP.NET Core** setting (`debug.javaScript`, on by default) controls it; off, the profile's browser opens as a plain browser.

Without Chrome, Edge or Chromium, the server is debugged alone.

## Debugging a web page

**Debug → Debug a Web Page…** opens any page in the browser under the browser debugger, with no .NET server: an `http` or `https` URL (a site served by something else), or an HTML file. `F5` with an HTML file open and no startup project does the same for that file, and so does **Debug in Browser** on an HTML file in Solution Explorer. Breakpoints in its scripts stop as in any session.

Not available:
- breakpoints in scripts referenced through `@Assets["…"]`, which are served under fingerprinted names (`app.abc123.js`): reference the plain path (`app.js`) to debug them;
- Firefox and Safari, which the browser debugger doesn't support.

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

- **Send a request:** click the green ▶ next to it, press `Ctrl+Alt+R` with the caret in it, or right-click → Send Request. The response appears in the **HTTP Response** window:
  - the status as a colored chip (green for 2xx, amber for 3xx, red for 4xx, 5xx and a request that failed) with the HTTP version, the time and the size;
  - the request's method and URL, with a link to it in its `.http` file;
  - **Body**, **Headers** (with their count) and **Request** (as sent, variables filled in) as tabs. The body is colored; **Pretty** formats JSON and XML, **Raw** shows it as received, and the wrap button wraps long lines;
  - **Send Again** sends the request again from its file; the toolbar copies the body, saves it to a file, or opens it in an editor;
  - the **History** of requests, newest first, each with its method and status, its time and when it was sent. Select one to see it again; the clear button empties the history.
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

Before the first run of a project with an `https` URL, Codalus IDE checks the ASP.NET Core development certificate and offers to trust it if needed. Debug → **Check the HTTPS Development Certificate** and **Trust the HTTPS Development Certificate** do it at any time. They use `dotnet dev-certs https`:
- **Windows and macOS** ask you to confirm.
- **Linux:** `--trust` covers .NET, and the browsers and OpenSSL where it can. If a browser still warns:
  1. Export the certificate: `dotnet dev-certs https --export-path ~/aspnetcore-dev.crt --format PEM`.
  2. Import it into that browser, or add it to the system store. On Debian and Ubuntu, copy it to `/usr/local/share/ca-certificates/` and run `sudo update-ca-certificates`.

## User secrets

Project → **Manage User Secrets** (also on a project's context menu) opens the project's `secrets.json`. If the project has no `UserSecretsId` yet, `dotnet user-secrets init` adds one to the project file first. The file lives outside the project folder, so it stays out of source control:
- **Windows:** `%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json`
- **Linux and macOS:** `~/.microsoft/usersecrets/<id>/secrets.json`

It is completed with the same schema as `appsettings.json`.

## Scaffolding

**Add New Scaffolded Item** (a project's or a folder's context menu → New Scaffolded Item, or Project → Add New Scaffolded Item) adds controllers, views, pages, components, endpoints and Identity. Pick a category (API, MVC, Razor Pages, Blazor, Minimal API, Identity) and a scaffolder; the form shows that scaffolder's options. Opened on a folder, the Folder field starts there. The first new file opens in the editor, and the Scaffolding output shows what ran.

- **Empty items** (an API or MVC controller, empty or with read/write actions; a Razor Page, view or component) come from the SDK's item templates, with the folder's namespace. An empty **minimal API endpoint group** is a static class with a `Map…Endpoints` method to call from `Program.cs`.
- **The Entity Framework scaffolders** (an API controller with actions, an MVC controller with views, a Razor view from a template, Razor Pages CRUD, Blazor CRUD, minimal API endpoints) and **Identity** run your `dotnet aspnet-codegenerator` in the project's folder; they build the project first. The model and DbContext fields offer the contexts and entity types the C# language service found, and take any name (a new DbContext name creates one). The database provider follows the project's EF Core provider.
- The code generator needs a local or global `dotnet-aspnet-codegenerator`, and the project needs `Microsoft.VisualStudio.Web.CodeGeneration.Design` (and `Microsoft.EntityFrameworkCore.Tools` for the Entity Framework scaffolders). When something is missing the form says so; **Install** restores or installs the tool as a local tool of the solution and adds the packages, at the project's .NET version.
- Scaffolding runs only in trusted folders: the code generator builds and loads your project.

## Service references (OpenAPI, gRPC and WCF)

A project's context menu → **Service References…** (or Project → Service References) lists its OpenAPI, gRPC and WCF references and adds new ones:
- **OpenAPI:** an OpenAPI (Swagger) document, from a file or a URL. The project gets an `OpenApiReference` item and the `NSwag.ApiDescription.Client` and `Microsoft.Extensions.ApiDescription.Client` packages, and NSwag generates a C# (or TypeScript) client when the project builds. Set the namespace and class name, and NSwag options such as `/UseBaseUrl:false`.
- **WCF:** a WCF or CoreWCF service's metadata or a WSDL file, generated by `dotnet-svcutil` into `Connected Services`, as Visual Studio does. See [WCF](wcf.md#add-service-reference).
- **gRPC:** a `.proto` file, from a file or a URL. The project gets a `Protobuf` item and the gRPC packages (`Grpc.Net.ClientFactory`, `Google.Protobuf` and `Grpc.Tools` for a client; `Grpc.AspNetCore` for a service), and `Grpc.Tools` generates the client or the service base when the project builds. Choose Client, Server, Both or None.

A document outside the project is copied into its `OpenAPIs` or `Protos` folder; a URL is downloaded there, and **Refresh** downloads it again. **Remove** removes the reference and keeps the document and the packages. These are the items `dotnet-openapi` and Visual Studio write, so the project builds the same way from the command line. `.proto` files are colored.
