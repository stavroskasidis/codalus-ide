# .NET Framework fixture (M43)

.NET Framework projects for loading, building, converting and (on Windows) running them ([ADR 0075](../../docs/adr/0075-net-framework-projects-and-builds.md)). Everything here builds on every OS: SDK-style projects get the reference assemblies from the `Microsoft.NETFramework.ReferenceAssemblies` packages (MIT), which the SDK adds to their restore; the old-style project gets the same packages, and its `packages.config` package, from Codalus IDE's own restore. They run only on Windows (D31).

| Project | What it has | Used by |
|---|---|---|
| `ConsoleApp` | SDK-style `net48` console app, C# 7.3, `App.config` with `supportedRuntime`, references `Library` | Loading and builds on every OS; Start Without Debugging on Windows (the smoke step `netfx.projects`) |
| `Library` | SDK-style library for `net48` and `net472` (for `LegacyApp`) | Multi-targeted .NET Framework; project references from SDK-style and old-style projects |
| `ConsoleApp.Tests` | SDK-style `net48` xUnit 2 tests (`Microsoft.NET.Test.Sdk` 18.10.1, `xunit.runner.visualstudio` 3.1.5) | Loading and builds now; VSTest's .NET Framework test host in M43b |
| `LegacyApp` | Old-style (non-SDK-style) `v4.7.2` console app as Visual Studio 2022 writes it: `ProjectGuid`, configuration property groups, `Properties/AssemblyInfo.cs`, `App.config` (`appSettings`), `packages.config` with `Codalus.Sample.Legacy` 1.0.0 (from `feed/`), a `HintPath` reference to it, framework references, a project reference to `Library`. `Drafts/NotInProject.cs` is in the folder but not in the project (`#error`) | Old-style evaluation, packages.config restore, the Dependencies node and builds on every OS; Convert to SDK-style (golden files in `tests/Codalus.Workbench.Tests/Golden/NetFramework`) |
| `LegacyWebApp` | Old-style ASP.NET (System.Web) project that imports `$(VSToolsPath)\WebApplications\Microsoft.WebApplication.targets` | Opens with IntelliSense and says why it can't build; left out of solution builds |
| `WinFormsApp` | SDK-style `net48` Windows Forms app, its form in Visual Studio for .NET Framework's designer style (`this.`, full type names, CodeDOM's casts) | Builds on every OS; the Windows Forms designer (M41) on .NET Framework; runs on Windows |

- `NetFramework.slnx` has every project.
- `nuget.config`: the local feed `feed/` (`Codalus.Sample.*` only) and nuget.org for the rest, with package source mapping.
- `feed/Codalus.Sample.Legacy.1.0.0.nupkg`: a `net45` library, generated with `dotnet run --project benchmarks/Codalus.Benchmarks.FixtureGen -- packages samples/NetFramework/feed Codalus.Sample.Legacy`.
- Restored packages.config packages go to `packages/` next to the solution (not committed).
