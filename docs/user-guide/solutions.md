# Solutions and projects

Codalus IDE opens `.sln` and `.slnx` solutions and SDK-style projects (`.csproj`, and the structure of `.fsproj` and `.vbproj`). Legacy, non-SDK-style project files are shown but not loaded.

## Solution Explorer

The tree shows the solution, its solution folders, projects, project folders and files (nested files, like `Form.Designer.cs`, sit under their parents). Folders come before files, each sorted by name with `_` before letters; a project's `Properties` (`My Project` in Visual Basic) and `wwwroot` folders come first. Each project also has its **Dependencies**:
- frameworks
- packages, each with the packages it brings in under it, and theirs under them (once the project is restored)
- projects
- assemblies
- analyzers, each with the files its source generators made in the project (expand it; click one to read it)

| To | Do |
|---|---|
| Open a file in a preview tab (replaced by the next preview) | Single-click it. |
| Keep a file open | Double-click it, or edit it. |
| Pin a tab to the left of its group | The pin beside the tab's close button (on the selected or hovered tab), or **Window → Pin or Unpin Tab**. A pinned tab shows its pin upright; click it to unpin. Pinned tabs take a row of their own above the group's other tabs; to keep them in one row, turn off **Show Pinned Tabs in a Separate Row** (`workbench.editor.pinnedTabsOnSeparateRow`, Appearance). |
| Create a file or folder | Use the context menu. New C# files get the right namespace. |
| Rename | Press `F2`. Renaming a project folder updates the solution and project references. |
| Delete | Press `Delete`. Files go to the OS trash. |
| Move or copy | Cut, Copy and Paste, or drag and drop. Moving a file into another project takes its Build Action and metadata along; dragging a linked file moves its link (Ctrl copies). Files dropped from the file manager are copied in. |
| Add files | **Add New Item** (`Ctrl+Shift+A`), **Add Existing Item** (`Shift+Alt+A`), or **Add Existing Item As Link**. |
| See what a code file declares | Expand a C# or Visual Basic file: its types, and under each type its members, come from the language service and follow your edits. Click one to go to it. Turn this off with Solution Explorer → Show Members Under Files (`solutionExplorer.showMembers`). |
| See files that aren't in the project | **Show All Files** on the Solution Explorer's toolbar: files on disk outside the project show dimmed. |
| Find the active document's file | **Sync with Active Document** (the toolbar's link button, `Ctrl+[`, `S`) selects it and scrolls it into view. **Track Active Item**, the toggle beside it (`solutionExplorer.trackActiveItem`, off by default), does it each time you switch documents. |
| Take a file out of the project, or put it back | **Exclude From Project** and **Include In Project** in the context menu. |
| See and change a file's Build Action, Copy to Output Directory or Custom Tool | **Properties** (`F4`). |
| Change the solution | Add Existing Project, New Solution Folder, Remove from Solution. |
| Add a project reference | Add Project Reference on a project. Remove one by deleting it under Dependencies. |
| Make a project the one F5 and Ctrl+F5 start | **Set as Startup Project** (the startup project is bold). |

Codalus IDE watches the files: changes made outside the IDE (a `git pull`, another editor) are picked up, and the affected projects are evaluated again.

## What a solution reopens with

Like the classic IDE's `.vs` folder, Codalus IDE keeps your own state for a solution in `.codalus/state/` next to it. The folder has one subfolder per solution file (`App.slnx/`, `App.sln/`, and `folder/` for a folder opened without a solution), so several solutions in one folder each keep their own state:
- `session.json`: the open documents (with their carets and pinned tabs) and the active one (the others come back as tabs behind it, and each makes its editor when you first show it); breakpoints, exception settings, Watch expressions and pinned data tips; the startup project or projects, launch profiles, devices and the ASP.NET Core environment; the solution configuration and platform; the target framework chosen for multi-targeted projects; the Solution Explorer's expanded nodes (Collapse All clears them); the Error List's filters and the Test Explorer's grouping.
- `bookmarks.json`: the solution's bookmarks.
- `solution.json`: the unloaded projects.

`.codalus/state/` holds its own `.gitignore` (`*`), so Git never shows it, even if your repository's `.gitignore` doesn't mention it. On Windows the `state` folder is hidden. Delete it to start the solution afresh. The shared files in `.codalus/` (`settings.json`, `tasks.json`, `launch.json`, `playlists/`) stay where they are, for you to commit.

Opening a solution writes nothing there, except to move in the state an earlier version kept elsewhere. The state is saved when you close the solution or the IDE, and bookmarks are saved when you change them. If the solution's folder can't be written (a read-only checkout or share), the state goes to the `solution-state` folder of your user data instead, and moves back next to the solution once the folder can be written. Remote windows don't keep this state yet.

## The Properties window

`F4` (View → Properties) shows the Solution Explorer's selection:
- **A file:** its **Build Action** (the item type: `Compile`, `None`, `Content`, `EmbeddedResource`, `AdditionalFiles`, and the project's own types), **Copy to Output Directory**, **Custom Tool** (the generator, such as `ResXFileCodeGenerator`) and its namespace, the link of a linked file, and every other metadata the project file sets. **Add Metadata** adds one.
- **A folder, a project, the solution or a dependency:** a summary, with links to the project's properties and the Configuration Manager.

As in the classic IDE, its toolbar has **Categorized** and **Alphabetical** (names with a parenthesis, such as **(Name)**, first), whose categories collapse with a click on their header, and **Property Pages**, which opens a project's properties, or the Configuration Manager for the solution (it is grayed for files and folders). The selected property's description is at the bottom: drag the line above it to resize it, or hide it with **Description** on a row's right-click menu, which also has **Reset**. A read-only value (a file's **Full Path**) can be selected and copied, and `Ctrl+C` (`⌘C` on macOS) copies the selected row's value. The keys are in [Keyboard](keyboard.md#the-properties-window); a form's components in the Windows Forms designer get the same grid, with editors ([Windows desktop apps](windows-desktop.md#properties-and-events)).

Values set in the project file are in bold. Changes are written the way the classic IDE writes them under SDK globbing: a file the globs already include gets an `Update` item for its metadata (`<None Update="notes.txt"><CopyToOutputDirectory>…`); a new Build Action is `<None Remove="notes.txt" />` plus `<Content Include="notes.txt" />`; choosing the value the project would have anyway removes the project's own setting. Plugins can add rows.

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

Packages are restored in the background when a trusted solution opens (setting **Projects → Restore on Open**). Project → Restore Packages restores again; the output is in View → Output → Restore. Opening skips the restore when every project's packages are up to date since its last restore; Output → Build Host says why it restored otherwise. Restores run in the solution's folder, so its `global.json` picks the .NET SDK, the one the IDE builds with, also for projects of another repository whose own `global.json` pins another. When a project fails the solution's restore (a Blazor WebAssembly app without its workload, say), the others are restored together without it, and Output → Restore shows its error. When a project's packages can't be read (restored outside the IDE with another SDK), the design-time build fails, and Codalus restores once more with the solution's SDK and builds the project again. To add, update and remove packages, see [NuGet packages and .NET tools](packages.md).

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

- **Unload Project** (a project's context menu) stops loading, analyzing and building it; it shows dimmed as *(unloaded)*. **Reload Project** brings it back. Unloaded projects are remembered per user in `.codalus/state/` (see [What a solution reopens with](#what-a-solution-reopens-with)).
- **Save As Solution Filter…** (File menu, or the solution's context menu) writes the loaded projects to a `.slnf`. Opening a `.slnf` opens its solution with only those projects loaded.

## .NET SDKs

A solution loads and builds with the same SDK as `dotnet build` in its folder: the one its `global.json` asks for, or else the newest one installed, previews included. Codalus itself runs on .NET 11, and loads older SDKs (a `global.json` that pins 10.0.401, say) on it. A preview SDK of a newer .NET (12) gets that .NET's runtime.

**.NET SDKs…** (Project menu) shows:
- the SDK the solution builds with, and the `global.json` that chose it;
- the installed SDKs, each with its support status: supported until a date, out of support since a date, or a preview;
- the installed workloads;
- a warning when Codalus can't load the SDK the solution asks for, and which SDK it uses instead: an SDK whose runtime is missing, an SDK below the build host's floor (2.1.200), or a global.json that asks for an SDK that isn't installed. The same warning goes to the Output window (Build Host).

SDKs from 2.1.200 to 5.0, pinned in global.json, build in the build host's own process; projects of every .NET version from .NET Core 2.0 work with the current SDK. [Older .NET versions](dotnet-versions.md) has the details.

**Use This SDK** pins the selected SDK in `global.json` (with the roll-forward policy you choose) and reloads the solution with it. **Remove Pin** takes the version out again.

**.NET 11 and C# 15:** the C# language service uses the .NET 11 SDK's compiler, so C# 15 (unions, collection expression arguments, labeled `break` and `continue`) shows no false errors, and the .NET 11 SDK's Razor generator runs in the editor.

**Generators built for a newer compiler:** when a newer SDK's analyzers or source generators (the Razor generator of a .NET 12 preview, say) need a newer C# compiler than Codalus has, Codalus loads the same ones from the newest other installed SDK that it can load, and the Output window (Language Services) says so.

## New projects and solutions

**File → New → Project…** (`Ctrl+Shift+N`, `⌘⇧N` on macOS), the start window's **Create a new project** and Solution Explorer's **Add → New Project…** open New Project: one window with three pages, **Back** and **Next** (**Create** on the last page). It's a window of its own over the IDE, which stays usable behind it; it asks everything it needs on its own pages.

### Create a new project

- **Recent project templates** on the left, the ones you used last first. Right-click one to **Pin to List** (pinned ones stay at the top) or **Remove from List** (`Delete` removes the selected one).
- **Search for templates** (`Alt+S`) matches template names first, then their short names (`console`, `webapi`), then descriptions and tags; the matched letters are bold. `Down` goes from the search box into the list, and `Esc` clears the search.
- The **language**, **platform** and **project type** filters narrow the list; **Clear all** resets them and the search. They're remembered.
- Each template shows its icon, its name, its description and its tags: its language, the platforms its projects run on and its kind of project. On Linux and macOS a template whose projects run only on Windows (WPF, Windows Forms, .NET Framework) has a **Windows only** tag: its projects build there but run on Windows.
- A template comes in each language it exists in (Console App in C#, F# and Visual Basic are three rows).
- Known template packs that aren't installed (the Avalonia and CoreWCF templates) are rows marked **Not installed**: select one and **Install** installs it from nuget.org with `dotnet new install`, in place, then selects its first template. **Install more templates…** opens Tools → Templates.

`Enter`, a double-click or **Next** goes on with the selected template.

### Configure your new project

- **Project name** starts as the template's suggestion (`ConsoleApp1`), made unique in the location.
- **Location**: type a folder, choose one of your recent locations from the field's drop-down (`Alt+Down`), or **Browse…**. It starts in your last location, else Environment → Templates → Default Location (`templates.defaultLocation`), else a `source/repos` folder in your home folder. `~` and environment variables are expanded.
- **Solution**, with a solution or folder open: **Create new solution** (the default) or **Add to solution** (**Add to folder** with a folder open).
- **Solution name** follows the project name until you change it.
- **Place solution and project in the same directory** puts the solution file in the project's folder, named after the project (the solution name box shows that name while it's checked).
- **Trust this location**: projects in a trusted folder are built and their packages restored ([Opening a solution](getting-started.md#opening-a-solution)). It's unchecked for a new folder, so the new solution opens showing only its structure until you trust it; check it to trust the folder once Create has made the project (a Create that fails or that you cancel trusts nothing). Under a folder you trust already, it's checked and can't be changed.
- **Project will be created in** shows the project's folder. A name Windows can't use (`CON`, characters like `?`), a project folder that isn't empty, an existing solution file, a folder that can't be read, or a path longer than Windows allows is shown in red and stops **Next**.

Solution Explorer's **Add → New Project…** (on the solution or a solution folder, or Project → Add New Project…) adds the new project to the open solution, in the solution folder that was selected, with its location starting in the solution's folder. New Project's window doesn't block the main window: if you close that solution or open another one while it's open, Add New Project closes, and New Project no longer offers to add to it.

### Additional information

- **Framework** lists the frameworks the template can target with the installed SDKs, each with its support: **Long Term Support**, **Standard Term Support**, **Preview** or **Out of support**. It starts on the newest released one, or the one you chose last for that template.
- The template's own options follow, as the template describes them for an IDE: **Include sample pages**, **Use controllers**, **Do not use top-level statements**, **Interactive render mode**… An option that depends on another is disabled until it applies. Some templates have different options per framework (xUnit's **xUnit.net version** and **Test runner** come with .NET 11.0), so the page changes with the framework.
- **Authentication type** (None, Individual Accounts, Microsoft identity platform, Windows) where the template has it. Individual Accounts and Microsoft identity platform need HTTPS, so **Configure for HTTPS** stays checked; Individual Accounts uses LocalDB on Windows and SQLite elsewhere; Microsoft identity platform gets placeholder settings in `appsettings.json` to fill in with your app registration.
- **Enable container support** adds a Dockerfile ([Containers](containers.md)), and **Enlist in Aspire orchestration** adds `<Solution>.AppHost` and `<Solution>.ServiceDefaults` projects (or uses the solution's own AppHost when adding a project), references them and adds the project to the AppHost; it installs the Aspire templates from nuget.org first if they're missing. With **Place solution and project in the same directory**, the two projects go beside the project's folder rather than inside it, where the project would compile their code. Both need a trusted location.
- A template with nothing but a framework says so; one with nothing to ask (Blank Solution) has no third page.
- If the location's `global.json` selects an SDK that can't build the chosen framework, a warning says so.

**Create** writes the solution and the project with your default .NET SDK (`dotnet new`, without restoring), closes New Project and opens the solution with the project and the template's start file (`Program.cs`). A trusted solution restores its packages when it opens (Restore on open). If Create fails, the window stays open with the error (a `dotnet` step that runs past three minutes is stopped and reported, not taken for a cancel), and **Show Output** shows `dotnet new`'s output. **Back** and **Next** keep what you entered, the framework and options included.

**File → New → Solution…** opens New Project on **Blank Solution**: a solution name, a location and trust.

### Keys

| Key | Does |
|---|---|
| `Alt+Right`, `Enter` | Next, or Create on the last page (`Enter` in the search goes on with the best match of what you typed) |
| `Alt+Left` | Back (from the first page, back to the start window when it came from there) |
| `⌘]`, `⌘[` (macOS) | Next or Create, and Back. `⌥→` and `⌥←` do the same outside text boxes; in a text box they move by word, as everywhere on macOS |
| `Esc` | Close (clears the search first) |
| `Alt+S`, `Down` | The search, then the templates |
| `Delete` | Remove the selected recent template |
| `Alt` + the underlined letter | The field or button: Pro**j**ect name, **L**ocation, S**o**lution, Solution na**m**e, **T**rust, **F**ramework, **B**ack, **N**ext, **C**reate |

New Project remembers your recent templates and locations, the filters, the framework per template and the options a template asks to remember. Templates you made with **Export Template** are listed with the others.
