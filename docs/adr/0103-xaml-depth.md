# 0103. XAML depth: Hot Reload, refactorings and failed bindings

- **Status:** accepted (0.13, 2026-11-13)
- **Date:** 2026-11-13
- **Deciders:** maintainer (D50, D56)
- **Milestone:** [M64](../roadmap.md#m64--xaml-depth-hot-reload-refactorings-and-xaml-debugging)
- **Amends:** [ADR 0047](0047-xaml-language-service.md) (rename crosses C# and XAML, and XAML has code actions), [ADR 0052](0052-live-visual-tree.md) and [ADR 0072](0072-wpf-previewer-hosting.md) (the live agents reload XAML and report failed bindings)

## Context

The XAML engine (M19a, M40a) edits, completes and checks Avalonia and WPF XAML in the C# host, and the live agents (M19b, M40b) show a running app's tree. But a XAML change needed a restart, a rename from XAML stopped at `x:Name` (and a C# rename never reached the XAML's binding paths, `x:Class` or types), XAML had no code actions, and a binding that failed at run time was silent in the IDE: it was a line of the app's trace, if the app logged at all.

## Decisions (D56, settled at M64's plan as recommended)

| Question | Verdict |
|---|---|
| How does XAML Hot Reload replace live content? | **Through the live agent in the running app:** Avalonia's runtime XAML compiler loads the saved file again into each live instance of its `x:Class`; WPF's XAML reader (as the WPF previewer reads loose XAML) populates each instance, on Windows only. |
| What does it keep, and what does it refuse? | **It keeps the data context and the live objects' state that XAML doesn't set**; named fields point at the new elements; event handlers the XAML names are connected by the loader to the running class's methods. **An edit it can't apply** (a changed `x:Class`, a handler or type the running app doesn't have, a parse error) **is an Error List row asking for a restart**, as a rude edit is. |
| On save or as you type? | **On save.** |
| MAUI and WinUI? | **Out**: they have no open runtime loader (M20's and M44's gaps). |

## Decision

### Rename across C# and XAML (the C# host)

- **From C#.** `RenameFeature` renames through Roslyn as before, then `XamlRename.WithXamlAsync` adds the symbol's uses in every XAML file of the solution: the files whose text has the name as a whole word are parsed, and each occurrence is resolved with the engine's own `XamlNavigation.TargetAsync` (the hover and Go to Definition path), so a use counts exactly when Go to Definition would reach the symbol from it: compiled binding paths under `x:DataType`, WPF's paths from `d:DataContext`, `{x:Static}`, `x:Class`, `x:DataType`, `DataType`, `TargetType`, `{x:Type}`, `d:DesignInstance`, element and property-element names, attributes, style selectors' types and event handlers. Symbols from different compilations are matched by their documentation ID and assembly. Types, properties, fields, ordinary methods and events take part; locals and parameters don't. WPF's markup compiler output (`obj/*.g.cs`, `*.g.i.cs`) is left out of the edit: the build writes it again from the renamed XAML.
- **From XAML.** Prepare Rename accepts, besides an element name (M19a), a symbol the solution declares in source (not metadata, not generated code) and a resource key defined in the project's XAML. A symbol is renamed through Roslyn (its C# uses) plus its XAML uses as above; the placeholder is the name under the caret (`TodoItemViewModel` in `vm:TodoItemViewModel`).
- **Resource keys.** An `x:Key`'s value is now a target too (hover, definition, rename). A key's rename covers the `x:Key` and the markup extensions that name it in the XAML of the project, the projects it references and those that reference it, and the string keys of `FindResource`, `TryFindResource`, `TryGetResource` and `Resources["…"]` in their C#.

### Code actions in XAML (the C# host)

`textDocument/codeAction` answers for XAML (`XamlCodeActions`), each action with its edit (no resolve step), so the lightbulb's list and preview work unchanged:
- **Extract Resource** on an attribute's plain value whose type XAML writes as an element (strings and numbers as `x:String`/`x:Double`… in Avalonia and `sys:` in WPF, a brush from a color, `Thickness`, `CornerRadius` and the like): to the file's resources (made as the root's first child when missing, inside its `ResourceDictionary` when it has one), with a variant that also replaces every equal value of the same property in the file, and to `App.axaml`/`App.xaml`. The key is the element and property (`DockPanelMargin`), made unique; rename it afterwards.
- **Extract Style** on an element's start tag: its plain property attributes (not names, classes, styles, events, directives or attached properties) become setters. Avalonia: a `Style` with a class selector in the root's `Styles`, and the class added to the element; WPF: a keyed `Style` with a `TargetType` in the root's resources, and `Style="{StaticResource …}"` on the element.
- **Generate from use**, on a binding path the data type lacks (`x:DataType`, a data template's type, WPF's `d:DataContext`) whose type is a class in source: a property (its type from the target property's when simple, else `object`; an `[ObservableProperty]` field when CommunityToolkit.Mvvm is referenced and the class is an `ObservableObject`); on a command binding, a `[RelayCommand]` method when CommunityToolkit.Mvvm is referenced (`SaveCommand` → `Save()`, the class made partial, the using added), else an `ICommand` property; on a converter resource that doesn't exist, a converter class in `Converters/` (Avalonia's or WPF's `IValueConverter`) and its resource with a namespace prefix.

### Failed bindings (the live agents, the shell)

- **Avalonia.** The agent hooks Avalonia's logger from the startup hook on, before the app's first binding: once `Avalonia.Base` loads it watches `Logger.Sink` for a minute; the app's own sink (Avalonia's `StringLogSink`, which `LogToTrace`, `LogToTextWriter` and `LogToDelegate` make) keeps its lines and the agent reads them too, and with none the agent installs a sink for `Binding` warnings only. A sink of another class is left alone (nothing is reported then). A failure (`An error occurred binding {Property} to {Expression} …`) is kept once per element, property and path, up to 500; the agent says `event bindings`, and the IDE's `bindings` request gets each with the element (found by the name or hash code the log names it by), its type and the classes it sits in, nearest first.
- **WPF** (Windows only): a listener on `PresentationTraceSources.DataBindingSource` at warnings reads WPF's binding errors (path, target element type and name, target property).
- **The IDE** (`XamlBindingLocator`) finds the declaring attribute: the property's attribute whose `{Binding}` has the path, in the file whose `x:Class` is the nearest of the element's classes first, the element of the failed type and name preferred. Each is an Error List warning, `XAML0100`, source **Binding**, at the path; failures with no XAML found are listed without a location. A new run clears them.

### XAML Hot Reload (the live agents, the shell)

- **Avalonia.** On save (`debug.xamlHotReload`, on by default), the shell resolves the file's project and its app as the previewer does (the app's own designer host, `Avalonia.Designer.HostApp.dll`, which holds the runtime XAML compiler of the app's Avalonia version; the file's `avares://` URI; `AvaloniaUseCompiledBindingsByDefault`) and sends `reload` with the saved text. The agent loads the designer host into the app once and calls its `AvaloniaXamlIlRuntimeCompiler.Load` with a `RuntimeXamlLoaderDocument` whose root instance is each live object of exactly the `x:Class` (the application, windows, and controls in their logical and visual trees), on the UI thread. Before each, the instance's `Styles`, `DataTemplates`, resources and merged dictionaries are emptied (the XAML adds them again) and it gets a new name scope; after, its fields named as registered elements point at the new ones. A failure puts those back and the content as it was, and answers with the loader's line and column.
- **WPF** (Windows only): the agent reads the text with WPF's schema context through the previewer's reader with event handlers kept, and System.Xaml's object writer populates each instance (`RootObjectInstance`, a new external name scope), resources emptied first; named fields are set again from the scope.
- **The shell.** A changed `x:Class` (against the files' classes when the app connected) is refused before anything is sent. **Applied**: the status bar says how many instances changed, or that nothing of the file is on screen (what opens later uses the built XAML). **Failed**: an Error List error, `XAML0101`, source **XAML Hot Reload**, at the line, with a notification whose **Restart** restarts the debug session or the program run without debugging. **No `x:Class`** (a styles or resource dictionary file): the status bar says a restart applies it. A save under the Codalus debugger no longer says that XAML edits need a restart when the live agent takes them.

## Consequences

- Rename and generate-from-use edit C# the language host already has; Extract Resource and Extract Style edit only XAML. Every action goes through the lightbulb's preview.
- The agent loads the app's own designer host into the app for the first reload: the same assembly the previewer runs, matched to the app's Avalonia version, so no Avalonia code ships with the IDE. Code a constructor attached to the replaced elements (event subscriptions in code, values set on named elements) doesn't run again; a restart does.
- Bindings are found only where the app logs them: an Avalonia app whose own log sink isn't Avalonia's reports none, and an app that raises the log level above warnings hides them.
- The WPF parts compile on every OS and run only on Windows: they're validated by Windows CI, not on Linux or macOS.
- Deferred (`TODO(issue)`):
  - Hot Reload of styles and resource dictionaries without an `x:Class` (replacing the live `StyleInclude`/`ResourceInclude`), and of App.axaml's theme without re-adding it.
  - A setting that breaks the debugger at a failed binding, as at a thrown exception.
  - XAML breakpoints (on an element's creation, on an attribute's set, Break When Value Changes in the Live Property Explorer).
  - Binding values drawn after their XAML lines while stopped (M62's renderer): the app's UI thread is stopped then, so the agent can't answer; it needs the debugger's evaluation.
  - The Live Property Explorer's explanation of why a style applies or doesn't.
  - Rename of a custom control's Avalonia property field (`XxxProperty`) and of attached properties together with their CLR accessors.
  - The WPF agent's ancestors for failed bindings (WPF's trace names no element instance).
