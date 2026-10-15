# Solutions and projects

Codalus IDE opens `.sln` and `.slnx` solutions and SDK-style projects (`.csproj`, and the structure of `.fsproj` and `.vbproj`). Legacy, non-SDK-style project files are shown but not loaded.

## Solution Explorer

The tree shows the solution, its solution folders, projects, project folders and files (nested files, like `Form.Designer.cs`, sit under their parents). Each project also has its **Dependencies**:
- frameworks
- packages, each with the packages it brings in under it, and theirs under them (once the project is restored)
- projects
- assemblies
- analyzers

| To | Do |
|---|---|
| Open a file in a preview tab (replaced by the next preview) | Single-click it. |
| Keep a file open | Double-click it, or edit it. |
| Create a file or folder | Use the context menu. New C# files get the right namespace. |
| Rename | Press `F2`. Renaming a project folder updates the solution and project references. |
| Delete | Press `Delete`. Files go to the OS trash. |
| Move or copy | Cut, Copy and Paste, or drag and drop. Moving a file into another project takes its Build Action and metadata along; dragging a linked file moves its link (Ctrl copies). Files dropped from the file manager are copied in. |
| Add files | **Add New Item** (`Ctrl+Shift+A`), **Add Existing Item** (`Shift+Alt+A`), or **Add Existing Item As Link**. |
| See files that aren't in the project | **Show All Files** on the Solution Explorer's toolbar: files on disk outside the project show dimmed. |
| Take a file out of the project, or put it back | **Exclude From Project** and **Include In Project** in the context menu. |
| See and change a file's Build Action, Copy to Output Directory or Custom Tool | **Properties** (`F4`). |
| Change the solution | Add Existing Project, New Solution Folder, Remove from Solution. |
| Add a project reference | Add Project Reference on a project. Remove one by deleting it under Dependencies. |
| Make a project the one F5 and Ctrl+F5 start | **Set as Startup Project** (the startup project is bold). |

Codalus IDE watches the files: changes made outside the IDE (a `git pull`, another editor) are picked up, and the affected projects are evaluated again.

## The Properties window

`F4` (View → Properties) shows the Solution Explorer's selection:
- **A file:** its **Build Action** (the item type: `Compile`, `None`, `Content`, `EmbeddedResource`, `AdditionalFiles`, and the project's own types), **Copy to Output Directory**, **Custom Tool** (the generator, such as `ResXFileCodeGenerator`) and its namespace, the link of a linked file, and every other metadata the project file sets. **Add Metadata** adds one.
- **A folder, a project, the solution or a dependency:** a summary, with links to the project's properties and the Configuration Manager.

Values set in the project file are in bold. Changes are written the way Visual Studio writes them under SDK globbing: a file the globs already include gets an `Update` item for its metadata (`<None Update="notes.txt"><CopyToOutputDirectory>…`); a new Build Action is `<None Remove="notes.txt" />` plus `<Content Include="notes.txt" />`; choosing the value the project would have anyway removes the project's own setting. Plugins can add rows.

## Show All Files, Include and Exclude

**Show All Files** adds the files and folders on disk that aren't in their project, dimmed (including `bin` and `obj`). **Exclude From Project** writes a `Remove` item (`<Compile Remove="Old.cs" />`; for a folder, `Remove="Folder\**"` for each kind of default item), and **Include In Project** removes it again, or adds an `Include` when a wider `Remove` still excludes the file.

## Adding items and templates

- **Add New Item** (`Ctrl+Shift+A`, Project or the context menu) lists item templates for the project's language and kind, with a search box, categories and a preview: Codalus's own (class, interface, record, struct, enum, text, JSON, XML, Markdown, resources, text template, app settings, HTML, CSS, JavaScript and TypeScript) and the `dotnet new` item templates installed on the computer. Web, MAUI and test templates show for those projects; **Show templates for every kind of project** shows them all. New C# files get the folder's namespace. `dotnet new` templates run only in trusted folders.
- **Add Existing Item** copies files into the selected folder; **Add Existing Item As Link** adds links to them instead (`<Compile Include="..\Shared\A.cs" Link="Shared\A.cs" />`).
- **Export Template** (Project menu) makes a project, or the selected file, into a `dotnet new` template in your templates folder and installs it: **New Project** and **Add New Item** offer it from then on.
- **Tools → Templates** lists the installed template packages: install one (an id, `id@version`, a `.nupkg` or a folder), uninstall, check for updates and update them.

## Opening a folder

**File → Open Folder** (`Ctrl+Shift+Alt+O`) opens a folder as it is, with or without a solution: the Solution Explorer shows the **folder view**, the files on disk, with project files marked "(not loaded)". Search, Git, the terminal and the editor work on the folder. `codalus <folder>` opens the folder's solution when it has exactly one, and the folder otherwise.

- **Load Project** (on a project file) or **Load All Projects** (on the folder) loads projects into an in-memory solution: they are evaluated, restored, built and get C# features like a solution's. **Switch Views** toggles between the folder view and the solution view. **Open Project** opens one project on its own instead. The loaded projects are remembered in `.codalus/folder.json`.
- **Tasks:** `.codalus/tasks.json` (Build → Configure Tasks) holds commands to run, in VS Code's format: `label`, `type` (`process` or `shell`), `command`, `args`, `options.cwd` and `options.env`, and `group` (`build` or `test`, with `isDefault`). Build → Run Task runs one, with its output in Output → Tasks. In a folder, **Build** runs the default build task when there is one, and builds the loaded projects otherwise.
- **Launch configurations:** `.codalus/launch.json` (Debug → Configure Launch): `name`, `type` (`project`, with `project`; or `program`, with `program`: an executable or a `.dll`), `args`, `cwd`, `env` and `preLaunchTask`. The toolbar's startup list shows them; **Start** (F5) debugs and **Start Without Debugging** (Ctrl+F5) runs the selected one.
- Variables: `${workspaceFolder}`, `${workspaceFolderBasename}`, `${file}`, `${fileDirname}`, `${fileBasename}`, `${fileBasenameNoExtension}`, `${fileExtname}`, `${relativeFile}`, `${lineNumber}`, `${selectedText}` and `${env:NAME}`.

## External tools

**Tools → External Tools** adds your own commands to the Tools menu and the command palette (they can have key bindings: search "External Tools" in Keyboard Shortcuts). Each has a title, a command, arguments and an initial directory, which may use macros such as `$(ItemPath)`, `$(ItemDir)`, `$(ProjectDir)`, `$(SolutionDir)`, `$(TargetPath)`, `$(CurLine)` and `$(SelText)` (the editor lists them all), and whether its output goes to Output → External Tools and whether to ask for the arguments first. They are kept in `externalTools.json` in your settings folder.

## Configurations and target frameworks

- The status bar shows the solution configuration (`Debug | Any CPU`). Click it to switch. Projects are evaluated again with the configuration they map to.
- For a project that targets several frameworks, the editor's project-context picker (top of the editor) chooses which framework the language service uses.

## Restore

Packages are restored in the background when a trusted solution opens (setting **Projects → Restore on Open**). Project → Restore Packages restores again; the output is in View → Output → Restore. To add, update and remove packages, see [NuGet packages and .NET tools](packages.md).

## Project properties

**Properties** (a project's context menu, **Alt+Enter** in the Solution Explorer, or Project → Project Properties) opens the project's property pages:
- **Application**: target frameworks, output type, assembly name, default namespace, nullable, implicit usings, language version
- **Build**: conditional compilation symbols, platform target, optimization, warnings and errors, debug symbols
- **Package**: what `dotnet pack` writes (id, version, authors, license, README, icon, symbols)
- **Code Analysis**: analysis level and mode, code style enforcement
- **Signing**: strong naming
- **Debug**: the launch profiles of `Properties/launchSettings.json` (what F5 and Ctrl+F5 start): arguments, working directory, environment variables, app URL and browser

Each property shows **where its value comes from**: set in the project file (with the line), inherited from an imported file such as `Directory.Build.props`, or the SDK's default. Click it to open that file. Changing a value writes the smallest possible edit to the project file; **Reset** removes the project's value, so the inherited or default one applies again. Choose a configuration (Debug, Release) at the top to change per-configuration properties (symbols, optimization, debug symbols, platform target) for that configuration only.

## References

**Reference Manager…** (a project's context menu) adds and removes:
- project references (tick the projects; a filter narrows the list)
- assembly references (a `.dll`, written with a relative `HintPath`)
- analyzers (an analyzer or source generator `.dll`)

Packages are managed in the [NuGet Package Manager](packages.md).

## Configurations, dependencies and build order

- **Configuration Manager…** (Build menu, or the solution's context menu): for each solution configuration and platform, which configuration and platform of each project builds, and whether it builds at all. Add and remove solution configurations and platforms; **Make Active** switches to the shown one.
- **Project Dependencies…**: projects a project waits for, besides those it references, and the resulting build order.

## Unloading projects and solution filters

- **Unload Project** (a project's context menu) stops loading, analyzing and building it; it shows dimmed as *(unloaded)*. **Reload Project** brings it back. Unloaded projects are remembered per user in `.codalus/solution.json`.
- **Save As Solution Filter…** (File menu, or the solution's context menu) writes the loaded projects to a `.slnf`. Opening a `.slnf` opens its solution with only those projects loaded.

## .NET SDKs

A solution loads and builds with the same SDK as `dotnet build` in its folder: the one its `global.json` asks for, or else the newest one installed, previews included. A .NET 11 solution gets the .NET 11 SDK, while Codalus itself runs on .NET 10.

**.NET SDKs…** (Project menu) shows:
- the SDK the solution builds with, and the `global.json` that chose it;
- the installed SDKs, each with its support status: supported until a date, out of support since a date, or a preview;
- the installed workloads;
- a warning when Codalus can't load the SDK the solution asks for, and which SDK it uses instead: an SDK whose runtime is missing, an SDK below the build host's floor (2.1.200), or a global.json that asks for an SDK that isn't installed. The same warning goes to the Output window (Build Host).

SDKs from 2.1.200 to 5.0, pinned in global.json, build in the build host's own process; projects of every .NET version from .NET Core 2.0 work with the current SDK. [Older .NET versions](dotnet-versions.md) has the details.

**Use This SDK** pins the selected SDK in `global.json` (with the roll-forward policy you choose) and reloads the solution with it. **Remove Pin** takes the version out again.

**Known gaps with .NET 11** (until Codalus moves to .NET 11 itself, when .NET 11 is released):
- **Razor:** the .NET 11 SDK's Razor generator needs a newer C# compiler than Codalus has. Codalus uses the Razor generator of the .NET 10 SDK instead, also for `net11.0` projects, so keep the .NET 10 SDK installed. The Output window (Language Services) says which one is used.
- **C# 15:** new C# 15 syntax shows errors in the editor, although it builds.

## New projects and solutions

The built-in **Templates** plugin creates projects and solutions with `dotnet new` (File → New Project or New Solution, or the command palette).

New Project asks for the template, the location, the name and, for templates that offer one, the target framework. The frameworks are those of the SDK the location uses, the template's default first. `dotnet new` runs in the location, so a `global.json` there picks the SDK.
