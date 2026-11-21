# Windows Forms with a control library's packages (#454)

A Windows Forms app whose form derives from a control library's form, the library delivered as NuGet packages, as a vendor's project template's app is, for the Windows Forms designer ([ADR 0073](../../docs/adr/0073-windows-forms-designer.md#10-control-libraries-from-packages)). The library is a stand-in of our own: no vendor's packages are used.

| Project | What it has |
|---|---|
| `src/Contoso.WinForms.Controls` | Package `Contoso.WinForms.Controls` 1.0.0: `ContosoForm` (the base form, with a `Skin` property), `ContosoButton` and the `ContosoClock` component (Toolbox items), `ContosoTextEdit` (a Toolbox item whose base, `ContosoEditBase`, is `[ToolboxItem(false)]`), `ContosoSpinEdit` and `ContosoDateEdit` (Toolbox items over that base through the library's own `ContosoToolboxItemAttribute`, derived from `ToolboxItemAttribute` as DevExpress's `DXToolboxItem` is, in the library's `[ToolboxTabName]` tab, `ContosoSpinEdit` with its own `[ToolboxBitmap]` picture, `ToolboxIcons/SpinEdit.bmp`), and what the Toolbox doesn't list: `ContosoPopupEdit` and `ContosoInnerEdit` (the library's attribute with its `Hidden` kind, and with `false`), `ContosoEditHelper` (inherits `[ToolboxItem(false)]`), `ContosoHiddenControl` (`[ToolboxItem(false)]`), `ContosoInvisibleComponent` (`[DesignTimeVisible(false)]`), an abstract, an internal and a parameterized control, and `ContosoDialog` (a form). `ContosoForm` and `ContosoButton` name designers in `Contoso.WinForms.Design`, which isn't shipped, as a vendor's controls do when its design-time package isn't referenced. |
| `src/Contoso.WinForms.Grid` | Package `Contoso.WinForms.Grid` 1.0.0, which depends on `Contoso.WinForms.Controls`: the `ContosoGrid` control. |
| `src/Contoso.WinForms.Board` | Package `Contoso.WinForms.Board` 1.0.0 (#461): a control library that ships its own designers for the classic IDE's designer server, in the vendors' layout: `lib/net11.0-windows/Design/WinForms/Server/Contoso.WinForms.Board.Design.Routing.dll` and `…/Server/lib/Contoso.WinForms.Board.Design.dll` (the short framework folder, as DevExpress names it, beside the runtime's `lib/net11.0-windows7.0/`). `ContosoBoard` (a container whose designer gives a dropped board a title and a column, a smart tag with a property and a link, verbs, a dialog that closes itself, a property filter, a design-time Add column button (a glyph while it's selected), and lanes, a tree of objects that aren't components, written as locals as a DevExpress grid's levels are), `ContosoCard` (whose designer name the routing assembly routes, and whose right half takes a click in design mode, opening a menu whose item pins it), `ContosoBoardColumn` (a component with a designer) and `ContosoBrokenPanel` (whose designer throws). |
| `src/Contoso.WinForms.Board.Design`, `src/Contoso.WinForms.Board.Design.Routing` | The board's designers and routes, built against Codalus's replacements of the designer server's API (`designtools/` next to the designer host: `-p:CodalusDesignTools=<folder>`, by default the repository's Debug build). Not packages of their own: `Contoso.WinForms.Board`'s pack takes their output. |
| `WinFormsVendorApp` | References `Contoso.WinForms.Board`: `BoardForm` has a board with a card and a lane in it, and the broken panel. |
| `WinFormsPackageApp` | References only `Contoso.WinForms.Grid` (`Contoso.WinForms.Controls` comes with it). `MainForm` derives from `Contoso.WinForms.ContosoForm` and has a `ContosoButton`; the app's code doesn't use `ContosoGrid`. `WinFormsPackages.slnx` has this project only. |

`nuget.config` restores from `feed/` only, into `.packages/`, so nothing comes from nuget.org and the machine's package cache never holds the stand-in packages. The tests pack the libraries into `feed/` first (`WinFormsDesignerPackageTests.cs` in `tests/Codalus.LanguageServices.Host.Tests`); to open the app by hand, do the same:

```bash
dotnet pack src/Contoso.WinForms.Controls -o feed
dotnet pack src/Contoso.WinForms.Grid -o feed
dotnet build src/Contoso.WinForms.Board.Design.Routing -c Release
dotnet pack src/Contoso.WinForms.Board -o feed
```

Used by `WinFormsDesignerPackageHostTests` (the designer host, Windows), `WinFormsToolboxPackageTests` (the Toolbox through the real build and C# hosts, Windows) and `WinFormsDesignerVendorHostTests` (the board's own designers in the designer host, Windows; #461, ADR 0106).
