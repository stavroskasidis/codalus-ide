# 0037. The data host, its providers and the database explorer

- **Status:** accepted (M27b plan, 2026-10-11; the maintainer's go-ahead to complete every milestone)
- **Date:** 2026-10-11
- **Deciders:** maintainer (D15 in the roadmap's decision register)

## Context

M27b gives Daedalus Studio what the classic IDE's Server Explorer, SQL Server Object Explorer and SQL editor do: connections to the databases .NET developers use (D15: SQL Server, PostgreSQL, SQLite and MySQL/MariaDB first), schema browsing, a table data editor, and a query editor with results, messages, timing, cancel, history and execution plans. M27a (ADR 0036) did EF Core.

The constraints:
- **Heavy work is out of process and supervised.** A hung query or a crashing native provider must never reach the shell. Every query can be cancelled.
- **The shell is Native AOT.** ADO.NET providers use reflection and native libraries; they don't belong in the shell.
- **No new host at startup** (the idle budget, ADR 0002).
- **Secrets** (ADR 0031): connection passwords never go in settings; they stay where the user put them (user secrets, appsettings, environment) or go to the OS credential store.
- Every dependency is OSI-licensed and MIT-compatible (the roadmap's licence rule).

## Options

| Question | Options | Verdict |
|---|---|---|
| Where providers run | The shell; the build host; a new host | **A new supervised `daedalus-datahost`**, started on the first database action, framework-dependent like the other hosts. It also exists for untrusted folders: it runs no workspace code. |
| SQL Server driver | Microsoft.Data.SqlClient with its native SNI; with managed SNI only | **Managed SNI only.** `Microsoft.Data.SqlClient` is MIT, but its Windows native networking (`Microsoft.Data.SqlClient.SNI.runtime`) is under Microsoft's proprietary licence terms. The host excludes that package and sets `Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows`, the managed implementation every other OS already uses. |
| The other drivers | — | **Npgsql** (PostgreSQL licence), **MySqlConnector** (MIT; MySQL and MariaDB), **Microsoft.Data.Sqlite** (MIT) with **SQLitePCLRaw** (Apache-2.0) and SQLite (public domain). |
| Schema metadata | `GetSchema()` collections; each engine's catalogs | **Each engine's catalogs** (`INFORMATION_SCHEMA` and `sys.*`, `pg_catalog`, `information_schema` for MySQL, `sqlite_schema` and `pragma_*`): `GetSchema` differs between providers and lacks keys and routines. |
| The data grid | Avalonia's DataGrid package; our own | **Our own virtualized grid** in the shell: DataGrid binds cells by reflection (not AOT-clean) and is a separate package. The grid shows a page at a time (200 rows by default), with explicit Commit and Revert. |
| Execution plans | Engine output as text; parsed into a tree | **Parsed into one tree shape** in the host: SQL Server's showplan XML (`SET SHOWPLAN_XML` estimated, `SET STATISTICS XML` actual), PostgreSQL's `EXPLAIN (FORMAT JSON)` (with `ANALYZE` for actual), MySQL's `EXPLAIN FORMAT=JSON` / MariaDB's `ANALYZE FORMAT=JSON`, SQLite's `EXPLAIN QUERY PLAN`. The raw plan opens in an editor on request. |
| Connection strings | Our own store; the project's own sources | **Both**: connections the user adds are kept per user, without their password, which goes to the OS credential store when the user asks to save it; connections the solution already has (`appsettings*.json`, user secrets, Aspire AppHost resources) are listed under the solution and read from their source each time. |

## Decision

**Process.** `daedalus-datahost` (`src/DaedalusStudio.Data.Host`) speaks JSON-RPC over stdio (`docs/data-protocol.md`), started lazily by `DataService` (workbench `Data/`) through `SupervisedProcess`; after a crash it restarts and the shell's connections are registered again. `DAEDALUS_DATAHOST` points at another build. The same executable with `--lsp` is the SQL language server (ADR 0038).

**Wire** (`data/*`): `data/connect` (an id, the engine and the full connection string, which the host keeps in memory only), `data/disconnect`, `data/test`, `data/databases`, `data/schema` (schemas, tables, views, routines, columns, keys and indexes of one database), `data/read` (a page of a table, with its key columns), `data/count`, `data/commit` (inserts, updates and deletes in one transaction, by key), `data/execute` (a batch: result sets, messages, records affected, elapsed time, and a plan when asked), `data/export` and `data/import` (CSV or JSON, streamed to and from a file). Cancelling a request (`$/cancelRequest`) cancels its command.

**The shell.**
- **Database Explorer** (a tool window): connections (the user's, and the solution's), databases, schemas, Tables, Views, Procedures and Functions, columns, keys and indexes; New Query, View Data, Refresh, Add, Edit and Remove Connection, Reverse Engineer (EF Core, ADR 0036).
- **Add Connection** (a document): the engine, host, port, database or file, authentication, user and password with "save the password" (OS credential store), extra options, or a raw connection string; Test.
- **Table data** (a document): paged, typed, editable cells (`NULL` kept apart from empty), new and deleted rows, Commit and Revert; Export and Import as CSV or JSON. Tables without a primary key are read-only.
- **The query editor** is an ordinary `.sql` editor connected to a connection (the status bar shows which, and lets the user change it). Execute runs the selection or the whole document, Execute Current Statement the statement at the caret; the **Query Results** window shows the result sets, the messages, the time and the rows, a Cancel button, the estimated or actual plan, and the history (kept per user, 200 entries).

**Rows sent to the shell** are capped (setting `data.maxRows`, 10,000 per result set); the grid says when a result was cut.

## Consequences

- A provider's crash or a hung query costs a host restart, not the IDE.
- Windows authentication to SQL Server works through managed SNI's SSPI support; features that need the native SNI (shared memory, some named-pipe setups) are gaps on Windows, and TCP is used instead.
- Engines beyond the four come through the same host later. `TODO(issue)`: database connection providers as plugins (the roadmap's M27 entry in the plugin API track), with the plugin platform (M22).
- `TODO(issue)`: Aspire resources while the AppHost runs (dynamic ports come from the dashboard), with M17.
