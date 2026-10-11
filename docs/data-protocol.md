# Shell ↔ data host protocol

`daedalus-datahost` ([ADR 0037](adr/0037-data-host-and-providers.md)) holds the database providers (SQL Server through Microsoft.Data.SqlClient with managed networking, PostgreSQL through Npgsql, MySQL and MariaDB through MySqlConnector, SQLite through Microsoft.Data.Sqlite), the connections and query execution. It speaks JSON-RPC 2.0 with LSP framing (`DaedalusStudio.Rpc`) over stdio. The shell starts it on the first database action (`DataService` in `src/DaedalusStudio.Workbench/Data`) and supervises it with `SupervisedProcess`: after a crash it restarts and the open connections are registered again. `DAEDALUS_DATAHOST` points at another build (an apphost or a `.dll`); otherwise `datahost/` next to the shell is used. Contracts are in `src/DaedalusStudio.Data.Protocol/DataMessages.cs`.

The same executable with `--lsp` is the SQL language server ([ADR 0038](adr/0038-sql-language-service-and-database-projects.md)); see [protocol.md](protocol.md) for `daedalus/sql/context`.

## Conventions

- **Connections** are registered by id with their **full** connection string (the password included), which the host keeps in memory only. Requests name the connection by id and, optionally, a `database` other than the connection's own (another connection string for PostgreSQL, which can't switch).
- **Values** travel as invariant strings; `null` is SQL NULL; binary values are `0x` hex (cut at 4 KB when read); dates are `yyyy-MM-dd HH:mm:ss.fffffff`. Values sent for edits and imports are converted to the column's type (SQLite: its declared type), and a value that doesn't convert is an error.
- **Cancellation**: `$/cancelRequest` cancels the request's command in the database (SQLite: `sqlite3_interrupt`). Handlers run on the thread pool, so a long query never holds other requests.

## Lifecycle

| Method | Params | Result | Notes |
|---|---|---|---|
| `initialize` | `{processId, protocolVersion}` | `{name, version, protocolVersion, engines}` | The first request after every (re)start. |
| `shutdown` (request), `exit` (notification) | | | |

## Requests

| Method | Params | Result | Notes |
|---|---|---|---|
| `data/connect` | `{id, engine (sqlserver \| postgresql \| mysql \| sqlite), connectionString}` | `{serverVersion, database?, defaultSchema?}` | Opens the connection once (testing it) and registers it. |
| `data/disconnect` | `{id}` | `{}` | Forgets it and closes its sessions (SQLite: releases the file). |
| `data/test` | `{engine, connectionString}` | `{succeeded, serverVersion?, error?}` | Opens and closes; never registers. |
| `data/databases` | `{id}` | `{databases, current?}` | SQL Server's databases the login can open, PostgreSQL's that allow connections, MySQL's schemas without the system ones, SQLite's attached schemas. |
| `data/schema` | `{id, database?}` | `{database?, defaultSchema?, objects}` | Every table, view, procedure and function: `{schema?, name, kind, columns: [{name, type, nullable, isKey, isIdentity, default?}], keys: [{name, kind (primary \| foreign \| unique), columns, referencedSchema?, referencedTable?, referencedColumns?}], indexes: [{name, columns, isUnique}]}`. From each engine's catalogs (`INFORMATION_SCHEMA` and `sys.*`, `pg_catalog`, `information_schema`, `sqlite_schema` and `pragma_*`). |
| `data/read` | `{id, database?, table: {schema?, name}, offset, count, orderBy?, descending, filter?}` | `{columns: [{name, type, nullable?}], rows, keyColumns, readOnly, readOnlyReason?, elapsedMilliseconds}` | A page, ordered by `orderBy` or the primary key. `filter` is a `WHERE` condition the user typed. Views and tables without a primary key are read-only. |
| `data/count` | `{id, database?, table, filter?}` | `{count}` | |
| `data/commit` | `{id, database?, table, changes: [{kind (insert \| update \| delete), key?, values?}]}` | `{affected}` | One transaction. Updates and deletes find their row by its key values as read; one that matches no row rolls everything back with `-33203`. |
| `data/execute` | `{id, text, database?, session?, plan (none \| estimated \| actual), maxRows}` | `{resultSets: [{columns, rows, truncated, recordsAffected}], messages: [{kind (info \| warning \| error), text, line?}], elapsedMilliseconds, plan?}` | A batch (SQL Server: split on `GO` lines, the next batch runs after an error). `session` keeps a connection for a query editor until `data/closeSession`. The engine's messages (`PRINT`, notices) and errors come back as messages; rows changed become an info message. `plan`: `{format (xml \| json \| text), raw, roots: [PlanNode], actual}`, a `PlanNode` being `{operation, detail?, estimatedRows?, actualRows?, estimatedCost?, costPercent?, children}`; see below. |
| `data/closeSession` | `{id, session}` | `{}` | |
| `data/export` | `{id, database?, format (csv \| json), path, table?, query?}` | `{rows}` | Every row of the table (or the query), streamed: CSV with a header row (RFC 4180), or a JSON array of objects. |
| `data/import` | `{id, database?, table, format, path}` | `{rows}` | CSV with a header row of column names (an empty unquoted field is NULL, `""` an empty string) or a JSON array of objects; one transaction, so a failing row imports nothing. |

**Plans.**
- SQL Server: `SET SHOWPLAN_XML ON` (estimated, nothing runs) or `SET STATISTICS XML ON` (actual: the statements run and their results come back too), parsed from the showplan XML's `RelOp` tree, each operator with its own share of the statement's cost.
- PostgreSQL: `EXPLAIN (FORMAT JSON)`, with `ANALYZE` for actual (the statement runs).
- MySQL: `EXPLAIN FORMAT=JSON`, and `EXPLAIN ANALYZE` (a text tree) for actual; MariaDB: `ANALYZE FORMAT=JSON` for actual.
- SQLite: `EXPLAIN QUERY PLAN`; there is no actual plan (`-33202`).

## Errors

| Code | Meaning |
|---|---|
| -33200 | The connection isn't registered (the shell connects again and retries). |
| -33201 | The database answered with an error, or a value didn't convert; the message is meant for the user. |
| -33202 | Not supported here (an actual plan on SQLite). |
| -33203 | A commit's row changed or was deleted since it was read: nothing was committed. |
