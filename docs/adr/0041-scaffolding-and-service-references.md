# 0041. Scaffolding and service references

- **Status:** accepted (M15c plan, 2026-10-11; the maintainer's go-ahead to complete every milestone)
- **Date:** 2026-10-11
- **Deciders:** maintainer

## Context

M15c finishes M15 ("ASP.NET Core and Hot Reload", [ADR 0022](0022-aspnet-core-loop.md)) with what Visual Studio's "Add > New Scaffolded Item" and "Connected Services" give an ASP.NET Core developer:
- **Scaffolding:** controllers (empty, with read/write actions, with Entity Framework), MVC views, Razor Pages (empty and CRUD), Blazor CRUD pages, minimal API endpoint groups and ASP.NET Core Identity.
- **Service references:** OpenAPI clients and gRPC clients and services, from a file or a URL, refreshed when the document changes.

The constraints:
- **Scaffolders run the user's code.** The Entity Framework scaffolders build the project and load its assembly to read the model; Identity changes the project. As with EF Core ([ADR 0036](0036-ef-core-integration.md)), that only happens out of process, from the build host, and only in trusted folders (ADR 0004).
- **MSBuild only runs in the build host** (ADR 0004): project edits (items, packages) happen there.
- **The tools are the user's.** The scaffolders' output and options change between ASP.NET Core versions; the project's own version must decide them.
- The roadmap left two choices to this ADR: `dotnet scaffold` or `dotnet-aspnet-codegenerator`, and Kiota or NSwag.

## Options

| Question | Options | Verdict |
|---|---|---|
| The scaffolding engine | `dotnet-aspnet-codegenerator` (MIT, `dotnet/Scaffolding`, released with every ASP.NET Core version: 10.0.2 for .NET 10); `dotnet scaffold` (MIT, the newer interactive tool, still preview and its command line still moving) | **`dotnet-aspnet-codegenerator`**, the user's (a local tool from `.config/dotnet-tools.json`, or a global one), run as a child of the build host in the project's folder. Its command line has been stable for years, and Visual Studio's scaffolders are built on it. `dotnet scaffold` is a `TODO(issue)` once it leaves preview. |
| Empty items | The code generator; the SDK's item templates | **The SDK's item templates** (`apicontroller`, `mvccontroller`, `page`, `view`, `razorcomponent`) through `templates/instantiate` ([ADR 0033](0033-project-items-templates-and-folder-mode.md)): no tool, no build, no package in the project. An empty minimal API endpoint group has no template; the IDE writes it. |
| The dialog | Hand-written per scaffolder; generated from each scaffolder's parameters | **Generated.** A catalog (`Scaffolders` in the workbench) describes each scaffolder: its category, engine, command, and parameters (name, model, DbContext, folder, flags, choices, the database provider, positional arguments). The form, the validation and the command line come from it. |
| Models and contexts | Typed names; DbContext discovery | **Both.** The fields take any name (a new context name makes the generator create one); M27a's Roslyn discovery (`daedalus/ef/contexts`) offers the project's contexts and their entity types. |
| The database provider | The generator's default (SQL Server); the project's | **The project's**: the generator assumes SQL Server and fails without its package, so `-dbProvider` follows the project's EF Core provider package unless the form says otherwise. |
| OpenAPI client generation | Kiota (MIT, a separate tool; generated files are checked in); NSwag through `<OpenApiReference>` (MIT; `Microsoft.Extensions.ApiDescription.Client` + `NSwag.ApiDescription.Client` generate at build time) | **NSwag through `<OpenApiReference>`**: the items `dotnet-openapi` and Visual Studio's Connected Services write, so projects move between IDEs and the command line unchanged, and the client follows the document at every build. Kiota is a `TODO(issue)` (as a second generator, the way Visual Studio offers it). |
| gRPC | `Grpc.Tools` through `<Protobuf>` | **`<Protobuf Include GrpcServices="Client \| Server \| Both \| None">`**, with `Grpc.Net.ClientFactory`, `Google.Protobuf` and `Grpc.Tools` for clients, `Grpc.AspNetCore` for services: what the gRPC templates and `dotnet-openapi add proto` write. |
| Who writes the items | `dotnet-openapi` (a separate global tool, not in the SDK); the build host | **The build host**, with the MSBuild construction model, as for every other project edit: no extra tool for the user to install, central package management handled by the existing package edits ([ADR 0012](0012-package-management.md)). The items and metadata are `dotnet-openapi`'s (`SourceUrl`, `CodeGenerator`, `Namespace`, `ClassName`, `Options`, `GrpcServices`, `Access`). |

## Decision

**Scaffolding.**
- `scaffold/status`, `scaffold/install` and `scaffold/run` on the build host (`docs/project-protocol.md`). `run` takes the generator (`controller`, `view`, `razorpage`, `area`, `identity`, `minimalapi`, `blazor`, `blazor-identity`) and its arguments; it runs `dotnet aspnet-codegenerator` with `DOTNET_CLI_TELEMETRY_OPTOUT=1`, one at a time, killed with its process tree on cancel, its output streamed (`scaffold/output`) to the Scaffolding output. The files it created or changed come from comparing the folder before and after; the generator sometimes exits with 0 after an error, so an error with nothing written fails.
- The code generator needs the project to reference `Microsoft.VisualStudio.Web.CodeGeneration.Design`, and the Entity Framework scaffolders `Microsoft.EntityFrameworkCore.Tools`. The dialog says what is missing; **Install** restores or installs the tool as a local tool and adds the packages, at the project's .NET major version (EF Tools at the project's EF Core version).
- **The scaffolders** (Visual Studio's set): API Controller (Empty, with read/write actions, with actions using Entity Framework), MVC Controller (Empty, with read/write actions, with views using Entity Framework), Razor View (Empty; from a template), Razor Page (Empty), Razor Pages using Entity Framework (CRUD or one page), Razor Component, Razor Components using Entity Framework (CRUD), Minimal API Endpoints (Empty; using Entity Framework), Identity (all pages or a list, the default UI, the context and the user class).
- **The dialog** (Add New Scaffolded Item, a document): categories, the scaffolders with what runs each, and the generated form. It opens from the Solution Explorer's project and folder menus (the folder fills the Folder field) and the Project menu. The first created file opens in the editor.

**Service references.**
- `serviceReferences/list`, `add`, `update` and `remove` on the build host. Add installs the packages the project lacks (the generators private), then writes the item; the version of each is the newest stable one in the global packages folder (it restores offline), else from the sources, else one known to work; then a restore.
- The workbench puts the document in the project: a file outside the project is copied into `OpenAPIs/` or `Protos/` (as Visual Studio does), a URL is downloaded there (16 MB at most) and kept as `SourceUrl`. **Refresh** downloads it again (to a temporary file first). Remove keeps the document and the packages.
- **The Service References document** of a project: the references with their generator, class, namespace, services and source; Save (metadata), Refresh, Open Document, Remove; a form to add one (kind, file or URL, namespace and class name or services, generator). `.proto` files get a TextMate grammar.

## Consequences

- Scaffolding behaves exactly as the command line does, with the project's own tool and templates; upgrading ASP.NET Core needs nothing from us. Each EF scaffolder run builds the project (5–10 s).
- The packages a service reference needs are the user's dependencies, recorded in their project, not ours.
- OpenAPI clients are generated at build time; until the first build, the language service doesn't know the client class.
- `TODO(issue)`: `dotnet scaffold` as a second engine once it is stable; Kiota as a second OpenAPI generator; Areas, and Identity's page list read from `--listFiles` instead of typed.
- `TODO(issue)`: multi-targeted projects pass no `-tfm`: the generator picks the first framework.
