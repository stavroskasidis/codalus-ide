# 0068. F# Interactive, scripts, tests and type providers

- **Status:** accepted (M36b, 2026-10-15)
- **Date:** 2026-10-15
- **Deciders:** maintainer (D10, D13), with the standing go-ahead to complete every milestone

## Context

M36a (ADR 0067) made F# editable through FsAutoComplete. The rest of M36 is:
- F# Interactive: send a line, a selection or a file; reset; show the values bound;
- `.fsx` scripts with `#r "nuget:"` and `#load`;
- Test Explorer support for Expecto (and xUnit and NUnit);
- F# analyzers;
- type providers under workspace trust;
- debugging.

The research for M36b found the following:
- **C# Interactive's window** (ADR 0015) is tied to `codalus-replhost`'s protocol: completion, tokens and "is the submission complete" are host requests, and its view model takes the host's service directly.
- **`dotnet fsi`** prints its own answers (`val squares: int list = [1; 4; 9]`, in color) and reads submissions ended by `;;`.
  - Its line editor (on by default) asks the terminal for the cursor position and moves it. With input written programmatically into a pseudo-terminal that fails ("The value must be greater than or equal to zero…").
  - With `--readline-` it reads plain lines; the pseudo-terminal's line discipline still edits a typed line.
  - It accepts line directives (`# 12 @"file.fsx"`), so its errors name the file and line they came from.
- **Scripts:** FsAutoComplete checks scripts by itself, `#load` and `#r "nuget:"` included (checked with Newtonsoft.Json).
- **Tests:**
  - The .NET test plugin's discovery and runs are VSTest's or Microsoft.Testing.Platform's, on the built assembly, so they don't depend on the language.
  - YoloDev.Expecto.TestSdk (MIT) is Expecto's VSTest adapter. Its 1.0.0 needs Expecto 9.x. It reports names as `<list>.<test>` (`Shapes.a rectangle's area is width times height`), which the Test Explorer shows as a class and a method.
  - F# tests aren't found from source: FsAutoComplete has no `codalus/testCandidates`.
- **Type providers:** FsAutoComplete runs a type provider's design-time code itself. FSharp.Data's `JsonProvider` gives `property High: decimal` once the package is restored, without a restart. Because FsAutoComplete runs only in trusted folders (ADR 0067), so do type providers.
- **Analyzers:** FsAutoComplete loads FSharp.Analyzers.SDK analyzers from folders (`enableAnalyzers`, `analyzersPath`).
- **Debugging:** netcoredbg debugs F# through portable PDBs.
  - Breakpoints in `.fs` files bind, F# lists show their `head` and `tail`, and member paths evaluate (`shapes.Head`).
  - Expressions are C#, and members F# adds by augmentation (a list's `Length`) aren't found.

## Options

| Question | Options | Verdict |
|---|---|---|
| F# Interactive's window | A second kind in C# Interactive's window, behind a new abstraction; `dotnet fsi` in the integrated terminal | **The integrated terminal** (as Ionide does): a terminal named "F# Interactive" running `dotnet fsi --readline-` (plus `fsharp.fsiExtraParameters`), in the solution's folder. fsi's own output shows the values. No new host, protocol or window. |
| Sending code | As typed; with line directives | **With a line directive** and `;;`: Send to F# Interactive (`Ctrl+E, Ctrl+E` in F# files, the editor's context menu) sends the selection or the line; Send Line (`Alt+'`); Send File (`#load` for a saved script, else the text); Send References (Solution Explorer: builds the project, then `#r` its output and its compile references, without the framework's reference packs); Reset (a new session). C# Interactive's Send stays for other files. |
| Running scripts | — | **Run Script:** `dotnet fsi <script>` in a terminal of its own (the saved file). |
| Expecto | Our own adapter; YoloDev's | **YoloDev's**, in the project: the test plugin needs nothing new. The fixture pins Expecto 9.0.4 with YoloDev.Expecto.TestSdk 1.0.0. |
| Analyzers | — | **Settings passed to FsAutoComplete:** `fsharp.analyzers` (off by default) and `fsharp.analyzersPath`. They run in FsAutoComplete, so trusted folders only. |
| Debugging | An F# expression evaluator; document | **Document,** as for Visual Basic (ADR 0066): netcoredbg's C# expressions. |

## Decision

- F# Interactive is a terminal session (`ShellHost.FSharpInteractive.cs`), with the commands and shortcuts above.
- Scripts, type providers and analyzers come from FsAutoComplete; Expecto comes through its VSTest adapter.
- Debugging F# is netcoredbg's, tested (`FSharpDebuggingTests`), with its gaps in the user guide.

## Consequences

- F# Interactive has no completion of its own in the terminal, and no line history (fsi's line editor is off). The editor, where code is written and sent from, has FsAutoComplete's.
- The F# Interactive window is a terminal tab: it can be split, searched and cleared like any terminal.
- F# tests appear after a build. Feeding FsAutoComplete's `fsharp/testDetected` notifications to the Test Explorer is a candidate. `TODO(issue)`
- Expecto names with dots split at the last dot (the test plugin's rule for names without a type). `TODO(issue)`
- F# expressions in Watch, Immediate and conditions are C#. Augmented members aren't found. `TODO(issue)`
