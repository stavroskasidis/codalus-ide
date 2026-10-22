# Moving to a new .NET release

Codalus follows every new .NET major version (D22, [ADR 0025](../adr/0025-hosts-and-the-users-sdk.md)). Each one is a milestone-sized task: it starts at the first release candidate and finishes at GA. .NET 11 is M30: M30a on the first release candidate, and M30b, the move itself, on the second one (moved forward by the maintainer), finished on the GA SDK in November 2026. .NET 12 follows in November 2027.

## At the first release candidate: the new SDK's projects work

Codalus still runs on the current .NET. Its build host rolls forward onto the new runtime for solutions that use the new SDK (ADR 0025).

- [ ] **Install the SDK where the tests run.**
  - `samples/Net<N>/global.json` pins the release candidate (copy `samples/Net11` for a new version).
  - `.github/actions/setup/action.yml` (`setup-dotnet`'s `dotnet-version`, used by every workflow) installs the same version.
  - `eng/validate.sh` installs it from the fixture's `global.json`, on Linux (WSL) and the Mac.
- [ ] **Projects:** the fixture opens, evaluates, builds, runs, debugs, tests (on both frameworks) and Hot Reloads: `DotnetVersionsTests` (Projects), `Net11Tests` (Shell) and the smoke test's `dotnet.net11` step. Update them for the new version.
- [ ] **netcoredbg:** debugging on the new runtime works with the pinned netcoredbg (`eng/get-netcoredbg.sh`), or a newer release is pinned. If none works, record the gap.
- [ ] **Lists of versions:**
  - target frameworks in `MsBuildKnowledge.TargetFrameworks` (the markup host's completion), including the platform variants;
  - `LangVersion` in `MsBuildKnowledge` and in the property pages (`ProjectPropertyPages`);
  - the property pages' hints that name a framework;
  - `DotnetSupport` (the SDK status): add the new channel, and fill in end-of-support dates from `releases-index.json` as Microsoft announces them.
- [ ] **Schemas:** new `global.json`, `runtimeconfig.json` and MSBuild properties in the markup host's schemas, if the release adds any.
- [ ] **New Project** ([ADR 0082](../adr/0082-new-project-wizard.md)): the template engine's cache isn't a contract, so check that the new SDK's still reads (`ProjectTemplateCatalogSdkTests` and `NewProjectServiceIntegrationTests` on the new SDK, and the smoke test's `projects.newProject`), and give the new channel a row in `DotnetSupport` so New Project labels its framework (Preview, then Standard or Long Term Support at GA).
- [ ] **Analyzers and generators:** check which Roslyn version the new SDK's generators reference (the Razor generator, the targeting pack's generators). Those newer than the language host's Roslyn don't load until the next step. Document them as gaps.
- [ ] **The user guide:** the prerequisites, and the known gaps in "Solutions and projects".

## At GA: Codalus moves to the new .NET

For .NET 11 this phase ran on the second release candidate (M30b, the maintainer's decision of 2026-10-16), and finishes with the GA swap at the end of this list. The notes in *italics* are what M30b ran into.

- [ ] **The SDK.** `global.json` uses the new SDK (`allowPrerelease: true` on a release candidate, without it at GA). Install it where you build: `dotnet-install.sh --version <sdk>` into `~/.dotnet`, next to the current one.
  - *Pick the newest release candidate the feeds have (`releases.json` of the channel lists `latest-sdk`).*
- [ ] **Target frameworks.** `Directory.Build.props` targets the new `net<N>.0`, so the shell, the workbench, every host, the built-in plugins, the tests, the benchmarks, the website and `eng/Codalus.Release` follow. What stays:
  - the plugin API (`Codalus.Plugins.Abstractions`, `Codalus.Plugins.Testing`), the `codalus-plugin` template and the sample plugins keep the previous `net<N-1>.0`, so plugins keep building with the previous SDK and load into the new host (ADR 0025's amendment);
  - `netstandard2.0` agents and the EF agent's `net8.0` (they load into the user's processes).
  - *Paths with a framework in them: `Codalus.Shell.csproj` copies the hosts and plugins from `bin/<configuration>/$(TargetFramework)`, and the tests find built hosts through `Codalus.Tests.RepoBuild.TargetFramework` (`tests/Shared/RepoBuild.cs`). Search for the old moniker in `src/`, `tests/` and `eng/` anyway.*
- [ ] **The new SDK's analyzers.** `AnalysisLevel` is `latest-recommended` and warnings are errors, so the new SDK's rules fail the build first. *.NET 11 added CA2027 (`Task.WhenAny` with `Task.Delay`: use `WaitAsync` with `ConfigureAwaitOptions.SuppressThrowing`), CA2026 (`JsonElement.Parse`), and new overloads that CA2016, CA1830, CA1860 and CA2263 point to.*
- [ ] **Native AOT:** `dotnet publish src/Codalus.Shell -c Release -r <rid>` has no new trimming or AOT warnings, **and the published shell starts** (`codalus-bench run --scenarios cold-start`, or the smoke test). A publish that succeeds proves nothing on its own.
  - *The .NET 11 SDK publishes Native AOT without a full Build ("optimized publish"), in an order that left Avalonia's resources out: no compiled XAML, a shell 20 MB smaller with no warnings at all, which failed at start. `UseOptimizedPublish=false` in the shell's project restores the full Build. Fewer warnings or a much smaller binary than before is a sign of this, not a win.*
- [ ] **Dependencies** on the new runtime: Avalonia, Dock, AvaloniaEdit and TextMate, the Roslyn, MSBuild and NuGet client packages, Microsoft.Testing.Platform, Coverlet and Velopack. Packages that ship with the runtime (`System.*` pins such as `System.Security.Cryptography.Pkcs`, `System.Drawing.Common`) move to the new version. Record version changes in `THIRD-PARTY-NOTICES.md`.
  - *Tools: `vpk` is a `net<N-1>.0` tool without a roll-forward policy, so `eng/package.sh` runs it with `DOTNET_ROLL_FORWARD=Major` (packaging jobs install only the new SDK). Check every `dotnet tool` the scripts install.*
- [ ] **Roslyn** in the language host, C# Interactive and the T4 host: the version of the new SDK's compiler (`ProductVersion` of `sdk/<version>/Roslyn/bincore/Microsoft.CodeAnalysis.dll`). Before GA it isn't on nuget.org: take the exact version from the .NET team's public `dotnet-tools` feed, mapped in `NuGet.config` for `Microsoft.CodeAnalysis.*` only. Then the new C# version's syntax and the new SDK's generators work in the editor.
  - *.NET 11 RC2 ships Roslyn 5.12.0-1.26504.105 (RC1 had 5.11). Its Razor generator writes a component's class and `@code` blocks to `<File>_razor.decl.g.cs` and the render tree to `<File>_razor.g.cs`: the language host maps both (`RazorMapping`), or Go to Definition lands in generated code.*
- [ ] **The hosts' prerequisite:** the hosts roll forward from the new .NET (ADR 0025), so the new SDK is the prerequisite: Help → Check Environment (`EnvironmentCheck.HostsRuntime`) reports SDKs without a new enough runtime, the `.deb` and `.rpm` packages recommend `dotnet-sdk-<N>.0`, the winget manifest depends on `Microsoft.DotNet.SDK.<N>` (`Microsoft.DotNet.SDK.Preview` on a release candidate), and the user guide's prerequisites and the website's Download page say so.
- [ ] **Fixtures:** the regular fixtures (`samples/BuildAndRun`, `samples/Debugging`, `samples/Testing`) get the new target framework; `samples/Testing` multi-targets the previous and the new one. The new C# version's features go into `samples/LanguageFeatures` (`App/CSharp<NN>.cs`, with `CSharp_<NN>_features_show_no_false_errors`). Update the tests that name the old framework in output paths, runtime strings (`".NET 10."`) and test-framework lists. The fixtures that stay on the previous framework need its runtime where the tests run.
- [ ] **Budgets:** measure startup, typing, idle memory and the shell's binary size before and after (`benchmarks/`, `CODALUS_BENCH_SHELL_DIR` for the "before" shell). None may regress.
- [ ] **CI and the validation machines:** the SDK pinned by `global.json` installs everywhere.
  - `.github/actions/setup/action.yml`: *setup-dotnet reads a `global.json` with `rollForward: latestFeature` as the channel's newest release, which a release candidate channel doesn't have: spell the SDK out until GA.* The extra SDKs are the previous version's (fixtures still on it).
  - `eng/validate.sh` checks with `dotnet --version` in the repository (it applies `global.json`), and installs the previous version's SDK for its runtimes.
  - The previous version's fixture (`samples/Net<N>`) can go once the regular fixtures target the new version.
- [ ] **netcoredbg on the new runtime** again, now that the fixtures run on it: the debugger tests, the dump tests (`CompanionTests`) and the smoke test's debugging steps.
  - *netcoredbg 3.2.0 debugs .NET 11 programs, but gives their stack frames no module, so exception module conditions can't apply: a documented gap, and `Module_conditions_skip_exceptions_thrown_in_other_modules` skips with the reason until a release fixes it.*
- [ ] **The target framework matrix** (M33, [ADR 0030](../adr/0030-older-dotnet.md)): the previous version becomes a row of it. Add a `Net<N>0` folder to `samples/TargetFrameworks` (app, library, xUnit project, and the next C# version's feature), its runtime to `eng/get-runtimes.sh`, its row to `Matrix.All` (`tests/Codalus.TargetFrameworks.Tests`) and its C# version to `DotnetFrameworks.DefaultCSharpVersion`. Update the support table in `docs/user-guide/dotnet-versions.md` from the suite's results on three OSes.
  - *`Net100` already had its folder and row from M33; M30b gave it its next feature (C# 15) and added .NET 10's runtime to `eng/get-runtimes.sh`. `DefaultCSharpVersion` computes the C# version from the framework.*
- [ ] **Validate on three OSes**, and run the full matrix once (manual dispatch).
- [ ] **At GA, after a release-candidate move:** the GA SDK in `global.json` (without `allowPrerelease`), `samples/Net<N>/global.json`, the setup action (back to `global-json-file`), Roslyn from nuget.org (and `dotnet-tools` out of `NuGet.config`), the runtime-shipped packages at their GA versions, `Microsoft.DotNet.SDK.<N>` in the winget manifest, the end-of-support date in `DotnetSupport`, and the "release candidate" wording in the user guide and on the website.
