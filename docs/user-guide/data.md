# Data and Entity Framework Core

Daedalus Studio works with Entity Framework Core through your own `dotnet-ef` tool, the same one you would run in a terminal, so its behaviour always matches your EF Core version.

## The Entity Framework window

Open it from **Project → Entity Framework**, the Solution Explorer's context menu on a project that references EF Core, or **View → Other Windows → Entity Framework**. The solution must be trusted: EF's design-time tools build and run your code.

- **Context**: every `DbContext` in the solution. They are found as soon as the C# language service has loaded the projects, with the provider and the connection string name your code configures (`UseSqlite(GetConnectionString("Blogging"))`, Aspire's `AddNpgsqlDbContext<T>("db")`…). **Refresh** builds and asks `dotnet ef dbcontext list` too, which finds contexts that only a design-time factory creates.
- **Database**: where migrations apply. By default, the database your app's configuration points to. You can pick any connection string the startup project already has in `appsettings*.json` or its user secrets. It is passed to `dotnet ef` for that command only, and never stored.
- **Migrations**: oldest first, each **Applied**, **Pending**, or **Unknown** when the database can't be reached (the window says why).
- The status line shows the `dotnet-ef` version (local or global tool) and the project's EF Core version.

Commands build the startup project first, so their errors appear in the Error List. Their output goes to the **Entity Framework** output channel.

### Installing dotnet-ef

When `dotnet ef` isn't available, the window offers **Install dotnet-ef**:
- if the solution's tool manifest (`.config/dotnet-tools.json`) lists it, the .NET tools are restored (`dotnet tool restore`);
- otherwise it is installed as a local tool at your EF Core version (a tool manifest is created if needed).

The startup project must reference `Microsoft.EntityFrameworkCore.Design`, as for any use of the EF tools.

## Migrations

- **Add Migration…** asks for a name (a C# identifier) and opens the new migration.
- **Update Database** applies the pending migrations. **The SQL is shown first**: the exact script the update runs, with **Apply**, **Open in Editor** (to read or save it, without applying) and **Cancel**.
- **Update to This Migration** (or double-click) moves the database to the selected migration. When it is older than the last applied one, that is a revert, and the Down script is shown first.
- **Remove Last Migration** deletes the last migration's files and updates the model snapshot. If it is applied, it is reverted first, after a confirmation.
- **Script…** generates SQL from one migration to another (from the empty database by default), optionally idempotent (each migration checks the history table first; SQLite doesn't support it) and without transactions. It opens as an unsaved `.sql` file.
- **Bundle…** creates a migrations bundle: an executable that applies the migrations without the SDK or the source, optionally self-contained. Run it with `--connection` to update another database.

EF Core 9 and later refuse to update a database while the model has changes no migration covers: add a migration first.

## The model view

**Model** opens the context's model as a diagram: a box per entity type with its table, keys (`PK`), foreign keys (`FK`) and properties, owned types next to their owner (dashed), and a line per relationship from the dependent to the principal, with its multiplicity and delete behaviour. Many-to-many relationships show their join entity. The **Debug View** tab is EF's own description of the model (`DebugView.LongView`). Refresh builds and reads the model again.

The model is read by a small helper that runs on your app's runtime with your app's EF Core, like `dotnet ef` does: it creates the context the way the EF tools do (your app's host builder, or your `IDesignTimeDbContextFactory`).

## Compiled models

**Compiled Model…** runs `dotnet ef dbcontext optimize` into a folder of the project (`CompiledModels` by default). Use the result with `options.UseModel(<Context>Model.Instance)` to make startup faster for large models.

## Reverse engineering

**Reverse Engineer…** (also on a project's context menu) generates entity types and a `DbContext` from an existing database with `dotnet ef dbcontext scaffold`:
- the project the files go to, which needs the provider's EF Core package and `Microsoft.EntityFrameworkCore.Design`;
- the connection string (pick one the project already has, or type one) and the provider;
- the output folder, the context's name, and the tables and schemas (all by default);
- options: data annotations instead of the fluent API, no `OnConfiguring` (the default, so no connection string with a password ends up in your code), the database's own names, no pluralization, and overwriting existing files.

The generated context opens when it is done.
