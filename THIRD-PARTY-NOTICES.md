# Third-party notices

Daedalus Studio is MIT-licensed. This file lists the third-party components that the repository uses, with their licenses. The repository policy is that every dependency must use an **OSI-approved license compatible with MIT**. Licenses are read from each package's own metadata (nuspec) when it is added, and checked again at every milestone checkpoint. Full license texts ship inside each package; installers will bundle them in M7. `TODO(issue)`: generate the bundled notice file automatically (e.g. dotnet-project-licenses) in M7.

Last verified: 2026-10-09 (M3). M1 added no packages: the shell (`daedalus`) resolves the same 39 packages as prototype A, and the plugin host, plugin SDK and Welcome plugin use none. M2 adds `Microsoft.Build` (compile-time) and `Microsoft.VisualStudio.SolutionPersistence` for the build host, both MIT per their nuspec. M3 adds Roslyn's Features packages (`Microsoft.CodeAnalysis.Features`, `.CSharp.Features`) and their dependencies to the language service host, all MIT per their nuspec; it removes `StreamJsonRpc` and its dependencies (the host now uses `Daedalus.Rpc`, like the other hosts), `Microsoft.CodeAnalysis.Workspaces.MSBuild` (the host gets its projects from the build host, ADR 0005), and prototype A (the M0 spike, retired).

## .NET (NuGet): product code

| Package | Version | License | Used by |
|---|---|---|---|
| Microsoft.CodeAnalysis.CSharp.Workspaces, .CSharp.Features, .Features, .Common, .CSharp, .Workspaces.Common, .Scripting.Common, .Analyzers (Roslyn) | 5.9.0 | MIT | language service host |
| Microsoft.CodeAnalysis.Elfie, Microsoft.DiaSymReader, Humanizer.Core (transitive via Roslyn Features) | 1.0.0 / 2.0.0 / 2.14.1 | MIT | language service host |
| System.Composition (and .AttributedModel, .Convention, .Hosting, .Runtime, .TypedParts), System.Configuration.ConfigurationManager, System.Diagnostics.EventLog, System.Security.Cryptography.ProtectedData (transitive via Roslyn) | 10.0.1 | MIT | language service host |
| System.Data.DataSetExtensions (transitive via Roslyn Features; the nuspec links the .NET (corefx) license) | 4.5.0 | MIT | language service host |
| Microsoft.Build.Locator | 1.11.2 | MIT | build host |
| Microsoft.Build (compile-time only; MSBuild comes from the installed SDK) | 17.11.48 | MIT | build host |
| Microsoft.VisualStudio.SolutionPersistence | 1.0.52 | MIT | build host (reads and writes .sln/.slnx) |
| Microsoft.Build.Framework, Microsoft.NET.StringTools (compile-time only; MSBuild comes from the installed SDK) | 17.11.48 / 18.4.0 | MIT | build host |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Skia, Avalonia.Win32 / .X11 / .Native, Avalonia.Headless(.XUnit) | 12.1.3 | MIT | shell, UI tests |
| Avalonia.AvaloniaEdit | 12.0.0 | MIT | shell (editor, Output) |
| Dock.Avalonia, Dock.Model, Dock.Model.Mvvm, Dock.Avalonia.Themes.Fluent (and Dock's transitive helpers) | 12.1.0.7 | MIT | shell |
| CommunityToolkit.Mvvm (transitive via Dock.Model.Mvvm) | 8.4.2 | MIT | shell |
| SkiaSharp, HarfBuzzSharp (and native assets) | per Avalonia 12.1.3 | MIT (wrappers). Native Skia is BSD-3-Clause; HarfBuzz uses the "Old MIT" license | shell |
| Avalonia.Angle.Windows.Natives (ANGLE) | 2.1.27548.20260419 | BSD-3-Clause (ANGLE Project) | shell, Windows |
| System.Drawing.Common | 10.0.12 | MIT | benchmark harness (Windows probes) |
| Vortice.DXGI, Vortice.Direct3D11, Vortice.DirectX, SharpGen.Runtime(.COM) | 3.8.3 / 2.4.2-beta | MIT | benchmark harness (Desktop Duplication) |

All 96 resolved NuGet packages (direct and transitive) were checked: 87 are MIT, 8 are Apache-2.0 (test-only xUnit, see below) and 1 is BSD-3-Clause (ANGLE).

## .NET (NuGet): tests only (not shipped)

| Package | Version | License |
|---|---|---|
| xunit.v3.mtp-v2 and its xunit.v3.* / xunit.analyzers dependencies | 3.2.2 / 1.27.0 | Apache-2.0 |
| Microsoft.Testing.Platform (transitive) | 2.x | MIT |


## Rust

None. The GPUI prototype (M0) and its crates were removed when Avalonia was chosen (ADR 0001).

## Tools used by the benchmark harness (downloaded, not committed or redistributed)

| Tool | Version | License |
|---|---|---|
| Intel PresentMon (optional; evaluated, not used for the published metrics) | 2.6.0 | MIT |
| Reference IDEs measured (VS Code, JetBrains Rider, Visual Studio) | installed versions | Proprietary. Only launched for measurement and never redistributed. |

## Fonts and assets

None are bundled. The shell uses platform fonts (Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono) by name.
