# Shell ↔ C# Interactive host protocol

C# Interactive runs submissions in **`daedalus-replhost`** ([ADR 0015](adr/0015-interactive-host.md)), a supervised process speaking JSON-RPC 2.0 over **stdio** with `Content-Length` framing, like the other hosts.
- The shell's client is `InteractiveService` in `src/DaedalusStudio.Workbench/Interactive` (AOT-safe).
- The host is `src/DaedalusStudio.Interactive.Host` (Roslyn scripting).
- The contracts are in `src/DaedalusStudio.Interactive.Protocol`.

## Process model

**Starting and restarting**
- The shell starts the host when the C# Interactive window first shows. `DAEDALUS_REPLHOST` points at another host (`.exe` or `.dll`); otherwise the shell uses `replhost/` next to it.
- After every (re)start the client sends `repl/reset` with the current configuration. On a crash (including a script calling `Environment.Exit`) the host restarts with an empty state, up to 5 times a minute.
- **Reset** disposes the process and starts a new one. This is also how a runaway submission is stopped: the shell sends `exit` and kills the process if it doesn't stop within 2 seconds.

**Concurrency**
- One submission runs at a time.
- `repl/completion`, `repl/signatureHelp`, `repl/tokens` and `repl/isComplete` are answered alongside it, on the last completed state.
- They never restore NuGet packages.

**Standard streams**
- What scripts write to `Console.Out` and `Console.Error` arrives as `repl/output`, tagged with the submission. All of it is sent before that submission's `repl/execute` result.
- Scripts read no input.

## Methods

| Method | Kind | Params → result | Notes |
|---|---|---|---|
| `repl/reset` | request | `{workingDirectory, references[], usings[], loadFiles[]}` → `{runtime, errors[]}` | Starts a new state: the shared framework plus `references` (assembly paths, or `nuget:Id, Version`), the default usings plus `usings`, then each `loadFiles` script as `#load`. `errors` lists references or scripts that failed; the state is usable regardless. Native files and copies of framework assemblies among `references` are skipped. |
| `repl/execute` | request | `{submission, code}` → `{success, value?, error?, diagnostics[], elapsedMilliseconds}` | `value`: a trailing expression without a semicolon, formatted as C# (`"text"`, `List<int>(3) { 1, 2, 3 }`). `error`: the formatted exception when the submission threw (`success` false, the state still advances). Compile errors fill only `diagnostics` (`{id, severity, message, line, character}`, zero-based in the submission) and leave the state unchanged. |
| `repl/output` | notification (host → shell) | `{submission, text, isError}` | Complete lines at once, a partial line after 100 ms or when the submission ends. Package restore messages arrive here too. |
| `repl/isComplete` | request | `{code}` → `{complete}` | Whether Enter should run the text: it parses as a complete submission, with no open block, string or comment. |
| `repl/completion` | request | `{code, position}` → `{start, items: CompletionItem[]}` | Items replace `code[start..position]`. LSP `CompletionItem`s with `label`, `kind`, `detail`. After a `.`: the members of the expression, type or namespace. Otherwise: the symbols in scope (including earlier submissions' variables) and keywords. |
| `repl/signatureHelp` | request | `{code, position}` → LSP `SignatureHelp` or null | Invocations and `new` expressions. Parameter labels are substrings of the signature label. |
| `repl/tokens` | request | `{code}` → `{data: int[]}` | LSP-encoded semantic tokens over the whole text, with the language host's legend (`TokenTypes`). One token per line part; script variables are `local`. |
| `shutdown` / `exit` | request / notification | | `exit` ends the process even while a submission runs. |

## `#r` and `#load`

- `#r "path"` and `#load "path"` resolve relative to the working directory.
- `#r "nuget: Id, Version"` (or `Id/Version`, or `Id` for the latest stable version) restores a throwaway `net<runtime>.0` project with the SDK's `dotnet restore`:
  - in `%TEMP%/daedalus-replhost/nuget/<hash>`;
  - with the nearest `nuget.config` above the working directory, if there is one;
  - without the folder's `Directory.Build.*` or `Directory.Packages.props`.
- The runtime assemblies of the package and its dependencies are added to the state.
- A failed restore writes its errors to `Console.Error`, and the directive reports CS0006.
