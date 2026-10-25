# 0036. EF Core integration

- **Status:** accepted (M27a plan, 2026-10-11; the maintainer's go-ahead to complete every milestone)
- **Date:** 2026-10-11
- **Deciders:** maintainer (D15 in the roadmap's decision register)

## Context

M27 replaces what the classic IDE's EF Core integration, Server Explorer, SQL Server Object Explorer and SQL editor give a .NET developer. Following the roadmap's rule 5 it is split:
- **M27a**, this ADR: EF Core. DbContext discovery, migrations (add, remove, list, apply, revert, script, bundle) with the SQL shown before it is applied, reverse engineering, a model view and compiled models.
- **M27b** (ADRs 0037 and 0038): the database explorer in a new `daedalus-datahost`, the query editor, the SQL language service and SQL database projects.

The constraints:
- **Design-time EF runs the user's code.** Creating a DbContext at design time builds the application's host (`WebApplication.CreateBuilder` … `Build()`) or calls the project's `IDesignTimeDbContextFactory`. That must never happen in the shell or in a host of ours, and only in trusted folders (D4, ADR 0004).
- **MSBuild only runs in the build host** (ADR 0004), and the build host has the project context: the startup project, its output and its target framework.
- **The EF Core version is the project's.** EF Core's design-time API (`Microsoft.EntityFrameworkCore.Design`) changes between versions; projects in a solution can use EF Core 3.1 to 10.
- The roadmap: "All of these run the user's own `dotnet-ef` (MIT, EF Core) against the user's projects, in a separate process, never inside a host."

## Options

| Question | Options | Verdict |
|---|---|---|
| How EF commands run | Load `Microsoft.EntityFrameworkCore.Design` into a host; run the user's `dotnet-ef` | **The user's `dotnet-ef`**, as a child process of the build host, with `--json --prefix-output` so its results are machine-readable and its messages are kept apart. `dotnet ef` finds the local tool (`.config/dotnet-tools.json`) or the global one, the way it does in a terminal. |
| Where DbContext types are discovered | `dotnet ef dbcontext list` only; Roslyn only; both | **Both.** The C# language host finds every class that derives from `DbContext` with the semantic model (`daedalus/ef/contexts`): instantly, without building or running anything, with the provider (`UseSqlite`, `UseSqlServer`, `UseNpgsql`, `UseMySql`…) and the connection string name it is configured with. `dotnet ef dbcontext list` is the authority once the project builds; it adds contexts created by factories in other assemblies. |
| Building | Let `dotnet ef` build; build first through the build host and pass `--no-build` | **Build first**, as Run does: the build's output and errors go to the Build output and the Error List, and `dotnet ef` runs with `--no-build`. |
| The model view | Parse the migration snapshot; parse `ToDebugString()` output; read the model through EF's metadata API | **The metadata API, in a small agent.** `dotnet-ef` has no command that returns the model. `daedalus-efagent` (net8.0, no dependencies) is started like `dotnet-ef` starts its `ef.dll`: `dotnet exec --depsfile <app>.deps.json --runtimeconfig <app>.runtimeconfig.json`, so it runs on the app's runtime with the app's EF Core. It creates the context with EF's public `DbContextActivator.CreateInstance` (the host builder or a design-time factory, as the tools do), reads `IDesignTimeModel.Model` (EF Core 6+) or `Model`, and writes entities, properties, keys, foreign keys, navigations (skip navigations too), indexes, owned types and the debug view (`DebugView.LongView`) as JSON. Everything EF-specific is reached by reflection, through member names that have been stable since EF Core 3.1. |
| Which connection migrations apply to | The app's configuration only; any connection string | **The app's configuration by default**, or a connection string the user picks from the startup project's `appsettings*.json` and user secrets (the M27b database explorer's connections too), passed as `--connection`. |

## Decision

**Processes.**
- `dotnet ef` runs as a child of the build host, in the startup project's folder, with `DOTNET_CLI_TELEMETRY_OPTOUT=1`, killed with its process tree when the request is cancelled or the solution closes. Every `ef/*` request requires a trusted solution.
- `daedalus-efagent` ships in `buildhost/efagent/` and runs the same way, on the startup project's runtime.
- The shell only renders: the Entity Framework tool window, dialogs, the scripts (as SQL editors) and the model view.

**Wire** (build host, `docs/project-protocol.md`): `ef/status` (is `dotnet-ef` available, its version, local or global, and the project's EF Core version), `ef/contexts`, `ef/contextInfo`, `ef/migrations`, `ef/addMigration`, `ef/removeMigration`, `ef/script`, `ef/updateDatabase`, `ef/bundle`, `ef/scaffold`, `ef/optimize`, `ef/model`; the notification `ef/output` streams `dotnet ef`'s messages to the Entity Framework output channel. The language host's `daedalus/ef/contexts` (`docs/protocol.md`) is the Roslyn side.

**Migrations.**
- **List:** `migrations list --json` with the chosen connection: applied or pending, and whether the model has changes the migrations don't cover. Without a database it still lists them, marked as unknown.
- **Add:** a name (validated as a C# identifier), an optional output folder; the new files open in the editor.
- **Remove:** the last migration, only if it isn't applied (`--force` reverts it first, after a confirmation).
- **Apply and revert:** "Update Database" to the latest migration or to a chosen one (a revert when it is older than the applied one). **The SQL is shown first**: `migrations script <applied> <target>` produces exactly the statements that `database update` will run, in a confirmation with Apply, Open in Editor and Cancel.
- **Script:** from and to migrations, idempotent (not for SQLite, which doesn't support it: the dialog says so) and without transactions; opened as an unsaved `.sql` document.
- **Bundle:** `migrations bundle` to a chosen file, self-contained or not, for a runtime identifier; the output path is shown.

**Reverse engineering** (`dbcontext scaffold`): a connection string, the provider (offered from the project's EF Core provider packages), the output and context folders, the context's name and namespace, tables and schemas (all, or a list; M27b picks them from the database's schema), data annotations, no `OnConfiguring` (the default, so no connection string with a password is written into code), database names, no pluralization, overwrite. The generated files open.

**Compiled models** (`dbcontext optimize`): an output folder and namespace; the generated files are listed.

**The model view** (a document): a read-only diagram (an entity per box with its keys, properties, owned types nested under their owner, lines for relationships with their multiplicity and delete behaviour, many-to-many through skip navigations) and the debug view.

**Discovery.** The Entity Framework window groups contexts by project. A project appears when it references EF Core (`Microsoft.EntityFrameworkCore*`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Pomelo.EntityFrameworkCore.MySql` and other providers), and its contexts come from Roslyn at once, then from `dotnet ef dbcontext list` when the user expands them. The startup project is the solution's startup project when it references the context's project, otherwise the context's own project.

**`dotnet-ef` itself** is the user's tool. When it is missing, the window offers to install it as a local tool at the project's EF Core version (`dotnet new tool-manifest`, `dotnet tool install dotnet-ef --version X`, through the M9 tools support), or `dotnet tool restore` when the manifest lists it but it isn't restored.

## Consequences

- EF commands behave exactly as they do in a terminal: the same tool version, the same build, the same configuration. Upgrading EF Core needs nothing from us.
- Each command starts `dotnet ef` (about 1–3 s on top of the build). The Entity Framework window caches its results until a build, a migration or a refresh.
- The model view depends on EF's metadata member names. A future EF version that renames them degrades the diagram (empty parts), not the rest; the debug view is the fallback.
- Connection strings passed with `--connection` are visible to local processes while `dotnet ef` runs, as they are when the command is typed in a terminal. The default (the app's own configuration) passes none.
- `TODO(issue)`: a Package Manager Console-style EF prompt (`Add-Migration` …) is not planned; the window and the commands cover it.
- `TODO(issue)`: DbContext discovery for Visual Basic and F# projects.
