# 0038. The SQL language service and SQL database projects

- **Status:** accepted (M27b plan, 2026-10-11; the maintainer's go-ahead to complete every milestone)
- **Date:** 2026-10-11
- **Deciders:** maintainer (D15 in the roadmap's decision register)

## Context

The roadmap's M27 asks for a SQL language service (highlighting, completion of keywords and of the active connection's schema objects, diagnostics, formatting), with T-SQL "through Microsoft's SQL Tools Service through the M11 LSP bridge if feasible, otherwise ScriptDom", and other dialects highlighted by grammar with schema completion. It also asks for SDK-style `.sqlproj` projects (Microsoft.Build.Sql) to load and build, with schema compare and publish only if DacFx's licence is OSI-approved (D15).

## Options

**T-SQL**
1. **SQL Tools Service** (`microsoft/sqltoolsservice`, MIT source). It is a self-contained .NET application of about 100 MB per platform, published as per-RID archives on GitHub releases (not on NuGet). It speaks JSON-RPC with VS Code's LSP shapes, but its IntelliSense needs its own `connection/connect` protocol, with the connection's credentials, before it completes schema objects, and it holds its own connections. Integrating it means a second on-demand download with checksums (like Node, ADR 0024), a connection protocol the bridge doesn't have, and a second copy of every connection in another process.
2. **ScriptDom** (`Microsoft.SqlServer.TransactSql.ScriptDom`, MIT, on NuGet, about 2 MB): Microsoft's T-SQL parser and script generator, used by SQL Tools Service and DacFx themselves. It gives parse errors with positions and a formatter; completion is ours, from the schema the data host already reads.

**Where the service runs**
- In the shell (Native AOT, no), in the data host's connection process, or **in the data host's executable started as an LSP server** (`daedalus-datahost --lsp`) through the M11 bridge, like any language server. The shell sends each SQL document's context (`daedalus/sql/context`: the dialect and the connection's schema) when the editor's connection or the schema changes.

**DacFx.** The `Microsoft.SqlServer.DacFx` package's licence (checked 2026-10-11, version 170.5.96) is the "Microsoft Software License Terms, Microsoft SQL Server Data-Tier Application Framework": proprietary, not OSI-approved. `Microsoft.Build.Sql` (the SDK-style project SDK, 2.3.0) is MIT, but it uses DacFx when it builds.

## Decision

**T-SQL through ScriptDom, in `daedalus-datahost --lsp`.** SQL Tools Service is feasible but costs a 100 MB download per platform and its own connection protocol for what ScriptDom and the data host's schema give in-process. Its value over this (refactorings, peek definition of database objects) is recorded as `TODO(issue)`.

**The language service** (`src/DaedalusStudio.Data.Host/Sql`), for the `sql` language (TextMate's SQL grammar colours every dialect):
- **Completion:** the dialect's keywords and functions; the connection's schemas, tables, views and routines (qualified as needed); columns after `alias.` or `table.`, resolving aliases from the statement's `FROM` and `JOIN` clauses; columns of the statement's tables otherwise. The schema is the one the shell already cached for the explorer, so completion is local.
- **Hover:** a table's or view's columns with their types and keys.
- **Diagnostics:** T-SQL parse errors from ScriptDom (`TSql170Parser`) when the document's dialect is SQL Server; other dialects get none (no open parser is good enough for PostgreSQL and MySQL; `TODO(issue)`).
- **Formatting:** T-SQL through ScriptDom's script generator (keywords upper-cased, one clause per line), only when the script parses; the others are left as they are.
- **Dialect:** from the editor's connection; a `.sql` file without one is T-SQL inside a `.sqlproj`, and generic SQL otherwise.

**SQL database projects.** SDK-style `.sqlproj` files load (evaluated by MSBuild like any SDK-style project, their `.sql` files in the Solution Explorer) and build through MSBuild into a `.dacpac`, with the SDK the project names. They get no design-time build. **Schema compare and publish are not built**: they need DacFx (or SqlPackage, which is built on it), whose licence isn't OSI-approved (D15). This stays a gap until the maintainer decides whether running the user's own SqlPackage, installed by the user like dotnet-ef, is acceptable.

## Consequences

- SQL editing works offline and needs no download.
- T-SQL diagnostics and formatting are exact (Microsoft's parser); PostgreSQL, MySQL and SQLite get highlighting and completion only.
- `TODO(issue)`: SQL Tools Service integration for its refactorings and object navigation, as an opt-in download.
- `TODO(issue)`: schema compare and publish of `.sqlproj` (D15: DacFx is proprietary); the maintainer decides about the user's own SqlPackage.
