# F#

Codalus IDE edits F# with [FsAutoComplete](https://github.com/ionide/FsAutoComplete), the language server Ionide uses, and formats it with [Fantomas](https://fsprojects.github.io/fantomas/). A solution can mix F# and C# projects.

## Installing the F# language service

The first time an F# file opens, Codalus IDE offers to install FsAutoComplete and Fantomas. It installs them once, into its cache folder, with `dotnet tool install`, so your NuGet sources and proxy apply. They take about 100 MB.
- **Settings:** **F# Language Service** (Tools → Options → F#, `fsharp.languageService`) asks first (`ask`), installs without asking (`install`), or never installs (`off`).
- **Installing later:** you can also install from the command palette (**Install the F# Language Service**) or Tools → Components….

FsAutoComplete loads the projects with MSBuild, so it runs only in trusted folders.

## Editing

`.fs`, `.fsi` and `.fsx` files get:
- **Completion,** including types and modules of namespaces that aren't opened yet (it adds the `open`). Setting: **Complete Unopened Namespaces**.
- **Quick Info,** with the signature and the XML documentation, and **signature help**.
- **Errors and warnings as you type,** and FSharpLint's suggestions (setting: **Linter**).
- **Hints:** unused `open`s and declarations, and names that can be simplified.
- **Semantic coloring.**
- **Navigation:** Go to Definition, Find All References, rename, the outline and folding.
- **Quick fixes** (`Ctrl+.`): adding missing match cases, record fields and interface members, and many more.
- **Inlay hints** for inferred types and parameter names (setting: **Inlay Hints**).
- **CodeLens:** each function's signature, and the number of references (settings: **Signature CodeLens**, **References CodeLens**).
- **Formatting:** Format Document with Fantomas, which reads the formatting settings from `.editorconfig` (`fsharp_*`, `max_line_length`, `indent_size`). A Fantomas the project has as a local tool (`dotnet tool install fantomas`), or that you've installed as a global tool, is used instead of Codalus IDE's.

**References to C# projects:** FsAutoComplete compiles only F#, so it reads a C# project an F# project references from its built assembly. Until you build the solution once, the C# project's types are reported as not defined.

## Compile order

In F#, a file can only use what the files before it define, so the order of an F# project's files matters. Solution Explorer shows an F# project's files in that order: the compile order, as the project file lists them.
- A folder sits where its first file is.
- A signature file (`Shapes.fsi`) is shown under its implementation (`Shapes.fs`), and moves with it.

To change the order, right-click a file:

| Command | Shortcut | What it does |
|---|---|---|
| **Move Up** | `Alt+Up` | The file moves one place up within its folder. |
| **Move Down** | `Alt+Down` | The file moves one place down within its folder. |
| **Add → Add Above…** | | Add New Item, with the new file placed before the selected one. |
| **Add → Add Below…** | | Add New Item, with the new file placed after the selected one. |

A file added any other way goes last, or, in a program, before the last file, which holds the entry point.

## Projects

- **Add New Item** offers F#'s Source File (a module), Class, Interface, Record, Signature File and Script File.
- **Properties** (`Alt+Enter` on the project): the Application page has F#'s settings:
  - Nullable (F# 9's nullness checking);
  - the F# language version;
  - warnings to turn on (`WarnOn`);
  - other compiler flags (`OtherFlags`).

  The Build page has **Tail calls**.
- **New Project** lists the F# templates and asks for the language when a template comes in several.

## F# Interactive

F# Interactive runs F# code as you send it, in a terminal of the integrated terminal named **F# Interactive** (`dotnet fsi`). It shows the value of each definition: `val squares: int list = [1; 4; 9]`.

| Command | Where | What it sends |
|---|---|---|
| **Send to F# Interactive** | `Ctrl+E, Ctrl+E` in an F# file; the editor's context menu | The selection, or the current line |
| **Send Line to F# Interactive** | `Alt+'`; the editor's context menu | The current line |
| **Send File to F# Interactive** | Command palette | A saved script with `#load`; any other file's text |
| **Send References to F# Interactive** | A project's context menu in Solution Explorer | Builds the project, then adds it and its references with `#r`, so its types can be used |
| **Reset F# Interactive** | Command palette | Starts it again: everything defined is gone |
| **F# Interactive** | View → Other Windows | Shows it, starting it if needed |

- **Errors** name the file and line the code came from: each submission carries a line directive.
- **Typing** directly into the terminal also works. End a submission with `;;`.
- **Options:** **F# Interactive Options** (`fsharp.fsiExtraParameters`) adds options to `dotnet fsi`, such as `--langversion:preview`.

## Scripts

`.fsx` files are checked like project files:
- `#load "Helpers.fsx"` makes what the loaded script defines known.
- `#r "nuget: Newtonsoft.Json, 13.0.3"` restores the package and makes its types known.

**Run Script**, in the editor's context menu of a script, runs the saved script with `dotnet fsi` in a terminal of its own.

## Type providers

Type providers work as you type: FsAutoComplete runs the provider (once the project's packages are restored), so the types and members it provides (FSharp.Data's `JsonProvider`, for example) complete and show in Quick Info. A type provider is code that runs while you edit, which is why the F# language service runs only in trusted folders.

## Analyzers

F# analyzers (built with [FSharp.Analyzers.SDK](https://github.com/ionide/FSharp.Analyzers.SDK)) report their findings as you type when **F# Analyzers** (`fsharp.analyzers`) is on. They're loaded from the folders in **F# Analyzers Path** (`fsharp.analyzersPath`, relative to the solution's folder; `packages/analyzers` and `analyzers` by default). Like type providers, they run only in trusted folders.

## Tests

The Test Explorer runs F# tests of xUnit, NUnit, MSTest and [Expecto](https://github.com/haf/expecto). Expecto needs its test adapter in the project (`YoloDev.Expecto.TestSdk` with `Microsoft.NET.Test.Sdk`).
- **Names:** an Expecto test list shows as a class, and each test in it as a method.
- **When they appear:** F# tests appear after the project is built (Run All builds it). They aren't found from the source beforehand.

## Debugging

F# programs debug like C# ones: breakpoints in `.fs` files, stepping, the call stack, locals (an F# list shows its `head` and `tail`) and data tips.

The debugger reads expressions as C#, in Watch and Immediate and in breakpoint conditions:
- Member paths work: `shapes.Head`, `order.Customer.Name`.
- For F#'s comparison operators, use C#'s: `==` for `=` and `!=` for `<>`. An option's `None` is `null`.
- Members F# adds to a type by augmentation, such as a list's `Length`, aren't found. Use the type's own members, or expand the value in Locals.
