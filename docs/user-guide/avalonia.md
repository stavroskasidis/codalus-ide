# Avalonia: XAML, the previewer and the live tools

Codalus IDE edits Avalonia's XAML (`.axaml`) with the same C# service as your code, previews it while you type, and shows a running app's elements and properties.

## Editing XAML

`.axaml` files get the editor's full support, with the types and members of your project and its packages:
- **Completion**: elements, attributes and values; markup extensions; resource keys; style classes and selectors. Compiled binding paths come from the `x:DataType` in scope.
- **Live errors**: unknown types and members, invalid values, undeclared prefixes, unknown binding paths. After a build, the XAML compiler's own errors (`AVLN…`) show too.
- **Hover**, **Go to Definition** (F12, into types, members, resources and styles), **Rename** of an `x:Name` (into the code-behind too), and **Format Document**.

## The XAML Previewer

View → Other Windows → XAML Previewer, or `Shift+F7` in an `.axaml` editor, shows the file as it renders:
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

How it works: running or debugging an Avalonia app loads a small agent into it (a .NET startup hook, `DOTNET_STARTUP_HOOKS`). It connects only to this IDE, on this machine. Your app needs no package or code for it. To turn it off, set **Live Visual Tree for Avalonia apps** off (Settings → Debugging, `debug.liveVisualTree`). A window without an adorner layer (a custom window without `AdornerDecorator`) shows no highlight, but its tree and properties still work.

## New Avalonia projects

File → New → Project lists the Avalonia templates once they're installed. Until then the list offers **Install the Avalonia templates…**, which runs `dotnet new install Avalonia.Templates` (from nuget.org).
