# Changelog

Hand-written notes for each release. The release pages add the list of changes, grouped by milestone, from the commits.

## 0.3.0

In progress on `develop`; nightlies show these notes.
- **Codalus runs on .NET 11** (M30b, on .NET 11's second release candidate until its release in November): install the [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0), which its helper processes now need; SDKs of older versions still build your solutions next to it. The C# editor has .NET 11's compiler, so C# 15 (unions, collection expression arguments, labeled `break` and `continue`) shows no false errors, and Razor files use the .NET 11 SDK's Razor generator. Plugins built for .NET 10 keep working.
- **Git Changes looks like a classic IDE's**: a branch picker with Fetch, Pull, Push and Sync, the outgoing and incoming counts, and Publish branch at the top; a commit button that commits the staged changes or all of them, and can also push or sync; collapsible Related Items, Merge Conflicts, Staged Changes, Changes and Stashes sections; files as a tree or a sortable list with a filter; stashes applied, popped or dropped in the window.
- **Releases**: one pipeline builds, tests and releases every branch. Every push to `develop` that passes it is published as a nightly, and the website deploys only once a stable release has passed every check.
- **Documentation**: the user guide, the website's Features page and the README cover everything up to 0.2.1.
- **Fixed:** after dragging the bottom tool windows into a group of their own, tool windows such as the Plugin Gallery, Coverage Results and Code Metrics Results opened in a new group stacked above them. A tool window now opens as a tab where it was last closed, or in the group of its usual place.
- **Fixed:** a tool window dragged among the documents showed its whole content in its tab. It now has a document tab with its title and icon.
- **Fixed:** opening the first tool window on the left (such as the Start page) could leave the documents area under a third of the window, with the right pane taking over half.
- **Fixed:** on a Mac whose home folder is on another disk than the app, **Restart to update** started the old version again and offered the same update. The update is now prepared on the app's own disk. A 0.2.1 install on such a Mac can't update past the bug itself: install this release's `.pkg` once.

## 0.2.1

A maintenance release: two fixes in the app, and 0.2.0 released again by the new release process.
- **Fixed:** floating a tool window or a document made it disappear until the app restarted. It now opens in a window of its own; closing that window closes it, and showing it again puts it back in the main window.
- **Security:** the data host's `System.Security.Cryptography.Pkcs` (from the SQL Server client) is 10.0.13, past two known vulnerabilities in 9.0.18 (GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7).
- **Releases are atomic.** A stable release now publishes nothing until every check has passed: the tests and the smoke tests on every OS, the benchmarks, the installers installed and smoke-tested, and the apt, dnf and zypper repositories built and installed from. Then it publishes the release page, the website with the Linux repositories, and the AUR package, in that order. For 0.2.0 the website and the Linux repositories didn't update.
- Fixes in the tests and the tools around the release.

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
- **Fixed:** opening a project in an app started from the macOS Finder or a Linux desktop failed with "Path to dotnet executable is not set": the IDE now takes the login shell's PATH and finds the .NET SDK where its installers put it.
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
