# 0069. WPF XAML language service

- **Status:** accepted (M40a, 2026-10-16; the maintainer's standing go-ahead for the milestones, D31)
- **Date:** 2026-10-16
- **Deciders:** maintainer
- **Amends:** [ADR 0047](0047-xaml-language-service.md) (the XAML language service)

## Context

D31 moved WinForms and WPF into scope as Windows-only features: they run, debug and test on Windows, and the other OSes are decided later. M40a asks for WinForms and WPF projects that load with IntelliSense, run, debug and test on Windows, and WPF XAML editing on the M19a engine, with the markup compiler's `MC…` errors mapped back from builds. Three facts shape it:
- The .NET SDK refuses to restore or build a `net*-windows` project on Linux and macOS (NETSDK1100) unless `EnableWindowsTargeting` is true. With it, WPF's and Windows Forms' reference packs download from NuGet and the projects build, markup compiler included (it is managed code).
- Avalonia's and .NET MAUI's targets make their XAML items additional files of the compilation; WPF's don't. Its markup compiler runs at design time (`DesignTimeMarkupCompilation` in `CoreCompileDependsOn`), writing the `x:Class`'s other part and the named elements' fields to `obj/*.g.cs` (`.g.i.cs` at design time) from the XAML on disk.
- WPF's bindings aren't compiled: a binding path, and a `StaticResource` key, are resolved when the XAML loads. The data context is often set in code, and resources may come from another assembly's compiled dictionaries.

## Decisions

**Loading on every OS.** The build host sets `EnableWindowsTargeting=true` as an environment variable at start on Linux and macOS (`WindowsDesktop.EnableWindowsTargeting`). An environment variable is MSBuild's lowest-priority property source, so a project or a user that sets it keeps its value, and it reaches every evaluation, design-time build and build of the host, its worker nodes, and the `dotnet restore` it starts. Builds therefore work there too; running doesn't (below). Projects report `UsesWpf` and `UsesWindowsForms` (`UseWPF`, `UseWindowsForms`).

**WPF's XAML as additional files.** The design-time build asks for the `Page` and `ApplicationDefinition` items and adds each `.xaml` to the compiler's command line as `/additionalfile:` (design time only; the real build's command line is the SDK's). The C# host then finds a `.xaml` file's project as it does for Avalonia and MAUI, and the editor's text replaces the additional document's. The design-time cache's schema moves to 2.

**The dialect, in the same engine** (`src/Codalus.LanguageServices.Host/Xaml`). A `.xaml` file is WPF's when its compilation references WPF (`System.Windows.DependencyObject` and `FrameworkElement`) and neither Avalonia nor MAUI (`XamlContext.IsWpf`); `.axaml` stays Avalonia's, and MAUI's and Avalonia's `.xaml` keep their behaviour. On top of ADR 0047:
- **Types and members:** namespaces from WPF's `[XmlnsDefinition]`s and `clr-namespace:`; dependency properties by convention (a `XxxProperty` field of type `DependencyProperty`, with or without a CLR wrapper); attached properties as a `DependencyProperty` field with a static `GetXxx` (the value type from it), and attached events as a static `AddXxxHandler(DependencyObject, handler)`; `[ContentProperty]`; WPF's markup extensions (`System.Windows.Markup.MarkupExtension`); values from `Colors` for brushes and colors and from WPF's static holder classes (`FontWeights`, `Cursors`…).
- **Completion:** WPF's markup extensions and binding arguments, `RelativeSource` (modes, `AncestorType`), `d:DesignInstance` (types, options), `d:DataContext` as a `d:DesignInstance` snippet, setters', triggers' and conditions' `Property` and `Value` from the style's or template's target type or from the element their `TargetName` names, `EventSetter`, `TargetName`, `StartupUri` and `ResourceDictionary.Source` from the project's XAML files, `xmlns` with WPF's namespace and `clr-namespace:`, WPF's `x:` directives (`x:Shared`, `x:FieldModifier`, `x:Uid`) instead of compiled bindings'.
- **The data context** (`XamlContext.WpfDataContext`): the closest `d:DataContext="{d:DesignInstance …}"`, `<X.DataContext>` element, or `DataContext="{Binding path}"` (followed from the scope above), and a data template's `DataType`; unknown in a data template without one and in resources and styles (their data context is where they're used). A binding that names its source (`ElementName`, `RelativeSource Self`, `TemplatedParent`, `AncestorType`, `Source={StaticResource}` of the same file) starts from that source; a user control's root element is its `x:Class`. Paths complete properties only.
- **Resources** (`XamlResources`): the keys a file sees are its own, those of the dictionaries it merges (relative, rooted and `pack://application:,,,/Assembly;component/…` sources resolved within the solution), `App.xaml`'s (the project's `Application` root) with its merged dictionaries, and those of the files that merge it. The list is complete when the project has an `App.xaml` and every merged dictionary was found.
- **Diagnostics:** unknown types, members and setter or trigger properties are errors (`XAML0003`, `XAML0004`), as the markup compiler or the loader would reject them. A binding path the data context lacks (`XAML0006`), an unresolved `d:DesignInstance` type (`XAML0003`), and a `StaticResource` key outside a complete resource list (`XAML0007`, new) are **warnings**: WPF reports them only at run time, and the editor's view of the data context and resources is partial. A source whose properties come at run time (`ICustomTypeDescriptor`, `ITypedList`, dynamic objects, XML nodes) isn't checked.
- **Navigation:** Go to Definition prefers declarations outside the markup compiler's `*.g.cs`/`*.g.i.cs` (so `x:Class` lands in the code-behind), and `x:Name` rename leaves them out (the next design-time build writes them from the renamed XAML).

**The markup compiler's errors.** `XamlBuildDiagnostics` takes `.xaml` files as well as `.axaml`: the last build's `MC…` errors are squiggles in their files at their lines, until the file changes or the next build, as `AVLN` errors are (ADR 0047).

**Running is Windows only (D31).** On Linux and macOS, Start, Start Without Debugging (Hot Reload included), multiple startup projects and the profiler's launch say that a WPF, Windows Forms or Windows-only project (every target framework `-windows`) runs only on Windows, before building it; the test plugin leaves out test projects whose every framework targets Windows, and says why on the project and in the run. On Windows nothing is special: these are CoreCLR apps, run through the usual paths and debugged by netcoredbg. A Windows machine as a remote target for the other OSes is decided later.

## Consequences

- WPF XAML is edited, checked and navigated on every OS before anything runs, and the tests of the language service run on every CI runner; running, debugging and testing are proven only on Windows.
- Builds on Linux and macOS succeed where a plain `dotnet build` fails with NETSDK1100; the user guide says to pass `-p:EnableWindowsTargeting=true` outside the IDE.
- The markup compiler's generated fields follow the XAML on disk: a field added or renamed in the editor reaches the code-behind's IntelliSense after a save and the next design-time build.
- Binding paths with no data context the editor can see (set in code) aren't completed or checked; resource keys of a library or behind another assembly's dictionary aren't checked. Both are warnings when they are, never errors.
- The WPF previewer and the Live Visual Tree for WPF are M40b ("WPF previewer hosting", a new ADR then).
