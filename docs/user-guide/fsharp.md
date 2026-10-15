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
