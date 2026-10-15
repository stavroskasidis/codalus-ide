# Third-party notices

Codalus IDE is MIT-licensed. This file lists the third-party components that the repository uses, with their licenses. The repository policy is that every dependency must use an **OSI-approved license compatible with MIT**. Licenses are read from each package's own metadata (nuspec) when it is added, and checked again at every milestone checkpoint. Release packages (ADR 0009) ship this file and `LICENSE` next to the binaries. `TODO(issue)`: also ship the full license texts of the bundled components (and the NOTICE required by Apache-2.0 for the Roslyn assemblies inside netcoredbg), generated automatically (e.g. with dotnet-project-licenses), before the first public release.

Last verified: 2026-10-15 (M20b: the Mono debugger adapter in the debug companion host (`debughost/`, ADR 0065) ships `Mono.Debugger.Soft`, vendored from mono/debugger-libs at commit e7fbb713 in `src/ThirdParty/Mono.Debugger.Soft` (MIT, Novell and Xamarin; its LICENSE is next to it). It is built without Mono.Cecil, and `Microsoft.FileFormats` is replaced by one type of ours, so it adds no packages. The repository's `eula.rtf` (a Microsoft EULA template no file references) is not copied. The test fixture `samples/Debugging/MonoDebuggee` uses .NET 8's desktop Mono runtime pack (`Microsoft.NETCore.App.Runtime.Mono.<rid>`, MIT) and is not shipped. M20a: no new shipped packages. Devices, workloads and runs use the user's own tools (`adb`, the Android emulator, `xcrun simctl`/`devicectl`, `security`, `dotnet workload`, and the Android workload's `InstallAndroidDependencies` target, which installs the Android SDK under its own license after the user agrees). MAUI XAML editing is our own code on Roslyn. One more Fluent UI System Icon (`phone_16_regular`, MIT, from the package already listed). The fixture `samples/Maui` references `Microsoft.Maui.Controls` 10.0.110 (MIT) and is not shipped. M19a: no new shipped packages. The XAML language service (ADR 0047) is our own code on Roslyn, already shipped: AvaloniaVS's completion engine was considered and not reused (no code copied). The previewer (ADR 0048) runs Avalonia's designer host (`Avalonia.Designer.HostApp`, MIT) from the user's own Avalonia package, never shipped by us, and speaks Avalonia's remote protocol through our own codec; `Avalonia.Remote.Protocol` 12.1.3 (MIT per its nuspec) is referenced by the tests only, to check that codec. The previewer agent (`Codalus.Previewer.Agent`, netstandard2.0, shipped in `debughost/`) builds against the same reference packages as the debugging startup hook and uses no packages. The fixture `samples/Avalonia` references Avalonia 12.1.3 (MIT) and is not shipped. M18b: the diagnostics host's new tools (ADR 0046) add no packages: heap snapshots are read from TraceEvent's GC events with our own graph, the File I/O tool calls the OS (`GetProcessIoCounters`, `/proc/<pid>/io`, `proc_pid_rusage`), and the IDE runs the user's own BenchmarkDotNet. The fixtures in `samples/Diagnostics` now use packages, not shipped: `Benchmarks` references BenchmarkDotNet 0.15.8 (MIT) with BenchmarkDotNet.Annotations, Perfolizer, Pragmastat, Iced, CommandLineParser and Gee.External.Capstone (all MIT per their nuspecs; Capstone's native library is BSD-3-Clause), ClrMD, TraceEvent, Roslyn and System.Management (MIT); `Activities` references Microsoft.EntityFrameworkCore.Sqlite 10.0.12 (MIT) with SQLitePCLRaw (Apache-2.0). M18a: the new diagnostics host (`codalus-diaghost`, ADR 0044) adds `Microsoft.Diagnostics.Tracing.TraceEvent` 3.2.8 (MIT) with `System.Reflection.TypeExtensions` 4.7.0 and `Microsoft.Extensions.DependencyInjection.Abstractions` 8.0.2 and `Microsoft.Extensions.Logging.Abstractions` 8.0.3 (through `Microsoft.Diagnostics.NETCore.Client`), all MIT per their nuspecs. TraceEvent's package also carries native Windows binaries, `KernelTraceControl.dll` (and `KernelTraceControl.Win61.dll`, for ETW kernel sessions, from the Windows Performance Toolkit) and `msdia140.dll` (the Visual Studio DIA SDK, for Windows PDBs), which are Microsoft redistributables under proprietary terms: the host excludes the package's build assets (`ExcludeAssets="build;buildTransitive"`), so they are never copied or shipped; EventPipe traces and portable PDBs need neither. Its managed COM interop assemblies `Dia2Lib.dll` and `TraceReloggerLib.dll` are part of the MIT package and ship with the host (unused without the native binaries). The fixtures in `samples/Diagnostics` used no packages then. M15c: the `samples/Scaffolding` fixture and the scaffolding tests restore dotnet-aspnet-codegenerator and its design package, MIT, EF Core Tools, MIT, and the gRPC client packages, Apache-2.0 and BSD-3-Clause, from their nuspecs; the packages the IDE adds to a user's project for a service reference are the user's dependencies, not ours; M27: the new data host (`codalus-datahost`, ADRs 0037 and 0038) adds `Microsoft.Data.Sqlite` (MIT) with `SQLitePCLRaw` (Apache-2.0; it bundles the SQLite library, which is in the public domain), `Microsoft.Data.SqlClient` (MIT) with its MIT dependencies, `Npgsql` (the PostgreSQL License, OSI-approved and permissive), `MySqlConnector` (MIT) and `Microsoft.SqlServer.TransactSql.ScriptDom` (MIT), 34 package versions in all, each read from its nuspec. `Microsoft.Data.SqlClient.SNI.runtime` (SqlClient's native networking on Windows) is under Microsoft's proprietary license terms: it is resolved by restore but excluded from the build (`ExcludeAssets="all"`), never shipped, and the host uses SqlClient's managed networking instead. `Microsoft.SqlServer.DacFx` (schema compare and publish) was checked for D15 and is proprietary ("Microsoft Software License Terms, Microsoft SQL Server Data-Tier Application Framework"), so it isn't used; `Microsoft.Build.Sql` (MIT) and `dotnet-ef` (MIT) are the user's own tools and fixtures' dependencies, not ours. The EF model agent (`codalus-efagent`, ADR 0036) uses no packages. M33: the .NET test plugin also bundles Coverlet's netstandard2.0 collector 6.0.4, and the macOS package the x64 build of netcoredbg 3.1.3-1062, both MIT; the test fixture `samples/TargetFrameworks` is standalone and not shipped. M14c: the new startup hook (`Codalus.Debugging.StartupHook`, netstandard2.0, shipped in `debughost/`) builds against `NETStandard.Library` 2.0.3 (MIT per its nuspec's license link) and `Microsoft.NETCore.Platforms` 1.1.0 (the .NET (corefx) license link, MIT), reference packages that put nothing in the output; the test fixture `samples/Debugging/Generator` references `Microsoft.CodeAnalysis.CSharp` 4.14.0 (MIT per its nuspec, not shipped); remote debugging copies the pinned Linux and macOS builds of netcoredbg, listed below, to the user's targets. M26b: adds `MSBuild.StructuredLogger` and `Microsoft.Build.Utilities.Core` (compile-time only) to the build host for the binary log viewer (ADR 0034), and `Mono.TextTemplating`, `Mono.TextTemplating.Roslyn` and `System.CodeDom` to the new T4 host (`codalus-t4host`, ADR 0035), which also uses the Roslyn version the other hosts use; all MIT per their nuspec. M14b: adds `Microsoft.Diagnostics.Runtime` (ClrMD) and `Microsoft.Diagnostics.NETCore.Client` to the new debug companion host (`codalus-debughost`, ADR 0032), with 15 packages the solution didn't resolve before (ClrMD's symbol and Azure authentication dependencies, below), all MIT per their nuspec. The totals below were recounted from every resolved package's nuspec. M11: adds `AvaloniaEdit.TextMate`, `TextMateSharp`, `TextMateSharp.Grammars` and `Onigwrap` to the shell and `YamlDotNet` to the new markup host, all MIT per their nuspec. Onigwrap bundles the native Oniguruma regular expression library (BSD-2-Clause), and TextMateSharp.Grammars embeds VS Code's built-in grammars and themes (MIT, with upstream grammar licenses kept in VS Code's `ThirdPartyNotices.txt`). JSON Schema validation is our own code: JsonSchema.Net was rejected because version 8 and later use the Open Source Maintenance Fee EULA, which isn't OSI-approved (ADR 0016). M10: adds `Microsoft.CodeAnalysis.CSharp.Scripting` (and the Roslyn packages it depends on, the same version as the language host's) to the new C# Interactive host, MIT per its nuspec; the terminal (ADR 0014) uses no third-party code. M9: adds the NuGet client libraries to the build host, compile-time only (at run time it uses the NuGet that ships in the user's .NET SDK, ADR 0012), Apache-2.0 per their nuspec; the fixture feed `samples/Packages/feed` is generated from our own code and not shipped. M8: adds `Microsoft.TestPlatform.TranslationLayer` to the .NET test plugin and bundles Coverlet with it, both listed below; the test fixture `samples/Testing` is standalone and not shipped. M7; M4–M7 added no NuGet packages, only the downloaded debugger below). M1 added no packages: the shell (`codalus`) resolves the same 39 packages as prototype A, and the plugin host, plugin SDK and Welcome plugin use none. M2 adds `Microsoft.Build` (compile-time) and `Microsoft.VisualStudio.SolutionPersistence` for the build host, both MIT per their nuspec. M3 adds Roslyn's Features packages (`Microsoft.CodeAnalysis.Features`, `.CSharp.Features`) and their dependencies to the language service host, all MIT per their nuspec; it removes `StreamJsonRpc` and its dependencies (the host now uses `Codalus.Rpc`, like the other hosts), `Microsoft.CodeAnalysis.Workspaces.MSBuild` (the host gets its projects from the build host, ADR 0005), and prototype A (the M0 spike, retired).

## .NET (NuGet): product code

| Package | Version | License | Used by |
|---|---|---|---|
| Microsoft.CodeAnalysis.CSharp.Workspaces, .CSharp.Features, .Features, .Common, .CSharp, .Workspaces.Common, .Scripting.Common, .Analyzers (Roslyn) | 5.9.0 | MIT | language service host |
| Microsoft.CodeAnalysis.CSharp.Scripting, .Scripting.Common, .CSharp, .Common, .Analyzers (Roslyn) | 5.9.0 | MIT | C# Interactive host (`codalus-replhost`, ADR 0015) |
| Microsoft.CodeAnalysis.Elfie, Microsoft.DiaSymReader, Humanizer.Core (transitive via Roslyn Features) | 1.0.0 / 2.0.0 / 2.14.1 | MIT | language service host |
| System.Composition (and .AttributedModel, .Convention, .Hosting, .Runtime, .TypedParts), System.Configuration.ConfigurationManager, System.Diagnostics.EventLog, System.Security.Cryptography.ProtectedData (transitive via Roslyn) | 10.0.1 | MIT | language service host |
| System.Data.DataSetExtensions (transitive via Roslyn Features; the nuspec links the .NET (corefx) license) | 4.5.0 | MIT | language service host |
| ICSharpCode.Decompiler | 11.1.0.9782 | MIT | language service host (Go to Definition into referenced assemblies) |
| Microsoft.Build.Locator | 1.11.2 | MIT | build host |
| Microsoft.TestPlatform.TranslationLayer, Microsoft.TestPlatform.ObjectModel (transitive) | 18.10.1 | MIT | .NET test plugin (VSTest through the SDK's vstest.console) |
| Microsoft.Build (compile-time only; MSBuild comes from the installed SDK) | 17.11.48 | MIT | build host |
| Microsoft.VisualStudio.SolutionPersistence | 1.0.52 | MIT | build host (reads and writes .sln/.slnx) |
| Microsoft.Build.Framework, Microsoft.NET.StringTools (compile-time only; MSBuild comes from the installed SDK) | 17.11.48 / 18.4.0 | MIT | build host |
| NuGet.Protocol, NuGet.Credentials and their dependencies NuGet.Packaging, .Configuration, .Versioning, .Common, .Frameworks (compile-time only; NuGet comes from the installed SDK) | 6.11.2 | Apache-2.0 | build host (package management) |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Skia, Avalonia.Win32 / .X11 / .Native, Avalonia.Headless(.XUnit) | 12.1.3 | MIT | shell, UI tests |
| Avalonia.AvaloniaEdit | 12.0.0 | MIT | shell (editor, Output) |
| AvaloniaEdit.TextMate | 12.0.0 | MIT | shell (TextMate highlighting, ADR 0016) |
| TextMateSharp, TextMateSharp.Grammars (embeds VS Code's built-in grammars and themes, MIT) | 2.0.3 | MIT | shell |
| Onigwrap (bundles native Oniguruma, BSD-2-Clause, K. Kosako) | 1.0.10 | MIT (wrapper), BSD-2-Clause (Oniguruma) | shell (TextMate regular expressions) |
| YamlDotNet | 18.1.0 | MIT | markup host (`codalus-markuphost`, YAML) |
| Dock.Avalonia, Dock.Model, Dock.Model.Mvvm, Dock.Avalonia.Themes.Fluent (and Dock's transitive helpers) | 12.1.0.7 | MIT | shell |
| CommunityToolkit.Mvvm (transitive via Dock.Model.Mvvm) | 8.4.2 | MIT | shell |
| SkiaSharp, HarfBuzzSharp (and native assets) | per Avalonia 12.1.3 | MIT (wrappers). Native Skia is BSD-3-Clause; HarfBuzz uses the "Old MIT" license | shell |
| Avalonia.Angle.Windows.Natives (ANGLE) | 2.1.27548.20260419 | BSD-3-Clause (ANGLE Project) | shell, Windows |
| Velopack | 1.2.161 | MIT | shell (the installers' hooks, M31; self-update, M32) |
| MSBuild.StructuredLogger | 2.3.246 | MIT | build host (the binary log viewer, M26b, ADR 0034) |
| Microsoft.Build.Utilities.Core (compile-time only; MSBuild comes from the installed SDK) | 17.11.48 | MIT | build host (StructuredLogger's MSBuild dependency) |
| Mono.TextTemplating, Mono.TextTemplating.Roslyn | 3.0.0 | MIT | T4 host (`codalus-t4host`, M26b, ADR 0035) |
| System.CodeDom (transitive via Mono.TextTemplating) | 6.0.0 | MIT | T4 host |
| Microsoft.CodeAnalysis.CSharp, .Common, .Analyzers (Roslyn, the T4 host's in-process compiler) | 5.9.0 | MIT | T4 host |
| Microsoft.Diagnostics.Runtime (ClrMD) | 4.1.745802 | MIT | debug companion host (`codalus-debughost`, M14b: dumps) |
| Microsoft.Diagnostics.NETCore.Client | 0.2.745401 | MIT | debug companion host (Create Dump); diagnostics host (`codalus-diaghost`, M18: EventPipe sessions) |
| Microsoft.Diagnostics.Tracing.TraceEvent (with its Microsoft.Diagnostics.FastSerialization, Dia2Lib and TraceReloggerLib assemblies; its native KernelTraceControl and msdia140 binaries are excluded, below) | 3.2.8 | MIT | diagnostics host (M18: reading EventPipe events and traces) |
| Microsoft.Extensions.DependencyInjection.Abstractions, .Logging.Abstractions (transitive via Microsoft.Diagnostics.NETCore.Client) | 8.0.2 / 8.0.3 | MIT | diagnostics host |
| System.Reflection.TypeExtensions (transitive via TraceEvent) | 4.7.0 | MIT | diagnostics host |
| Azure.Core, Azure.Identity, System.ClientModel, System.Memory.Data, Microsoft.Identity.Client, Microsoft.Identity.Client.Extensions.Msal, Microsoft.IdentityModel.Abstractions (transitive via ClrMD) | 1.53.0 / 1.21.0 / 1.10.0 / 10.0.3 / 4.83.1 / 4.83.1 / 8.14.0 | MIT | debug companion host |
| Microsoft.Extensions.Configuration.Abstractions, .DependencyInjection.Abstractions, .Diagnostics.Abstractions, .FileProviders.Abstractions, .Hosting.Abstractions, .Logging.Abstractions, .Options, .Primitives, Microsoft.Bcl.AsyncInterfaces, System.Security.Cryptography.ProtectedData (transitive via ClrMD; the last links the .NET (corefx) license) | 10.0.3 / 4.5.0 | MIT | debug companion host |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.12 | MIT | data host (`codalus-datahost`, M27b: SQLite) |
| SQLitePCLRaw.bundle_e_sqlite3, .core, .provider.e_sqlite3, .lib.e_sqlite3 (bundles native SQLite, public domain) | 2.1.12 | Apache-2.0 (SQLite: public domain) | data host |
| Microsoft.Data.SqlClient, Microsoft.Data.SqlClient.Extensions.Abstractions, .Internal.Logging, Microsoft.SqlServer.Server | 7.1.1 / 1.0.0 | MIT | data host (SQL Server, managed networking) |
| Microsoft.IdentityModel.Abstractions, .JsonWebTokens, .Logging, .Protocols, .Protocols.OpenIdConnect, .Tokens, System.IdentityModel.Tokens.Jwt (transitive via SqlClient) | 8.16.0 | MIT | data host |
| Microsoft.Bcl.Cryptography, Microsoft.Extensions.Caching.Abstractions, .Caching.Memory, .Options, .Primitives, System.Configuration.ConfigurationManager, System.Diagnostics.EventLog, System.Security.Cryptography.ProtectedData, System.Threading.RateLimiting (transitive via SqlClient) | 9.0.18 | MIT | data host |
| System.Security.Cryptography.Pkcs (SqlClient's, pinned past its vulnerable 9.0.18) | 10.0.13 | MIT | data host |
| Microsoft.Extensions.DependencyInjection.Abstractions, .Logging.Abstractions (transitive via Npgsql) | 10.0.0 | MIT | data host |
| Npgsql | 10.0.3 | PostgreSQL License | data host (PostgreSQL) |
| MySqlConnector | 2.6.2 | MIT | data host (MySQL and MariaDB) |
| Microsoft.SqlServer.TransactSql.ScriptDom | 180.117.0 | MIT | data host (the SQL language server: T-SQL parsing and formatting) |
| Microsoft.Data.SqlClient.SNI.runtime (resolved, excluded with `ExcludeAssets="all"`, not shipped) | 7.1.0 | Microsoft proprietary | none |
| KernelTraceControl.dll, KernelTraceControl.Win61.dll, msdia140.dll (native binaries inside the TraceEvent package; its build assets are excluded, so they are not copied or shipped) | with TraceEvent 3.2.8 | Microsoft proprietary (Windows Performance Toolkit, Visual Studio DIA SDK redistributables) | none |
| System.Drawing.Common | 10.0.12 | MIT | benchmark harness (Windows probes) |
| Vortice.DXGI, Vortice.Direct3D11, Vortice.DirectX, SharpGen.Runtime(.COM) | 3.8.3 / 2.4.2-beta | MIT | benchmark harness (Desktop Duplication) |

All 181 resolved NuGet package versions (163 packages, direct and transitive) were checked: 159 are MIT (four of them link the .NET (corefx) or .NET Standard license, which is MIT), 19 are Apache-2.0 (the NuGet client libraries, compile-time only, SQLitePCLRaw, and test-only xUnit, see below), 1 is BSD-3-Clause (ANGLE), 1 is the PostgreSQL License (Npgsql), and 1 is proprietary and excluded from the build (the SqlClient SNI runtime, above).

## Coverlet (bundled with the .NET test plugin, ADR 0011)

The plugin ships Coverlet's VSTest data collector (`coverage/collector`, from `coverlet.collector`) and console tool (`coverage/console`, from `coverlet.console`) with the assemblies their packages carry. They are downloaded at build time (`PackageDownload`), not referenced.

| Component | Version | License |
|---|---|---|
| coverlet.collector, coverlet.console (with coverlet.core) | 10.1.0 | MIT |
| Mono.Cecil (and .Mdb, .Pdb, .Rocks) | 0.11.6 | MIT |
| Microsoft.Extensions.DependencyInjection(.Abstractions), .DependencyModel, .FileSystemGlobbing | 10.0.12 (DependencyModel 8.0.2 in the collector) | MIT |
| Microsoft.Bcl.AsyncInterfaces, Microsoft.Bcl.Cryptography, System.Formats.Asn1, System.Security.Cryptography.Pkcs | 10.0.12 | MIT |
| Microsoft.TestPlatform.ObjectModel (with CoreUtilities, PlatformAbstractions; collector) | 18.10.1 | MIT |
| System.CommandLine (console) | 2.0.12 | MIT |
| NuGet.Frameworks (collector), NuGet.Versioning | 7.6.0 | Apache-2.0 |

For test hosts of .NET Core 2.0 to .NET 7 (M33, ADR 0030), the plugin also ships the last collector built for netstandard2.0, in `coverage/collector-netstandard2.0`, with what its package carries:

| Component | Version | License |
|---|---|---|
| coverlet.collector (with coverlet.core) | 6.0.4 | MIT |
| Mono.Cecil (and .Mdb, .Pdb, .Rocks) | 0.11.6 | MIT |
| Microsoft.Extensions.DependencyInjection(.Abstractions), .DependencyModel, .FileSystemGlobbing | 6.0.x | MIT |
| Microsoft.Bcl.AsyncInterfaces, System.Text.Json, System.Text.Encodings.Web, System.Memory, System.Buffers, System.Collections.Immutable, System.Reflection.Metadata, System.Numerics.Vectors, System.Runtime.CompilerServices.Unsafe, System.Threading.Tasks.Extensions | as in the package (6.0.x and 4.5.x) | MIT |
| Microsoft.TestPlatform.ObjectModel (with CoreUtilities, PlatformAbstractions) | 17.8.0 | MIT |
| Newtonsoft.Json | 13.0.3 | MIT |
| NuGet.Frameworks, NuGet.Versioning | 6.8.0 | Apache-2.0 |

## .NET (NuGet): tests only (not shipped)

| Package | Version | License |
|---|---|---|
| xunit.v3.mtp-v2 and its xunit.v3.* / xunit.analyzers dependencies | 3.2.2 / 1.27.0 | Apache-2.0 |
| Microsoft.Testing.Platform (transitive) | 2.x | MIT |
| Microsoft.Testing.Extensions.HangDump (CI's hang dumps) and its Microsoft.Diagnostics.NETCore.Client dependency | 2.0.2 / 0.2.x | MIT |
| Microsoft.CodeAnalysis.CSharp, .Common, .Analyzers and their System.* dependencies (the `samples/Debugging/Generator` fixture) | 4.14.0 / 3.11.0 | MIT |
| NETStandard.Library, Microsoft.NETCore.Platforms (reference packages of the netstandard2.0 startup hook; nothing is shipped from them) | 2.0.3 / 1.1.0 | MIT |
| dotnet-aspnet-codegenerator (a local tool of the `samples/Scaffolding` fixture), Microsoft.VisualStudio.Web.CodeGeneration.Design and their dependencies | 10.0.2 | MIT |
| Microsoft.EntityFrameworkCore.Tools (the `samples/Scaffolding` fixture) | 10.0.12 | MIT |
| BenchmarkDotNet, BenchmarkDotNet.Annotations, Perfolizer, Pragmastat, Iced, CommandLineParser, Gee.External.Capstone and their Microsoft dependencies (the `samples/Diagnostics/Benchmarks` fixture; the IDE runs it, nothing of it ships) | 0.15.8 / 0.6.1 / 3.2.4 / 1.21.0 / 2.9.1 / 2.3.0 | MIT (Capstone's native library: BSD-3-Clause) |
| Microsoft.EntityFrameworkCore.Sqlite and SQLitePCLRaw (the `samples/Diagnostics/Activities` fixture) | 10.0.12 / 2.1.x | MIT / Apache-2.0 |
| Grpc.Net.ClientFactory, Grpc.Net.Client, Grpc.Tools (the gRPC service reference the scaffolding tests add) | 2.71.0 / 2.72.0 | Apache-2.0 |
| Google.Protobuf (the same) | 3.30.2 | BSD-3-Clause |
| Avalonia.Remote.Protocol (the previewer codec's conformance tests, M19a) | 12.1.3 | MIT |


## Vendored source (committed, built from source)

| Component | Version | License | Used by |
|---|---|---|---|
| Mono.Debugger.Soft ([mono/debugger-libs](https://github.com/mono/debugger-libs)), in `src/ThirdParty/Mono.Debugger.Soft` | commit e7fbb713 (2026-02-11) | MIT (Novell, Xamarin) | debug companion host (`debughost/`): the Mono debugger adapter (M20b, ADR 0065) |

## Debugger (downloaded by `eng/get-netcoredbg.sh`; not committed; bundled by installers from M7)

| Component | Version | License | Notes |
|---|---|---|---|
| netcoredbg (Samsung) | 3.2.0-1092 | MIT | The debug adapter (ADR 0007). Pinned by SHA-256. Remote debugging (M14c, ADR 0039) downloads the build of a target's platform (linux-x64, linux-arm64, osx-arm64) on first use into the cache, verified by the same pinned checksums, and copies it to the target. |
| netcoredbg (Samsung), macOS x64 build | 3.1.3-1062 | MIT | Apple silicon only: debugs the programs of .NET 5 and earlier, which run as x64 under Rosetta 2 (M33, ADR 0030; `eng/get-netcoredbg.sh --x64`, `debugger/x64/` in the macOS package). 3.2.0 has no macOS x64 build. Pinned by SHA-256; it carries the same dbgshim and Roslyn components as below. |
| dbgshim (inside the netcoredbg archive; .NET diagnostics) | per netcoredbg 3.2.0-1092 | MIT | Debugging shim of the .NET runtime. |
| Microsoft.CodeAnalysis, .CSharp, .Scripting, .CSharp.Scripting (inside the netcoredbg archive) | 2.x (2017 builds) | Apache-2.0 | netcoredbg's expression evaluator. Apache-2.0 is OSI-approved and compatible with MIT redistribution; installers must include its license and NOTICE. |

## Web language servers (downloaded on demand into the cache folder, M16b, ADR 0024; not committed or shipped)

The shell ships only `web-servers/package.json` and `package-lock.json` (our own). The first time a web file is opened (with the user's consent, setting `web.languageServers`), it downloads the pinned Node.js release from nodejs.org, verified by SHA-256, and runs `npm ci` from that lockfile, which checks every package's integrity hash.

| Component | Version | License | Used for |
|---|---|---|---|
| Node.js (OpenJS Foundation; bundles npm, Artistic-2.0, and other components under their own permissive licenses, listed in its LICENSE file) | 24.21.0 | MIT | runs the web language servers |
| vscode-langservers-extracted (VS Code's HTML and CSS servers, and vscode-eslint's server) | 4.10.0 | MIT | HTML, CSS, SCSS and Less; ESLint, running the project's own ESLint (M16d) |
| typescript-language-server | 6.0.2 | Apache-2.0 | JavaScript and TypeScript |
| TypeScript (tsserver) | 5.9.3 | Apache-2.0 | JavaScript and TypeScript |
| Their dependencies, per the lockfile: Apache-2.0: typescript 5.9.3, typescript 4.9.5, typescript-language-server 6.0.2; BSD-2-Clause: css-select 5.2.2, css-what 6.2.2, domelementtype 2.3.0, domhandler 5.0.3, domutils 3.2.2, entities 4.5.0, nth-check 2.1.1; ISC: boolbase 1.0.0, semver 7.8.5; MIT: @vscode/l10n 0.0.18, core-js 3.50.0, dom-serializer 2.0.0, he 1.2.0, jsonc-parser 3.3.1, node-html-parser 6.1.13, picomatch 2.3.2, regenerator-runtime 0.13.11, request-light 0.7.0, vscode-css-languageservice 6.3.10, vscode-html-languageservice 5.6.2, vscode-json-languageservice 5.7.2, vscode-jsonrpc 9.0.3, vscode-langservers-extracted 4.10.0, vscode-languageserver 10.1.2, vscode-languageserver-protocol 3.18.4, vscode-languageserver-textdocument 1.0.15, vscode-languageserver-types 3.17.5, vscode-languageserver-types 3.18.4, vscode-markdown-languageservice 0.5.0, vscode-nls 5.2.0, vscode-uri 3.2.0 | | | |

## Browser debugger (downloaded on demand into the cache folder, M16c, ADR 0026; not committed or shipped)

The first browser debug session (F5 on a Blazor WebAssembly app) downloads the pinned `js-debug-dap` release from GitHub, verified by SHA-256, and runs it on the Node.js above. The release is one bundled script with its MIT license; it carries no other notices.

| Component | Version | License | Used for |
|---|---|---|---|
| vscode-js-debug (`js-debug-dap`, Microsoft) | 1.140.0 | MIT | debugging Blazor WebAssembly and JavaScript in Chrome, Edge or Chromium |

## Installers (Velopack's `vpk`, installed by `eng/package.sh` into `.work/`; not committed)

`vpk` (Velopack's packaging tool, MIT, 1.2.161, the library's version) builds the Windows Setup.exe, the macOS .pkg and .app, and the Linux AppImage (M31, ADR 0028). It puts some of its own programs into what users install:

| Component | Version | License | Notes |
|---|---|---|---|
| Velopack's `Setup.exe`, `Update.exe` and launcher stub (Windows), `UpdateMac` and `UpdateNix` | 1.2.161 | MIT | **Redistributed**: they install, update and uninstall the app. They are Rust programs from the Velopack repository, built with permissively licensed crates. |
| AppImage type2 runtime (continuous build of 2026-03-07, from vpk's `vendor/`) | — | MIT | **Redistributed**: it is the executable header of the Linux `.AppImage`. It is linked statically with squashfuse (BSD-2-Clause), zstd (BSD-3-Clause), zlib (Zlib), mimalloc (MIT), musl (MIT) and **libfuse3 (LGPL-2.1)**. The runtime is a separate program inside the AppImage, not linked with Codalus IDE's code. LGPL-2.1 is OSI-approved; its source is published by the AppImage and libfuse projects. Only the AppImage carries it. |
| zstd (`zstd.exe`), mksquashfs (backhand), a WiX fork, signtool | per vpk 1.2.161 | BSD-3-Clause or GPL-2.0, MIT/Apache-2.0, MS-RL, Windows SDK license | Build tools inside vpk only; not redistributed. The WiX fork is only used for MSI installers, which we don't build. |

The `.deb` and `.rpm` are built with `dpkg-deb` and `rpmbuild` from the build machine, and the apt and dnf repositories with `apt-ftparchive`, `createrepo_c` and `gpg` (tools only).

## Rust

None. The GPUI prototype (M0) and its crates were removed when Avalonia was chosen (ADR 0001).

## Tools used by the benchmark harness (downloaded, not committed or redistributed)

| Tool | Version | License |
|---|---|---|
| Intel PresentMon (optional; evaluated, not used for the published metrics) | 2.6.0 | MIT |
| Reference IDEs measured (VS Code, JetBrains Rider, Visual Studio) | installed versions | Proprietary. Only launched for measurement and never redistributed. |

## Fonts, icons and assets

No fonts are bundled. The shell uses platform fonts (Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono) by name.

| Component | Version | License | Used for |
|---|---|---|---|
| Fluent UI System Icons (Microsoft, the npm package `@fluentui/svg-icons`) | 1.1.344 (SHA-256 pinned in `eng/get-fluent-icons.sh`) | MIT (checked again 2026-10-12: the repository's `LICENSE` and the package's `license` field) | **Redistributed**, compiled into the shell (D24, M34a): 125 SVGs listed in `src/Codalus.Shell/Icons/fluent-icons.txt`, copied unchanged into `Icons/Fluent/` and turned into geometries at build time (`eng/icons/CompileIcons.cs`). Its licence follows. |

| Material Icon Theme (Material Extensions, the npm package `material-icon-theme`) | 5.39.0 (SHA-256 pinned in `eng/get-material-icons.sh`) | MIT (checked 2026-10-12: the package's `LICENSE` and `license` field) | **Redistributed**, compiled into the shell (D24, M34a): 123 file, folder and project SVGs listed in `src/Codalus.Shell/Icons/material-icons.txt`, copied unchanged into `Icons/Material/` and turned into colored geometries at build time (`eng/icons/CompileIcons.cs`). No brand logos of products we don't integrate are used (the theme's Visual Studio icon is left out). Its licence follows. |

Fluent UI System Icons' licence:

```
MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Material Icon Theme's licence:

```nThe MIT License (MIT)
Copyright (c) 2025 Material Extensions

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Website (served by GitHub Pages; not part of the app)

The website (`website/`, an ASP.NET Core Blazor app on the shared framework, exported to static files) loads nothing from third-party hosts: its fonts are served from the site itself.

| Component | Version | License | Used for |
|---|---|---|---|
| Markdig (Alexandre Mutel) | 1.4.0 | BSD-2-Clause (per its nuspec) | Renders the user guide into the website's docs pages when the site is exported; nothing of it is published. |
| Schibsted Grotesk (The Schibsted Grotesk Project Authors; the latin variable font from `@fontsource-variable/schibsted-grotesk`) | 5.3.0 | OFL-1.1 | **Redistributed** by the website, `website/wwwroot/assets/fonts/schibsted-grotesk.woff2`, with its licence in `OFL-schibsted-grotesk.txt` beside it (linked from every page's footer). |
| JetBrains Mono (The JetBrains Mono Project Authors; the latin variable font from `@fontsource-variable/jetbrains-mono`) | 5.3.0 | OFL-1.1 | **Redistributed** by the website, `website/wwwroot/assets/fonts/jetbrains-mono.woff2`, with its licence in `OFL-jetbrains-mono.txt` beside it (linked from every page's footer). |

## Schemas (bundled)

| Component | Version | License | Used for |
|---|---|---|---|
| The Compose Specification's JSON schema (`compose-spec.json`, compose-spec/compose-spec) | `main` of 2026-10-11 | Apache-2.0 | **Redistributed** as an embedded resource of the markup host (M17a, ADR 0042): validation and hover in `compose.yaml` and `docker-compose*.yml`. Copyright The Compose Specification Authors; the Apache License 2.0 applies to that file, unchanged. |

## Container engines (M17, ADR 0042)

Docker and Podman are the user's own programs, run through their CLIs; nothing of theirs is shipped, and no container image is. Fast mode runs the user's build output on the base images their Dockerfiles name (the .NET templates use `mcr.microsoft.com/dotnet/aspnet` and `runtime`, MIT-licensed .NET on Linux distributions). Remote debugging copies the pinned Linux netcoredbg (listed above) into the container.
