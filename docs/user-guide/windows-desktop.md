# WPF and Windows Forms

Codalus IDE opens, edits and builds WPF and Windows Forms projects on modern .NET (`net8.0-windows` and later) on Windows, Linux and macOS. They run, debug, test and use Hot Reload on Windows, where WPF XAML also gets a previewer and running WPF apps the Live Visual Tree. On Linux and macOS, starting one says that it runs only on Windows.

| | Windows | Linux and macOS |
|---|---|---|
| Open the project, IntelliSense, WPF XAML editing | ✓ | ✓ |
| Build | ✓ | ✓ |
| Run, debug, Hot Reload | ✓ | Windows only |
| Run their tests (`net*-windows` test projects) | ✓ | Windows only |
| WPF XAML Previewer, Live Visual Tree and Live Property Explorer | ✓ | Windows only |

## Opening and building on Linux and macOS

The .NET SDK refuses to restore or build a project that targets Windows on another OS (error NETSDK1100), unless the `EnableWindowsTargeting` property is `true`. Codalus IDE sets it for its own restores, design-time builds and builds there, so these projects load with IntelliSense and build without any change to them. The SDK downloads the Windows Desktop reference assemblies from NuGet the first time.

A project, or an `EnableWindowsTargeting` environment variable you set yourself, keeps its own value. On the command line, outside the IDE, pass `-p:EnableWindowsTargeting=true` to `dotnet build`.

## Running, debugging and testing on Windows

WPF and Windows Forms apps on modern .NET run on CoreCLR, so they take the same paths as other .NET apps (see [Building and running](build-and-run.md) and [Debugging](debugging.md)):
- **Start Without Debugging** (`Ctrl+F5`) builds the app and starts it; its window opens on the desktop.
- **Start** (`F5`) debugs it with breakpoints, stepping, locals and the other debugger windows.
- **Hot Reload** runs it under `dotnet watch`: C# changes apply when you save (or **Apply Code Changes**, `Alt+F10`). XAML Hot Reload isn't available.
- **Tests** in `net*-windows` test projects (xUnit, NUnit, MSTest) show in the Test Explorer and run there.

On Linux and macOS, Start, Start Without Debugging and the Performance Profiler say that the app runs only on Windows, and build nothing. The Test Explorer shows these projects' tests from their source and says on the project that they run only on Windows.

## New projects and items

File → New Project lists **WPF Application**, **WPF Class Library**, **WPF User Control Library**, **WPF Custom Control Library**, **Windows Forms App**, **Windows Forms Class Library** and **Windows Forms Control Library**. On Linux and macOS they come last, marked "Windows only".

**Add New Item** (`Ctrl+Shift+A`) offers, in a WPF project:
- **Window**, **Page** and **User Control**: the `.xaml` file and its `.xaml.cs` code-behind;
- **Resource Dictionary**: a `.xaml` file of resources to merge.

In a Windows Forms project:
- **Form** and **User Control**: the `.cs` file and its `.Designer.cs`.

Solution Explorer nests a window's `.xaml.cs` under its `.xaml`, and a form's `.Designer.cs` and `.resx` under its `.cs`. A strongly typed resource class (`Resources.Designer.cs`) nests under its `.resx`.

## Editing WPF XAML

`.xaml` files in a WPF project get the same editing as [Avalonia's XAML](avalonia.md#editing-xaml), with WPF's conventions:
- **Completion** of elements (WPF's controls through their XML namespace, and yours through `clr-namespace:` prefixes), properties, events and dependency properties. It also completes attached properties (`Grid.Row`, `ToolTipService.ShowDuration`, and your own: a `DependencyProperty` field with static `GetX`/`SetX` methods) and attached events.
- **Values:** enums, `True`/`False`, named colors, and WPF's well-known values (`FontWeights`, `Cursors`…).
- **Markup extensions:** `Binding`, `StaticResource`, `DynamicResource`, `TemplateBinding`, `RelativeSource`, `x:Static`, `x:Type`, `x:Null` and `x:Reference`, with their arguments (binding modes, `UpdateSourceTrigger`, `RelativeSource` modes and ancestor types).
- **Styles and templates:** a setter's or trigger's `Property` and `Value` from the style's or template's `TargetType`, or from the element its `TargetName` names; `TemplateBinding` from the template's target type.
- **Resources:** `StaticResource` and `DynamicResource` keys from the file, `App.xaml`, and the dictionaries they merge (`<ResourceDictionary Source="Themes/Colors.xaml"/>`, also as `/Assembly;component/…` pack URIs of the solution's projects).
- **Binding paths** from the data context the editor can see:
  - `d:DataContext="{d:DesignInstance Type=vm:MainViewModel}"`;
  - a `<Window.DataContext>` element;
  - a `DataContext="{Binding …}"`, followed from the data context above it;
  - a data template's `DataType`;
  - the source a binding names (`ElementName`, `RelativeSource Self`, `TemplatedParent` or `AncestorType`).

  Without one of these (a data context set in code), paths aren't completed or checked.
- **Live errors:** syntax errors, unknown types, properties and setter properties, and invalid enum values. WPF resolves bindings and resources when the XAML loads, not when it builds, so a binding path the data context lacks, and a `StaticResource` key that the file, `App.xaml` and their merged dictionaries don't define, are **warnings**. Resource keys aren't checked in a class library (its resources come from the app that uses it), nor when a merged dictionary is in another assembly.
- **The markup compiler's errors** (`MC…`) from the last build show in their `.xaml` files at their lines, until the file changes or the next build.
- **Hover** and **Go to Definition** into WPF's types, your view models, controls and resources, the event handlers and the code-behind (`x:Class`); the markup compiler's generated `obj/*.g.cs` is left out.
- **Rename** an `x:Name`: the name, `ElementName=` references, and its uses in the code-behind.
- **Formatting**, folding and the Document Outline.

## The WPF XAML Previewer

On Windows, View → Other Windows → **XAML Previewer**, or `Shift+F7` in a WPF `.xaml` editor, shows the file as WPF draws it, next to the editor. It's the same tool window as [Avalonia's](avalonia.md#the-xaml-previewer):
- It runs your app's own .NET and WPF: the first preview builds the app, and saving a C# or XAML file of the app rebuilds it. App.xaml's resources and the dictionaries it merges apply, and your own controls (a user control the window uses) show as they're built.
- It follows the editor: the preview updates 150 ms after you stop typing. A XAML error shows with its line above the last good preview.
- **Design-time data:** `d:DataContext="{d:DesignInstance Type=vm:MainViewModel, IsDesignTimeCreatable=True}"` on the root creates your view model for the preview, so bindings show its data. Without `IsDesignTimeCreatable=True` the bindings show nothing, as in Visual Studio. `d:DesignWidth` and `d:DesignHeight` set the size; without them, a window's `Width` and `Height`, else the content's size.
- **Zoom** and **render scale** (100–200 %) are on its toolbar. WPF has no theme variants, so the theme picker hides.
- **Click** an element in the preview to select its tag in the XAML; a click on a button's text, or on an item of a list, selects the button or the list. The caret's element is outlined in the preview.

What the preview shows is your XAML, not your compiled class: the code-behind's constructor and event handlers don't run (`Click="…"` and `EventSetter`s are left out, as designers do). A window or page shows its content at its size, without the window's frame. A resource dictionary or `App.xaml` shows a short description; their resources show in the previews that use them. Pointer hover isn't shown.

The previewer needs an app on .NET 8 or later. A WPF class library's XAML previews through a WPF app in the solution that references it (the startup project first). It runs your code, so it works only in folders you trust.

On Linux and macOS the XAML Previewer says that the WPF previewer runs only on Windows, and builds and starts nothing; WPF XAML still edits with completion, errors and navigation.

## The Live Visual Tree for WPF apps

On Windows, run or debug a WPF app (`Ctrl+F5` or `F5`) and open Debug → Windows → **Live Visual Tree** and **Live Property Explorer**. They work as for [Avalonia apps](avalonia.md#the-live-visual-tree-and-live-property-explorer):
- **Live Visual Tree** shows the app's windows and the elements your XAML declares (WPF's logical tree); **Show Visual Tree** adds the parts that control and data templates make, in gray. Selecting an element highlights it in the app. **Select Element in the App** makes your next click in the app select the element under it, without clicking it. **Go to XAML** opens the window's `.xaml` file with the element's tag selected.
- **Live Property Explorer** lists the selected element's dependency properties, and the attached properties set on it (`Grid.Row`, `DockPanel.Dock`), with where each value comes from: **Local** (set on the element, or bound, in bold), **Style**, **Style trigger**, **Implicit style**, **Theme style**, **Template**, **Template trigger**, **Inherited** or **Default**. Type a new value to set it in the running app, written as in XAML (`8,4`, `Red`, `Auto`, `Center`). It isn't written back to your XAML.

Running or debugging a WPF app loads a small agent into it (a .NET startup hook). It connects only to this IDE, on this machine, and your app needs no package or code for it. It needs .NET 8 or later. To turn it off, set **Live Visual Tree for Avalonia and WPF apps** off (Settings → Debugging, `debug.liveVisualTree`). While the app is paused in the debugger, the tree can't refresh.

## Not available yet

- XAML Hot Reload (changes made in the Live Property Explorer stay in the running app).
- The WPF previewer and the Live Visual Tree on Linux and macOS (through a Windows machine), and for WPF on .NET Framework.
- The Windows Forms designer: forms open as code (planned).
- Running and debugging these apps from Linux and macOS through a Windows machine.
- WPF and Windows Forms on .NET Framework.
