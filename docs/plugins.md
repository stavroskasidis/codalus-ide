# Writing plugins

Plugins extend Codalus IDE with commands, menus, key bindings, settings, tool windows, notifications, output channels and status bar items. This guide walks through the sample in [`samples/plugins/HelloWorld`](../samples/plugins/HelloWorld). The design and its reasons are in [ADR 0003](adr/0003-plugin-api.md), and the wire protocol is in [plugin-protocol.md](plugin-protocol.md).

**API version 1.1.0** ([ADR 0058](adr/0058-plugin-api-1-0.md)). The API is frozen: minor releases (1.x) only add, and a member goes away only in a major release, after it has been marked obsolete (warning `CODALUS0001`) for at least two minor releases. Ask for `^1.0.0`, or `^1.1.0` when you use what 1.1 added. See the [changelog](#changelog).

## How plugins run

- A plugin is a .NET 10 class library (`net10.0`; the API's packages target it, so the .NET 10 SDK builds plugins). It runs in a **plugin host** process (`codalus-pluginhost`, on .NET 11 since M30b), never in the IDE process, so a slow or crashing plugin can't freeze the UI. Built-in plugins share one host, and third-party plugins share another. A plugin can ask for a host of its own with `"isolation": "dedicated"`.
- Everything a user can see before your code runs comes from the **manifest** (`codalus-plugin.json`): commands in the palette and menus, key bindings, tool windows and settings, including the Settings UI.
- Your code is loaded on the first **activation event**: a contributed command is run, a contributed tool window is opened, a setting changes, or startup. Until then no host process runs at all.
- If a host crashes, Codalus restarts it and reactivates its plugins, and the user is notified. If it keeps crashing, the host is stopped for the session and its plugins are marked failed.

## Project setup

The quickest start is the template (since M22b):

```bash
dotnet new install Codalus.Templates
dotnet new codalus-plugin -n Contoso.Greeter --publisher contoso
```

It makes `Contoso.Greeter.slnx` with the plugin (`src/Contoso.Greeter`: the manifest, a command, a setting and a status bar item) and its unit tests (`tests/Contoso.Greeter.Tests`, on [`Codalus.Plugins.Testing`](#testing-plugins); `--skipTests` leaves them out). The plugin's id is `<publisher>.<name>` in lower case. Until the packages are on nuget.org (M22c), install the template from a clone (`dotnet new install src/Codalus.Templates/content/CodalusPlugin`) and pack the two API packages into a local feed (`dotnet pack src/Codalus.Plugins.Abstractions -o feed`, and `Codalus.Plugins.Testing`).

By hand, a plugin project is:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- The host provides the API at runtime, so it is not copied next to the plugin. -->
    <PackageReference Include="Codalus.Plugins.Abstractions" Version="1.0.0" ExcludeAssets="runtime" />
  </ItemGroup>
  <ItemGroup>
    <None Update="codalus-plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

The `Codalus.Plugins.Abstractions` package isn't on nuget.org yet (it comes with the marketplace, M22c). Until then, reference the project directly, as the samples do. Every type and member is in the [API reference](user-guide/plugin-api.md).

## The manifest

```json
{
  "id": "codalus-samples.hello",
  "version": "1.0.0",
  "displayName": "Hello World",
  "publisher": "codalus-samples",
  "engines": { "codalus": "^1.0.0" },
  "entry": { "runtime": "dotnet", "assembly": "HelloWorld.dll" },
  "activationEvents": [ "onSettingChanged:hello.announce" ],
  "contributes": {
    "commands": [ { "id": "hello.sayHello", "title": "Say Hello", "category": "Hello" } ],
    "menus": {
      "menuBar/tools": [ { "command": "hello.sayHello", "group": "plugins", "order": 1 } ],
      "toolWindow/title": [ { "command": "hello.refresh", "when": "view == hello.view" } ]
    },
    "keybindings": [ { "key": "ctrl+alt+h", "mac": "cmd+alt+h", "command": "hello.sayHello" } ],
    "toolWindows": [ { "id": "hello.view", "title": "Hello", "location": "right", "kind": "tree", "icon": "info" } ],
    "settings": {
      "title": "Hello World",
      "properties": {
        "hello.greeting": { "type": "string", "default": "Hello", "description": "The greeting to use." },
        "hello.times": { "type": "integer", "default": 1, "minimum": 1, "maximum": 5 }
      }
    }
  }
}
```

| Field | Meaning |
|---|---|
| `id` | `publisher.name`, lower case. Unique. |
| `version` | SemVer. |
| `engines.codalus` | An npm-style range over the **plugin API version** (`^0.1.0`, `~0.1.2`, `>=0.1.0 <0.3.0`, `0.1.x`, `a \|\| b`). Plugins whose range doesn't include the running API are not loaded; the user is told why. |
| `entry` | `runtime` is `dotnet`; `assembly` is the plugin DLL next to the manifest. If it contains more than one public `ICodalusPlugin`, name one with `type`. |
| `activationEvents` | `onStartup`, `onCommand:<id>`, `onView:<toolWindowId>`, `onSettingChanged:<key>`, `onSolutionOpened`, `onTestProjects` (the open solution has a test project; since 0.2.1), `onHostingProvider:<id>` (the shell needs a hosting provider the plugin declares; since 0.2.7, implicit), `onDebug` (a debug session starts; since 0.2.7), and since 1.0 `onAuthenticationRequest:<id>` (a session is asked of an authentication provider the plugin declares; implicit) and `onCustomEditor:<viewType>` (a file opens in its custom editor; implicit). Contributed commands and tool windows activate the plugin implicitly, so most plugins list nothing here. Prefer lazy events: `onStartup` costs every user startup time. `onView:testExplorer` fires when the Test Explorer opens. |
| `isolation` | `shared` (default) or `dedicated` (a host process of its own). |
| `runsOn` | Where the plugin runs in a remote window: `ui` (default), with the window on this machine, or `workspace`, on the machine the workspace is on (the server), next to its files and tools ([ADR 0057](adr/0057-plugin-placement.md)). |
| `permissions` | What the plugin does beyond its contributions, shown before it installs from a gallery (since M22c): `network`, `processes` (starting programs), `fileSystem` (files outside the workspace) and `secrets`. They inform the user and aren't enforced; declare what you use. |
| `enableProposedApi` | Use [proposed APIs](#proposed-apis). Honored for built-in plugins, and for others when Codalus runs with `--enable-proposed-api`. |

### Translating the manifest

A manifest string that is a whole `%key%` (VS Code's convention) is replaced when the manifest loads. The text comes from `codalus-plugin.nls.<locale>.json` for the display language (`pt-BR`, then `pt`), else from `codalus-plugin.nls.json` (English) next to the manifest. Each file is a flat `{ "key": "text" }` object. Titles, descriptions, categories, setting texts and anything else the manifest shows can be translated this way. A key no file has stays as written. Ship the `nls` files next to the manifest; the built-in Welcome plugin does. A plugin's own text (tree labels, messages) uses the same files through `context.Localize("key", "English")` (since 0.2.9). The display language is the setting `workbench.locale` (or `--locale`); `qps-ploc` shows every replaced string pseudo-translated, so untranslated text stands out.

### Contribution points

- **commands:** `id`, `title`, `category` (shown as "Category: Title"), `enablement` (a when-clause; disabled commands are hidden from the palette and greyed in menus).
- **menus:** locations `commandPalette` (only to restrict visibility with `when`; every command is in the palette by default), `menuBar/file|edit|view|git|project|build|debug|test|analyze|tools|window|help` and, since M34a, their submenus (`menuBar/edit/goTo`, `menuBar/edit/find`, `menuBar/edit/advanced`, `menuBar/edit/intelliSense`, `menuBar/edit/refactor`, `menuBar/file/new`, `menuBar/git/branches`, `menuBar/debug/windows` and the rest named in `BuiltIns.Menus.cs`), `toolWindow/title` (buttons above a tool window), `solutionExplorer/context` (the Solution Explorer's context menu; use the `explorerItemKind` context key; submenus `solutionExplorer/context/add`, `/git` and `/entityFramework` since M34a) `documentTab/context` (a document tab's context menu, since M34e) and `editor/context` (submenus `editor/context/git`, `/snippets`, `/outlining` and `/breakpoint` since M34a) (the code editor's context menu; its commands get the document URI and the zero-based caret line and column as arguments; since 0.2.1). Entries take `command`, `group`, `order` and `when`. Groups are separated in menus.
- **keybindings:** `key` plus optional `mac`, `linux` and `win` overrides, `command`, `when` and `args`. Chords are space-separated (`ctrl+k ctrl+h`). `primary` means ⌘ on macOS and Ctrl elsewhere. Users override bindings in the Keyboard Shortcuts editor; user bindings win over plugin bindings, which win over defaults.
- **toolWindows:** `id`, `title`, `location` (`left`, `right`, `bottom`), `kind` (`tree`, `markdown`, or since 1.0 `view`, a [declarative view](#declarative-views-and-custom-editors)) and `icon`.
- **settings:** `title` (the Settings UI category) and `properties`, each with `type` (`string`, `boolean`, `integer`, `number`, `string[]`), `default`, `description`, `enum` and `enumDescriptions`, `minimum`/`maximum`, `scope` (`user`, or `workspace` to allow per-workspace overrides) and `order`. Invalid values in settings.json fall back to the default and are reported in the Settings UI.
- **hostingProviders** (since 0.2.6, proposed API): Git hosting providers, `id`, `label`, `hosts` (the remotes' hosts by default; `*.example.com` matches subdomains), `serverSetting` (a setting with an enterprise server's URL, whose host counts too) and `icon`. The shell knows them before the plugin loads, and activates the plugin only when it needs the provider: to sign in, or for a repository on a host where the user is signed in. See [Proposed APIs](#proposed-apis).

- **propertyProviders** (since 0.2.8): rows for the Properties window, `{ command, when }`. The command gets `{ kind, path, project }` of the Solution Explorer's selection (`kind` as in `explorerItemKind`) and returns `[{ category, name, value, description }]`; the rows are read only and show under their category (default: the plugin's name). `when` limits the selections it is asked for.
- **externalToolMacros** (since 0.2.8): macros for External Tools' command, arguments and initial directory, `{ name, command, description }`. A tool that uses `$(name)` runs the command first; its string result replaces the macro.
- **themes** (since 1.0): color themes in VS Code's format, `{ id, label, uiTheme, path }`. `uiTheme` is the base (`vs`, `vs-dark`, `hc-black` or `hc-light`), and the file's `colors`, `tokenColors` and `semanticTokenColors` go over it: UI colors onto the shell's tokens (`sideBar.background`, `statusBar.background`, `editor.background` and the other keys in `Theming/ThemeFiles.cs`), TextMate scopes onto the editor's token types. `id` is a value of `workbench.colorTheme`; the user's color customizations apply over it (`"[Label]"`). The base is Classic's Light, Dark or High Contrast, also since the theme families (M45): a plugin's theme isn't drawn over Harbor or the others, and `themeFamily` is `classic` while it's in use. `samples/plugins/MidnightTheme` is one.
- **iconThemes** (since 1.0): file icon themes in VS Code's format, `{ id, label, path }`: `iconDefinitions` (`iconPath` relative to the file), `file`, `folder`, `folderExpanded`, `fileExtensions`, `fileNames`, `folderNames` and `folderNamesExpanded`. The Solution Explorer, tabs and pickers use it when `workbench.iconTheme` is its `id`. Raster icons (PNG, ICO, BMP, JPEG, WebP) are drawn as they are. SVG icons (since M22b) are converted once, outside the IDE's process, into vector drawings like the IDE's own icons; an SVG using more than paths and groups with fills falls back to the built-in icon and is named in Output → Codalus. `samples/plugins/BlockIcons` is one.
- **debuggers** (since 1.0): debug adapters speaking DAP over stdio, `{ type, label, command, args, languages }`. `launch.json` configurations whose `type` is `type` start `command` (a file in the plugin's folder, a `.dll` running with `dotnet`, or a program on `PATH`; `${pluginDir}` is the plugin's folder), and the configuration, with `${workspaceFolder}` and the other variables expanded, is sent as the `launch` request. The session uses the debugger's windows like netcoredbg's: breakpoints, call stack, variables, the Debug Console.
- **customEditors** (since 1.0): `{ viewType, displayName, filenamePatterns }`. Files whose names match open in the plugin's editor, a [declarative view](#declarative-views-and-custom-editors), unless the user picks Open With → Text Editor.
- **authentication** (since 1.0): `{ id, label }` of the [authentication providers](#authentication) the plugin offers. The Accounts page lists them before the plugin loads.
- **customTools** (since 0.2.8): custom tools (single-file generators, ADR 0035), `{ name, command, extension, description }`. A project item whose `Generator` metadata is `name` runs the command on Run Custom Tool and when it is saved: the command gets `{ path, content, namespace, project }` and returns the generated text, or `{ content, extension }`. The output is written next to the input with the extension (default `.cs`) and nested under it, as for the built-in `ResXFileCodeGenerator` and `TextTemplatingFileGenerator`.

### When-clauses

`&&`, `||`, `!`, parentheses, `==`, `!=`, `=~` (regex), `<`, `<=`, `>`, `>=`, and bare keys (truthy). Context keys the shell sets: `isWindows`, `isMac`, `isLinux`, `isDevelopment`, `theme` (`light`, `dark`, `highContrast`, or Classic's `blue`: a theme family's variant gives its kind), `themeFamily` (since M45: `classic`, `harbor`, `graphite`, `daylight` or `ember`), `focusedView` (the focused tool window id, or the kind of the focused document), `activeDocumentKind`, `inCommandPalette`, `toastsVisible`, `solutionOpen`, `solutionTrusted`, `explorerItemKind` (`solution`, `solutionFolder`, `solutionItem`, `project`, `folder`, `file`, `dependencies`, `dependencyGroup`, `dependency`), `explorerItemIsProjectReference`, `editorTextFocus` (an editor has keyboard focus), `editorLanguageId` (`csharp`, or absent for plain text), `editorHasSelection`, `editorHasMultipleSelections`, `buildRunning`, `programRunning` (a program started with Run Without Debugging is still running), `hasStartupProject`, `debugActive`, `debugRunning`, `debugStopped`, `hasBreakpoints`, `gitRepository` (the open solution is in a git repository), `gitMerging`, `gitRepositories` (the solution spans several repositories; since 0.2.6), `gitLfs` (the repository uses Git LFS), `hostingRepository` (the repository is on a host the user is signed in to), `hostingProvider` (that provider's id), `testsRunning`, `testExplorerSelection`, `coverageAvailable` (a coverage run's results are shown), and since 0.2.8 `folderMode` (a folder is open without a solution), `explorerFolderView`, `explorerShowAllFiles`, `explorerItemExcluded` (a file on disk that isn't in its project), `explorerItemInProject`, `explorerItemLinked`, `explorerItemIsProjectFile` and `explorerProjectLoaded`, and since 0.6 `propertyGridFocus` (a property grid has keyboard focus: the Properties window's, or a designer's collection editor's). Every text document, editor tabs included, has `activeDocumentKind == textFile`. In `toolWindow/title`, `view` is the tool window's id.

## The code

```csharp
public sealed class HelloPlugin : ICodalusPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var output = context.Window.CreateOutputChannel("Greetings");
        context.Subscriptions.Add(context.Commands.Register("hello.sayHello", async (_, ct) =>
        {
            var greeting = context.Settings.GetValue("hello.greeting", "Hello");
            output.AppendLine($"{greeting}, Codalus!");
            await context.Window.ShowNotificationAsync(NotificationSeverity.Info, $"{greeting}, Codalus!", ["Show Output"], ct);
            return null;
        }));
        context.Subscriptions.Add(context.Window.RegisterTreeDataProvider("hello.view", new GreetingTree(context.Settings)));
        return Task.CompletedTask;
    }
}
```

- `IPluginContext` gives you `Commands`, `Window`, `Settings`, `Log` and `Subscriptions`, and the [workbench APIs](#workbench-apis): `Tests`, `Workspace`, `Build`, `Debug`, `Terminals`, `Hosting`, `Secrets`, `FileSystems`, `Authentication` and `SourceControl`. Add every registration to `Subscriptions`; they are disposed on deactivation.
- **Commands** take and return JSON-serializable values (`CommandInvocation.Arg<T>(i)` reads arguments). `Commands.ExecuteAsync` runs any command, including the shell's built-ins below.
- **Window:** notifications with actions (the task completes with the chosen action, or null), `ShowQuickPickAsync` (pick from a list in the command palette) and `ShowInputBoxAsync` (ask for a line of text; for a path, `Browse = InputBoxBrowse.Folder` or `File` adds a Browse button that opens the OS's picker, since 1.1) since 0.1.1, output channels, status bar items, tree data providers and `SetMarkdown` for Markdown views.
- **Settings:** `GetValue<T>(key, fallback)` and a `Changed` event (`e.Affects("section")`).
- **Log** goes to Output → Plugins.

### Tree views

Implement `ITreeDataProvider<T>`: `GetChildrenAsync(element)` (null means the roots) and `GetTreeItem(element)`, which returns a `TreeItem` with a label, description, tooltip, icon, collapsible state and a command to run when the item is clicked. Raise `Changed` with an element (or null for everything) to refresh. The host maps your elements to handles; you never see the UI.

### Markdown views

`Window.SetMarkdown(viewId, markdown)` supports headings, paragraphs, bulleted and numbered lists, block quotes, fenced code, horizontal rules, `**bold**`, `*italic*`, `` `code` `` and links. Link targets are `https://…` (opened in the browser) or `command:<id>` (runs a command).

### Progress

`Window.WithProgressAsync(title, (progress, ct) => …, cancellable: true)` runs work as a task in the status bar, the way the IDE shows loading projects and builds: the title and the latest `ProgressReport` (a message, and a percentage when the work knows it) take the place of "Ready", with a progress bar. With `cancellable`, the bar's ✕ cancels the token passed to the work. Several tasks can run; the newest shows, and the … button lists them all.

### Declarative views and custom editors

A tool window of kind `view` is a declarative view (D2): the plugin describes it as a tree of `ViewNode`s and the IDE draws it with its own controls, in the theme, keyboard-accessible, with no UI code in the plugin.

```csharp
context.Window.SetView("todoboard.summary", ViewNode.Stack(
    ViewNode.Heading("Todos"),
    ViewNode.Row(ViewNode.TextBox("title", placeholder: "New todo"), ViewNode.Button("add", "Add", primary: true)),
    ViewNode.Table("items", ["Done", "Title"], rows)));
context.Subscriptions.Add(context.Window.OnViewEvent("todoboard.summary", async (e, ct) => { /* e.ElementId, e.Kind, e.Value, e.Row */ }));
```

- Elements: `Stack`, `Row`, `Heading`, `Label` (plain, `muted`, `code`), `Button`, `TextBox`, `CheckBox`, `Dropdown`, `Table`, `Toolbar` and `Separator`.
- Events: `click`; `change` with the value (a text box when the user presses Enter or leaves it); `select` and `activate` (double-click or Enter) with a table's row.
- Call `SetView` again to update the view. Elements keep the user's input across updates by their `Id`.

A **custom editor** opens files in such a view. Declare it (`contributes.customEditors`), then `Window.RegisterCustomEditor(viewType, provider)`. The provider's `OpenAsync` gets a `CustomEditorDocument` (a view id, the path and the text) and fills the view; `SetCustomEditorText(viewId, text)` gives the file's new text, which marks the document modified, and the user saves it with Ctrl+S. `samples/plugins/TodoBoard` is a board for `*.todos.json` files and a summary tool window.

### Icons and status bar text

Icons are names from the shell's set, so they follow the theme, including high contrast: `search`, `gear`, `keyboard`, `color-mode`, `history`, `extensions`, `folder`, `file`, `book`, `check`, `circle`, `home`, `info`, `warning`, `error`, `bell`, `bell-dot`, `close`, `chevron-right`, `chevron-down`, `refresh`, `output`, `list`, `play`, `link-external`, `clear-all`, `lock`, `arrow-down`, `star`, `question`, `edit`, `discard`, `trash`, `layout`, `dot`, `solution`, `project`, `package`, `link`, `file-code`, `file-data`, and since 0.2.6 `cloud`, `pull-request`, `comment`, `issue`, `worktree`, `account`, `sync`, and since 0.2.7 `pin`. Since M34a most of them are Fluent UI System Icons (D24), and the commands' icons are available too (`folder-open`, `save`, `save-all`, `build`, `breakpoint`, `step-into`, `merge`, `terminal` and the others in `Controls/Icon.cs`); file-type icons are `mi:` and a name from `Icons/material-icons.txt` (`mi:csharp`, `mi:folder-src-open`), and `name@Family` colors an icon with a command family's token (`beaker@Test`). Status bar text may start with `$(icon)`, for example `"$(home) Start"`.

### Shell commands for plugins

| Command | Arguments |
|---|---|
| `workbench.view.<toolWindowId>` | optional `{ "preserveFocus": true }` |
| `workbench.openExternal` | an `http(s)` URL |
| `workbench.setSetting` | `key`, `value` (null resets) — writes the user's settings.json |
| `workbench.openSolution` | optional path of a solution, project or folder (none: ask) |
| `workbench.getRecentSolutions` | none; returns `[{ path, name }]`, most recent first |
| `solution.getInfo` | none; returns `{ path, format, trusted }` of the open solution, or null |
| `solution.addProjectFile` | project path; adds it to the open solution (in the selected solution folder) |
| `editor.action.revealDefinition`, `editor.action.goToImplementation`, `editor.action.triggerSuggest`, `editor.action.showHover`, `editor.action.find`, `editor.action.gotoLine` | none; act on the active editor |
| `editor.action.referenceSearch`, `editor.action.rename`, `editor.action.quickFix`, `editor.action.formatDocument`, `editor.action.formatSelection`, `workbench.action.gotoAll`, `workbench.action.gotoFile`, `workbench.action.gotoType`, `workbench.action.gotoMember`, `workbench.action.gotoText`, `workbench.action.findInFiles` | none; act on the active editor (Code Search and Find in Files on the solution) |
| `build.buildSolution`, `build.rebuildSolution`, `build.cleanSolution`, `build.cancel`, `run.stop`, `run.selectStartupProject` | none |
| `build.buildProject`, `build.rebuildProject`, `build.cleanProject`, `run.startWithoutDebugging`, `run.setStartupProject`, `run.selectLaunchProfile` | optional project path (default: the selected or active document's project; for run commands, the startup project) |
| `debug.start`, `debug.continue`, `debug.stop`, `debug.restart`, `debug.stepOver`, `debug.stepInto`, `debug.stepOut`, `debug.pause`, `debug.toggleBreakpoint`, `debug.toggleBreakpointEnabled`, `debug.editBreakpoint`, `debug.deleteAllBreakpoints`, `debug.exceptionSettings` | none (`debug.start` takes an optional project path) |
| `debug.attach` | optional process id |
| `debug.addWatch` | optional expression (default: the selection or the expression at the caret) |
| `git.commit`, `git.refresh`, `git.switchBranch`, `git.createBranch`, `git.merge`, `git.abortMerge`, `git.fetch`, `git.pull`, `git.push`, `git.showDiff`, `git.stageFile`, `git.toggleBlame` | none (file commands act on the active editor) |
| `editor.activePosition` | none; returns `[documentUri, line, character]` (zero-based) of the active editor's caret, or null. Since 0.2.1. |
| `workbench.openFile` | a file path or `file:` URI, optional zero-based line. Since 0.2.1. |
| `project.addReferenceTo` | project path, referenced project path. Since 0.2.1. |
| `testing.runAll`, `testing.debugAll`, `testing.coverAll`, `testing.runFailed`, `testing.runNotRun`, `testing.repeatLast`, `testing.cancel`, `testing.runAtCursor`, `testing.debugAtCursor`, `testing.coverAtCursor`, `testing.refresh`, `testing.nextFailure`, `testing.previousFailure`, `testing.exportTrx`, `testing.importTrx` | none. Since 0.2.1. |
| `file.saveAll`, `workbench.navigateBack`, `workbench.navigateForward`, `workbench.splitEditorRight`, `workbench.splitEditorDown`, `workbench.view.errorList` | none |
| `workbench.showCommandPalette`, `workbench.openSettings`, `workbench.openKeyboardShortcuts`, `workbench.selectTheme`, `workbench.showLogs`, `workbench.view.plugins` | none |

The built-in Welcome plugin (`src/Plugins/Codalus.Plugins.Welcome`) and Templates plugin (`src/Plugins/Codalus.Plugins.Templates`: New Project and New Solution with quick picks, input boxes and `dotnet new`) are complete examples that reach the shell only through these commands.

## Languages, grammars and language servers

Since 0.2.4 a plugin can add a language without any code ([ADR 0016](adr/0016-language-platform.md)). The contribution points use VS Code's shapes, so existing grammars and language configurations work unchanged. `samples/plugins/TodoLanguage` is a complete example: the `.todo` language with a grammar, a language configuration and a small LSP server.

```json
{
  "id": "contoso.todo",
  "version": "1.0.0",
  "engines": { "codalus": "^0.2.4" },
  "contributes": {
    "languages": [
      { "id": "todo", "aliases": ["Todo"], "extensions": [".todo"], "filenames": ["TODO"], "configuration": "language-configuration.json" }
    ],
    "grammars": [
      { "language": "todo", "scopeName": "source.todo", "path": "syntaxes/todo.tmLanguage.json" }
    ],
    "languageServers": [
      { "id": "todo-server", "languages": ["todo"], "command": "TodoLanguage.dll", "settingsSection": "todo" }
    ]
  }
}
```

**Declarative plugins**
- A plugin whose contributions are only languages, grammars, language servers, snippets, themes, icon themes, debuggers and settings may leave out `entry`.
- It never loads in a plugin host, and its contributions apply as soon as it is discovered.

**`languages`**
- `id` is what other contributions refer to.
- Files match by `filenames` (exact), `filenamePatterns` (`*`, `?`), `extensions` (the longest wins) and `firstLine` (a regular expression), in that order.
- Contributing an existing id adds file patterns to that language.
- `configuration` is a `language-configuration.json`. The editor uses its comments (Toggle Line Comment), brackets, `autoClosingPairs` and `surroundingPairs`, `folding` (markers and `offSide`) and indentation patterns.

**`grammars`**
- A TextMate grammar (JSON or plist) for a language.
- It colors the language's files, with the theme's TextMate colors. Semantic tokens from a language server are applied on top.

**`languageServers`**
- An LSP server over stdio, for one or more `languages`.
- `command` is a file in the plugin's folder (a `.dll` runs with `dotnet`) or a program on `PATH`. `${pluginDir}` in `command` and `args` is the plugin's folder.
- The shell starts it with the first document of its languages and restarts it if it crashes.
- `initializationOptions` are sent in `initialize`. `workspace/configuration` requests are answered with the settings under `settingsSection`.
- The server's results go through the same editor features as C#: completion, hover, signature help, definition, references, rename, code actions, formatting, folding, semantic tokens and diagnostics ([protocol.md](protocol.md#other-language-servers-adr-0016)).

## Snippets

Since 0.2.5 a plugin can contribute code snippets ([ADR 0018](adr/0018-editing-refactoring-ui-and-analysis.md)), in VS Code's format:

```json
"contributes": {
  "snippets": [ { "language": "csharp", "path": "snippets/csharp.json" } ]
}
```

The file maps names to `{ "prefix", "body", "description" }`; `prefix` and `body` may be strings or arrays (a body array is joined with line breaks). Bodies use TextMate snippet syntax: `$1`, `${1:default}`, `${1|a,b|}`, `$0`, `$TM_SELECTED_TEXT`, `$TM_FILENAME_BASE` and the other variables VS Code defines. Snippets show in completion by prefix, in Insert Snippet, and (when the body uses the selection) in Surround With. The user's snippets with the same name win. A plugin whose only contributions are declarative (snippets, languages, grammars, language servers, settings) needs no `entry`.

## Workbench APIs

These were proposed APIs in 0.2.x, proved by the built-in plugins, and are stable members of `IPluginContext` since 1.0:

- **`Tests`: test controllers** (ADR 0010, since 0.2.1), modelled on VS Code's testing API. `CreateController(id, label)` returns a controller whose `Items` (a tree of `TestItem`: id, label, URI, range, traits, children) appear in the Test Explorer, with editor glyphs on the items' ranges. `CreateRunProfile(label, Run|Debug|Coverage, handler)` adds a way to run tests: the handler gets a `TestRunRequest` (included and excluded items; `Include` null means all) and a token that the user's Cancel cancels. `CreateRun(request)` reports progress: `Enqueued`, `Started`, `Passed`, `Failed`/`Errored` with `TestMessage`s (message, expected, actual, stack trace, location), `Skipped`, `AppendOutput`, `AddAttachment`, `AddCoverage` (ADR 0011), then `Complete`. Item ids are unique within a controller; changes are sent to the shell in batches.
- **`Workspace`:** the open solution (`GetSolutionAsync`: projects with target frameworks, test project and test platform, compile files and project references), `GetProjectOutputAsync` (the build output per target framework), `FindTestCandidatesAsync` (test classes and methods found in source by the language service, before any build), `GetTypeOutlineAsync` (the type at a position and its methods), and the events `SolutionChanged` and `DocumentsChanged`.
- **`Build`:** `BuildAsync(projects)` builds through the shell (after a build in progress; output in the Build channel and the Error List), and the `Finished` event.
- **`Debug`:** `LaunchAsync` and `AttachAsync` start a debug session (netcoredbg, ADR 0007) and return its process id and an `Ended` task.
  - **Visualizers** (0.2.7, ADR 0032): `RegisterVisualizer(new DebugVisualizerOptions(id, title, types), visualize)` adds a visualizer to the View menu of the debugger's variable trees, for values whose type is in `types` (a name ending in `*` is a prefix: `System.Collections.Generic.Dictionary<*`). When the user picks it, `visualize` gets the value's expression, type and text, and `EvaluateAsync` to read more of the paused program (members, indexers, method calls and private fields; no `new`, casts or lambdas). It returns `VisualizerView.FromText(text, languageId)` (a read-only document) or `VisualizerView.FromTable(columns, rows)`. Activate with `onDebug`. `samples/plugins/DictionaryVisualizer` shows a `Dictionary<TKey, TValue>` as a key/value table.
- **`Terminals`** (0.2.3): `CreateAsync(new TerminalOptions { Program = "dotnet", Arguments = ["watch"] })` runs a command in an integrated terminal (ADR 0014); without `Program` it opens the user's shell (`Profile`, or the default). The `ITerminal` takes typed input (`SendTextAsync("dir\r")`), raises `Output` with the program's text when `ReportOutput` is set, completes `Exited` with the exit code, and closes on `KillAsync` or disposal.

The built-in .NET test plugin (`src/Plugins/Codalus.Plugins.DotNetTest`) uses all of them.

Since 0.2.6 ([ADR 0031](adr/0031-git-hosting-and-repository-features.md)):

- **`Hosting`: Git hosting providers.** Declare the provider in the manifest (`contributes.hostingProviders`), then `RegisterProvider` an instance of your `HostingProvider` subclass when the plugin activates. The shell shows the Accounts page, the Pull Requests and Work Items windows, review comments in editors, CI status and `#` completion in commit messages; the provider talks to its host.
  - Required: `ResolveRepository` (the repository a remote URL points to, or null when it's another host's), `GetAccountAsync` (from the stored session; no network call), `SignInAsync` (the OAuth device flow: show the code with the `IDeviceCodePrompt` the shell passes, then poll the host) and `SignOutAsync`.
  - Optional (the defaults throw `NotSupportedException`, and the shell leaves the feature out): `SignInWithTokenAsync`, `GetRepositoryInfoAsync`, `GetPullRequestsAsync` (all, mine, review requested), `GetPullRequestAsync` (description, review threads on file lines, conversation), `GetCheckoutAsync` (a remote or a fork's URL, a ref, the local branch), `CreatePullRequestAsync`, `GetBranchStatusAsync`, `GetWorkItemsAsync` (assigned to the user, or by number or words), `GetOwnersAsync` and `PublishAsync`.
  - Throw `HostingAuthenticationException` when the stored session is no longer valid: the shell forgets the account and asks the user to sign in again.
- **`Secrets`: the plugin's own secrets** (`GetAsync`, `StoreAsync`, `DeleteAsync` by key), kept in the OS credential store: Windows Credential Manager, the macOS Keychain, or the Secret Service through libsecret. Never write a token anywhere else.

The built-in GitHub, Azure DevOps and GitLab plugins (`src/Plugins/Codalus.Plugins.GitHub`, `.AzureDevOps`, `.GitLab`, on the shared `Codalus.Plugins.Hosting`) are complete examples.

New in 1.0. `samples/plugins/HelloWorld` has one of each (`MemoryFiles`, `MemoryAccounts`, `FolderScm`).

### File systems

`FileSystems.RegisterProvider(scheme, provider)` serves files under a scheme of the plugin's: an archive's contents, a cloud store, a database's scripts. The IDE shows them under `FileSystems.RootOf(scheme)`, a path of this machine's form (`\\codalus-plugin.invalid\<scheme>` on Windows, `/codalus-plugin/<scheme>` elsewhere), and the editor reads and saves through the provider: **Browse a Plugin's Files…** in the command palette picks one to open, and so does any path under the root. An `IFileSystemProvider` implements `StatAsync`, `ReadDirectoryAsync` and `ReadFileAsync`; the write members (`WriteFileAsync`, `CreateDirectoryAsync`, `DeleteAsync`, `RenameAsync`) default to read-only. Paths are the provider's own, `/`-separated from `/`. Throw `FileNotFoundException`, `DirectoryNotFoundException`, `UnauthorizedAccessException` or `IOException`; the IDE sees a missing file, a denied access or a failure. `NotifyChanged(scheme, paths)` reloads open editors.

### Authentication

A plugin can sign users in to a service for itself and for other plugins (`contributes.authentication`, then `Authentication.RegisterProvider(id, label, provider)`). An `IAuthenticationProvider` lists sessions (`GetSessionsAsync`), signs in (`CreateSessionAsync`: a browser, a device code, an input box) and signs out (`RemoveSessionAsync`); keep tokens in `Secrets`. Any plugin asks with `Authentication.GetSessionAsync(providerId, scopes, createIfNone)`: the provider's plugin activates if needed, and when another plugin asks, the user is asked to allow it (once per plugin and provider in a session). The Accounts page (Git Hosting Accounts… in the command palette) shows each provider's sessions under Other sign-ins, with Sign Out; call `NotifySessionsChanged` after a sign-in or sign-out the IDE didn't start.

### Source control

`SourceControl.RegisterProvider(id, label, root, provider)` adds a source control system for a folder (Mercurial, Perforce, Subversion); Git is built in. `Update(id, state)` shows its `SourceControlState`: groups of files (`Changes`, `Staged`…) with statuses, a commit box's placeholder and button label, and a short status. The Source Control Providers window (View → Other Windows) shows each provider; the commit button calls `AcceptAsync(message)`, and opening a file compares it with `GetOriginalAsync(path)`.

## Proposed APIs

Proposed APIs are not covered by the versioning policy: they may change in any release, and are proved by built-in plugins before they become stable. Set `"enableProposedApi": true` and call `context.Proposed()` (namespace `Codalus.Plugins.Proposed`). Third-party plugins also need `--enable-proposed-api`. 1.0 had none: they all became members of `IPluginContext`. Webviews are expected to start here.

### Language model providers (proposed)

`context.Proposed().LanguageModels.RegisterProvider(provider)` offers chat models to AI assistance ([ADR 0062](adr/0062-ai-provider-api-and-data-boundaries.md)). Declare the provider in the manifest, so the IDE lists it before your plugin runs:

```json
"contributes": {
  "languageModelProviders": [ { "id": "acme", "label": "Acme AI", "description": "Acme's hosted models.", "local": false } ]
}
```

Your plugin activates only when the user picks the provider (`onLanguageModelProvider:acme`, implicit). Derive from `LanguageModelProvider`:
- `GetModelsAsync`: the models you offer (`LanguageModelInfo`: id, name, context window, whether it takes tools).
- `ChatAsync(request, progress, cancellationToken)`: send `request.Messages` (a `ChatMessage` is a role, text, and tool calls or a tool result), report each piece of text and each complete tool call with `progress.Report(new ChatResponsePart(text))`, and return how the reply ended (`ChatResult`). Stop when the token is cancelled.
- `GetStatusAsync` and `ConfigureAsync`: whether you're ready (a key, an endpoint), and asking the user for what's missing with `Window.ShowInputBoxAsync`. Keep keys in `Secrets`.
- Throw `LanguageModelException` with a message the user can act on; set `NeedsConfiguration` when the key is missing or refused.

The IDE decides what is sent: your provider never reads the workspace. The built-in providers (`src/Plugins/Codalus.Plugins.OpenAI`, `Codalus.Plugins.Anthropic`) are examples.

## Testing plugins

`Codalus.Plugins.Testing` (since M22b) runs a plugin in memory, with no IDE and no plugin host, so its tests are ordinary unit tests:

```csharp
var context = new TestPluginContext("contoso.greeter");
context.Window.NotificationResponder = n => n.Actions.FirstOrDefault(); // the user's answer
await using var host = await PluginTestHost.ActivateAsync<GreeterPlugin>(context);

context.Settings.Set("greeter.greeting", "Ahoy");                      // raises Settings.Changed
Assert.Equal(1, await context.Commands.ExecuteAsync<int>("greeter.greet"));
Assert.Equal("Ahoy!", Assert.Single(context.Window.Notifications).Message);
```

- **Commands:** `ExecuteAsync` runs the plugin's commands with arguments and results through JSON, as over the protocol. `OnShellCommand` answers the IDE's commands the plugin calls, and `Executed` records every call.
- **Window:** responders answer notifications, quick picks and input boxes. The window records output channels, status bar items, Markdown and declarative views (`GetView`, `FindElement`) and tasks with their reports (`Progress`; `Cancel()` presses ✕).
  - **Trees:** `GetTreeChildrenAsync` expands a tree as the IDE does, `ClickTreeItemAsync` runs a row's command, and `TreeRefreshes` counts its provider's `Changed`.
  - **Views and custom editors:** `ClickAsync`, `ChangeAsync` and `RaiseViewEventAsync` act in a view; `OpenCustomEditorAsync` opens a file in a custom editor, and `GetCustomEditorText` gives its new text.
- **Providers:** the file systems, authentication and source control providers the plugin registered are there to call (`context.FileSystems["scheme"]`, `context.Authentication.Providers`, `context.SourceControl.Providers` with each one's `State`). `context.Authentication.Sessions` supplies other plugins' sessions; `Consent` says whether the user allows them.
- **Settings and secrets** are in memory. `Tests`, `Workspace`, `Build`, `Debug`, `Terminals` and `Hosting` are whatever the test sets (they throw `NotSupportedException` otherwise).
- Disposing the `PluginTestHost` deactivates the plugin and disposes its subscriptions.

`tests/Codalus.Plugins.Testing.Tests` tests the HelloWorld and TodoBoard samples this way.

## Running and debugging

In Codalus IDE (since M22b): make the plugin's project the startup project. The project is a plugin when its folder has `codalus-plugin.json`. Then:
- **Debug → Debug Plugin in Development Instance** builds it and starts a second Codalus IDE with the build output loaded. The plugin runs there in a plugin host of its own, activated at once, and this IDE's debugger attaches to that host before the plugin activates, so breakpoints in `ActivateAsync` are hit too. Activation has no time limit while you're stopped at a breakpoint.
- **Debug → Run Plugin in Development Instance** does the same without the debugger.
- The development instance keeps its settings and state in the solution's `.codalus/plugin-dev`, apart from yours, and passes `--enable-proposed-api` when the manifest asks for proposed APIs.

By hand:
1. Build the plugin.
2. Start Codalus with the build output: `codalus --plugin-dir path/to/bin/Debug/net10.0`. `--plugin-dir` is repeatable and takes a plugin folder or a folder of plugins; it wins over installed copies with the same id.
3. Check **View → Plugins** for its state, and **Output → Plugins** for the activation log and anything you write to `context.Log`.
4. To debug, add `--debug-plugin-host <file>`: the `--plugin-dir` plugins then run in their own `codalus-pluginhost`, which writes its process id to the file and waits up to a minute for a debugger before activating them. Attach to that process.

Installed plugins live in the user plugins folder (**Tools → Open User Plugins Folder**), one subfolder per plugin.

## Publishing

Plugins are NuGet packages of type `CodalusPlugin` ([ADR 0060](adr/0060-plugin-distribution.md)), found and installed from the IDE's Plugin Gallery. The template's project packs one: `dotnet pack` puts the build output and the manifest under `plugin/`, and leaves out the plugin API, which the IDE provides.
- The package's id and version must be the manifest's `id` and `version`.
- Push it to nuget.org (`dotnet nuget push`) or any NuGet v3 feed your users add to **Plugin Sources**. nuget.org signs packages, and the IDE requires that signature for packages from there.
- Declare `permissions` in the manifest; users see them before installing.
- An update is a new version: installed copies update at their next start.

## Changelog

### 1.1.0 (Codalus IDE 0.5)

Added: `InputBoxOptions.Browse` and the enum `InputBoxBrowse`. With `Folder` or `File`, the input box has a Browse button beside it that opens the OS's folder or file picker where the typed path is, and puts the choice in the box, as every path field in the IDE does. The protocol's `window/showInputBox` takes `browse` (`folder` or `file`). Plugins that set it ask for `^1.1.0`; plugins built for 1.0 keep working.

### Command sweep fixes (no API version change)

The activation event `onSolutionOpened`, documented since 1.0, now fires when a solution opens; before, a plugin activating only on it never started. A manifest's menu on `documentTab/context` is no longer reported as an unknown location, and a `when` or `enablement` clause that doesn't parse is a warning (it was silently always true). `PublicAPI.txt` now writes out each parameter's `out`/`in`/`ref`, nullable reference types and generic constraints: the same API, recorded more fully. The API didn't change.

### M47 (no API version change)

Code Search ([ADR 0078](adr/0078-code-search-and-its-persistent-index.md)): `workbench.action.gotoAll` and `workbench.action.featureSearch` keep their ids and open Code Search's window on its Code Search and Feature Search tabs, and `workbench.action.gotoFile`, `gotoType`, `gotoMember` and `gotoText` open it on a filter. A language server a plugin contributes through the LSP bridge appears in Code Search when it answers `workspace/symbol`, and a plugin's commands and settings in Feature Search. The API didn't change.

### M45 (no API version change)

The theme families ([ADR 0077](adr/0077-theme-families.md)): `workbench.colorTheme` takes `harbor`, `graphite`, `daylight`, `ember` or `classic`, alone (following the system) or with `.light`, `.dark` or `.highContrast` (Classic also `.blue`), and the values from before (`dark`, `followSystem`…) read as Classic. The new context key `themeFamily` names the family; `theme` stays `light`, `dark`, `highContrast` or `blue`. Contributed color themes apply over Classic's variant for their `uiTheme`, as before. The API didn't change.

### Razor colors (no API version change)

Color themes can color Razor files: `semanticTokenColors` takes Razor's token types (`razorComponentElement`, `razorDirective`, `markupElement`… listed in [the protocol](protocol.md#semantic-token-legend)), and `tokenColors` reach them through their TextMate scopes (`entity.name.tag`, `entity.other.attribute-name`, `punctuation.definition.tag`, `keyword.control.razor`, `comment`, `string`). The API didn't change.

### M30b (no API version change)

Codalus and its plugin host moved to .NET 11. `Codalus.Plugins.Abstractions`, `Codalus.Plugins.Testing` and the `codalus-plugin` template stay on `net10.0`: plugins built for .NET 10 load into the .NET 11 host unchanged, and keep building with the .NET 10 SDK. A plugin may target `net11.0` instead, to use .NET 11's libraries. The API didn't change.

### M28a (proposed API)

Added, as a proposed API: `IProposedApi.LanguageModels`, `LanguageModelProvider` and the chat types, and `contributes.languageModelProviders` with the activation event `onLanguageModelProvider:<id>` ([Language model providers](#language-model-providers-proposed)). The stable API didn't change.

### M22d (no API version change)

The Components window and plugin performance (activation time, command time and host memory per plugin, slow plugins offered to turn off; [ADR 0061](adr/0061-components-and-plugin-performance.md)) are in the shell. The API didn't change.

### M22c (no API version change)

The manifest's `permissions`, and plugins as NuGet packages of type `CodalusPlugin` with a Plugin Gallery in the IDE ([Publishing](#publishing)). The API didn't change.

### M22b (no API version change)

New packages and tools around API 1.0 ([ADR 0059](adr/0059-plugin-developer-experience.md)): [`Codalus.Plugins.Testing`](#testing-plugins), the `dotnet new codalus-plugin` template (`Codalus.Templates`), Run and Debug Plugin in Development Instance and `--debug-plugin-host` ([Running and debugging](#running-and-debugging)), the generated [API reference](user-guide/plugin-api.md), and SVG icons in icon themes. The API's documentation now covers every record parameter; its surface didn't change.

### 1.0.0 (M22a)

The API is frozen ([ADR 0058](adr/0058-plugin-api-1-0.md)): from here, minor releases only add, and removals wait for a major release after a deprecation (`CODALUS0001`) of at least two minor releases. The public surface is recorded in `src/Codalus.Plugins.Abstractions/PublicAPI.txt`.

Breaking for 0.3.0 plugins: ask for `"codalus": "^1.0.0"`, and use `context.Tests`, `context.Workspace`, `context.Build`, `context.Debug`, `context.Terminals`, `context.Hosting` and `context.Secrets` where you called `context.Proposed()`. `enableProposedApi` isn't needed for them any more.

Added:
- `IWindow.WithProgressAsync` and `ProgressReport`: [tasks in the status bar](#progress).
- Declarative views: `ViewNode`, `ViewEvent`, `IWindow.SetView` and `OnViewEvent`, tool windows of kind `view`.
- Custom editors: `contributes.customEditors`, `ICustomEditorProvider`, `CustomEditorDocument`, `IWindow.RegisterCustomEditor` and `SetCustomEditorText`; activation event `onCustomEditor:<viewType>`.
- `contributes.themes` (color themes) and `contributes.iconThemes` (file icon themes, raster icons), in VS Code's formats.
- `contributes.debuggers`: DAP adapters for launch configuration types.
- `IPluginContext.FileSystems`: [file system providers](#file-systems).
- `IPluginContext.Authentication` and `contributes.authentication`: [authentication providers](#authentication); activation event `onAuthenticationRequest:<id>`.
- `IPluginContext.SourceControl`: [source control providers](#source-control).

### 0.3.0 (M39)

Breaking: the product is renamed from Daedalus Studio to Codalus IDE ([ADR 0056](adr/0056-rename-to-codalus.md)). Plugins built for 0.2.x need to be rebuilt:
- the package and namespaces are `Codalus.Plugins.Abstractions` (`ICodalusPlugin`, was `IDaedalusPlugin` in `DaedalusStudio.Plugins.Abstractions`);
- the manifest is `codalus-plugin.json` (and `codalus-plugin.nls*.json`), its engine `"codalus": "^0.3.0"`;
- activation events, ids and context keys keep their shapes, with `codalus` for `daedalus` (`codalus.welcome`);
- environment variables are `CODALUS_*`, and protocol extensions `codalus/…`.

Added in 0.3.0: the manifest's `runsOn` (`ui` or `workspace`) places a plugin in a remote window. A workspace plugin runs in a plugin host on the server, from its folder copied with the server.

Otherwise a plugin that builds against 0.3.0 behaves as it did on 0.2.9.

### 0.2.9 (M23c)

Additive (plugins built for 0.2.0–0.2.8 keep working): `context.Localize(key, english)` and `context.Localize(key, english, args…)` (`PluginLocalization`) give a plugin's own text in the display language. The text comes from the same `codalus-plugin.nls.<locale>.json` files as the [manifest's](#translating-the-manifest), else `english`. The display language reaches the plugin host as `CODALUS_DISPLAY_LOCALE`. The built-in Welcome plugin translates its Start page this way.

### M23b (no version change)

Additive (manifests for 0.2.0–0.2.8 keep working):
- [Manifest translation](#translating-the-manifest): `%key%` placeholders with `codalus-plugin.nls.json` and `codalus-plugin.nls.<locale>.json`.
- Users can turn plugins off: the setting `plugins.disabled` (ids), which the Plugins window edits. A turned-off plugin is discovered but its contributions aren't applied, and it doesn't activate. It applies at the next start (File → Restart).
- Shell commands `workbench.restart`, `workbench.customize`, `workbench.profiles.switch`, `.new`, `.delete`, `.export` and `.import`, and `settingsSync.turnOn`, `.syncNow` and `.turnOff`. The context keys `profile` (the active profile's name) and `locale` (the display language).
- The settings `workbench.locale`, `workbench.commandBarCustomizations` (the user's menu and toolbar changes; a plugin's menu items can be removed and moved like the shell's), `settingsSync.target` and `settingsSync.ignoredSettings`. The settings, keyboard shortcuts and snippets that a profile holds include a plugin's own settings.

### M34e (no version change)

Additive: the menu location `documentTab/context`, a document tab's context menu (its commands act on the active document, which the right-clicked tab becomes).

### M34a (no version change)

Additive (manifests for 0.2.0–0.2.8 keep working): menus may contribute to `menuBar/analyze` and to the menus' submenus (`menuBar/<menu>/<submenu>`, `editor/context/<submenu>`, `solutionExplorer/context/<submenu>`). The menu bar and the context menus are trees now: the built-in Templates plugin's New Project and New Solution moved from `menuBar/file` to `menuBar/file/new`, and its Solution Explorer entry to `solutionExplorer/context/add`. A plugin's items in a top-level menu stay there. Icon names gained the commands' icons, and most icons are drawn from Fluent UI System Icons.

### 0.2.8 (M26)

Additive (plugins built for 0.2.0–0.2.7 keep working):
- `contributes.propertyProviders`, `contributes.externalToolMacros` and (M26b) `contributes.customTools` ([above](#contribution-points)).
- Shell commands `workbench.openFolder` (an optional folder path), `workbench.view.properties`, `explorer.showAllFiles`, `explorer.switchViews`, `explorer.excludeFromProject`, `explorer.includeInProject`, `project.addNewItem` (an optional project path), `project.addExistingItem`, `project.addExistingItemAsLink`, `project.exportTemplate`, `tools.templates`, `tools.externalTools`, `folder.loadAllProjects`, `folder.loadProject`, `folder.unloadProject`, `folder.openProject`, `tasks.run` (an optional task label), `tasks.configure`, `launch.configure`, `run.selectLaunchConfiguration` (an optional name), and `externalTools.run.<id>` for each external tool; the context keys listed under [When-clauses](#when-clauses).
- `solution.getInfo` returns `format: folder` and the folder as `path` in folder mode; `solution.addProjectFile` loads the project into the folder.
- Icons `files`, `file-add`, `properties` and `tools`.
- M26b: shell commands `build.batchBuild`, `build.checkUpToDate` (an optional project path), `build.showTimings`, `build.buildWithBinaryLog`, `build.openBinaryLog` (an optional `.binlog` path), `build.openBuildLog` and `project.runCustomTool` (an optional file path); the settings `build.verbosity`, `build.logFileVerbosity`, `build.maxParallelProjects`, `build.fastUpToDateCheck`, `build.beforeRun`, `build.binaryLog`, `build.runAnalyzersDuringBuild` and `build.runCustomToolsOnSave`; the language `t4` (`.tt`, `.ttinclude`, `.t4`).

### 0.2.7 (M14b)

Additive (plugins built for 0.2.0–0.2.6 keep working):
- The activation event `onDebug`.
- Proposed: debugger visualizers, `IDebug.RegisterVisualizer` ([above](#proposed-apis)).
- Shell commands `debug.openDump`, `debug.createDump`, `debug.detachAll` and `debug.clearDataTips`; the setting `debug.stepOverPropertiesAndOperators`.
- The icon `pin`.

### 0.2.6 (M13b)

Additive (plugins built for 0.2.0–0.2.5 keep working):
- Proposed `IHosting` (`context.Proposed().Hosting`) with `HostingProvider`, `IDeviceCodePrompt`, `HostingAuthenticationException` and the records they use; proposed `ISecrets` (`context.Proposed().Secrets`). `IProposedApi` gained `Hosting` and `Secrets` (the host implements it, plugins don't).
- The manifest's `contributes.hostingProviders` and the activation event `onHostingProvider:<id>` (implicit).
- Protocol 0.2: `hosting/*` and `secrets/*` ([plugin-protocol.md](plugin-protocol.md#hosting-providers-and-secrets)) and the error codes `-33010` to `-33013`. A 0.2 host without them still works with this shell.
- Shell commands `hosting.accounts`, `hosting.showChecks`, `hosting.refresh`, `git.pullRequests`, `git.createPullRequest`, `git.workItems`, `git.publish`, `git.worktrees`, `git.addWorktree`, `git.switchRepository`, `git.lfsLocks`, `git.lfsLock`, `git.lfsUnlock` and `git.lfsPull`; the context keys `hostingRepository`, `hostingProvider`, `gitRepositories` and `gitLfs`; the settings `hosting.enabled`, `hosting.ciStatus`, `hosting.statusRefreshSeconds`, `hosting.reviewComments` and `hosting.showResolvedComments`.
- Icons `cloud`, `pull-request`, `comment`, `issue`, `worktree`, `account` and `sync`.

### 0.2.5 (M12b)

Additive (plugins built for 0.2.0–0.2.4 keep working):
- `contributes.snippets` ([above](#snippets)); snippets are declarative contributions.
- Shell commands `editor.action.insertSnippet`, `editor.action.surroundWithSnippet`, `editor.action.codeCleanup`, `editor.action.duplicateSelection`, `editor.action.moveLinesUpAction`/`DownAction`, `editor.action.smartSelect.expand`/`shrink`, `bookmarks.toggle`/`next`/`previous`/`clearAll`, `workbench.compareFiles`, `analysis.syntaxVisualizer`, `analysis.codeMetrics.solution`/`project`; the settings `editor.formatOnSave`, `files.autoRecover`, `codeCleanup.fixes`, `codeCleanup.onSave` and `taskList.tokens`.

### 0.2.4 (M11)

Additive (plugins built for 0.2.0–0.2.3 keep working):
- `contributes.languages`, `contributes.grammars` and `contributes.languageServers` ([above](#languages-grammars-and-language-servers)).
- `entry` is optional for declarative plugins.
- Shell commands `editor.action.commentLine`, `editor.action.addCommentLine`, `editor.action.removeCommentLine`, `editor.action.triggerWordCompletion` and `markdown.showPreviewToSide`; the setting `editor.autoClosingBrackets`.

### 0.2.3 (M10)

Additive (plugins built for 0.2.0–0.2.2 keep working):
- Proposed `ITerminals` (`context.Proposed().Terminals`): `CreateAsync` opens the user's shell (a profile) or runs a program in an integrated terminal ("run command in terminal"); the returned `ITerminal` sends text, raises `Output` (with `ReportOutput`) and completes `Exited` with the exit code; `KillAsync` and disposing close it.
- Shell commands `workbench.view.terminal`, `terminal.new`, `terminal.split`, `terminal.kill`, `terminal.clear`, `terminal.find`, `terminal.openInTerminal`, `terminal.runSelectedText`, `workbench.view.interactive`, `interactive.reset`, `interactive.clear`, `interactive.sendSelection` and `interactive.initializeWithProject`.

### 0.2.2 (M9)

Additive (plugins built for 0.2.0 and 0.2.1 keep working):
- Proposed `IWorkspace.GetPackagesAsync` (a project's top-level and transitive NuGet packages) and `IWorkspace.GetProjectPropertiesAsync` (evaluated MSBuild properties), read only.
- Shell commands `packages.manage`, `packages.manageSolution`, `packages.sources`, `packages.tools`, `project.pack`, `project.properties`, `project.references`, `solution.configurationManager`, `solution.projectDependencies`, `project.unload`, `project.reload`, `solution.saveFilter` and `solution.sdks`; the context key `explorerProjectUnloaded`.

### 0.2.1 (M8)

Additive (plugins built for 0.2.0 keep working):
- Proposed APIs: test controllers, workspace, build and debug ([above](#proposed-apis)). Built-in plugins may use proposed APIs in every build.
- Activation events `onTestProjects` and `onSolutionOpened`; menu locations `menuBar/test` and `editor/context`; context keys `testsRunning`, `testExplorerSelection` and `coverageAvailable`.
- Shell commands `editor.activePosition`, `workbench.openFile`, `project.addReferenceTo` and the `testing.*` commands.
- Protocol 0.2: the proposed-API methods (docs/plugin-protocol.md) and `enableProposedApi` in `plugins/load`. A 0.1 host still works with a 0.2 shell (the same major).
- Icons `test-passed`, `test-failed`, `test-skipped`, `test-running`, `test-none`, `beaker`, `run-all`, `filter`, `collapse-all`, `coverage`.

### 0.2.0 (after M7): breaking

The SDK became the assembly and NuGet package **`DaedalusStudio.Plugins.Abstractions`**, with the namespace **`DaedalusStudio.Plugins`**; it was `Daedalus.Plugins.Abstractions` and `Daedalus.Plugins`. The `Daedalus.*` package prefix on nuget.org belongs to unrelated packages, so every Daedalus Studio assembly and namespace moved to `DaedalusStudio.*` (renamed again to `Codalus.*` in [0.3.0](#030-m39)). To update a plugin then:
1. Reference `DaedalusStudio.Plugins.Abstractions`.
2. Replace `using Daedalus.Plugins;` with `using DaedalusStudio.Plugins;`.
3. Set `"engines": { "daedalus": "^0.2.0" }`.

Plugins built for 0.1.x are refused with a message: they reference an assembly that no longer exists. Nothing else changed: the protocol, the manifest format and the API surface are the same.

### Unreleased (M4)

Additive: menu locations `menuBar/build` and `menuBar/debug` (Tools moved after them), build, run and debug commands, and the context keys `buildRunning`, `programRunning`, `hasStartupProject`, `debugActive`, `debugRunning`, `debugStopped` and `hasBreakpoints` (M4 and M5); the `menuBar/git` location, Git commands and the context keys `gitRepository` and `gitMerging` (M6). Menu bar menus are ordered 10, 20, … (Git is 35). The plugin API is unchanged.

### 0.1.1 (M2)

Additive: `IWindow.ShowQuickPickAsync`, `IWindow.ShowInputBoxAsync`, `QuickPickItem`, `InputBoxOptions`; protocol methods `window/showQuickPick` and `window/showInputBox` (protocol version stays 0.1). New menu location `solutionExplorer/context` and solution context keys. Plugins built for 0.1.0 keep working.

### 0.1.0 (M1)

The first API: manifest, activation events, commands, menus, key bindings, settings, notifications, output channels, status bar items, tree and Markdown views. Protocol version 0.1.
