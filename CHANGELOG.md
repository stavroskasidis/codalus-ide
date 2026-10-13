# Changelog

Hand-written notes for each release. The release pages add the list of changes, grouped by milestone, from the commits.

## 0.2.0

**Daedalus Studio is now Codalus IDE** ([ADR 0056](docs/adr/0056-rename-to-codalus.md)): the CLI is `codalus`, the packages are `codalus` (and `codalus-bin` on the AUR), and the repository is `stavroskasidis/codalus-ide`. It's a clean break, made while there are no users to move:
- settings, state and plugins live in new folders (`Codalus`, `codalus`); nothing is carried over from `Daedalus Studio`;
- environment variables are `CODALUS_*`, and per-solution files are under `.codalus/`;
- 0.1.0 doesn't update to 0.2.0: uninstall it and install Codalus IDE.

New since 0.1.0:
- **Remote development** (M21): open a folder on an SSH machine, in a WSL distribution, in a running container or in a Dev Container (File → Open → Remote Window). The build, the C# language service, terminals and the debugger run there; forwarded ports, the Ports window, and reconnecting after a network drop. Tests can run in WSL, Docker or over SSH (`testenvironments.json`).
- **Plugin API 1.0** (M22a, [ADR 0058](docs/adr/0058-plugin-api-1-0.md)): the API is stable, and minor releases only add to it. New for plugins: color and file icon themes in VS Code's formats, debug adapters, declarative views and custom editors, file system, sign-in and source control providers, and tasks with progress. Plugins built for 0.x ask for `^1.0.0` ([changelog](docs/plugins.md#changelog)).
- **Running tasks in the status bar**: loading projects (*Loading project 29 of 96: …*), restoring packages and builds show their progress where "Ready" is, with a progress bar and cancel.
- **Personalization** (M23): Fonts and Colors over any theme, the ReSharper and Emacs keyboard schemes, importing VS Code settings and `.vssettings`, Export and Import Settings, saved window layouts, full screen and distraction-free mode, profiles, settings sync with a folder or a Git repository, Tools → Customize for menus and toolbars, and the interface's text ready for translation.
- **A start window** (M38) with recent solutions, projects and folders, and **Help → Check Environment** (M37), which finds and offers to install a missing .NET SDK, git and the optional tools.
- **Avalonia**: the Live Visual Tree and Live Property Explorer for running apps (M19b), and the Avalonia templates in New Project.
- Document tabs that fit their group, with pinned and selected tabs always in view, and more polish (M34e); and the new mark.

## 0.1.0

The first preview release of Daedalus Studio, an open-source (MIT), cross-platform IDE for .NET. It covers the editor, the project system, NuGet, build and run, debugging, testing, Git, the terminal and C# Interactive, profiling, data tools, containers and Aspire, web and Blazor, and Avalonia XAML: see the [features](https://github.com/stavroskasidis/daedalus-studio#features) and the [user guide](https://github.com/stavroskasidis/daedalus-studio/blob/master/docs/user-guide/README.md). It's a 0.x release: expect rough edges, and please report what you find in the [issues](https://github.com/stavroskasidis/daedalus-studio/issues).

**Before you install**: you need the .NET SDK 10.0 or newer and git 2.30 or newer ([installing and first start](https://github.com/stavroskasidis/daedalus-studio/blob/master/docs/user-guide/getting-started.md)).

**The installers aren't code-signed yet**, so each OS asks once:
- **Windows** ("Windows protected your PC"): choose **More info**, then **Run anyway**.
- **macOS** ("Apple could not verify…"): close the message, open System Settings → Privacy & Security, and choose **Open Anyway** next to Daedalus Studio (or the installer); then open it again.
- **Linux**: the `.deb` and `.rpm` packages and our apt and dnf repositories are signed with the [project's key](https://github.com/stavroskasidis/daedalus-studio/blob/master/eng/packaging/linux/daedalus-studio-packages.asc).

Installed builds update themselves from signed feeds (the Velopack installers and the AppImage), and `SHA256SUMS.txt` lists every file's checksum.

**Platforms**: Windows 11 x64, macOS 15 or newer on Apple silicon, and Linux x64 and arm64. Windows on Arm and Intel Macs aren't built yet.

**Not in this release yet**: remote development (SSH, WSL, containers), Hot Reload while debugging, .NET MAUI, Visual Basic and F#; see the [roadmap](https://github.com/stavroskasidis/daedalus-studio/blob/master/docs/roadmap.md).
