# 0010. Test platform integration

- **Status:** accepted (M8 plan approval, 2026-10-09)
- **Date:** 2026-10-09
- **Deciders:** maintainer

## Context

M8 (`docs/roadmap.md`) asks for a Test Explorer that discovers, runs, debugs and reports tests across a solution:
- **frameworks:** xUnit v2 and v3, NUnit 3 and 4, MSTest 3 and later, and TUnit
- **platforms:** both **VSTest** and **Microsoft.Testing.Platform (MTP)**
- **editor:** results in the editor
- **CI results:** TRX import and export

The roadmap's principles apply:
- The shell owns generic UI, and providers own data.
- New features are plugins unless they sit on a latency-critical path.
- New plugin APIs start as *proposed*.
- Heavy work runs out of process.

The two platforms work differently:
- **VSTest:** `vstest.console` loads a framework adapter into a `testhost` process per test assembly. IDEs drive it through `Microsoft.TestPlatform.TranslationLayer` (MIT). The SDK ships `vstest.console.dll`, so nothing extra is installed.
- **MTP:** the test project builds into an executable that runs its own tests. IDEs start it in **server mode** (`--server jsonrpc --client-host <host> --client-port <port>`) and speak a documented JSON-RPC protocol over TCP. The protocol is version 1.0, specified in `microsoft/testfx` `docs/mstest-runner-protocol` (MIT). xUnit v3, MSTest's runner and TUnit use MTP natively. xUnit v3 and MSTest can also run on VSTest through an adapter.

Debugging uses netcoredbg (ADR 0007). It sends DAP's `process` event, so a launched debuggee's process id is known.

## Options

| Option | Verdict |
|---|---|
| Test support built into the shell (workbench service plus views) | Fast to build, but it puts VSTest's object model in the AOT shell, which is reflection-heavy and not AOT-safe. It also gives third parties nothing to plug into. Rejected. |
| Test support in `daedalus-lshost` | Mixes Roslyn's lifetime with test runs, and a crashing adapter takes the language service down. Rejected. |
| **A generic test controller API (proposed) that the shell renders, with a built-in `daedalus.dotnettest` plugin in a dedicated plugin host as the .NET provider** | **Chosen.** It matches VS Code's `TestController`. The shell stays AOT-clean and owns the virtualized UI. The provider can be replaced or joined by others (F# Expecto in M25, remote environments in M21). |
| Driving `dotnet test` and parsing its output | Loses streaming results, test-case selection and debugging hooks. Rejected. |
| MTP through the source-only reference client (`Microsoft.Testing.Platform.ServerMode.Client.Sources`) | Wire-compatible by construction, but it pulls the platform's serialization stack into the plugin. The protocol is small and documented, so a client of about 300 lines on our own JSON-RPC types is easier to keep AOT- and version-tolerant. We may revisit this if the protocol grows. |

## Decision

### Process model
- **`daedalus.dotnettest`** is a built-in plugin with `"isolation": "dedicated"`, so it gets its own plugin host. A hung adapter or a large discovery never touches other plugins.
- **Lazy start.** It activates on:
  - `onTestProjects`: a new activation event the shell fires once an evaluated project of the open solution is a test project
  - `onView:testExplorer`
  - its own commands

  A solution without test projects never starts it.
- **VSTest runs** go through `VsTestConsoleWrapper` on `<SdkPath>/vstest.console.dll` (the build host already reports `SdkPath`). There is one wrapper session per plugin, started on first use. Each test assembly runs in its own `testhost`, as with `dotnet test`.
- **MTP runs** start the test application (its apphost, or `dotnet <dll>`) in server mode. The plugin's own client connects to the app over loopback TCP, using LSP `Content-Length` framing. One process serves one request unless the server advertises `experimental_multiRequestSupport`.

### Choosing the platform per project
- The build host evaluates `IsTestProject` and `IsTestingPlatformApplication` and reports them in `ProjectInfo`.
- `IsTestingPlatformApplication == true` means **MTP**. Otherwise `IsTestProject == true` means **VSTest**.
- The setting `testing.platform` (`auto` | `vstest` | `mtp`) overrides the choice for projects that support both.

### Plugin API (proposed, `DaedalusStudio.Plugins.Proposed`)
- **`ITests.CreateController`** returns a controller with:
  - a tree of `TestItem`s: id, label, URI, range, traits, children
  - run profiles: `Run`, `Debug` and `Coverage`, each with a handler
  - `CreateRun(request)` → `ITestRun`, which takes `Enqueued`, `Started`, `Passed`, `Failed`, `Errored`, `Skipped`, output, attachments and coverage (ADR 0011), then `End`
- **`IWorkspace`:**
  - the solution's projects: test properties, target frameworks, Compile items, and the output path per framework through `project/runInfo`
  - `FindTestCandidatesAsync`, answered by the language host
- **`IBuild`:** builds through `BuildService`, which keeps one build at a time and the Build output.
- **`IDebug`:** launches or attaches through `DebugService`, and returns the debuggee's process id.

Item and result updates are batched on the wire (`tests/*`, documented in `docs/plugin-protocol.md`). **Built-in plugins may use proposed APIs in release builds.** Third-party plugins still need `enableProposedApi` and `--enable-proposed-api` (ADR 0003).

### Discovery
1. **Source-based, immediately.**
   - The plugin sends each test project's Compile items to the language host's `daedalus/testCandidates`.
   - The language host parses them syntax-only (open buffers win over the file on disk), so this works before design-time builds and before any build.
   - It reports methods and classes with known test attributes, with fully qualified names and ranges.
   - The tree is project → namespace → class → method.
2. **Runner-based, after a build.** The runner's tests are authoritative: theories, data rows and generated tests. They are merged with the source items by fully qualified name; parameterized cases become children of their method. Runner items keep the source location when the runner gives none.
3. **Rescans** happen when a test project's file changes (debounced) and after builds.

### Runs
- A run builds the affected test projects first (one solution build, or one project build), unless the build is up to date.
- It then runs per project, in parallel up to `testing.maxParallelProjects`.
- VSTest gets the selected `TestCase`s, or whole sources when everything in a project is selected. MTP gets the selected nodes, or no selection (everything).
- Results stream into the Test Explorer.
- **Cancel:**
  - VSTest: `CancelTestRun`, then `AbortTestRun` after 5 s.
  - MTP: `$/cancelRequest`, then the process tree is killed after 5 s.
- **Settings:**
  - VSTest gets runsettings: the user's `.runsettings` (`testing.runSettingsFile`), merged with generated `RunConfiguration` values (target framework, architecture, parallelism, environment variables).
  - MTP reads its own `testconfig.json`. Environment variables are passed to the process.

### Debugging tests
- **VSTest:** `RunTestsWithCustomTestHostAsync` with an `ITestHostLauncher3`:
  - `LaunchTestHost(startInfo)` launches the test host under netcoredbg (`IDebug.LaunchAsync`) and returns its process id from DAP's `process` event.
  - `AttachDebuggerToProcess` (used when vstest starts the host itself) attaches.
- **MTP:** the server-mode command line itself is launched under netcoredbg, and the run request is sent once the app connects.
- Breakpoints, stepping and the debug windows are the normal ones (ADR 0007). Stopping the debug session cancels the run.

### Results
- The shell's `TestService` keeps:
  - the last result per test, and a history of the last `testing.historyLength` runs
  - output and attachments
- TRX files are exported and imported. Imported runs appear in the same views, matched to known tests by fully qualified name.
- Playlists are JSON under `.daedalus/playlists/` next to the solution (F7). Visual Studio `.playlist` files can be imported.

## Consequences
- **What the shell gains:** a generic, virtualized Test Explorer, a test margin and result details that any controller can fill. The plugin API gets its first proposed surface (tests, workspace, build and debug), proved by a built-in before API 1.0 (M22).
- **Memory:** the dedicated plugin host costs about 50–80 MB while a solution with tests is open. The idle budget (no solution) is unchanged.
- **Debugging limits:** VSTest's debug path depends on vstest calling our launcher. Frameworks that start their own child processes (for example .NET Framework hosts) are not debuggable. That is a known gap; .NET Framework is not a supported target.
- **Protocol risk:** MTP server mode is version 1.0. The client requests that version and tolerates unknown node properties.
- **Not in M8:** Live Unit Testing and test impact analysis (out of scope). Remote test environments come in M21. CodeLens entries come in M12. `TODO(issue)`.
