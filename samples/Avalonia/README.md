# Avalonia fixture (M19)

An Avalonia 12 MVVM app and a control library for XAML editing and the live previewer ([ADR 0046](../../docs/adr/0046-xaml-language-service.md), [ADR 0047](../../docs/adr/0047-avalonia-previewer-hosting.md)).

| Project | What it has | Used by |
|---|---|---|
| `AvaloniaApp` | `Views/MainWindow.axaml` (compiled bindings with `x:DataType`, `d:DataContext` from `ViewModels/DesignData.cs`, `d:DesignWidth`/`d:DesignHeight`, named elements used in the code-behind, a `StaticResource`, a control from the library, a `DataTemplate` with its own `x:DataType`), `Views/TodoItemView.axaml` (a `UserControl` with `Design.DataContext`), `Styles/AppStyles.axaml` (style classes, a template selector, `Design.PreviewWith`), `App.axaml` (resources, merged dictionaries, `FluentTheme`). | XAML completion, diagnostics, navigation and rename; the previewer; the smoke test |
| `AvaloniaControls` | `RatingControl` (a templated control with styled and direct properties) and its `ControlTheme` in `Themes/Generic.axaml`. | Types from another project; previewing a library's XAML through the app |

The packages are Avalonia's (MIT), pinned to the version the shell uses so they come from the local NuGet cache.
