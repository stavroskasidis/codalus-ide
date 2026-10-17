# 0047. XAML language service

- **Status:** accepted (M19a, 2026-10-12; the maintainer's standing go-ahead for the milestones)
- **Date:** 2026-10-12
- **Deciders:** maintainer
- **Amended by:** [ADR 0071](0071-wpf-xaml-language-service.md) (WPF XAML, M40a)

## Context

M19 asks for rich `.axaml` editing: completion of elements, attributes, values, markup extensions, resources, style selectors, `x:Class` and `x:DataType`; binding paths of compiled bindings, which need the view models' types; live parse errors and the XAML compiler's (XamlX, `AVLN…`) errors from builds; Go to Definition, `x:Name` rename, hover and formatting. The roadmap leaves open where it lives (the build host, the markup host or a new host) and suggests reusing AvaloniaVS's or the Avalonia VS Code extension's completion engine.

Everything XAML needs to know is type information: which CLR types an XML namespace holds (`[XmlnsDefinition]` attributes, `using:` and `clr-namespace:`), their properties, Avalonia properties (`XxxProperty` fields), attached properties, events, enums, content properties, pseudo-classes, and the view models' members for binding paths. That information is in the referenced assemblies and, for the app's own controls and view models, in its source.

## Decisions

**It lives in the C# language host (`daedalus-lshost`)**, as Razor does (ADR 0023), and not in the markup host or a new host:
- The C# host already has every project's Roslyn compilation from the design-time builds (ADR 0005): Avalonia's types from metadata, the app's and its libraries' types from **source**. Completion, diagnostics and binding paths are right before the project is built, and follow C# edits as they are typed (a view model's renamed property breaks the binding's diagnostic at once).
- Avalonia's build already makes every `AvaloniaXaml` item an **additional file** of its project (its name generator reads them). The host treats `.axaml` files like `.razor` files: the editor's text replaces the additional document's, so the name generator's `x:Name` fields follow the edits, and `x:Name` rename finds the code-behind's uses of the generated field with Roslyn's `SymbolFinder`.
- The markup host knows XML and MSBuild but has no compilation; giving it one would duplicate the C# host's workspace. A new host would do the same, and cost a process.

The shell registers the language `axaml` ("Avalonia XAML", TextMate's XML grammar) and routes it to the C# service (`Languages = [csharp, razor, axaml]`); the C# features never run on a XAML file (no Miscellaneous File is made for it).

**The engine is our own** (`src/DaedalusStudio.LanguageServices.Host/Xaml`): a tolerant XAML parser (`XamlSyntax`), a type system over a Roslyn compilation (`XamlTypeSystem`, one per compilation), and the features (`XamlCompletion`, `XamlNavigation`, `XamlAnalysis`, `XamlDiagnostics`). AvaloniaVS's completion engine (MIT) was considered: it reads metadata from the built assemblies (with dnlib), so it knows nothing until a build and nothing typed since, and it would bring a second type model next to Roslyn's. No code was copied, so no notice is needed.

**Features.**
- **Completion:** element names (the default namespace's types, each prefix's types, property elements of the parent), attributes (settable properties, events, attached properties `Owner.Property` of the namespaces in scope, `x:`/`d:`/`mc:` directives, minus those already set), values (enums, booleans, named colors for brushes and colors, static instances such as `ThemeVariant.Dark`, types for `x:DataType`/`TargetType`/`DataType`, `x:Class` from the project's partial classes, `xmlns` values, style classes, event handlers from the code-behind), markup extensions and their arguments (`StaticResource`/`DynamicResource` keys from the project's and its references' XAML, binding paths from the `x:DataType` in scope, through indexers, `#Name` and `$parent[Type]`, binding arguments and modes, `ElementName`, `x:Static` members, `TemplateBinding` properties), setters' properties and values from the style's or theme's target type, and selectors (types, `.classes`, `:pseudo-classes` from `[PseudoClasses]`, `#names`, `/template/`). Items carry their documentation; attributes insert `Name="$0"`.
- **Diagnostics** (250 ms after an edit of the file, 600 ms after any other edit): syntax errors (`XAML0001`), undeclared prefixes (`XAML0002`), unknown types (`XAML0003`), unknown members (`XAML0004`), invalid enum values (`XAML0005`), and compiled bindings' paths that the data type doesn't have (`XAML0006`, when `x:CompileBindings`, `CompiledBinding` or the project's `AvaloniaUseCompiledBindingsByDefault`, read from the analyzer config, say they compile). They are the XAML compiler's errors that a parse and the type information can see; the rest comes from builds.
- **The XAML compiler's errors** (`AVLN…`) from the last build are squiggles in their `.axaml` files (the diagnostics source `axaml.build`, `XamlBuildDiagnostics` in the workbench) until the file is edited or the next build; the Error List shows each once.
- **Hover** (types, members with their documentation, resources and where they are defined, directives) and **Go to Definition** (types and members, in source or decompiled like C#'s, resource keys, style classes to their styles, binding path members, `x:Class`, named elements).
- **Rename** of `x:Name`: the name, `#Name` in bindings and selectors, `ElementName=Name`, and the code-behind's uses of the generated field.
- **Formatting** (document and range): elements indented by depth, attributes on their own lines under the first attribute (or one level in), trailing whitespace trimmed; only whitespace changes, nothing when the file doesn't parse. Folding ranges and document symbols (the Document Outline) come from the parse.
- **`daedalus/xaml/logicalTree`** (docs/protocol.md): the controls a file declares, nested as their logical parents (through content property elements, not templates, styles or resources), with the root named by its `x:Class`, for the previewer's selection (ADR 0048).

## Consequences

- XAML is as current as C#: nothing waits for a build, except the XAML compiler's own errors.
- Resources defined in compiled themes (Fluent's) aren't offered or checked: they exist only as compiled XAML in assemblies. Unknown `StaticResource` keys are therefore not reported (`TODO(issue)`: read themes' keys from their `!AvaloniaResources`).
- The diagnostics are a subset of the compiler's: a file can be clean in the editor and still fail to build; the build's errors then show in it.
- Formatting re-indents but doesn't wrap or reorder attributes (`TODO(issue)`: settings for attribute wrapping, as VS's XAML formatter has).
