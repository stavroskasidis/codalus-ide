# 0033. Project items, templates, folder mode and external tools

- **Status:** accepted (M26a plan, 2026-10-11; the maintainer's goal of 2026-10-11: complete every milestone)
- **Date:** 2026-10-11
- **Deciders:** maintainer

## Context

M26 is the rest of the classic IDE's project and build features. It is split in two (roadmap rule 5): **M26a** is the project system (this ADR), **M26b** build depth (ADRs 0034 and 0035). M26a asks for:
- a Properties window (F4) for the Solution Explorer's selection, whose edits of SDK-style globbed items write `Update` items, never duplicate `Include` items;
- Show All Files, and Include In Project / Exclude From Project, which write `Remove` items under globbing;
- linked files, and drag and drop between projects with the project files updated;
- Add New Item with item templates (`dotnet new` and a built-in set), Export Template and a template management page;
- folder mode (the classic IDE's Open Folder) with per-folder build and launch configuration in `.daedalus/`;
- External Tools with argument macros.

Constraints: MSBuild runs only in the build host, only for trusted folders (ADR 0004); edits keep the project file's formatting; the shell is Native AOT; built-in features register like plugins.

## Decisions

**Items are edited by evaluating the result**
- The build host owns item edits (`items/*` in `docs/project-protocol.md`). An edit is applied to the project file's construction model, the project is **evaluated again in memory** with the edited XML, and the edit is adjusted until the evaluated items are what was asked. That is how globbing is handled without special cases: a file that a glob already includes gets an `Update` item for its metadata; a file a glob would include as the wrong item type gets `<Old Remove>` plus `<New Include>`; a metadata value that equals what the project would evaluate to without its own definition removes that definition instead (the minimal change, as for properties in ADR 0013).
- The shapes are the ones the classic IDE writes: metadata as child elements of a new item (`<None Update="a.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>`), existing metadata changed in place (attribute or element, as written); new items go into the first unconditioned `ItemGroup` with items of the same type and operation, else a new `ItemGroup` at the end.
- **Exclude From Project** removes the file's explicit `Include` and `Update` items and, while a glob still includes it, adds `<Type Remove="file" />` for each item type that includes it. A folder gets `Remove="Folder\**"` for each default item type of the project (`Compile`, `EmbeddedResource`, `None`, and `Content` where the SDK globs it), as the classic IDE writes it. **Include In Project** removes exact `Remove` items, and adds an `Include` only when a wider `Remove` or a disabled glob still excludes the file; the item type is the one the SDK's globs would give it (`$(DefaultLanguageSourceExtension)` → `Compile`, `.resx` → `EmbeddedResource`, else `None`).
- **Linked files** are `<Compile Include="..\Shared\A.cs" Link="Folder\A.cs" />` (the item type by extension). Add Existing Item copies files into the folder (globs pick them up) or, with Add As Link, adds links.
- **Moves between projects:** `fs/move` moves the file on disk, then carries each explicit item of the source project that named it (`EmbeddedResource Include`, `None Update` with metadata, …) to the target project with the same item edit as above, and removes it from the source. A copy carries without removing. A linked item dragged to another project adds the link there (a move also removes it from the source).
- **Show All Files** lists the project folder through the host (`fs/listTree`, capped at 20,000 entries; `.git`, `.vs` and `node_modules` are skipped). Files on disk that aren't items show dimmed, with Include In Project.

**The Properties window is a shell tool window over item and project data**
- F4 (and View → Properties Window) shows the Solution Explorer's selection. Files: Build Action (the item type, from `AvailableItemName` and the common types), Copy to Output Directory, Custom Tool (`Generator`), Custom Tool Namespace, the link, and every other metadata the project file sets, editable, plus Add Metadata. Folders, projects, the solution and dependencies show a read-only summary with links to the property pages and the Configuration Manager.
- Plugins add read-only rows through a declarative `contributes.propertyProviders` (a command that returns rows), plugin API 0.2.8.

**Templates: the built-in set in the shell, `dotnet new` through the CLI**
- **Add New Item** is a document: a search box, categories, the templates for the project's language and kind (Web, MAUI, test projects; "Show all" lifts the filter), a preview and a name. Its templates are a built-in set (class, interface, record, struct, enum, text, JSON, XML, resources, text template, Markdown, `appsettings.json`, editorconfig) and the `dotnet new` item templates (`dotnet new list --type item --ignore-constraints`, parsed by the dashed header rule, cached per SDK). A built-in template with the same short name as a `dotnet new` one wins: it needs no trust, works offline and previews its content.
- Built-in templates are generated in the workbench (namespace from `RootNamespace` and the folder, the type name from the file name) and created with `fs/createFile`; templates that set item metadata (a `.tt`'s `Generator`) apply it with `items/set`.
- A `dotnet new` item template is instantiated by the build host (`templates/instantiate`), only in trusted folders: its constraints (project capabilities) evaluate the project with MSBuild. The created files are found by comparing the folder before and after.
- **Listing, installing, updating and uninstalling** template packages runs the `dotnet` CLI from the workbench (`TemplateService`), in a neutral folder: it runs no workspace code, and the template manager works without a solution. `DAEDALUS_TEMPLATE_HIVE` (tests) passes `--debug:custom-hive`, so tests never touch the user's templates.
- **New Project** (project templates) is the shell's three-page wizard since 0.6.0, over the same `TemplateService` in the same neutral folder: [ADR 0082](0082-new-project-wizard.md).
- **Export Template** copies a project (without `bin`, `obj`, `.vs`, `.daedalus` and `*.user`) or items into `<user data>/templates/<short name>/`, writes `.template.config/template.json` (`sourceName` the project or item name; for items a `namespace` parameter replacing the original namespace), and installs it with `dotnet new install <folder>`.

**Folder mode: a folder workspace with an in-memory solution**
- **Open Folder** opens a folder as the workspace. `solution/open` accepts a folder: the host answers with format `folder`, the folder as `path`, and the projects the user loaded (`.daedalus/folder.json`); `folder/discover` lists the project files under it, and `folder/setProjects` changes the loaded ones. Loaded projects are evaluated, built (through a generated `.daedalus/folder.slnx`) and served by the language host like a solution's; trust works as for solutions.
- The Solution Explorer has two views, switched like the classic IDE's: the **folder view** (the disk tree, discovered projects marked, with Load Project, Load All Projects and Open Project) and the **solution view** of the loaded projects. Search, Git and the terminal use the folder; file operations work in both views; the host's watcher reports structural changes (`fs/changed`).
- **Tasks and launch configurations** live in `.daedalus/tasks.json` and `.daedalus/launch.json` (VS Code's shapes, the equivalents of `tasks.vs.json` and `launch.vs.json`): tasks are a command with arguments, a working directory and environment, a `group` (`build`, `test`), run in the Output window or a terminal; `${workspaceFolder}`, `${file}`, `${fileDirname}` and `${env:NAME}` expand. In folder mode Build runs the default build task (or builds the loaded projects), and Run and Debug use the selected launch configuration: `type: "project"` (a project, as a startup project) or `type: "program"` (an executable or a `.dll`, debugged with netcoredbg).
- `SolutionInfo.Directory` (not a wire field) is the workspace folder for every format, so features that used the solution file's folder work in folder mode.

**External Tools: user commands with macros**
- Tools are kept in `<user data>/config/externalTools.json`: title, command, arguments, initial directory, use the Output window, prompt for arguments, and a stable id. The External Tools document edits, orders and tests them.
- Each tool is a command `externalTools.run.<id>` in the Tools menu (an "External Tools" group) and the palette, so it can have a key binding.
- Macros: `$(ItemPath)`, `$(ItemDir)`, `$(ItemFileName)`, `$(ItemExt)`, `$(ProjectDir)`, `$(ProjectFileName)`, `$(ProjectPath)`, `$(SolutionDir)`, `$(SolutionFileName)`, `$(TargetPath)`, `$(TargetDir)`, `$(TargetName)`, `$(TargetExt)`, `$(BinDir)`, `$(CurLine)`, `$(CurCol)`, `$(CurText)` and `$(SelText)`; plugins add more through `contributes.externalToolMacros` (a command returning the value), plugin API 0.2.8.
- With "Use Output window" the tool's output goes to an "External Tools" channel; otherwise it starts on its own (GUI tools). Tools run as the user's own commands: no trust prompt, like the terminal.

## Consequences

- Item edits rely on MSBuild's own evaluation, so `Directory.Build.props` globs, `EnableDefaultItems=false` and custom item types behave as in a build. Every edit costs one or two in-memory evaluations of one project (tens of milliseconds).
- Plugin API 0.2.8 (additive): `contributes.propertyProviders` and `contributes.externalToolMacros`; M26b adds `contributes.customTools`.
- Folder mode reuses the solution machinery, so loaded projects get every solution feature; a folder without loaded projects still has search, Git, the terminal, tasks and launch configurations.
- `TODO(issue)`, not in M26a: multi-selection in the Solution Explorer (edits act on one item); `LinkBase` for linked globs; the classic IDE's `.vstemplate` item templates; CMake and other non-.NET folder workflows.

## Amendment (2026-10-30): Show All Files lists a folder that isn't in the project once it's expanded

On a large solution, Show All Files listed every loaded project's whole folder again, one project at a time, at every folder change, build and evaluation, `bin` and `obj` included, and each listing rebuilt the project's subtree on the UI thread (M56, [checkpoint](../checkpoints/m56.md)).
- **As the classic IDE does,** a project's listing goes into the folders that hold its items and the folders that are expanded. `bin`, `obj` and the other folders that aren't in the project show at once, dimmed, and what is in them is listed once they're expanded ("Loading…" until then), then again when they change. `fs/listTree` takes the folders to go into (`folders`, optional; without it, everything, as before).
- **Only the projects a change concerns are listed again:** for a folder that changed, the projects whose folders hold it; after a build, the project built and the projects it references; after an evaluation, that project. The listings run off the UI thread, four at a time, and a listing equal to the last one keeps the project's subtree.
- **The tree's build touches no disk:** the build host marks the analyzers that aren't on disk in the compiler inputs it sends (`missingAnalyzers`, optional), so a host on another machine reports its own disk.
- The folder view still lists the whole workspace, and skips the rebuild when the listing didn't change. `TODO(issue)`: list it lazily too.

