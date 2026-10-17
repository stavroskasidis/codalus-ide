# .NET Framework

Codalus IDE opens, edits and builds .NET Framework projects (`net48`, `net472` and the other 4.x versions, and 2.0 to 3.5 as best effort) on Windows, Linux and macOS: SDK-style projects and the older non-SDK-style ("old-style") projects with `packages.config`. They run on Windows, on the .NET Framework itself. On Linux and macOS, starting one says that it runs only on Windows.

| | Windows | Linux and macOS |
|---|---|---|
| Open an SDK-style `net4x` project, IntelliSense (C# 7.3) | ✓ | ✓ |
| Open a non-SDK-style project, its Dependencies (references, `packages.config`) | ✓ | ✓ |
| Restore `packages.config` packages | ✓ | ✓ |
| Build | ✓ | ✓ |
| Convert to SDK-style | ✓ | ✓ |
| Start Without Debugging | ✓ | Windows only |
| Debug (`F5`), run tests | coming in a later release | Windows only |
| Projects that need Visual Studio's own targets (ASP.NET web applications) | open with IntelliSense, don't build | open with IntelliSense, don't build |

## Reference assemblies: nothing to install

Building for .NET Framework needs its reference assemblies. Codalus IDE gets them from Microsoft's `Microsoft.NETFramework.ReferenceAssemblies` packages on NuGet (MIT), so no targeting pack or Developer Pack is needed, on any OS:
- **SDK-style projects:** the .NET SDK adds the package to the project's restore by itself wherever the targeting pack isn't installed. The Dependencies node shows it under Frameworks.
- **Non-SDK-style projects:** Codalus restores the package of the project's `TargetFrameworkVersion` into NuGet's global packages folder, and its builds use it when the framework's reference assemblies aren't installed (on Windows with a Developer Pack, the Developer Pack's are used). The Dependencies node shows it under Frameworks (`Microsoft.NETFramework.ReferenceAssemblies.net472`), or the installed framework.

Each framework compiles with its default C# version, 7.3, unless the project sets `LangVersion`; the editor reports what the compiler reports. In an executable, `AutoGenerateBindingRedirects` writes the binding redirects the packages need into the built `.exe.config`.

Outside Codalus (`dotnet build` on the command line), SDK-style projects build the same way. Non-SDK-style projects build with `dotnet build` only where the Developer Pack is installed; converting them to SDK-style (below) makes them build everywhere.

## Non-SDK-style projects

A non-SDK-style project (`<Project ToolsVersion="15.0" xmlns="…">`, importing `Microsoft.CSharp.targets` and listing its files) loads like any other: Solution Explorer marks it "(non-SDK-style)" and shows its files, and the Dependencies node shows:
- the framework (the reference assemblies, above);
- **Packages:** the packages its `packages.config` lists, with their versions, resolved once they're restored;
- **Assemblies:** its `<Reference>` items, those with a `HintPath` resolved when the file is there;
- **Projects:** its project references (an old-style project can reference an SDK-style one; the SDK-style project needs the old-style one's framework among its `TargetFrameworks`).

**Restore** (on opening, Build → Restore, and before each build) runs `dotnet restore` for the SDK-style projects, then restores what `dotnet restore` doesn't: the `packages.config` packages, into the `packages` folder next to the solution (or NuGet's `repositoryPath` setting), from the solution's package sources (its `NuGet.Config`, package source mapping and credentials), and the reference assemblies. The Output window's Restore output lists each package.

**Building** uses the .NET SDK's MSBuild, which has `Microsoft.CSharp.targets` (and Visual Basic's) for these projects. Their output goes where the project says (`bin\Debug\`).

### Projects that need Visual Studio's targets

Some project types import Visual Studio's own MSBuild targets, under `MSBuild\Microsoft\VisualStudio` (`$(VSToolsPath)`): ASP.NET web applications (`Microsoft.WebApplication.targets`), and others. Those targets come only with Visual Studio and its Build Tools, whose MSBuild is proprietary, so Codalus IDE doesn't build these projects. They open with IntelliSense; Solution Explorer marks them "(non-SDK-style, can't build here)" and says why under the project; a solution build leaves them out and says so in the Build output; building one alone fails with the reason. ASP.NET on System.Web (Web Forms, MVC 5) needs IIS or IIS Express to run, which is out of scope.

## Convert to SDK-style

Right-click a non-SDK-style project in Solution Explorer → **Convert to SDK-Style…**. Codalus shows what would change, as a diff, with notes, and changes nothing until you press **Convert**:
- the project becomes `<Project Sdk="Microsoft.NET.Sdk">` with `TargetFramework` (`net472`) and `OutputType`; `UseWindowsForms` or `UseWPF` for Windows Forms and WPF projects;
- what the SDK does by itself is left out: `ProjectGuid`, `FileAlignment`, the default configuration properties (`OutputPath`, `DefineConstants`, `Optimize`, `DebugType`…), the framework references it adds (`System`, `System.Core`, `System.Xml`…), and the `Compile`, `EmbeddedResource` and `None` items its globs include. Files in the project's folder that weren't in the project get a `Remove`, so they stay out; items with metadata (generated resources and settings) become `Update` items;
- `packages.config` becomes `PackageReference` items (a package another one depends on is left out when the packages are restored), its `HintPath` references and NuGet's imports of package targets go, and `packages.config` is deleted;
- `AssemblyInfo.cs`'s attributes that the SDK generates (title, description, company, product, copyright, versions) become properties, and the file keeps only the rest (`ComVisible`, `Guid`), or goes;
- configuration properties that differ from the SDK's defaults stay, in their configuration's group; targets and imports Codalus doesn't know stay as they were.

After converting, the project restores its packages as package references. Projects that import Visual Studio's targets aren't converted (an SDK-style ASP.NET project needs an SDK of its own). The converter follows the approach of Microsoft's [try-convert](https://github.com/dotnet/try-convert) tool, with Codalus IDE's own code.

## Running on Windows

**Start Without Debugging** (`Ctrl+F5`) builds the app and starts its `.exe`, which runs on the .NET Framework installed on Windows (not through `dotnet`); its console output comes to the Output window, or a console window (the `run.console` setting). Help → **Check Environment** shows the installed .NET Framework (from the registry) next to the highest version the solution's projects target, and .NET Framework 3.5 when a project targets 2.0 to 3.5, with the download page.

**Start** (`F5`) says that debugging the .NET Framework's runtime comes in a later release, and offers Start Without Debugging: Codalus's debugger, netcoredbg, debugs .NET (CoreCLR) only. Hot Reload (`dotnet watch`) needs .NET 6 or later.

On Linux and macOS, Start, Start Without Debugging and the Performance Profiler say that the app runs only on Windows, and build nothing. Running .NET Framework apps on Mono isn't supported.

## Tests

`net4x` test projects (xUnit 2, NUnit 3, MSTest v2 on `Microsoft.NET.Test.Sdk`) open and build everywhere. Running them is coming with the debugger; on Linux and macOS the Test Explorer shows their tests from the source and says on the project that they run only on Windows.

## New projects

File → New Project lists **.NET Framework Console App** and **.NET Framework Class Library**: SDK-style projects for .NET Framework 4.8 (or 4.8.1, 4.7.2, 4.6.2), written by Codalus IDE (the .NET SDK's own templates don't target .NET Framework), in C# 7.3 with a `Main` method. On Linux and macOS they come last, marked "Windows only".

## Not supported

- Debugging and running tests (a later release), and running on Mono.
- Building with Visual Studio's MSBuild or Build Tools; COM references (`ResolveComReference`), ClickOnce publishing, the Entity Framework 6 designer, Web Forms, C++/CLI and VSTO.

- The Windows Forms designer and WPF's previewer with .NET Framework apps haven't been validated yet: their forms and XAML edit as code.

See also: [WPF, Windows Forms and WinUI 3](windows-desktop.md), [Older .NET versions](dotnet-versions.md), [NuGet packages](packages.md).
