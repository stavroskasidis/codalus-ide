# Testing

Daedalus Studio finds, runs and debugs **xUnit** (v2 and v3), **NUnit**, **MSTest** and **TUnit** tests, on both
test platforms: **VSTest** (the classic `Microsoft.NET.Test.Sdk` projects) and **Microsoft.Testing.Platform** (MTP:
xUnit v3, `MSTest.Sdk`, TUnit, and projects that enable it). Each project uses the platform it is set up for. Run with
Coverage measures code coverage with [Coverlet](https://github.com/coverlet-coverage/coverlet). Tests and coverage work on every .NET version from .NET Core 2.0, with the test SDK and adapter versions each one supports ([Older .NET versions](dotnet-versions.md)).

## The Test Explorer

Test → Test Explorer (`Ctrl+E T`). It lists every test of the solution, grouped by project, namespace and class.

- **Before any build**, tests come from your source code: methods with `[Fact]`, `[Theory]`, `[Test]`, `[TestCase]`,
  `[TestMethod]`, `[DataTestMethod]` and similar attributes. After a build, the test runners list the tests
  themselves, so theory cases, data rows and generated tests appear too.
- **Outcomes** are shown with an icon and a color: ✓ passed, ✗ failed, – skipped, ○ not run. Groups show the worst
  outcome under them. The summary above the list counts them.
- **Group by** project, namespace and class (the default), project, outcome, traits (xUnit traits, NUnit and MSTest
  categories) or duration.
- **Search** filters as you type. Plain words match test names. Fields narrow further:
  - `outcome:failed` (also `passed`, `skipped`, `notrun`, `running`)
  - `project:Calculator.Tests`
  - `trait:Category=Integration`, or just `trait:Integration`
  - `-` excludes: `-outcome:passed`. Quotes keep words together: `"Divide by zero"`.
- **Details** of the selected test appear below the list: the message, expected and actual values, the stack trace
  (click a frame to open its source), the test's output and its earlier results.
- **Keys**: `Enter` runs the selection, `Ctrl+Enter` debugs it, `F12` (or double-click) opens the test,
  `Left`/`Right` collapse and expand.

## Running tests

| Command | Shortcut |
|---|---|
| Run All Tests | `Ctrl+R A` |
| Debug All Tests | `Ctrl+R Ctrl+A` |
| Run Tests at Cursor (the test or class around the caret) | `Ctrl+R T` |
| Debug Tests at Cursor | `Ctrl+R Ctrl+T` |
| Repeat Last Run | `Ctrl+R L` |
| Run Failed Tests | `Ctrl+R F` |
| Run Tests That Have Not Run, Run with Coverage, Cancel | Test menu, Test Explorer toolbar |

Running builds the projects first (saving open documents, like a build). Results stream in while the tests run; the
status bar shows the totals afterwards, and the run's log is in Output → Tests. **Cancel** stops the run, and stops
test processes that don't stop by themselves after a few seconds.

**In the editor**, a glyph in the margin marks each test and test class with its last outcome. Click it to run, debug,
or run the test with coverage, or to show it in the Test Explorer. The editor's context menu has the same commands.
Test → Go to Next Failed Test opens the failing line of the next failed test.

**Debugging** a test stops at your breakpoints like any program (see [Debugging](debugging.md)); stopping the
debugger ends the run.

## Code coverage

**Run with Coverage** (Test → Run All Tests with Coverage, the coverage button in the Test Explorer, or a test glyph's
menu) runs the tests and measures which lines of your code they ran. It works for every framework and both test
platforms, and your projects don't need to reference anything: Daedalus brings
[Coverlet](https://github.com/coverlet-coverage/coverlet) with it. (If an MTP project references `coverlet.mtp`, that
is used instead.) Test projects themselves and the test frameworks are not measured.

- **In the editor**, a bar in the gutter and a faint tint mark each line that can be covered: **blue** covered,
  **amber** partly covered (the line ran, but not every branch of its conditions did), **red** not covered. Hover the
  bar for the hit count and branches. After you edit a file, its colors fade, because the lines may have moved; run
  again to refresh them. Test → Toggle Coverage Coloring turns the colors off and on.
- **Coverage Results** (Test → Coverage Results, which opens by itself after a coverage run) lists assemblies,
  namespaces, classes and methods with the share of lines and branches covered. Double-click a class or method to
  open it. The search box filters by name.
- **Export** writes the results as a Cobertura XML file (Test → Export Coverage), which
  [ReportGenerator](https://github.com/danielpalme/ReportGenerator) and most CI services read. **Clear Coverage**
  removes the results and the colors.
- Each coverage run replaces the previous results. Settings → Testing → `coverage.include` and `coverage.exclude` take
  Coverlet filters such as `[MyApp.*]*` or `[*]MyApp.Generated.*`.

## Settings

Settings → Testing:

| Setting | What it does |
|---|---|
| `testing.platform` | VSTest or Microsoft.Testing.Platform, for projects that support both (`auto`: what the project enables). |
| `testing.runSettingsFile` | A `.runsettings` file for VSTest projects, relative to the solution folder. MTP projects read their own `testconfig.json`. |
| `testing.targetFramework` | The framework to test in multi-targeted projects (empty: all of them; each framework's results are shown under the test). |
| `testing.architecture` | The process architecture of VSTest test hosts. |
| `testing.parallel` | Run test projects in parallel. |
| `testing.environment` | Environment variables for test processes, as `NAME=value`. |
| `testing.discoverAfterBuild` | After a build, ask the runners for the tests (default: on). |
| `testing.runAfterBuild` | After a build, run the tests of the projects it built. |
| `testing.historyLength` | How many earlier results to keep per test. |
| `testing.showExplorerOnRun` | Open the Test Explorer when a run starts. |

## Playlists

A playlist is a saved set of tests: pick it in the Test Explorer's playlist box to see and run only those.

- **Create**: select tests, right-click → Add to Playlist… (or Test → New Playlist…).
- **Import** a Visual Studio `.playlist` file with Test → Import Test Playlist (.playlist)….
- Playlists are saved as JSON in `.daedalus/playlists/` next to the solution, with paths relative to it, so you can
  commit and share them. Add `.daedalus/` to `.gitignore` if you don't want to.

## Test results files (TRX)

- **Export**: Test → Export Test Results (TRX)… writes the results shown in the Test Explorer, in the format
  `dotnet test --logger trx` writes.
- **Import**: Test → Import Test Results (TRX)… shows a results file (from CI, for example) on the matching tests.
  Tests are matched by name; the notification says how many matched.

## Create Unit Tests

Right-click a class or method → **Create Unit Tests…** (also in the Test menu). Pick a test project; Daedalus writes
`<Class>Tests.cs` there with a test per public method (or just the method under the cursor), in the project's
framework, adds a reference to the code's project if needed, and opens the file. Each test is an Arrange, Act, Assert
stub that fails until you write it.

## Snapshot testing: Daedalus as a diff tool

`daedalus diff <left> <right>` shows two files side by side, in the running window if there is one. To use it for
[Verify](https://github.com/VerifyTests/Verify) snapshots, register it once in your test project with DiffEngine's
custom-tool API (shown for DiffEngine 15; check its documentation for your version):

```csharp
DiffTools.AddTool(
    name: "Daedalus Studio",
    autoRefresh: true,
    isMdi: false,
    supportsText: true,
    requiresTarget: true,
    launchArguments: new(
        Left: (temp, target) => $"diff \"{target}\" \"{temp}\"",
        Right: (temp, target) => $"diff \"{temp}\" \"{target}\""),
    exePath: @"C:\Program Files\Daedalus Studio\daedalus.exe", // where daedalus is installed
    binaryExtensions: []);
```

## Known limitations

- .NET Framework test projects are not supported (Daedalus targets .NET 8 and later).
- Tests that run in child processes they start themselves can't be debugged.
- Remote test environments (WSL, containers, SSH) come with remote development; Live Unit Testing is not planned.
