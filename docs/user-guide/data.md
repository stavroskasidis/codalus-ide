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

## The Database Explorer

**View → Other Windows → Database Explorer** (`Ctrl+Alt+D`), or **Tools → Database Explorer**, lists:
- **your connections**, which you add with **Add Connection…**: SQL Server, PostgreSQL, MySQL/MariaDB or SQLite; the server, port, database (or the SQLite file), the user and password (or Windows authentication for SQL Server), extra options, or a whole connection string; **Test Connection** checks it. Connections are kept for your user account, without their password: tick **Save the password** to keep it in the operating system's credential store (Windows Credential Manager, the macOS Keychain, the Secret Service on Linux); otherwise you are asked once per session.
- **From the solution**: the connection strings of the open solution's `appsettings*.json` files and user secrets, and the databases of a .NET Aspire AppHost that have a fixed port (with the password Aspire generated in the AppHost's user secrets). They are read from those files each time and never copied anywhere. A SQLite file named relative to the project is looked for in the project's folder.

Expanding a connection connects it and shows its databases (the connection's own first), then **Tables**, **Views**, **Procedures** and **Functions**, and for each table its columns (type, nullability, identity, foreign key), keys and indexes. The context menu has View Data, New Query, Reverse Engineer with EF Core (which fills the reverse engineering form with the connection and the selected tables), Refresh, Disconnect, Edit and Remove Connection.

Database work happens in a separate process, `daedalus-datahost`: a query that hangs or a driver that crashes never stops the IDE. SQL Server is reached through managed networking on every OS: Microsoft's native networking library for Windows isn't open source, so it isn't shipped.

## Table data

**View Data** (or double-click a table) shows its rows a page at a time (`data.pageSize`, 200), with First, Previous, Next and Last, the row count, and a **filter** (a `WHERE` condition, then Enter).
- Edit cells in place; **Ctrl+0** sets a cell to NULL. Changed rows show `*`, new ones `+` (**Add Row**), deleted ones `−` (**Delete Row**).
- Nothing is saved until **Commit**, which applies every change in one transaction, finding rows by their primary key. If another session changed or deleted one of the rows in the meantime, nothing is saved. **Revert** discards the changes.
- Views, and tables without a primary key, are read-only.
- **Export…** writes every row (not just the page) as CSV or JSON; **Import…** inserts the rows of a CSV file (with a header row of column names) or a JSON array of objects, in one transaction.

## Queries

**New Query** opens a SQL editor connected to the selected connection (and database); any `.sql` file can be connected with **Change the Query's Connection…** (also the status bar item `$(database) …` of a SQL editor).
- **Execute** (`Ctrl+Shift+E`) runs the selection, or the whole editor; **Execute Current Statement** (`Ctrl+Shift+Enter`) the statement at the caret. SQL Server scripts may use `GO` lines.
- The **Query Results** window shows each result set as a grid (at most `data.maxRows` rows, 10,000, and it says when a result was cut), the **Messages** (`PRINT` output, notices, errors with their line, rows affected), the time and the row count. **Cancel** (`Alt+Break`) stops the query in the database.
- **Display Estimated Execution Plan** (`Ctrl+Shift+L`) and **Execute with Actual Execution Plan** (`Ctrl+Shift+M`) show the plan of the statement at the caret (or the selection) as a tree, with each operator's share of the cost and its estimated and actual rows: SQL Server's showplan, PostgreSQL's and MySQL's `EXPLAIN`, SQLite's `EXPLAIN QUERY PLAN` (estimated only). **Open Plan** opens the engine's own plan (XML, JSON or text). An actual plan runs the statement, as it does in other tools.
- A query editor keeps its connection between runs, so temporary tables and `SET` options last.
- **History** lists your last 200 queries with their connection, time and outcome; double-click one to open it in a new editor.

## SQL editing

`.sql` files are colored for every dialect. With a connection, completion offers the dialect's keywords, the database's tables, views and routines, and their columns (after `alias.` or `table.`, with the statement's aliases resolved); hovering a table shows its columns. SQL Server's T-SQL also gets syntax errors as you type and **Format Document**, from Microsoft's T-SQL parser (formatting is skipped for scripts with comments, which it would drop).

## SQL database projects

SDK-style SQL projects (`.sqlproj` using `Microsoft.Build.Sql`) open with their scripts and build into a `.dacpac` with Build, like any project. In a `.slnx`, give them `Type="C#"` (in a `.sln`, the SDK-style project type), as `dotnet build` needs. Schema compare and publishing to a database aren't available: they need DacFx, whose license isn't open source.
