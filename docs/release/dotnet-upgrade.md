# Moving to a new .NET release

Daedalus follows every new .NET major version (D22, [ADR 0025](../adr/0025-hosts-and-the-users-sdk.md)). Each one is a milestone-sized task: it starts at the first release candidate and finishes at GA. .NET 11 is M30 (M30a on the release candidate, M30b at GA); .NET 12 follows in November 2027.

## At the first release candidate: the new SDK's projects work

Daedalus still runs on the current .NET. Its build host rolls forward onto the new runtime for solutions that use the new SDK (ADR 0025).

- [ ] **Install the SDK where the tests run.**
  - `samples/Net<N>/global.json` pins the release candidate (copy `samples/Net11` for a new version).
  - `.github/workflows/ci.yml` and `release.yml` (`setup-dotnet`'s `dotnet-version`) install the same version.
  - `eng/validate.sh` installs it from the fixture's `global.json`, on Linux (WSL) and the Mac.
- [ ] **Projects:** the fixture opens, evaluates, builds, runs, debugs, tests (on both frameworks) and Hot Reloads: `DotnetVersionsTests` (Projects), `Net11Tests` (Shell) and the smoke test's `dotnet.net11` step. Update them for the new version.
- [ ] **netcoredbg:** debugging on the new runtime works with the pinned netcoredbg (`eng/get-netcoredbg.sh`), or a newer release is pinned. If none works, record the gap.
- [ ] **Lists of versions:**
  - target frameworks in `MsBuildKnowledge.TargetFrameworks` (the markup host's completion), including the platform variants;
  - `LangVersion` in `MsBuildKnowledge` and in the property pages (`ProjectPropertyPages`);
  - the property pages' hints that name a framework;
  - `DotnetSupport` (the SDK status): add the new channel, and fill in end-of-support dates from `releases-index.json` as Microsoft announces them.
- [ ] **Schemas:** new `global.json`, `runtimeconfig.json` and MSBuild properties in the markup host's schemas, if the release adds any.
- [ ] **Analyzers and generators:** check which Roslyn version the new SDK's generators reference (the Razor generator, the targeting pack's generators). Those newer than the language host's Roslyn don't load until the next step. Document them as gaps.
- [ ] **The user guide:** the prerequisites, and the known gaps in "Solutions and projects".

## At GA: Daedalus moves to the new .NET

- [ ] `global.json` uses the GA SDK, without `allowPrerelease`. `Directory.Build.props` targets the new `net<N>.0`, and every host follows.
- [ ] **Native AOT:** `dotnet publish` of the shell has no new trimming or AOT warnings.
- [ ] **Dependencies** on the new runtime: Avalonia, Dock, AvaloniaEdit and TextMate, the Roslyn, MSBuild and NuGet client packages, Microsoft.Testing.Platform, Coverlet and Velopack. Record version changes in `THIRD-PARTY-NOTICES.md`.
- [ ] **Roslyn** in the language host and C# Interactive: the version of the new SDK's compiler, from nuget.org. Then the new C# version's syntax and the new SDK's generators work in the editor.
- [ ] **Fixtures:** the regular fixtures (`samples/BuildAndRun`, `samples/Debugging`, `samples/Testing`) get the new target framework. The new C# version's features go into `samples/LanguageFeatures`.
- [ ] **Budgets:** measure startup, typing, idle memory and the shell's binary size before and after (`benchmarks/`). None may regress.
- [ ] **CI and the validation machines:** the SDK pinned by `global.json` installs everywhere. The previous version's fixture (`samples/Net<N>`) can go once the regular fixtures target the new version.
- [ ] **The target framework matrix** (M33, [ADR 0030](../adr/0030-older-dotnet.md)): the previous version becomes a row of it. Add a `Net<N>0` folder to `samples/TargetFrameworks` (app, library, xUnit project, and the next C# version's feature), its runtime to `eng/get-runtimes.sh`, its row to `Matrix.All` (`tests/DaedalusStudio.TargetFrameworks.Tests`) and its C# version to `DotnetFrameworks.DefaultCSharpVersion`. Update the support table in `docs/user-guide/dotnet-versions.md` from the suite's results on three OSes.
- [ ] **Validate on three OSes**, and run the full matrix once (manual dispatch).
