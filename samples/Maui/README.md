# .NET MAUI fixture (M20)

A .NET MAUI app and a library of its pages, for devices, workloads, the run path and MAUI XAML editing ([ADR 0064](../../docs/adr/0064-maui-devices-and-mobile-runs.md)).

| Project | What it has | Used by |
|---|---|---|
| `MauiViews` | A plain `net10.0` library with `Microsoft.Maui.Controls` (its packages have `net10.0` assets, so it builds without the workloads): `Views/TodoPage.xaml` (compiled bindings with `x:DataType`, a `DataTemplate` with its own `x:DataType`, attached properties `Grid.Row` and the library's own `controls:Badge.IsHighlighted`, a named `Entry` used in the code-behind, an event handler), `ViewModels/TodoViewModel.cs`, `Models/TodoItem.cs`, `Controls/Badge.cs` (a bindable property and an attached one). | MAUI XAML completion, diagnostics and navigation (the C# host's tests, with no workload installed) |
| `TodoApp` | The MAUI app (`UseMaui`, from `dotnet new maui`, trimmed): `net10.0-android`, plus `net10.0-ios` and `net10.0-maccatalyst` on macOS and `net10.0-windows10.0.19041.0` on Windows (unpackaged, `WindowsPackageType=None`, with a "Windows Machine" launch profile, as the template has it); `App.xaml`, `AppShell.xaml` (the library's page), `Resources/Styles`, and the platform files under `Platforms/` (`Android/MainActivity.cs`, `Windows/App.xaml`, WinUI's). | Project loading (`UsesMaui`, missing workloads), the device picker and `dotnet build -t:Run`; building for Android where the `maui-android` workload and the Android SDK are installed; the Windows target ("Windows Machine", M44) on Windows with `maui-windows` |

The packages are `Microsoft.Maui.Controls` 10.0.110 (MIT), the version the `maui-android` workload of the 10.0.400 SDK band brings.
