# WinForms and WPF fixture (M40)

A WPF app, a Windows Forms app and a test project for each, on `net11.0-windows`, for loading, running, debugging and testing Windows desktop projects and for WPF XAML editing ([ADR 0069](../../docs/adr/0069-wpf-xaml-language-service.md)). They build on every OS with `EnableWindowsTargeting=true`, which Codalus's build host sets on Linux and macOS; they run on Windows only.

| Project | What it has | Used by |
|---|---|---|
| `WpfApp` | An MVVM app: `App.xaml` merging `Themes/Colors.xaml` and `Themes/Styles.xaml` (brushes, a button style with setters, a control template with `TemplateBinding` and a `TargetName` trigger, style triggers), `MainWindow.xaml` (`d:DataContext` with `d:DesignInstance`, bindings with modes, `UpdateSourceTrigger` and `StringFormat`, `ElementName`, `StaticResource` and `DynamicResource`, a typed `DataTemplate`, attached properties `Grid.Row`, `DockPanel.Dock`, `ToolTipService.ShowDuration` and the app's own `controls:Highlight.IsOn`, a named `TextBox` used in the code-behind, an event handler), `Controls/RatingControl.xaml` (a user control with two dependency properties, `ElementName` and `RelativeSource AncestorType` bindings), `ViewModels/MainViewModel.cs`, `Models/TodoItem.cs` | WPF XAML completion, diagnostics and navigation (the C# host's tests, on every OS); loading and builds; run, debug and the smoke step `windows.desktop` on Windows |
| `WpfApp.Tests` | xUnit v3 (Microsoft.Testing.Platform) tests of the view model | Test runs on Windows; "Windows only" elsewhere |
| `WinFormsApp` | A form (`MainForm.cs`, `.Designer.cs`, `.resx`), a user control (`Controls/CounterControl.cs`, `.Designer.cs`, `.resx`), strongly typed resources (`Properties/Resources.resx` and `Resources.Designer.cs`), and `Counter.cs`, the logic the tests use | Loading, nesting in Solution Explorer, builds; running on Windows |
| `WinFormsApp.Tests` | MSTest (`MSTest.Sdk`) tests of `Counter` | Test runs on Windows |

`WindowsDesktop.slnx` has all four; `WpfApp.slnx` has the WPF app alone, for the C# host's tests.
