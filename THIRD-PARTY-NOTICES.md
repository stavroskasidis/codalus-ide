# Third-party notices

Daedalus Studio is MIT-licensed. This file lists the third-party components that the repository uses, with their licenses. The repository policy is that every dependency must use an **OSI-approved license compatible with MIT**. Licenses are read from each package's own metadata (nuspec or Cargo.toml) when it is added, and checked again at every milestone checkpoint. Full license texts ship inside each package; installers will bundle them in M7. `TODO(issue)`: generate the bundled notice file automatically (dotnet-project-licenses / cargo-about) in M7.

Last verified: 2026-10-08 (M0).

## .NET (NuGet): product code and prototype A

| Package | Version | License | Used by |
|---|---|---|---|
| Microsoft.CodeAnalysis.CSharp.Workspaces, .Workspaces.MSBuild, .Common, .CSharp, .Workspaces.Common, .Analyzers (Roslyn) | 5.9.0 | MIT | language service host |
| Microsoft.Build.Locator | 1.11.2 | MIT | language service host |
| Microsoft.Build.Framework, Microsoft.NET.StringTools (compile-time only; MSBuild comes from the installed SDK) | 17.11.48 / 18.4.0 | MIT | language service host |
| StreamJsonRpc, Microsoft.VisualStudio.Threading, Microsoft.VisualStudio.Validation, Nerdbank.Streams, MessagePack (transitive) | 2.25.29 and transitive | MIT | language service host |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Skia, Avalonia.Win32 / .X11 / .Native, Avalonia.Headless(.XUnit) | 12.1.3 | MIT | prototype A, UI tests |
| Avalonia.AvaloniaEdit | 12.0.0 | MIT | prototype A |
| Dock.Avalonia, Dock.Model, Dock.Model.Mvvm, Dock.Avalonia.Themes.Fluent (and Dock's transitive helpers) | 12.1.0.7 | MIT | prototype A |
| CommunityToolkit.Mvvm (transitive via Dock.Model.Mvvm) | 8.x | MIT | prototype A |
| SkiaSharp, HarfBuzzSharp (and native assets) | per Avalonia 12.1.3 | MIT (wrappers). Native Skia is BSD-3-Clause; HarfBuzz uses the "Old MIT" license | prototype A |
| Avalonia.Angle.Windows.Natives (ANGLE) | 2.1.27548.20260419 | BSD-3-Clause (ANGLE Project) | prototype A, Windows |
| System.Drawing.Common | 10.0.12 | MIT | benchmark harness (Windows probes) |
| Vortice.DXGI, Vortice.Direct3D11, Vortice.DirectX, SharpGen.Runtime(.COM) | 3.8.3 / 2.4.2-beta | MIT | benchmark harness (Desktop Duplication) |

All 91 resolved NuGet packages (direct and transitive) were checked: 82 are MIT, 8 are Apache-2.0 (test-only xUnit, see below) and 1 is BSD-3-Clause (ANGLE).

## .NET (NuGet): tests only (not shipped)

| Package | Version | License |
|---|---|---|
| xunit.v3.mtp-v2 and its xunit.v3.* / xunit.analyzers dependencies | 3.2.2 / 1.27.0 | Apache-2.0 |
| Microsoft.Testing.Platform (transitive) | 2.x | MIT |

## Rust (crates.io): prototype B only

There are 853 crates across all targets. The full per-crate list is in [docs/licenses/spike-gpui-crates.md](docs/licenses/spike-gpui-crates.md), and `cargo deny check licenses` enforces the policy (`spikes/gpui/deny.toml`). Key components:

| Crate(s) | Version | License | Notes |
|---|---|---|---|
| gpui-pre-* (Zed's GPUI, published snapshot) | 0.3.8 | Apache-2.0 | Only the Apache-2.0 GPUI crates are used. **Zed's editor and most of Zed are GPL-3.0 and are not used.** |
| gpui-kit, gpui-component, gpui-kit-assets | 0.7.1 | Apache-2.0 (code); docs CC-BY-4.0 (not redistributed) | |
| gpui-base | 0.7.1 | Apache-2.0 | **Vendored and modified** (one added method). See `spikes/gpui/vendor/PATCHES.md`. |
| wgpu, naga | 29.0.4 | MIT OR Apache-2.0 | |
| accesskit, accesskit_windows / _macos / _unix | 0.24.x | MIT OR Apache-2.0 | |
| tree-sitter, tree-sitter-c-sharp | 0.26.13 / 0.23.5 | MIT | |
| lsp-types | 0.97.0 | MIT | |
| ropey | 2.0.0-beta.1 | MIT OR Apache-2.0 | |
| serde, serde_json, anyhow, futures | 1.x / 0.3 | MIT OR Apache-2.0 | |

### Pending decision (raised at the M0 checkpoint)

The crates below arrive transitively through GPUI. Each one needs a maintainer decision **only if prototype B is chosen**:

| Crate | License | Path | Issue |
|---|---|---|---|
| option-ext 0.2.0 | MPL-2.0 | dirs-sys ← dirs ← shellexpand | OSI-approved, file-level weak copyleft. Compatible with distributing an MIT program if the MPL files stay MPL. |
| dwrote 0.11.5 | MPL-2.0 | zed-font-kit | Same as above. |
| cbindgen 0.28.0 | MPL-2.0 | build dependency (macOS) | Build tool only, not distributed. |
| libbz2-rs-sys 0.2.5 | bzip2-1.0.6 | async-compression | Permissive BSD-style license, but **not OSI-approved**, so it conflicts with the fixed rule. |

## Tools used by the benchmark harness (downloaded, not committed or redistributed)

| Tool | Version | License |
|---|---|---|
| Intel PresentMon (optional; evaluated, not used for the published metrics) | 2.6.0 | MIT |
| Reference IDEs measured (VS Code, JetBrains Rider, Visual Studio) | installed versions | Proprietary. Only launched for measurement and never redistributed. |

## Fonts and assets

None are bundled by the prototypes. They use platform fonts (Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono) by name. Prototype B embeds GPUI Kit's default icon set (`gpui-kit-assets`, Apache-2.0).
