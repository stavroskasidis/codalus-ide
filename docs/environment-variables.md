# Environment variables

This page lists every `CODALUS_*` environment variable that Codalus IDE, its hosts, the scripts in `eng/`, the workflows and the tests read or set, with the file that reads it. Command-line options (`--user-data-dir`, `--locale`, `--remote` and the rest) are in CLAUDE.md's "Running the shell"; the user guide mentions the user-facing variables where they matter (`docs/user-guide/git.md`, `docs/user-guide/troubleshooting.md`). Unless a row says otherwise, an empty value counts as unset, a path can be absolute or relative to the working directory, and a switch is on only with the exact value given (`1`, or `0` for the ones that turn something off).

## Using the IDE

| Variable | What it does |
|---|---|
| `CODALUS_USER_DATA_DIR` | A folder that holds all of the IDE's files: `config/` (settings, keybindings), `data/` (plugins), `state/`, `cache/` and `logs/`, instead of each OS's usual places. `--user-data-dir` does the same and wins when both are given. Sign-ins made under it are kept apart from the default profile's. For portable installs, tests and benchmarks. `src/Codalus.Platform/AppPaths.cs` |
| `CODALUS_LOCALE` | The display language when `--locale` isn't given: a locale with a catalogue (`en`, …) or a pseudo-language (`qps-ploc`, `qps-plocm`). It wins over the `workbench.locale` setting and the OS's language. Read once at startup. `src/Codalus.Shell/ShellHost.Localization.cs` |
| `CODALUS_NO_SHELL_PATH` | Set to any value to skip importing the login shell's `PATH` on macOS and Linux. Without it, an IDE that wasn't started from a terminal (no `TERM`) asks the user's login shell for its `PATH` once at startup and puts those folders first, so a Finder or desktop launch finds `dotnet`, Homebrew and `~/.dotnet`. `src/Codalus.Workbench/Setup/LoginShellPath.cs` |
| `CODALUS_NODE_CACHE` | A folder used instead of the IDE's cache folder for on-demand downloads: Node.js, the web language servers, vscode-js-debug and the netcoredbg builds for remote targets. The Components window lists what is installed there. Several windows or test runs can share one folder; `eng/smoke-test.sh` uses `.work/node-cache`, and the web and browser tests set their own. `src/Codalus.Shell/ShellHost.WebLanguages.cs` |
| `CODALUS_GIT` | The git executable to run instead of `git` from the `PATH`. Help → Check Environment and the smoke test use it too. `src/Codalus.Workbench/Git/GitService.cs` |
| `CODALUS_UPDATE_TOKEN` | A GitHub token for reading a private repository's releases when checking for updates. Without it, the GitHub CLI's token (`gh auth token`) when it is signed in. The public repository needs neither. `src/Codalus.Shell/Infrastructure/Updates.cs` |
| `CODALUS_CREDENTIAL_STORE` | `memory` keeps hosting sign-ins in the process only, instead of the OS's credential store (Credential Manager, Keychain, libsecret), so they last until the IDE closes. Any other value uses the OS's store. The shell tests set it, and `eng/smoke-test.sh` sets it on Linux. `src/Codalus.Platform/Credentials/CredentialStore.cs` |

## Hosts and tools

Each host variable takes the path of the host's apphost or its `.dll` (run with `dotnet`); without it, the shell uses the folder next to it that a published install has. The shell tests point them at the hosts built with the tests (`tests/Codalus.Shell.Tests/ShellFixture.cs`). The benchmark harness removes `CODALUS_PLUGINHOST`, `CODALUS_BUILDHOST`, `CODALUS_LSHOST` and `CODALUS_USER_DATA_DIR` from the shells it starts, so they use their published hosts.

| Variable | What it does |
|---|---|
| `CODALUS_PLUGINHOST` | `codalus-pluginhost` instead of `pluginhost/`. `src/Codalus.Workbench/Plugins/PluginManager.cs` |
| `CODALUS_BUILDHOST` | `codalus-buildhost` instead of `buildhost/`. The build host tests also use it, to test a published host. `src/Codalus.Workbench/Projects/SolutionService.cs` |
| `CODALUS_LSHOST` | `codalus-lshost`, the C# language service host, instead of `lshost/`. `src/Codalus.LanguageServices.Client/LanguageHostClient.cs` |
| `CODALUS_REPLHOST` | `codalus-replhost`, the C# Interactive host, instead of `replhost/`. `src/Codalus.Workbench/Interactive/InteractiveService.cs` |
| `CODALUS_MARKUPHOST` | `codalus-markuphost` (JSON, XML and MSBuild, YAML, `.editorconfig`) instead of `markuphost/`. `src/Codalus.Workbench/Languages/MarkupHostLaunch.cs` |
| `CODALUS_DEBUGHOST` | `codalus-debughost`, the debug companion host, instead of `debughost/`. `src/Codalus.Workbench/Debugging/DebugCompanion.cs` |
| `CODALUS_T4HOST` | `codalus-t4host`, which runs T4 templates, instead of `t4host/`. `src/Codalus.Workbench/CustomTools/CustomTools.cs` |
| `CODALUS_DATAHOST` | `codalus-datahost`, the database explorer's host and (with `--lsp`) the SQL language server, instead of `datahost/`. `src/Codalus.Workbench/Data/DataService.cs` |
| `CODALUS_DIAGHOST` | `codalus-diaghost`, the profiling and diagnostics host, instead of `diaghost/`. `src/Codalus.Workbench/Diagnostics/DiagnosticsService.cs` |
| `CODALUS_EFAGENT` | `codalus-efagent.dll`, which reads an EF Core model for the model view, instead of `efagent/` next to the build host (`buildhost/efagent/`). The build host reads it, inheriting it from the shell. `src/Codalus.Projects.Host/SolutionSession.EntityFramework.cs` |
| `CODALUS_PREVIEWER_AGENT` | `Codalus.Previewer.Agent.dll`, the startup hook for the XAML Previewer and the Live Visual Tree, instead of `debughost/` next to the shell (then next to the running assembly, for tests). `src/Codalus.Workbench/Previewer/PreviewService.cs` |
| `CODALUS_STARTUP_HOOK` | `Codalus.Debugging.StartupHook.dll`, which holds a program started in the integrated terminal until the debugger attaches, instead of `debughost/` next to the shell (then next to the running assembly). `src/Codalus.Workbench/Debugging/HeldLaunch.cs` |
| `CODALUS_SERVER` | A framework-dependent `codalus-server.dll`, or its folder, for remote windows: the server that `--remote local` starts, and the one bundled for a remote machine when the install has no `remote/<rid>/` build for it (it is used before `remote/server/`). For development and tests. `src/Codalus.Workbench/Remote/RemoteServerBundle.cs` |
| `CODALUS_NETCOREDBG` | The netcoredbg executable, or its folder. Without it: `debugger/` next to the shell, then `.work/netcoredbg` in a source checkout (`eng/get-netcoredbg.sh`). `eng/validate.sh` and the pipeline's smoke job set it. `src/Codalus.Debugging/DebugAdapter.cs` |
| `CODALUS_NETCOREDBG_X64` | Apple silicon only: the x64 netcoredbg (or its folder) that debugs .NET 5 and earlier under Rosetta 2. Without it: `debugger/x64/` next to the shell, then `.work/netcoredbg-x64` (`eng/get-netcoredbg.sh --x64`). On other machines the regular netcoredbg is used. `src/Codalus.Debugging/DebugAdapter.cs` |
| `CODALUS_NETCOREDBG_<PLATFORM>` | The netcoredbg to copy to a remote target (SSH, a container, WSL) of that platform: a folder with `netcoredbg` in it, or an archive (`.tar.gz` or `.zip`). The name is built from the target's platform, upper case with `_` for `-`: `<PLATFORM>` is `LINUX_X64`, `LINUX_ARM64` or `OSX_ARM64`. It is tried first, then this machine's netcoredbg when the platforms match, then `.work/netcoredbg-<platform>` (`eng/get-netcoredbg.sh --platform <platform>`), then a verified download into the cache. The remote debugging shell tests set `CODALUS_NETCOREDBG_LINUX_X64`. `src/Codalus.Workbench/Debugging/Remote/RemoteDebuggers.cs` |

## Diagnostics and tuning

| Variable | What it does |
|---|---|
| `CODALUS_NETCOREDBG_LOG` | A folder: netcoredbg on this machine starts with `--engineLogging`, writing its DAP traffic to `netcoredbg-<time>-<id>.log` there. `src/Codalus.Debugging/Transport.cs` |
| `CODALUS_JSDEBUG_TRACE` | A folder: each browser debugging session writes vscode-js-debug's log there (`js-debug-<time>.log`); `eng/smoke-test.sh` sets it to `<results>/js-debug`, kept when the smoke test fails. `src/Codalus.Workbench/Debugging/Browsers.cs` |
| `CODALUS_GENERATOR_TIMINGS` | `1`: after each background pass, the C# host runs the source generators of the projects it compiled once more, one project at a time, and logs each generator's cost. It doubles the generators' work, so it is for measuring only. `src/Codalus.LanguageServices.Host/GeneratorTimings.cs` |
| `CODALUS_LANGUAGE_CACHE` | `0` turns off the C# host's state between sessions: each project's background diagnostics and reference assembly in `obj/<config>/<tfm>/codalus.language.*`. `src/Codalus.LanguageServices.Host/LanguageStateCache.cs` |
| `CODALUS_DESIGNTIME_CACHE` | `0` turns off the build host's design-time build cache (`obj/codalus.designtime.json`), so every open runs the design-time builds. `src/Codalus.Projects.Host/DesignTimeCache.cs` |
| `CODALUS_DESIGNTIME_CACHE_TRACE` | A folder: the build host writes what each project fingerprint hashed there, one text file per fingerprint, to compare two that should have matched. `src/Codalus.Projects.Host/Fingerprint.cs` |
| `CODALUS_DESIGNTIME_NODES` | A positive number: the most MSBuild nodes a batch of design-time builds uses (default: half the processors). Batches of fewer than six projects stay in process. `src/Codalus.Projects.Host/DesignTimeBuilder.cs` |
| `CODALUS_BUILD_IN_PROCESS` | `1`: builds run in the build host's own MSBuild node instead of worker nodes, as they always do for MSBuild before 17, and design-time batches stay in process. `src/Codalus.Projects.Host/SolutionBuilder.cs` |
| `CODALUS_DEBUG_WAIT_SECONDS` | How many seconds a program held for the debugger waits before it goes on without it (default 120). The startup hook clears it, so the program's child processes don't see it. `src/Codalus.Debugging.StartupHook/StartupHook.cs` |
| `CODALUS_PLUGINHOST_DEBUG_WAIT` | How many seconds the plugin host of Debug Plugin in Development Instance waits for a debugger before it activates the plugins (default 60). The tests set `1`. `src/Codalus.Plugins.Host/PluginHostServer.cs` |
| `CODALUS_REMOTE_LATENCY_MS` | A positive number of milliseconds: simulated network latency on a remote window's connection, for budgets and tests. `src/Codalus.Shell/ShellHost.Remote.cs` |

## Smoke test, benchmarks and UI snapshots

| Variable | What it does |
|---|---|
| `CODALUS_SMOKE_ONLY` | `<id>[,<id>…]`: `--smoke-test` runs only these steps and reports the others as skipped. A step that needs an earlier step's state needs that step listed too. For diagnosing one step. `src/Codalus.Shell/Infrastructure/SmokeTest.cs` |
| `CODALUS_SMOKE_SKIP` | `<id>[,<id>…]=<reason>`: `--smoke-test` skips these steps and puts the reason in the report, for a known gap on one platform. Nothing in the repository sets it at the moment. `src/Codalus.Shell/Infrastructure/SmokeTest.cs` |
| `CODALUS_BROWSER_HEADLESS` | `1` runs the browser of browser debugging (Chrome, Edge or Chromium) headless. `eng/smoke-test.sh` and the browser tests set it. `src/Codalus.Workbench/Debugging/Browsers.cs` |
| `CODALUS_BENCH_EVENTS` | A file: the shell appends timestamped benchmark events to it as JSON lines (`ev`, `wall_us`, `mono_us`; see `benchmarks/README.md`). The benchmark harness sets it for each launch. `src/Codalus.Diagnostics.BenchEvents/BenchEvents.cs` |
| `CODALUS_BENCH_SHELL_DIR` | The folder of another published shell for the harness to measure instead of `artifacts/shell`, for before and after numbers. `benchmarks/Codalus.Benchmarks.Harness/Targets.cs` |
| `CODALUS_BENCH_SETTINGS` | The text of a `settings.json` that the harness gives the shell's fresh user data for scenarios without a solution, to compare configurations. `benchmarks/Codalus.Benchmarks.Harness/Targets.cs` |
| `CODALUS_BENCH_VSCODE` | The path of the VS Code executable that the harness compares against, when it isn't in its default place. The pipeline's benchmarks job sets it on Windows. `benchmarks/Codalus.Benchmarks.Harness/Targets.cs` |
| `CODALUS_BENCH_RIDER` | The path of the Rider executable that the harness compares against. Without it, the newest `rider64.exe` under `C:\Program Files\JetBrains` on Windows, and none elsewhere. `benchmarks/Codalus.Benchmarks.Harness/Targets.cs` |
| `CODALUS_UI_SNAPSHOTS` | A folder: the headless UI tests (rendering in every theme, the polish, navigation, Test Explorer and target framework tests) save the frames they render there, for a visual review. `tests/Codalus.Shell.Tests/RenderingTests.cs` |
| `CODALUS_BUDGET_LOG` | A file: the profiler tests append their measured numbers (sampling overhead, flame graph frame time) to it. `tests/Codalus.Workbench.Tests/DiagnosticsHostTests.cs` |

## Tests and validation

| Variable | What it does |
|---|---|
| `CODALUS_UPDATE_PUBLIC_API` | `1`: `PublicApiTests` rewrites `src/Codalus.Plugins.Abstractions/PublicAPI.txt` from the plugin API instead of failing on additions. `tests/Codalus.Plugins.Tests/PublicApiTests.cs` |
| `CODALUS_UPDATE_API_REFERENCE` | `1`: `ApiReferenceTests` regenerates `docs/user-guide/plugin-api.md` from the plugin API's XML docs. `tests/Codalus.Plugins.Tests/ApiReferenceTests.cs` |
| `CODALUS_UPDATE_LOCALIZATION` | `1`: the shell tests rewrite `src/Codalus.Shell/Localization/en.json` and `tests/Codalus.Shell.Tests/localization-literals.txt` after UI text changed (`docs/translating.md`). `tests/Codalus.Shell.Tests/LocalizationCatalogTests.cs` |
| `CODALUS_TEST_WSL` | The WSL distribution (default `Ubuntu`, with .NET in it) that the remote, remote debugging and test environment tests use as their target on Windows. `tests/Codalus.Debugging.Tests/RemoteFixture.cs` |
| `CODALUS_TEST_CONTAINER_TOOL` | The container CLI for the container, Aspire and publishing tests (`docker`, `podman`, or a command line). Without it, the engine the shell would pick: `docker` when it is on the `PATH`, else `podman`. `tests/Codalus.Workbench.Tests/ContainerRuntimeTests.cs` |
| `CODALUS_TEST_CONSOLE_WINDOWS` | `1` runs the Windows tests that debug in a console window of its own outside CI. They open a window on the desktop, so they are off locally by default; CI runs them. `tests/Codalus.Debugging.Tests/ConsoleWindowLaunchTests.cs` |
| `CODALUS_RESEARCH` | `1` runs the Hot Reload-while-debugging research test (ADR 0040), which is slow and not part of regular runs. `tests/Codalus.Debugging.Tests/HotReloadResearchTests.cs` |
| `CODALUS_DOTNET_COMPAT` | The dotnet root of the target framework matrix from `eng/get-runtimes.sh` (default `.work/dotnet-compat`). Without a root the matrix tests skip; when the variable is set, a missing or incomplete root fails them. `eng/validate.sh` and `compat.yml` set it. `tests/Codalus.TargetFrameworks.Tests/CompatRoot.cs` |
| `CODALUS_TRASH_DIR` | A folder: the build host moves deleted files and folders there instead of the OS trash. The shell and build host tests set it. `src/Codalus.Projects.Host/Trash.cs` |
| `CODALUS_TEMPLATE_HIVE` | A folder: `dotnet new` runs with `--debug:custom-hive` there, in the workbench's template service and in the build host's `templates/instantiate`, so tests don't touch the user's template settings. `src/Codalus.Workbench/Projects/TemplateService.cs` |
| `CODALUS_FIXTURE_HANG` | `1` makes the `Hangs` tests of `samples/Testing` sleep for 10 minutes. `DotNetTestRunControlTests` sets it in a test run's environment to test cancelling. `samples/Testing/tests/XunitV3Tests/CalculatorTests.cs` |
| `CODALUS_M26A_TEST` | A value a test sets to check `${env:…}` expansion in `.codalus/tasks.json` and `launch.json`. `tests/Codalus.Workbench.Tests/ProjectSystemDepthTests.cs` |
| `CODALUS_REMOTE_TEST` | A variable a test passes in a remote pseudo-terminal's environment; nothing reads it. `tests/Codalus.Workbench.Tests/RemoteServerTests.cs` |
| `CODALUS_WSL_DIR` | The Linux folder that `eng/wsl-test.sh` mirrors the working tree into (default `~/codalus`). Give each git worktree its own. `eng/wsl-test.sh` |

## Packaging and releases

| Variable | What it does |
|---|---|
| `CODALUS_FEED_KEY` | The update feed's signing key (ECDSA P-256, PEM). `eng/package.sh` signs each `releases.<channel>.json` with it (`.sig`); without it the feed is unsigned and installed builds refuse it. The pipeline passes the `FEED_SIGNING_KEY` secret; `eng/test-update.sh` uses a throwaway key. `eng/package.sh` |
| `CODALUS_PREVIOUS_RELEASE` | A folder with the previous release of the channel (its `releases.<channel>.json` and full `.nupkg`), so Velopack also builds a delta package. `eng/package.sh` |
| `CODALUS_RELEASE_NOTES` | A Markdown file with the release notes that the update feed shows. `eng/package.sh` |
| `CODALUS_PACKAGES_OUT` | The output folder instead of `artifacts/packages`. `eng/test-update.sh` builds into its own. `eng/package.sh` |
| `CODALUS_CODESIGN_IDENTITY` | macOS: the identity that signs the app (and the `.pkg`, unless `CODALUS_INSTALLER_IDENTITY` is set). Without it the packages are unsigned. The pipeline passes the secret of the same name. `eng/package.sh` |
| `CODALUS_INSTALLER_IDENTITY` | macOS, with `CODALUS_CODESIGN_IDENTITY`: the identity that signs the `.pkg` (default: the app's). `eng/package.sh` |
| `CODALUS_NOTARY_PROFILE` | macOS, with `CODALUS_CODESIGN_IDENTITY`: the notarytool keychain profile that notarizes the packages. `eng/package.sh` |
| `CODALUS_UPDATE_SOURCE` | A local folder with update feeds and packages that the app updates from instead of GitHub's releases (`eng/test-update.sh`, `codalus --update-now`). `src/Codalus.Shell/Infrastructure/Updates.cs` |
| `CODALUS_UPDATE_TEST_KEY` | The feed public key to trust instead of the project's (base64 of the DER public key), for a `CODALUS_UPDATE_SOURCE` folder only. `src/Codalus.Shell/Infrastructure/Updates.cs` |

## Internal

These are set by the IDE for its own child processes. Don't set them yourself.

| Variable | What it does |
|---|---|
| `CODALUS_ASKPASS_PIPE` | The pipe of the running IDE's askpass server, set for git and ssh with `GIT_ASKPASS` and `SSH_ASKPASS` pointing at `codalus`. Started with it, the token and one argument, `codalus` asks the IDE for the answer and prints it. `src/Codalus.Workbench/Git/Askpass.cs` |
| `CODALUS_ASKPASS_TOKEN` | The secret that the askpass pipe expects with each question. `src/Codalus.Workbench/Git/Askpass.cs` |
| `CODALUS_DEBUG_WAIT` | An empty temporary folder for a program held for the debugger: the startup hook writes the process id to `pid` there and waits for `go`. The hook clears it. `src/Codalus.Workbench/Debugging/HeldLaunch.cs` |
| `CODALUS_DISPLAY_LOCALE` | The display language, set for every process the IDE starts. Plugins read it through `PluginLocalization` in the plugin API. To choose the language, use `--locale` or `CODALUS_LOCALE`. `src/Codalus.Plugins.Abstractions/PluginLocalization.cs` |
| `CODALUS_PREVIEW_AGENT_PORT` | The loopback port that the previewer agent in Avalonia's designer host connects back to. The agent clears it. `src/Codalus.Workbench/Previewer/PreviewerSession.cs` |
| `CODALUS_PREVIEW_AGENT_TOKEN` | The token the previewer agent says hello with. `src/Codalus.Workbench/Previewer/PreviewerSession.cs` |
| `CODALUS_LIVE_AGENT_PORT` | The loopback port that the live agent in a running Avalonia app (Live Visual Tree, Live Property Explorer) connects back to. The agent clears it. `src/Codalus.Workbench/Previewer/LiveSession.cs` |
| `CODALUS_LIVE_AGENT_TOKEN` | The token the live agent says hello with. `src/Codalus.Workbench/Previewer/LiveSession.cs` |
| `CODALUS_PLUGINHOST_DEBUG_FILE` | Given to the plugin host of plugins under development when they are debugged (`--debug-plugin-host`): the host writes its process id to this file and holds activations until a debugger attaches. `src/Codalus.Workbench/Plugins/PluginManager.cs` |
| `CODALUS_BASH_LOGIN` | `1` when a bash terminal profile asks for a login shell: the shell integration script then loads the login files instead of `.bashrc`, and unsets it. `src/Codalus.Workbench/Terminal/TerminalService.cs` |
| `CODALUS_USER_ZDOTDIR` | The user's own `ZDOTDIR` (or `HOME`) while a zsh terminal starts with `ZDOTDIR` pointing at the shell integration scripts, which load the user's files from it, restore `ZDOTDIR` and unset it. `src/Codalus.Workbench/Terminal/TerminalService.cs` |

Two names in the code look like environment variables but aren't: `CODALUS_SVG_CONVERTER` is a compilation symbol of `codalus-pluginhost` (`src/Codalus.Plugins.Host/Codalus.Plugins.Host.csproj`) that compiles in the SVG converter of `eng/icons/CompileIcons.cs` for `--compile-icons`, and `__CODALUS_PATH__` is the marker `src/Codalus.Workbench/Setup/LoginShellPath.cs` prints around the login shell's `PATH`.
