# 0067. F# through FsAutoComplete

- **Status:** accepted (M36a, 2026-10-15)
- **Date:** 2026-10-15
- **Deciders:** maintainer (D10: FsAutoComplete through the LSP bridge; D13: F# is last), with the standing go-ahead to complete every milestone

## Context

Since the MVP, F# projects loaded, built and ran, and `.fs` files were colored by a TextMate grammar. D10 chose FsAutoComplete (Apache-2.0, on FSharp.Compiler.Service, MIT) as the F# language server, through the LSP bridge (ADR 0016), over an F# host of our own.

The research for M36a found the following:
- **Distribution.** FsAutoComplete and Fantomas are .NET tools on nuget.org (`fsautocomplete` 0.85.3, with net8.0, net9.0 and net10.0 builds; `fantomas` 7.0.3, net8.0 and rolling forward). FsAutoComplete is about 25 MB per framework, Fantomas about 30 MB.
- **Projects.** FsAutoComplete loads projects itself, with Ionide.ProjInfo running MSBuild in its own process. With the `AutomaticWorkspaceInit` option it loads the solution under its root folder, `.slnx` included. It can't be given our design-time builds instead.
- **C# references.** FsAutoComplete compiles only F#. It reads a referenced C# project from its built reference assembly, so it reports errors until that project is built.
- **Formatting.** FsAutoComplete formats with Fantomas, which it looks for as a local tool (`.config/dotnet-tools.json`), then a global tool, then on `PATH`. Without one, formatting fails with "Fantomas install not found".
- **Settings.** FsAutoComplete reads an `FSharp` section from `workspace/didChangeConfiguration`. It needs no `workspace/configuration`.
- **Protocol differences:**
  - its hover has a `command:` link;
  - its semantic token legend adds `member`, `module`, `cexpr` and others;
  - its references CodeLens runs `fsharp.showReferences` with three arguments;
  - it doesn't answer our `codalus/` requests.
- **Compile order.** The order of `Compile` items in an `.fsproj` is the compile order. F# projects list their files explicitly (`EnableDefaultCompileItems` is false). The classic IDE shows them in that order, with Move Up, Move Down, Add Above and Add Below.

## Options

| Question | Options | Verdict |
|---|---|---|
| Getting the tools | Ship them with the IDE; download and unpack the `.nupkg`s ourselves with pinned hashes; the user's `dotnet tool install --tool-path` at pinned versions | **`dotnet tool install --tool-path`** into the cache folder (`<cache>/fsharp/<tool>/<version>`), offered the first time an F# file opens (setting `fsharp.languageService`: `ask`, `install`, `off`). The user's NuGet sources and proxy apply, and the SDK checks nuget.org's package signatures. The tool takes the newest framework the user's runtimes run, and the `fantomas` command shim comes with it. The Components window lists it, so offline layouts carry it. |
| Fantomas | Require the user's; install ours | **Ours at the end of FsAutoComplete's `PATH`.** A project's local tool, or a global one, still wins. |
| MSBuild outside the build host (ADR 0004) | Feed FsAutoComplete our project data; accept its own evaluation | **Accept it, as D10 implies.** FsAutoComplete starts only when no solution is open or the solution is trusted, and it starts again when a solution opens or is trusted (`LanguageService.RestartAsync`), because it loads its root folder once. |
| Settings | The shared settings; the server's own | **Its own:** `LanguageServiceDescriptor.Settings` sends an `FSharp` section built from `fsharp.*` (completion of unopened namespaces, FSharpLint, inlay hints, signature and references CodeLens, the analyzers for unused opens and declarations and simplified names, stub generation). |
| Protocol differences | Leave them; adapt in the bridge | **Adapt, generically:** `command:` links are removed from hover Markdown; FsAutoComplete's token types are aliased (`member` → method, `module` → class, `cexpr` → keyword); a lens's position is its second argument whatever follows it, and `fsharp.showReferences` is a command (Find All References); `codalus/testCandidates` failures are ignored per language (F# tests come from discovery after a build); `codalus/documentOptions` isn't asked of FsAutoComplete or the web servers. |
| Compile order | Alphabetical, as other projects; the project's order | **The project's order** in Solution Explorer: compiled files in compile order, folders where their first file is, everything else after, by name; `Shapes.fsi` under `Shapes.fs`. A new build-host request, `items/move`, moves a `Compile` include before or after another. Move Up and Move Down (`Alt+Up`, `Alt+Down`) move a file with its signature file within its folder (`CompileOrder`); Add Above and Add Below open Add New Item and place the file. A new `Compile` include in an F# project goes after the last one, or before it in a program (its entry point), never sorted; `.fsi` files are included as `Compile`. |
| Items and properties | — | F# item templates (Source File, Class, Interface, Record, Signature File, Script File; the first five as `Compile` items) and F# property pages (nullness, the F# version, `WarnOn`, `OtherFlags`, tail calls). |

## Decision

- FsAutoComplete 0.85.3 and Fantomas 7.0.3, pinned in `FSharpTools` (workbench `FSharp/`), installed with `dotnet tool install --tool-path` with the user's consent, and started with `dotnet` from the SDK the IDE found, with `DOTNET_ROOT` set and Fantomas's tool path at the end of `PATH`.
- Trusted folders only, as for MSBuild.
- The bridge changes above apply to any server, not only FsAutoComplete.
- Compile order through `items/move`, in Solution Explorer and Add New Item.

## Consequences

- F# gets FsAutoComplete's features: completion with unopened namespaces, Quick Info with XML documentation, signature help, diagnostics with FSharpLint and the analyzers, navigation, rename, code fixes, inlay hints, signature and references CodeLens, semantic highlighting, Fantomas formatting with `.editorconfig`.
- Updating FsAutoComplete or Fantomas is a change of the two constants. An install keeps the versions it has until the constants change.
- F# → C# references need the C# project built. Building it automatically when FsAutoComplete reports the missing assembly is a candidate. `TODO(issue)`
- FsAutoComplete doesn't run in remote windows yet: it would need `dotnet tool install` on the remote machine. `TODO(issue)`
- Moving a folder in the compile order (all of its files) isn't offered; files move within their folder. `TODO(issue)`
- F# Interactive, scripts, Expecto tests, type providers and F# analyzers are M36b.
