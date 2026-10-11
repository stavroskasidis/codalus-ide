# Third-party notices

Daedalus Studio is MIT-licensed. This file lists the third-party components that the repository uses, with their licenses. The repository policy is that every dependency must use an **OSI-approved license compatible with MIT**. Licenses are read from each package's own metadata (nuspec) when it is added, and checked again at every milestone checkpoint. Release packages (ADR 0009) ship this file and `LICENSE` next to the binaries. `TODO(issue)`: also ship the full license texts of the bundled components (and the NOTICE required by Apache-2.0 for the Roslyn assemblies inside netcoredbg), generated automatically (e.g. with dotnet-project-licenses), before the first public release.

Last verified: 2026-10-11 (M33: the .NET test plugin also bundles Coverlet's netstandard2.0 collector 6.0.4, and the macOS package the x64 build of netcoredbg 3.1.3-1062, both MIT; the test fixture `samples/TargetFrameworks` is standalone and not shipped. M14b: adds `Microsoft.Diagnostics.Runtime` (ClrMD) and `Microsoft.Diagnostics.NETCore.Client` to the new debug companion host (`daedalus-debughost`, ADR 0032), with 15 packages the solution didn't resolve before (ClrMD's symbol and Azure authentication dependencies, below), all MIT per their nuspec. The totals below were recounted from every resolved package's nuspec. M11: adds `AvaloniaEdit.TextMate`, `TextMateSharp`, `TextMateSharp.Grammars` and `Onigwrap` to the shell and `YamlDotNet` to the new markup host, all MIT per their nuspec. Onigwrap bundles the native Oniguruma regular expression library (BSD-2-Clause), and TextMateSharp.Grammars embeds VS Code's built-in grammars and themes (MIT, with upstream grammar licenses kept in VS Code's `ThirdPartyNotices.txt`). JSON Schema validation is our own code: JsonSchema.Net was rejected because version 8 and later use the Open Source Maintenance Fee EULA, which isn't OSI-approved (ADR 0016). M10: adds `Microsoft.CodeAnalysis.CSharp.Scripting` (and the Roslyn packages it depends on, the same version as the language host's) to the new C# Interactive host, MIT per its nuspec; the terminal (ADR 0014) uses no third-party code. M9: adds the NuGet client libraries to the build host, compile-time only (at run time it uses the NuGet that ships in the user's .NET SDK, ADR 0012), Apache-2.0 per their nuspec; the fixture feed `samples/Packages/feed` is generated from our own code and not shipped. M8: adds `Microsoft.TestPlatform.TranslationLayer` to the .NET test plugin and bundles Coverlet with it, both listed below; the test fixture `samples/Testing` is standalone and not shipped. M7; M4–M7 added no NuGet packages, only the downloaded debugger below). M1 added no packages: the shell (`daedalus`) resolves the same 39 packages as prototype A, and the plugin host, plugin SDK and Welcome plugin use none. M2 adds `Microsoft.Build` (compile-time) and `Microsoft.VisualStudio.SolutionPersistence` for the build host, both MIT per their nuspec. M3 adds Roslyn's Features packages (`Microsoft.CodeAnalysis.Features`, `.CSharp.Features`) and their dependencies to the language service host, all MIT per their nuspec; it removes `StreamJsonRpc` and its dependencies (the host now uses `DaedalusStudio.Rpc`, like the other hosts), `Microsoft.CodeAnalysis.Workspaces.MSBuild` (the host gets its projects from the build host, ADR 0005), and prototype A (the M0 spike, retired).

## .NET (NuGet): product code

| Package | Version | License | Used by |
|---|---|---|---|
| Microsoft.CodeAnalysis.CSharp.Workspaces, .CSharp.Features, .Features, .Common, .CSharp, .Workspaces.Common, .Scripting.Common, .Analyzers (Roslyn) | 5.9.0 | MIT | language service host |
| Microsoft.CodeAnalysis.CSharp.Scripting, .Scripting.Common, .CSharp, .Common, .Analyzers (Roslyn) | 5.9.0 | MIT | C# Interactive host (`daedalus-replhost`, ADR 0015) |
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
| YamlDotNet | 18.1.0 | MIT | markup host (`daedalus-markuphost`, YAML) |
| Dock.Avalonia, Dock.Model, Dock.Model.Mvvm, Dock.Avalonia.Themes.Fluent (and Dock's transitive helpers) | 12.1.0.7 | MIT | shell |
| CommunityToolkit.Mvvm (transitive via Dock.Model.Mvvm) | 8.4.2 | MIT | shell |
| SkiaSharp, HarfBuzzSharp (and native assets) | per Avalonia 12.1.3 | MIT (wrappers). Native Skia is BSD-3-Clause; HarfBuzz uses the "Old MIT" license | shell |
| Avalonia.Angle.Windows.Natives (ANGLE) | 2.1.27548.20260419 | BSD-3-Clause (ANGLE Project) | shell, Windows |
| Velopack | 1.2.161 | MIT | shell (the installers' hooks, M31; self-update, M32) |
| Microsoft.Diagnostics.Runtime (ClrMD) | 4.1.745802 | MIT | debug companion host (`daedalus-debughost`, M14b: dumps) |
| Microsoft.Diagnostics.NETCore.Client | 0.2.745401 | MIT | debug companion host (Create Dump) |
| Azure.Core, Azure.Identity, System.ClientModel, System.Memory.Data, Microsoft.Identity.Client, Microsoft.Identity.Client.Extensions.Msal, Microsoft.IdentityModel.Abstractions (transitive via ClrMD) | 1.53.0 / 1.21.0 / 1.10.0 / 10.0.3 / 4.83.1 / 4.83.1 / 8.14.0 | MIT | debug companion host |
| Microsoft.Extensions.Configuration.Abstractions, .DependencyInjection.Abstractions, .Diagnostics.Abstractions, .FileProviders.Abstractions, .Hosting.Abstractions, .Logging.Abstractions, .Options, .Primitives, Microsoft.Bcl.AsyncInterfaces, System.Security.Cryptography.ProtectedData (transitive via ClrMD; the last links the .NET (corefx) license) | 10.0.3 / 4.5.0 | MIT | debug companion host |
| System.Drawing.Common | 10.0.12 | MIT | benchmark harness (Windows probes) |
| Vortice.DXGI, Vortice.Direct3D11, Vortice.DirectX, SharpGen.Runtime(.COM) | 3.8.3 / 2.4.2-beta | MIT | benchmark harness (Desktop Duplication) |

All 136 resolved NuGet package versions (129 packages, direct and transitive) were checked: 120 are MIT (two of them link the .NET (corefx) license, which is MIT), 15 are Apache-2.0 (the NuGet client libraries, compile-time only, and test-only xUnit, see below) and 1 is BSD-3-Clause (ANGLE).

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


## Debugger (downloaded by `eng/get-netcoredbg.sh`; not committed; bundled by installers from M7)

| Component | Version | License | Notes |
|---|---|---|---|
| netcoredbg (Samsung) | 3.2.0-1092 | MIT | The debug adapter (ADR 0007). Pinned by SHA-256. |
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
| AppImage type2 runtime (continuous build of 2026-03-07, from vpk's `vendor/`) | — | MIT | **Redistributed**: it is the executable header of the Linux `.AppImage`. It is linked statically with squashfuse (BSD-2-Clause), zstd (BSD-3-Clause), zlib (Zlib), mimalloc (MIT), musl (MIT) and **libfuse3 (LGPL-2.1)**. The runtime is a separate program inside the AppImage, not linked with Daedalus Studio's code. LGPL-2.1 is OSI-approved; its source is published by the AppImage and libfuse projects. Only the AppImage carries it. |
| zstd (`zstd.exe`), mksquashfs (backhand), a WiX fork, signtool | per vpk 1.2.161 | BSD-3-Clause or GPL-2.0, MIT/Apache-2.0, MS-RL, Windows SDK license | Build tools inside vpk only; not redistributed. The WiX fork is only used for MSI installers, which we don't build. |

The `.deb` and `.rpm` are built with `dpkg-deb` and `rpmbuild` from the build machine, and the apt and dnf repositories with `apt-ftparchive`, `createrepo_c` and `gpg` (tools only).

## Rust

None. The GPUI prototype (M0) and its crates were removed when Avalonia was chosen (ADR 0001).

## Tools used by the benchmark harness (downloaded, not committed or redistributed)

| Tool | Version | License |
|---|---|---|
| Intel PresentMon (optional; evaluated, not used for the published metrics) | 2.6.0 | MIT |
| Reference IDEs measured (VS Code, JetBrains Rider, Visual Studio) | installed versions | Proprietary. Only launched for measurement and never redistributed. |

## Fonts and assets

None are bundled. The shell uses platform fonts (Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono) by name.
