# M8b checkpoint: code coverage

Date: 2026-10-09. Decisions: [ADR 0011](../adr/0011-code-coverage.md). Continues [M8a](m8a.md); M8 is complete with this checkpoint.

## Built
- **Coverlet 10.1.0**, bundled with the .NET test plugin (downloaded at build time, not referenced; licenses in `THIRD-PARTY-NOTICES.md`). Test projects don't need to reference anything:
  - **VSTest**: Coverlet's data collector (`coverage/collector`, its net8.0 build) through the "XPlat code coverage" run settings and the test adapter path.
  - **Microsoft.Testing.Platform**: `coverlet.mtp` when the project references it (`--coverlet`), otherwise the test app runs wrapped in `coverlet.console` (`coverage/console`), whose report is read after the app exits.
  - Every path writes Cobertura. The plugin merges a run's reports (hits summed, branches maxed per line) and reports line and method coverage through the test controller API (`ITestRun.AddCoverage`). The report files are attached to the run.
  - Test projects and test frameworks are excluded; `coverage.include` and `coverage.exclude` take Coverlet filters.
- **Shell**:
  - `CoverageStore` (workbench): the latest coverage. A run's reports merge, and the next run replaces them.
  - **Editor coloring**: a gutter bar and a faint line tint, blue covered, amber partly covered, red uncovered; the bar's tooltip gives hits and branches. Edited documents fade (the lines may have moved). Test → Toggle Coverage Coloring.
  - **Coverage Results** tool window (opens after a coverage run): assembly → namespace → class → method with line and branch percentages, a name filter, double-click to open.
  - **Export Coverage** (Cobertura XML, for ReportGenerator and CI services) and **Clear Coverage**, in the Test menu and the window's toolbar. Context key `coverageAvailable`.
  - Run with Coverage was already a profile in M8a: Test menu, Test Explorer toolbar and context menu, the test margin and the editor's context menu.
- **Tests**: plugin integration tests measure the fixture's `Calculator` through all four paths (xUnit v2 and NUnit on VSTest, xUnit v3 and MSTest on MTP through the console): covered, uncovered and partly covered lines, the tests excluded, the method tree. Workbench tests for merging, replacing, the tree and Cobertura export. A headless UI test for the coloring, staleness, the toggle, Coverage Results and Clear. The end-to-end smoke test of the published app gained three M8 steps on part of `samples/Testing`: source discovery, a run on VSTest and MTP with the margin glyphs, and a coverage run with the coloring and Coverage Results (passing on Windows).
- **Docs**: [user guide: Code coverage](../user-guide/testing.md#code-coverage).

## Not done, and deviations
- **HTML report** through ReportGenerator (optional in the roadmap): export Cobertura and run ReportGenerator yourself. `TODO(issue)`.
- **Coverage in the Solution Explorer** (percentages on projects and files) and in the Test Explorer. `TODO(issue)`.
- **Line tracking through edits**: edited documents fade rather than move their coloring. `TODO(issue)`.
- **Accumulating coverage across runs** (Visual Studio can merge results): each run replaces the last. `TODO(issue)`.
- The exported Cobertura file is rebuilt from the merged data, so each line belongs to the method that starts closest above it; the plugin's own per-project reports are listed as attachments in Output → Tests.
- Coverage isn't collected for debug runs, or for .NET Framework projects.

## CI
_Filled in after the checkpoint run._
