# WPF, Windows Forms and WinUI 3

Codalus IDE opens, edits and builds WPF and Windows Forms projects on modern .NET (`net8.0-windows` and later) on Windows, Linux and macOS. They run, debug, test and use Hot Reload on Windows, where WPF XAML also gets a previewer, running WPF apps the Live Visual Tree, and forms the Windows Forms designer. On Linux and macOS, starting one says that it runs only on Windows. [WinUI 3](#winui-3) apps (the Windows App SDK) open and edit everywhere, and build, run and debug on Windows, packaged or not.

| | Windows | Linux and macOS |
|---|---|---|
| Open the project, IntelliSense, WPF XAML editing | ✓ | ✓ |
| Build | ✓ | ✓ |
| Run, debug, Hot Reload | ✓ | Windows only |
| Run their tests (`net*-windows` test projects) | ✓ | Windows only |
| WPF XAML Previewer, Live Visual Tree and Live Property Explorer | ✓ | Windows only |
| Windows Forms designer and Toolbox | ✓ | Windows only (forms open as code) |
| WinUI 3: open the project, IntelliSense, WinUI XAML editing | ✓ | ✓ |
| WinUI 3: build, run, debug, Hot Reload, packaged (MSIX) apps | ✓ | Windows only |

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

## The Windows Forms designer

On Windows, a form's or user control's `.cs` file opens in the **Windows Forms designer**: double-click it in Solution Explorer (it has a form icon) or open it. **View Code** (`F7`) shows its code, and **View Designer** (`Shift+F7`, or the Solution Explorer's and the editor's context menu) shows the designer again. The `.Designer.cs` file itself opens as code. To open forms as code by default, turn **Open Forms in the Designer** off (Settings → Windows Forms, `windowsForms.openInDesigner`).

The designer draws the form with your app's own .NET and Windows Forms, as Visual Studio's does: the first time it builds the project, your own controls (a user control on the form) show as they're built, and the form's resources apply.
- **Select** a control with a click; `Ctrl+click` or `Shift+click` adds it to the selection or takes it out; drag on the form's background (or `Alt`+drag on a container) for a rubber band. `Esc` selects the parent, `Ctrl+A` everything on the form.
- **Move** the selected controls by dragging them; dropped on a panel, a group box, a tab page or a split container's panel, a control moves into it. The arrow keys move them a pixel, `Shift`+arrows resize them.
- **Resize** the selected control with its grab handles (the form from its right and bottom ones).
- **Snap lines** show while you move or resize a control, as in Visual Studio: its edges line up with its neighbours' (blue), its text with theirs (a magenta baseline: a label with its text box), and it keeps their margins and the form's padding (dashed: 12 pixels from the form's edge, 6 between two controls). Hold `Alt` to move freely.
- **Delete**, **Cut** (`Ctrl+X`), **Copy** (`Ctrl+C`), **Paste** (`Ctrl+V`, into the selected container, a little lower and to the right, with new names) and **Duplicate** (`Ctrl+D`). Deleting a container deletes what's inside. Pasted controls keep their items, tool tips and data bindings; their event handlers stay hooked up when you paste into the same form (in another form the handlers aren't there, so they're left out).
- **Undo** (`Ctrl+Z`) and **Redo** (`Ctrl+Y`) are the designer file's own: the designer's changes are edits of `.Designer.cs`, so they undo in the code view too, and the designer loads the form again from the code.
- **Double-click** a control to go to its default event's handler (a button's `Click`, a form's `Load`), written in the form's code file when it doesn't exist yet.
- A docked control shows its dock, and an anchored one (other than top and left) lines to the edges it's anchored to.

**Every change is written as code**, the way Visual Studio's designer writes it, in your file's own style (for .NET, or for .NET Framework with `this.` and full type names): a moved button changes its one `Location` line; a new one adds its field, its creation, its commented section with its properties in Visual Studio's order and its `Controls.Add`. The rest of the file doesn't move, so source control and local history show only what changed. The changes are in the designer file's editor until you save (`Ctrl+S` saves the designer).

### The Format menu and tab order

With the designer active, the **Format** menu (and the surface's context menu) lines up the selected controls against the primary selection (the one with white grab handles), as Visual Studio's does:
- **Align**: Lefts, Centers, Rights, Tops, Middles, Bottoms, and to Grid (8 pixels);
- **Make Same Size**: Width, Height, both, and Size to Grid;
- **Horizontal Spacing** and **Vertical Spacing**: Make Equal, Increase, Decrease, Remove;
- **Center in Form**: Horizontally, Vertically;
- **Order**: **Bring to Front** and **Send to Back**, which change the order of the parent's `Controls.Add` (its z-order).

View → **Tab Order** numbers every control with its `TabIndex` (a group box's controls as `5.0`, `5.1`…). Click the controls in the order the `Tab` key should visit them: each container counts from 0. `Esc` or View → Tab Order again ends it.

### Smart tags

A control or component with designer tasks shows a small arrow at its top right when it's selected (or press `Shift+Alt+F10`). It opens **_Type_ Tasks**, the tasks its Windows Forms designer offers: a list box's **Edit Items…** and **Use Data Bound Items**, a tab control's **Add Tab** and **Remove Tab**, a panel's **Dock in Parent Container**, a menu strip's **Insert Standard Items**. They run in the designer and what they change is written as code. A task that opens one of Windows Forms' own dialogs opens the IDE's editor instead (a collection's editor, or importing an image), or says it isn't available here.

### Menus and tool strips

Select a menu strip, a tool strip, a status strip or a context menu strip to edit it on the form: **Type Here** at its end adds an item. In a menu, type the item's text (`&File`; a `-` makes a separator); in a tool strip or a status strip, choose the item's type (a button, a label, a combo box, a drop-down button…). Items are named as in Visual Studio (`fileToolStripMenuItem`, `toolStripButton1`).
- Select a menu item to open its drop-down, with its own **Type Here**; `Enter` or a click on the selected item edits its text in place.
- `Ctrl`+arrows move the selected item among its siblings; `Delete` deletes it (and its drop-down's items).
- An item's properties are in the Properties window; its items are also in the **Items** collection editor.

The items are written as Visual Studio writes them: their fields, creations and sections, and the strip's `Items.AddRange(new ToolStripItem[] { … })` in their order.

### The component tray

Components that don't show on the form (a timer, a tool tip, a binding source, an image list, an error provider, and the form's menu and tool strips) are in the **component tray** under it, with their icons. Click one to select it; `F2` (or its context menu's **Rename**) renames it in place; `Delete` deletes it; drag one to move it among the others, or **Line Up Icons** to arrange them in rows. The arrangement is kept in the form's `.resx` (Visual Studio's `TrayLocation`), so both IDEs show the same order.

### Collections

Properties that are collections show **(Collection)** and a **…** button in the Properties window (or the smart tag's **Edit Items…** task), which opens their editor:
- **items as text**, one a line: a list box's, a combo box's or a domain up-down's `Items`;
- **members**, added (of a type you choose), removed and moved up and down, each with its properties: a list view's `Columns`, a tab control's `TabPages`, a tool strip's or menu's `Items` and `DropDownItems`, a data grid view's `Columns`;
- a list view's `Items` (by text, written as `ListViewItem` locals), a table layout panel's `ColumnStyles` and `RowStyles` (size type and size), an image list's `Images`.

The code gets Visual Studio's form for each: `Items.AddRange(new object[] { … })`, `Columns.AddRange(new ColumnHeader[] { … })`, `ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F))`, `Images.SetKeyName(0, "save.png")`.

### Localizable forms and images

Set the form's **Localizable** property to `True` to translate it, as in Visual Studio: its localizable properties (texts, sizes, locations, images…) move from the code to the form's `.resx`, and each control's section starts with `resources.ApplyResources(button1, "button1")`. The form's **Language** property (in the Properties window, with the form selected) chooses the language you design: **(Default)**, or a culture (`fr`, `fr-CA`, `de`…, from the list or typed). In a culture, the form shows its translation, and what you change goes to that culture's file (`OrderForm.fr.resx`, created when needed), only where it differs from the default. Renaming a control renames its resource keys in every culture's file; deleting it removes them. Setting **Localizable** back to `False` moves the values back to the code.

An **image** property (`Image`, `BackgroundImage`, a form's `Icon`) has two buttons in the Properties window: one imports an image file into the form's `.resx` (written as `(Image)resources.GetObject("button1.Image")`), the other copies it into the project's `Resources` folder and adds it to `Properties/Resources.resx` (written as `Properties.Resources.save`).

The `.resx` files are saved with the form (`Ctrl+S`), and undo takes back the code and the resources together.

### Data binding

A form with a `BindingSource` (from the Toolbox's **Data** category, its `DataSource` set to one of your classes, such as `typeof(Customer)`) can bind its controls' properties to the class's members: the Properties window's **(DataBindings)** category lists a control's bindable properties (`Text`, `Checked`, `Tag`…), each with a list of the binding sources' members (`customerBindingSource - Name`). The code gets `textBox1.DataBindings.Add(new Binding("Text", customerBindingSource, "Name", true))`; **(none)** removes it. Visual Studio's Data Sources window isn't there: set the binding source's `DataSource` in the Properties window or the code.

**Code the designer can't read** (an `if` or a loop in `InitializeComponent`, a lambda as an event handler, a call to one of the form's own methods, a field that isn't a component) opens as code instead, at that line, and a notification says why, as in Visual Studio. The .NET SDK's `WFO3000` analyzers already flag most of it as errors.

### The Toolbox

View → **Toolbox** (`Ctrl+Alt+X`) lists Windows Forms' controls and components by Visual Studio's categories: **Common Controls**, **Containers**, **Menus & Toolbars**, **Data**, **Components**, **Printing** and **Dialogs**. Your project's own controls and components (and those of the projects it references) come first, under **_Project_ Components**; mark a class `[ToolboxItem(false)]` to leave it out. The search box filters them.

Click an item, then click on the form (it gets its default size) or drag a rectangle (it gets that size); or drag the item onto the form. It's named as in Visual Studio (`button1`, `textBox2`…) and selected; `Esc` or the **Pointer** cancels.

### Properties and events

With the designer active, the **Properties** window (`F4`) shows the selected component's properties, as a classic IDE's property grid:
- by category or alphabetically (its toolbar), values the code sets in bold, the selected property's description at the bottom;
- for several selected components, the properties they share, a value they differ in left blank, and a value set on all of them;
- edited as text (`12, 9` for a point, `Segoe UI, 9pt, style=Bold` for a font, `255, 128, 0` or a name for a color, with a swatch), from a list (enums, `True`/`False`, `Dock`), or with a toggle per flag (`Anchor`'s Top, Bottom, Left and Right);
- with a picker for **Dock** (a box with the five positions) and **Anchor** (a cross with its four sides to toggle);
- **(Name)** renames the component, in the designer file and wherever the form's code uses it;
- **extender properties** that other components give it: a tool tip's **ToolTip on toolTip1** (written as `toolTip1.SetToolTip(button1, "…")`), a help provider's, an error provider's.

**Events** (its toolbar) lists the component's events with their handlers. Type a method's name to hook the event to it; clear it to unhook the event (the method stays). The arrow next to an event goes to its handler, writing `button1_Click` in the code file first when there's none.

### Limits

- Not yet: Visual Studio's Data Sources window (bind through a `BindingSource` instead), complex data binding's designer (a grid's `DataSource` is an ordinary property), the component designer (a class that is a component but not a control opens as code), and a design surface for WPF (its XAML Previewer shows the window, and selects both ways).
- Smart tag tasks and collection items that open one of Windows Forms' own dialogs (a data grid view's **Add Column…**, a binding source's data source wizard) aren't available; set those properties in the Properties window.
- The designer runs your code (your controls' constructors), so it works only in folders you trust.

On Linux and macOS, forms open as code (the status bar says once that the designer is Windows only), View Designer says so, and the code edits, completes and builds as usual.

## WinUI 3

WinUI 3 apps (`<UseWinUI>true</UseWinUI>`, the Windows App SDK's `Microsoft.WindowsAppSDK` package) open on every OS with IntelliSense for their C# and their XAML. Their packages restore everywhere (Codalus IDE sets `EnableWindowsTargeting`, as for WPF), but WinUI's XAML compiler is a Windows program, so they build, run and debug on Windows. On Linux and macOS, Start, Start Without Debugging, Build and Rebuild say so and build nothing, and the code-behind shows errors for `InitializeComponent` and the named elements' fields (the XAML compiler writes them, on Windows).

### Running and debugging unpackaged apps

An unpackaged app (`<WindowsPackageType>None</WindowsPackageType>`) runs on CoreCLR like any .NET program: **Start Without Debugging** builds and starts it, **Start** (`F5`) debugs it with breakpoints, stepping and the debugger windows, and **Start with Hot Reload** runs it under `dotnet watch`, where C# changes apply when you save. It needs the Windows App SDK runtime installed, unless it carries it (`<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`, which needs a runtime identifier: `win-x64`, `win-arm64`…). Help → Check Environment lists the runtime each app's `Microsoft.WindowsAppSDK` version needs, and installs it with winget when you agree.

### Packaged apps and Developer Mode

A packaged app (MSIX, the default when the project has a `Package.appxmanifest` and doesn't set `WindowsPackageType`) can't be started as a plain program: Windows gives it its identity when it starts it. So **Start Without Debugging** and **Start**:
1. check that **Developer Mode** is on, since Windows registers a build's files as an app only then. When it's off, Codalus IDE says why and opens **Settings → System → For developers** for you (Help → Check Environment does too). The other way is to set `WindowsPackageType` to `None` and run it unpackaged;
2. build the app, then register its build's layout (the `AppxManifest.xml` next to its output) with `Add-AppxPackage -Register` in PowerShell, with the Windows App SDK runtime it depends on, from the restored package. The command and its output are in the app's Run channel;
3. start it the way Windows starts packaged apps (the application user model id, `<package family name>!App`), and follow it until it exits (**Stop** ends it), or, with **Start**, attach the debugger to it.

The app stays registered afterwards, as after a deployment from Visual Studio; remove it from Settings → Apps. Breakpoints in code that runs before the debugger attaches (the app's start-up) aren't hit: attach to a running app's later code, or debug it unpackaged. Hot Reload isn't available for packaged apps.

### Editing WinUI XAML

`.xaml` files in a WinUI project get WinUI's XAML, on every OS:
- **Completion** of WinUI's controls (`Microsoft.UI.Xaml.Controls` and the other `Microsoft.UI.Xaml` namespaces, from the default XML namespace) and yours through `using:` prefixes, properties, events, attached properties (`Grid.Row`, `ToolTipService.ToolTip`, your own), values (enums, named colors, `FontWeights`), and WinUI's directives: `x:Load`, `x:Phase`, `x:DefaultBindMode`, `x:DataType`, `x:Uid`, `x:FieldModifier`.
- **Markup extensions:** `x:Bind`, `Binding`, `StaticResource`, `ThemeResource`, `CustomResource`, `TemplateBinding`, `RelativeSource` and `x:Null`, with their arguments (`Mode`, `BindBack`, `UpdateSourceTrigger`…).
- **`x:Bind` paths** start from the page's (or user control's, or window's) own class: its properties, fields, methods (function bindings like `{x:Bind Format(ViewModel.Count)}`) and named elements, private ones included; in a data template, from its `x:DataType`. They're compiled, so a path the class doesn't have is an **error**, as WinUI's XAML compiler reports it. `{Binding}` paths are resolved when the app runs, so they're **warnings**, from `d:DataContext` or a data template's `x:DataType`.
- **Resources:** `StaticResource` and `ThemeResource` keys from the file, `App.xaml`, and the dictionaries they merge (`ms-appx:///Themes/Colors.xaml`, or a relative path). Keys aren't reported missing, since WinUI's own theme resources (`XamlControlsResources`) aren't listed in any file.
- **Live errors:** syntax errors, unknown types and properties, setter properties, invalid enum values, `x:Load` and `x:DefaultBindMode` values, and `x:Bind` paths. WinUI's XAML compiler's errors (`WMC…`) from the last build show in their `.xaml` files.
- **Hover**, **Go to Definition** (into WinUI's types, your classes, the code-behind, resources, event handlers and `x:Bind` paths), `x:Name` rename, formatting, folding and the Document Outline.

`.xaml` files of WPF, .NET MAUI and Avalonia projects keep their own XAML; a MAUI app's `Platforms/Windows/App.xaml` is WinUI's.

### New items

**Add New Item** in a WinUI project offers **Blank Window**, **Blank Page** and **User Control** (the `.xaml` file and its `.xaml.cs` code-behind) and **Resource Dictionary**. File → New Project has no WinUI 3 template: the .NET SDK has none, and there's no maintained open-source template pack yet. Create one in Visual Studio, or start from a [WinUI sample](https://github.com/microsoft/WindowsAppSDK-Samples).

There's no WinUI previewer (Microsoft has none either), and XAML Hot Reload isn't available.

## Not available yet

- XAML Hot Reload (changes made in the Live Property Explorer stay in the running app).
- A WinUI 3 previewer, WinUI templates in New Project, and building WinUI apps on Linux and macOS.
- The WPF previewer and the Live Visual Tree on Linux and macOS (through a Windows machine), and for WPF on .NET Framework.
- The Windows Forms designer on Linux and macOS (through a Windows machine), and its later parts (snap lines, tab order, smart tags, menus and tool strips, localizable forms).
- Running and debugging these apps from Linux and macOS through a Windows machine.
- WPF and Windows Forms on .NET Framework.
