# 0011. Code coverage

- **Status:** accepted (M8 plan approval, 2026-10-09)
- **Date:** 2026-10-09
- **Deciders:** maintainer

## Context

M8b asks for code coverage:
- for tests run from the Test Explorer, on VSTest and on MTP
- a Coverage Results window: assembly, namespace, class, method, with line and branch percentages
- covered, partly covered and uncovered marks in the editor
- merging across test projects, and a Cobertura export

The roadmap lists `Microsoft.CodeCoverage` and `Microsoft.Testing.Extensions.CodeCoverage` as proprietary traps: their licences are Microsoft's own, not OSI-approved, so they are **excluded**. Daedalus never ships or drives them. A user's project may still reference them (`Microsoft.NET.Test.Sdk` brings `Microsoft.CodeCoverage` in). Using them is the user's own business, but Daedalus doesn't depend on them.

**Coverlet** (MIT, `coverlet-coverage/coverlet`, 10.1.0) instruments assemblies with Mono.Cecil and records hits. It ships as:
- `coverlet.collector`: a VSTest data collector
- `coverlet.console`: a .NET tool that instruments, runs any command, then writes the report
- `coverlet.mtp`: an MTP extension, which a project has to reference

`coverlet.core` itself is not published on its own.

## Options

| Option | Verdict |
|---|---|
| `Microsoft.CodeCoverage` (dynamic instrumentation) | Proprietary. Excluded. |
| Coverlet, with the collector or extension referenced by each test project | It's the default in `dotnet new` templates, but it fails for projects that don't reference it, and Daedalus shouldn't edit users' projects to run coverage. |
| **Coverlet, shipped with the plugin, chosen per platform; Cobertura as the interchange format** | **Chosen.** No project changes are needed, and there is one parser and one model. |
| Our own instrumentation | Months of work that Coverlet already does. Rejected. |

## Decision

- **Engine:** the `daedalus.dotnettest` plugin ships `coverlet.collector` and `coverlet.console` in its folder (`coverage/collector/` and `coverage/console/`). It runs them with the SDK's `dotnet`. Nothing is installed globally.
- **VSTest:** a coverage run adds:
  - `TestAdapterPath` pointing at the bundled collector
  - a `DataCollector` entry, `XPlat Code Coverage`, with `Format=cobertura`

  to the runsettings. The `testhost` produces a Cobertura attachment per test assembly. If a project references its own `coverlet.collector`, the one VSTest resolves first is used.
- **MTP:**
  - If the project references `coverlet.mtp`, the app gets `--coverlet --coverlet-output-format cobertura --coverlet-output <file>`.
  - Otherwise the app runs under the bundled `coverlet.console`: `<test app dll> --target <dotnet|apphost> --targetargs "<server-mode arguments>" --format cobertura --output <file>`. The server-mode connection works as in a normal run.
- **Filters:**
  - By default only the solution's own projects are instrumented. Test projects and generated code (`[GeneratedCode]`, `[CompilerGenerated]`, `*.g.cs`) are excluded.
  - `coverage.include` and `coverage.exclude` take Coverlet filter expressions (`[Assembly]Namespace.Type`).
- **Model:**
  - The plugin parses each Cobertura file and merges them. Hits per line are summed, and branch counts are maxed per line.
  - It reports through `ITestRun.AddCoverage(FileCoverage)`: document URI, lines with hit counts, branch covered/total per line, and the containing assembly/class/method for the results tree.
  - The shell's `CoverageStore` keeps the latest coverage run. A new coverage run replaces it, and **Clear Coverage** removes it.
- **Shell:**
  - **Coverage Results window:** a tree of assembly → namespace → class → method, with covered/total lines, line %, and branch %.
  - **Editor:** a gutter colour per line. Covered is green, partly covered (some branches) is yellow, uncovered is red. The colours come from theme tokens, including high contrast.
  - The colours can be toggled (**Show Coverage Colouring**). They stay with the line they were reported for until the document changes, then fade.
  - **Export** writes the merged Cobertura XML.
- **Run with Coverage** is a run profile, so it appears wherever Run and Debug appear.

## Consequences
- Coverage works for VSTest and MTP projects without changing them, as long as `dotnet` can run the bundled Coverlet, which targets .NET 8 and later.
- **Cost:** instrumentation adds the usual Coverlet overhead (rewriting assemblies on disk, then restoring them). Assemblies in use by another process can't be instrumented; that run reports a warning and no coverage for them.
- **Not in M8b:**
  - An HTML report through ReportGenerator (Apache-2.0). `TODO(issue)`; the Cobertura export can be fed to it.
  - Coverage in the Solution Explorer. `TODO(issue)`.
- **Licences:** Coverlet's MIT licence and its bundled dependencies (Mono.Cecil MIT, Microsoft.Extensions.* MIT) are recorded in `THIRD-PARTY-NOTICES.md`.
