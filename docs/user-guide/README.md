# Codalus IDE user guide

Codalus IDE is a free, open-source (MIT) IDE for .NET on Windows, macOS and Linux.

1. [Installing and first start](getting-started.md)
2. [Solutions and projects: the Properties window, Show All Files, items and templates, opening a folder, external tools](solutions.md)
3. [NuGet packages and .NET tools](packages.md)
4. [Editing and navigating code](editing.md)
5. [Building and running](build-and-run.md)
6. [Debugging](debugging.md)
7. [Web development: HTML, CSS and JavaScript, Hot Reload, Razor and Blazor, endpoints, .http files, certificates and secrets](web.md)
8. [Data: Entity Framework Core, the Database Explorer, table data, queries and SQL projects](data.md)
9. [Testing](testing.md)
10. [Git](git.md)
11. [Terminal and C# Interactive](terminal.md)
12. [The window, settings, themes and keyboard shortcuts](settings.md)
13. [Troubleshooting, logs and crash reports](troubleshooting.md)
14. [Older .NET versions: .NET Core 2.0 to .NET 10, and older SDKs in global.json](dotnet-versions.md)
15. [Containers, Aspire and publishing](containers.md)
16. [Profiling and diagnostics: the Performance Profiler, Diagnostic Tools and .NET Counters](profiling.md)
17. [Keyboard shortcuts on each operating system: completion keys, and the shortcuts Windows, Linux and macOS keep](keyboard.md)
18. [Avalonia: XAML editing, the XAML Previewer, the Live Visual Tree and Live Property Explorer, new Avalonia projects](avalonia.md)
19. [Remote development: SSH, WSL, containers and Dev Containers](remote.md)
20. [AI assistance: AI Chat with the provider you choose, inline and next edit suggestions, AI actions, agent mode, MCP, and what is never sent](ai.md)
21. [.NET MAUI: devices, emulators and simulators, running and debugging on Android and iOS, the workloads and the Android SDK, MAUI XAML](maui.md)
22. [Visual Basic: editing, mixed C# and VB solutions, what the editor adds as you type, project options, tests and debugging](visual-basic.md)
23. [F#: editing with FsAutoComplete and Fantomas, the compile order, F# Interactive, scripts, type providers, analyzers, Expecto tests and debugging](fsharp.md)
24. [WCF: Add Service Reference with dotnet-svcutil, CoreWCF services, the WCF Test Client, and system.serviceModel in configuration files](wcf.md)
25. [WPF, Windows Forms and WinUI 3: opening and building them on every OS, running, debugging and testing on Windows, WPF and WinUI XAML editing, packaged apps, new projects and items](windows-desktop.md)
26. [.NET Framework: SDK-style and non-SDK-style projects on every OS, packages.config, the reference assemblies, Convert to SDK-style, running, debugging and tests on Windows](net-framework.md)
27. [Plugin API reference: every type and member of the plugin API](plugin-api.md)

Writing plugins is covered in [plugins.md](../plugins.md), with the [API reference](plugin-api.md).

## Keyboard shortcuts at a glance

`Ctrl` means `Cmd` on macOS, except where [keyboard.md](keyboard.md) says (completion is the Control key, `⌃Space`). These are the default keyboard scheme's; the VS Code scheme differs where [settings.md](settings.md#keyboard-shortcuts) says. Every command is also in the search box (`Ctrl+Q`) and the command palette (`Ctrl+Shift+P`), and every shortcut can be changed (Tools → Open Keyboard Shortcuts). A few shortcuts differ on Linux and macOS, where the OS keeps the classic one ([keyboard.md](keyboard.md)).

| Area | Shortcut | Command |
|---|---|---|
| General | `Ctrl+Q` | Feature Search: commands, settings and Options pages |
| | `Ctrl+Shift+P` | Command palette |
| | `Ctrl+Alt+I` | AI Chat |
| | `Ctrl+,` | Code Search (go to all) |
| | `Alt+F6`, `Alt+Shift+F6` | Move focus between panes |
| | `Ctrl+Alt+L`, `Ctrl+;` | Solution Explorer, search it |
| | `Ctrl+Shift+O` | Open solution or project |
| | `Ctrl+K Ctrl+R` | Open recent |
| | `Ctrl+Shift+Alt+O` | Open a folder |
| Projects | `F4` | Properties window |
| | `Ctrl+Shift+A`, `Shift+Alt+A` | Add New Item, Add Existing Item |
| | `Alt+Enter` (Solution Explorer) | Project properties |
| Editing | `Ctrl+Space` | Completion |
| | `Ctrl+Shift+Space` | Parameter info |
| | `Ctrl+K Ctrl+I` | Quick Info |
| | `Ctrl+.`, `Alt+Enter` | Quick actions and refactorings |
| | `F2`, `Ctrl+R Ctrl+R` | Rename |
| | `Ctrl+K Ctrl+D` | Format document |
| | `Ctrl+F`, `Ctrl+H`, `F3` | Find, replace, find next |
| | `Ctrl+Alt+Up/Down`, `Shift+Alt+.` | Add carets |
| | `Ctrl+M Ctrl+M` | Toggle folding |
| Navigation | `F12`, `Ctrl+F12` | Go to definition, implementation |
| | `Shift+F12` | Find all references |
| | `Ctrl+T` | Code Search: files, types, members and text, with a preview |
| | `Ctrl+Shift+F`, `Ctrl+Shift+H` | Find in files, replace in files |
| | `Ctrl+-`, `Ctrl+Shift+-` | Navigate back, forward |
| Build and run | `Ctrl+Shift+B`, `F6` | Build solution |
| | `Ctrl+Break` | Cancel the build |
| | `Ctrl+F5` | Start without debugging |
| Debugging | `F5` | Start debugging, continue |
| | `Shift+F5`, `Ctrl+Shift+F5` | Stop, restart |
| | `F10`, `F11`, `Shift+F11` | Step over, into, out |
| | `F9`, `Ctrl+F9` | Toggle a breakpoint, enable or disable it |
| | `Ctrl+Alt+P` | Attach to process |
| | `Shift+Alt+P` | Reattach to the last process |
| | `Ctrl+F10` | Run to Cursor |
| | `Shift+F9` | QuickWatch |
| | `Ctrl+Alt+E` | Exception Settings |
| | `Ctrl+Alt+B`, `Ctrl+Alt+C`, `Ctrl+Alt+H`, `Ctrl+Alt+U` | Breakpoints, Call Stack, Threads, Modules |
| Profiling | `Alt+F2` | Performance Profiler |
| | `Ctrl+Alt+F2` | Diagnostic Tools |
| Data | `Ctrl+Alt+D` | Database Explorer |
| | `Ctrl+Shift+E`, `Ctrl+Shift+Enter` | Execute the query, the current statement |
| | `Ctrl+Shift+L`, `Ctrl+Shift+M` | Estimated plan, actual plan |
| | `Alt+Break` | Cancel the query |
| Web | `Ctrl+Alt+R` | Send the .http request at the caret |
| | `Alt+F10` | Apply code changes (Hot Reload) |
| Testing | `Ctrl+E T` | Test Explorer |
| | `Ctrl+R A`, `Ctrl+R Ctrl+A` | Run, debug all tests |
| | `Ctrl+R T`, `Ctrl+R Ctrl+T` | Run, debug the tests at the cursor |
| | `Ctrl+R L`, `Ctrl+R F` | Repeat the last run, run failed tests |
| Terminal | `` Ctrl+` `` | Show or hide the terminal |
| | `` Ctrl+Shift+` `` | New terminal |
| | `Ctrl+E Ctrl+E` | Send selection to C# Interactive |
| Git | `Ctrl+0 Ctrl+G` | Git Changes |
| | `Ctrl+0 Ctrl+H` | History |
| | `Ctrl+Shift+Alt+B` (in the editor) | Toggle blame |
