# M8a checkpoint: Test Explorer

Date: 2026-10-09. Decisions: [ADR 0010](../adr/0010-test-platform-integration.md). This is the first post-MVP milestone ([roadmap](../roadmap.md#m8--testing-test-explorer-and-code-coverage)); coverage is [M8b](m8b.md).

## Built
- **Proposed plugin API** (`DaedalusStudio.Plugins.Proposed`, API 0.2.1, protocol 0.2; [plugins.md](../plugins.md#proposed-apis), [plugin-protocol.md](../plugin-protocol.md)):
  - **Test controllers**, modelled on VS Code's: an item tree, run profiles (Run, Debug, Coverage), runs that report states, messages, output, attachments and coverage. Item and result updates are batched.
  - **`IWorkspace`** (the solution, project outputs per framework, source test candidates, type outlines), **`IBuild`** and **`IDebug`** (launch or attach under netcoredbg; the debuggee's process id comes from DAP's `process` event).
  - Built-in plugins may use proposed APIs without `--enable-proposed-api`. The API stays at 0.2.x because a 0.3.0 would reject plugins that ask for `^0.2.0`; every addition is additive.
- **Build host**: `ProjectInfo` gains `isTestProject` and `testPlatform` (`vstest`, `mtp`); `project/runInfo` takes a target framework.
- **Language host**: `daedalus/testCandidates` (test methods and classes from syntax, before design-time builds; xUnit, NUnit, MSTest, TUnit attributes) and `daedalus/typeOutline` (for Create Unit Tests). The shell now starts the C# host as soon as a trusted solution with C# projects opens, which the 1 s tree budget needed.
- **Built-in plugin `daedalus.dotnettest`**, in a dedicated host:
  - **Source discovery** from the language host, replaced by the runners' tests after a build (theories, data rows, generated tests), merged by fully qualified name.
  - **VSTest** through `Microsoft.TestPlatform.TranslationLayer` on the SDK's `vstest.console`: discovery and runs per target framework, `.runsettings` (chosen and generated), cancel, and debugging through the custom test-host launcher.
  - **Microsoft.Testing.Platform** through our own client for its JSON-RPC server mode (protocol 1.0, over TCP): discovery, runs by node, cancel, and debugging by starting the test app under netcoredbg.
  - Settings: platform, run settings file, target framework, architecture, parallelism, environment, run after build, discover after build.
- **Shell**:
  - **Test Explorer** (Ctrl+E T): a virtualized tree grouped by project/namespace/class, project, outcome, trait or duration; the filter syntax (`outcome:`, `project:`, `trait:`, `-`, quotes); run all, selected, failed and not-run tests, repeat the last run, cancel; a summary line.
  - **Details**: message, expected and actual, stack trace with clickable frames, output and the last N results (`testing.historyLength`). The run's log and the files it produced (attachments, such as coverage reports) are in Output → Tests.
  - **Editor**: outcome glyphs in a test margin (click: Run, Debug, Run with Coverage, Show in Test Explorer); Run/Debug Tests at Cursor and Go to Next/Previous Failed Test; the same commands in the editor's new context menu (`editor/context`).
  - **Test menu** and the classic IDE's chords: Ctrl+R A, Ctrl+R Ctrl+A, Ctrl+R T, Ctrl+R Ctrl+T, Ctrl+R L, Ctrl+R F. Open Recent moved from Ctrl+R to **Ctrl+K Ctrl+R**, because a single-key Ctrl+R shadowed the chords.
  - **Playlists** as JSON in `.daedalus/playlists/` (paths relative to the solution), and import of the classic IDE's `.playlist` files.
  - **TRX** export and import; imported results appear on the matching tests.
  - **Create Unit Tests** (editor context menu, Test menu): a `<Class>Tests.cs` with a stub per public method in the chosen test project's framework, adding the project reference if needed.
  - **`daedalus diff <left> <right>`** shows a file comparison, in the running instance if there is one (single instance over a named pipe), so Daedalus can be Verify's diff tool.
- **Fixture** `samples/Testing`: xUnit v2 (VSTest), xUnit v3 (MTP), NUnit, MSTest on VSTest and on MTP (`MSTest.Sdk`), TUnit, and a multi-targeted project (net9.0, net10.0), each with passing, failing, skipped, theory, output-writing and (opt-in) hanging tests, plus a breakpoint marker for the debugging test.
- **Tests**: language host candidates and outlines; DAP process id; plugin integration tests on the fixture for every framework and platform (discover, run, cancel the hanging test, debug to a breakpoint, Create Unit Tests); the workbench model (filters, grouping, TRX round trip, playlists, stack traces); headless UI tests for the Test Explorer, the margin and `daedalus diff`.
- **Docs**: [user guide: Testing](../user-guide/testing.md), [protocol.md](../protocol.md), [project-protocol.md](../project-protocol.md), [plugins.md](../plugins.md), [plugin-protocol.md](../plugin-protocol.md), benchmarks README.

## Benchmarks
Scenario `test-explorer` (new; `FixtureGen tests` generates `samples/Tests5k`: 20 xUnit projects, 5,000 tests), Windows, i5-13600KF, 5 runs ([result](../../benchmarks/results/windows/shell/test-explorer-20261009-174506.json)):

| Metric | p50 | max | Budget |
|---|---|---|---|
| `test_tree_ms` (solution structure → every test listed from source, no build) | 963 ms | 1,033 ms | p50 ≤ 1,000 ms |
| `test_filter_frame_ms` (filter applied → frame rendered) | 3.8 ms | 19.8 ms | max ≤ 50 ms |
| `test_first_result_ms` (Run → first result, build included) | 3.1 s | 3.5 s | informational |

The tree budget was missed at first (1.4–2.0 s): the C# host only started with the first opened document. Starting it with the solution and shortening the plugin-side debounces to 50 ms brought it under budget.

## Not done, and deviations
- **Rider comparison** numbers were not taken (the roadmap asked for them before proposing budgets). The budgets above come from the roadmap's targets. `TODO(issue)`.
- **CodeLens** test entries wait for CodeLens (M12).
- **.NET Framework** test projects are not supported. Tests running in processes they start themselves can't be debugged.
- **"Run affected tests after a build"** runs the tests of the test projects the build built, not a dependency-based selection. `TODO(issue)`: test impact analysis is out of scope per the roadmap.
- The test margin is mouse-only, like the breakpoint margin; the commands at the cursor are its keyboard path. `TODO(issue)` with the M7 accessibility gaps.
- **Known flaky test**: `ShellHeadlessTests.F6_cycles_focus_through_the_visible_panes` timed out once on Linux (WSL) and passes on Windows. Watch it in CI.

## CI
See [M8b](m8b.md#ci): one 3-OS run covers both checkpoints.
