# Avalonia: XAML, the previewer and the live tools

Codalus IDE edits Avalonia's XAML (`.axaml`) with the same C# service as your code, previews it while you type, and shows a running app's elements and properties.

## Editing XAML

`.axaml` files get the editor's full support, with the types and members of your project and its packages:
- **Completion**: elements, attributes and values; markup extensions; resource keys; style classes and selectors. Compiled binding paths come from the `x:DataType` in scope.
- **Live errors**: unknown types and members, invalid values, undeclared prefixes, unknown binding paths. After a build, the XAML compiler's own errors (`AVLN…`) show too.
- **Hover**, **Go to Definition** (F12, into types, members, resources and styles), and **Format Document**.

### Rename across C# and XAML

**Rename** (`F2`) works both ways, through the usual preview:
- Renaming a C# view model property, command method, type or static member also renames its uses in the solution's XAML: compiled binding paths (under `x:DataType`), `{x:Static}`, `x:Class`, `x:DataType`, `{x:Type}`, element names and attributes, style selectors and event handlers.
- In XAML, rename a binding path, an element or `x:DataType` type, an `x:Class`, a `{x:Static}` member or an event handler: the C# symbol is renamed with all its uses, in C# and in XAML.
- Rename a **resource key** (its `x:Key`, or a `{StaticResource}`/`{DynamicResource}` naming it): every lookup in the project, its libraries and the apps using them changes, and so do `FindResource("…")`-style keys in their C#.
- An `x:Name` renames the name, its `#Name` and `ElementName` uses, and the code-behind's uses of its field.

Bindings without an `x:DataType` (reflection bindings) aren't typed, so their paths aren't renamed.

### XAML code actions

Put the caret in XAML and press `Ctrl+.` (or click the lightbulb):
- **Extract Resource** (on an attribute's value): moves it to a keyed resource in the file's resources, or to `App.axaml`, and looks it up with `{StaticResource}`. A variant also replaces every equal value of the same property in the file. The key comes from the element and property (`DockPanelMargin`); rename it afterwards if you like.
- **Extract Style** (on an element's tag): moves its property attributes into a style with a class selector in the root's `Styles`, and gives the element the class.
- **Generate property**: a binding path that the `x:DataType`'s class doesn't have becomes a property of that class (an `[ObservableProperty]` field when the class is a CommunityToolkit.Mvvm `ObservableObject`).
- **Generate command**: a `Command` binding to a command that doesn't exist becomes a `[RelayCommand]` method when the project uses CommunityToolkit.Mvvm (`SaveCommand` → `Save()`), else an `ICommand` property.
- **Generate converter**: a `{StaticResource …Converter}` that doesn't exist becomes a converter class in `Converters/` and its resource in the file.

## The XAML Previewer

View → Other Windows → XAML Previewer, or `Shift+F7` in an `.axaml` editor, shows the file as it renders (it previews WPF's `.xaml` files too, on Windows: see [WPF](windows-desktop.md#the-wpf-xaml-previewer)):
- It runs your app's own Avalonia, with design-time data (`d:DataContext`, `Design.DataContext`, `Design.PreviewWith`). It updates 150 ms after you stop typing.
- **Zoom**, **render scale** (100–200 %) and **theme** (Default, Light, Dark) are on its toolbar.
- **Click** an element in the preview to select its tag in the XAML. The caret's element is outlined in the preview.
- A XAML error shows with its line above the last good frame. Saving a C# or XAML file of the app rebuilds the preview.

The previewer runs your code, so it works only in folders you trust.

## The Live Visual Tree and Live Property Explorer

Run or debug an Avalonia app (`Ctrl+F5` or `F5`) and open Debug → Windows → **Live Visual Tree** and **Live Property Explorer**.

**Live Visual Tree** shows the app's windows and their elements:
- By default it lists only the elements your XAML declares. **Show Visual Tree** adds the parts that control templates make, shown in gray.
- **Selecting** an element highlights it in the running app and shows its properties.
- **Select Element in the App** (the arrow button): the next click in the app selects the element under it here, without clicking it.
- **Go to XAML** (or double-click, or Enter) opens the window's `.axaml` file with the element's tag selected.

**Live Property Explorer** lists the selected element's Avalonia properties:
- Each value shows where it comes from: **Local** (set on the element, in bold), **Style**, **Style trigger**, **Template**, **Inherited**, **Animation** or **Default**.
- Type a new value and leave the cell to set it in the running app, written as in XAML (`8,4`, `Red`, `#FF336699`, `Center`). It isn't written back to your XAML; change the XAML to keep it.

**Break When Value Changes**, on a property's context menu, stops the debugged app when it changes, and **Styles** says why each style applies or doesn't: see [XAML breakpoints](#xaml-breakpoints) and [Why a style applies](#why-a-style-applies-or-doesnt).

How it works: running or debugging an Avalonia app loads a small agent into it (a .NET startup hook, `DOTNET_STARTUP_HOOKS`). It connects only to this IDE, on this machine. Your app needs no package or code for it. To turn it off, set **Live Visual Tree for Avalonia and WPF apps** off (Settings → Debugging, `debug.liveVisualTree`); XAML Hot Reload and failed bindings, below, go with it. A window without an adorner layer (a custom window without `AdornerDecorator`) shows no highlight, but its tree and properties still work.

## XAML Hot Reload

While an Avalonia app runs from the IDE (`Ctrl+F5` or `F5`), **save** an `.axaml` file and the running app changes: each window or user control of its `x:Class` on screen is loaded again from the saved XAML, in place.
- What XAML doesn't set stays as it was: the data context, values your code set, the window's size and place. The elements the XAML declares are new: named fields (`x:Name`) point at the new ones, and the event handlers it names are your class's methods.
- The status bar says how many instances changed. A window that isn't open yet uses the XAML the app was built with when it opens.
- An edit the running app can't take is an Error List error (source **XAML Hot Reload**) at its line, with **Restart** offered: a changed `x:Class`, a type or event handler the running app doesn't have (code you added since it started), or XAML that doesn't parse. The app keeps the last XAML that applied.
- It uses Avalonia's own runtime XAML compiler, from your app's Avalonia package (the one the previewer runs).
- Styles and resource dictionary files without an `x:Class` (and `StyleInclude`d files) apply after a restart, and code that the constructor ran on the old elements (an event subscribed in code) doesn't run again.

To turn it off, set **XAML Hot Reload** off (Settings → Debugging, `debug.xamlHotReload`).

## Failed bindings

A binding that fails in the running app (a path the data context doesn't have, a value that can't convert) is an Error List warning (source **Binding**, `XAML0100`) at the binding's line in your XAML, found through the element's place in the app. Each is listed once per element; a new run starts a new list.

The agent reads Avalonia's log: it works when your app logs with Avalonia's own sinks (`LogToTrace`, as the templates do, `LogToTextWriter`, `LogToDelegate`) at warnings or below, or doesn't log at all. A log sink of your own class hides them. Compiled bindings (`x:DataType`) catch most mistakes at build time; these rows catch the rest.

To stop the debugger at a failed binding instead, as at a thrown exception, turn on **Break when a binding fails** (Settings → Debugging, `debug.breakOnFailedBinding`, off by default): see [XAML breakpoints](#xaml-breakpoints) below.

## XAML breakpoints

While you debug an Avalonia app (`F5`), a breakpoint in an `.axaml` file's margin stops the app at its XAML, as a breakpoint stops C# at its line:
- **On an element's start tag** (the line where `<Button` begins): it stops when the element is **created**, before XAML sets its properties. An element of a data template's line stops for each item.
- **On an attribute's line** (an attribute on a line of its own, `Text="{Binding Count}"`, or a property element such as `<ListBox.ItemTemplate>`): it stops when XAML **sets that property** on the element; for a binding or a dynamic resource, at each value it sets afterwards too.
- **The stop** shows the XAML line as the current statement, with a frame for it on top of the Call Stack and your app's frames under it (Avalonia's, the window's `InitializeComponent`, your code). Its Locals are `element`, the element, and `stop`, what stopped. Continue, step or stop as at any breakpoint.
- **Break When Value Changes**: right-click a property in the **Live Property Explorer**. The app stops when that property of that element changes, wherever from, at the code that changed it (a red dot marks the property while it's on).
- **Break when a binding fails** (`debug.breakOnFailedBinding`): the app stops at each new failed binding, at the binding's XAML, with the exception helper saying **Binding Failed** and why; its check box turns the setting off.

What they need, and what they don't do:
- A **Debug build** on **Avalonia 12** or later: its XAML compiler gives each element its file and line at run time (`AvaloniaXamlCreateSourceInfo`, on in Debug builds). Without it, the breakpoints are hollow and their tooltip says why.
- The live agent (`debug.liveVisualTree`, on by default) and the Codalus debugger (the default `debug.adapter`).
- A breakpoint on an event (`Click=`), a directive (`x:Key`) or a design-time attribute can't stop: nothing sets a property there. An object that isn't an Avalonia object (a `Setter`) doesn't stop either.
- Conditions, hit counts, filters, logpoints and dependencies aren't applied to XAML breakpoints: they stop at every hit.
- XAML Hot Reload keeps the breakpoints working on the elements it reloads.

## Why a style applies, or doesn't

The **Live Property Explorer**'s **Styles**, under the properties, lists for the selected element its control theme, the theme's styles for it, and each style of its scope that targets its type (its own, its window's, the application's and the files they include):
- ✓ **Applies**, ◌ **Inactive now** (its type matches, a class or pseudo-class doesn't, as `:pointerover` while the pointer is away), ✗ **Doesn't apply**.
- For one that doesn't, **the part of its selector that fails and why**: `'.task': it has no class task (its classes: title)`, `'StackPanel >': its parent is a DockPanel`, `'#Save': its name is IncrementButton`.
- Its **setters**, and for one that applies, those a local value or an animation overrides: `FontSize = 20 (overridden: Local)`.
- **Go to** its XAML (a Debug build's styles know their line).

Whether a style applies is Avalonia's own answer; the failing part is read from the selector against the element as it is now (`:nth-child()` isn't checked).

Not yet: binding values drawn after XAML lines while stopped.

## New Avalonia projects

File → New → Project lists the Avalonia templates once they're installed. Until then the list offers **Install the Avalonia templates…**, which runs `dotnet new install Avalonia.Templates` (from nuget.org).
