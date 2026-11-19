# What's new in 1.0

Codalus IDE 1.0 is the first stable release. Since the first preview, 0.1.0, it has gained its own .NET debugger, the Windows desktop app models and .NET Framework, Visual Basic, F# and .NET MAUI, remote development, AI that works with coding agents, and a long list of fixes and speed-ups for large solutions. From 1.0 on, an update within 1.x keeps your settings, the command line and your user data working ([below](#what-1x-keeps-stable)). Each release's own notes are in the [changelog](../../CHANGELOG.md).

## New since 0.1

**Debugging**
- **The Codalus debugger**, Codalus IDE's own .NET debugger, is the default ([debugging.md](debugging.md#the-codalus-debugger)). Over netcoredbg it adds Set Next Statement, Step Into Specific, the Disassembly, Registers and Memory windows, symbol servers, and expressions with `new`, lambdas and LINQ. netcoredbg stays as a fallback (`debug.adapter`).
- **Hot Reload while debugging**: C#, Visual Basic and Razor edits apply to the program you debug, with **Apply Code Changes** (`Alt+F10`), on Continue, or when you save ([Hot Reload while debugging](debugging.md#hot-reload-while-debugging)).
- **Inline values** after each line at a stop ([inline values](debugging.md#inline-values)), and **async code**: Step Over an `await` stops on the method's next line, runtime async (.NET 11) included, and the Call Stack shows the methods awaiting the current one ([async code](debugging.md#async-code-and-runtime-async-net-11)).
- **Compose and Aspire under the debugger**: every .NET service of a compose app on your Debug build, debugged in one session, and every project of an Aspire AppHost ([containers.md](containers.md#compose)).

**App models and languages**
- **WPF, Windows Forms and WinUI 3** open, edit and build on every OS, and run, debug and test on Windows: the Windows Forms designer with its Toolbox, the WPF XAML Previewer, the Live Visual Tree, and packaged apps ([windows-desktop.md](windows-desktop.md)).
- **.NET Framework** projects, old-style ones and `packages.config` included, build on every OS and debug on Windows with a .NET Framework debugger of Codalus IDE's own ([net-framework.md](net-framework.md)). **WCF**: Add Service Reference, CoreWCF, and a WCF Test Client ([wcf.md](wcf.md)).
- **Visual Basic** ([visual-basic.md](visual-basic.md)) and **F#** ([fsharp.md](fsharp.md)), each with editing, tests and debugging.
- **.NET MAUI** on Android, iOS simulators, Mac Catalyst and Windows, with the devices, emulators and Android SDK tools built in ([maui.md](maui.md)).
- **XAML depth**: XAML Hot Reload, rename across C# and XAML, XAML refactorings, failed bindings in the Error List and XAML breakpoints, for Avalonia, and for WPF on Windows ([avalonia.md](avalonia.md#xaml-hot-reload)).
- **.NET 11**: Codalus IDE runs on it, with its C# 15 compiler in the editor.

**Remote development**
- Open a folder on another machine over SSH, in WSL, in a container or in a Dev Container: the build, the language services, terminals and the debugger run there ([remote.md](remote.md)).

**AI, off until you turn it on**
- **AI Chat** with the provider you choose, inline and next edit suggestions, and AI actions ([ai.md](ai.md#ai-chat)).
- **Coding agents**: Claude, Codex, Gemini CLI, GitHub Copilot and others in AI Chat over ACP, connected from one dialog, or in **AI Console** with their own CLI ([external agents](ai.md#external-agents-acp), [AI Console](ai.md#ai-console)).
- **The IDE's tools for agents** (`codalus mcp`): the compiler's symbols and references, builds, tests, running and debugging, the web app's browser and the desktop app's windows, Git, packages and migrations, each action under your approval rules ([the IDE's tools](ai.md#the-ides-tools)).

**The IDE around them**
- **Code Search** (`Ctrl+T`) with a preview you can edit ([editing.md](editing.md#code-search)).
- **Git**: every repository of a solution in Git Changes, Manage Branches and the Git Repository window, the classic IDE's merge editor, and signing in to GitHub, GitLab and Azure DevOps (Server too) from Git Changes ([git.md](git.md)).
- **Your setup**: a first-run setup ([getting-started.md](getting-started.md#the-first-run-setup)); four theme families beside the Classic themes ([themes](settings.md#themes)); profiles and Settings Sync ([Settings Sync](settings.md#settings-sync)); the VS Code, ReSharper and Emacs keys; and the interface's text ready for translation.
- **Plugins**: the plugin API is 1.x, with the Plugin Gallery, plugins as NuGet packages and `dotnet new codalus-plugin` ([plugins.md](../plugins.md)).
- **Large solutions**: opening, analyzing, typing, Git and test runs with hundreds of projects and thousands of files stay responsive.
- **No telemetry from the tools Codalus IDE runs**: builds, tests and the other tools it starts run with their telemetry turned off ([settings.md](settings.md#updates-and-network-access)).
- **Installing**: the macOS installer and app are signed and notarized, Linux packages install from apt, dnf and zypper repositories, and Arch Linux has an install command ([getting-started.md](getting-started.md#installing)).

## What 1.x keeps stable

Anything that works with one 1.x version works with every later 1.x version, and means the same ([compatibility.md](compatibility.md)):
- your **settings**: every key and the values it accepts;
- the **`codalus` command line**: every form and option `codalus --help` lists;
- your **user data**: keyboard shortcuts and snippets, saved window layouts, profiles, and the folders and repositories Settings Sync uses;
- the **plugin API**, for plugins built for 1.0 ([plugins.md](../plugins.md)).

A 1.x version only adds, or changes what you keep the first time it starts, without you doing anything. What goes away is deprecated first and removed only in 2.0. The one exception is the netcoredbg debugger: a 1.x version may deprecate `debug.adapter`'s `netcoredbg` value, and a later one remove it once the Codalus debugger passes the same tests on every operating system ([netcoredbg](compatibility.md#netcoredbg)).

## Known limits

The pages say more, and list the smaller ones:
- **Debugging** ([known limitations](debugging.md#known-limitations)): no data breakpoints and no mixed-mode (native) debugging; the Codalus debugger reads sources from your disk, not from embedded sources or Source Link; F# expressions are read as C#'s subset; the async call stack is for state machines only; a macOS remote machine is debugged by netcoredbg.
- **Windows desktop apps** ([limits](windows-desktop.md#limits), [not available yet](windows-desktop.md#not-available-yet)): they run, debug, preview and design on Windows only. The Windows Forms designer has no Data Sources window or component designer, and a control vendor's own designers (DevExpress's Grid Designer and smart tags) aren't available ([#461](https://github.com/stavroskasidis/codalus-ide/issues/461)). There's no design surface for WPF, and no previewer or XAML Hot Reload for WinUI 3.
- **.NET MAUI** ([not available yet](maui.md#not-available-yet)): no debugging on an iPhone or iPad, and no MAUI XAML previewer or XAML Hot Reload.
- **.NET Framework** ([not supported](net-framework.md#not-supported)): no Mono, mixed-mode debugging, Edit and Continue or test coverage; COM references, ClickOnce, Web Forms, C++/CLI and VSTO aren't supported.
- **Containers** ([limits](containers.md#limits)): Linux containers only, no HTTPS inside them, and profiling a compose service on Linux engines only.
- **Testing** ([known limitations](testing.md#known-limitations)): tests that start child processes can't be debugged, and tests run in WSL, containers or over SSH aren't debugged or covered there.
- **AI** ([the safety model, and its limits](ai.md#the-safety-model-and-its-limits)): an external agent can read and write files by itself; only the changes it makes through the IDE show in the preview first.
- **Platforms**: Windows 11 x64, macOS 15 or newer on Apple silicon, and Linux x64 and arm64. There are no builds for Windows on Arm or Intel Macs.

## Windows signing

The Windows installer isn't code-signed yet: signing follows in a 1.0.x update. Until then, its first start shows SmartScreen's "Windows protected your PC": choose **More info**, then **Run anyway** ([installing](getting-started.md#installing)). Windows asks only once. The macOS installer and app are signed and notarized, and the Linux repositories and `.rpm` packages are signed with the project's key.
