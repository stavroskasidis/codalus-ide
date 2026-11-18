# Windows Forms with a control library's packages (#454)

A Windows Forms app whose form derives from a control library's form, the library delivered as NuGet packages, as a vendor's project template's app is, for the Windows Forms designer ([ADR 0073](../../docs/adr/0073-windows-forms-designer.md#10-control-libraries-from-packages)). The library is a stand-in of our own: no vendor's packages are used.

| Project | What it has |
|---|---|
| `src/Contoso.WinForms.Controls` | Package `Contoso.WinForms.Controls` 1.0.0: `ContosoForm` (the base form, with a `Skin` property), `ContosoButton` and the `ContosoClock` component (Toolbox items), `ContosoTextEdit` (a Toolbox item whose base, `ContosoEditBase`, is `[ToolboxItem(false)]`), `ContosoSpinEdit` and `ContosoDateEdit` (Toolbox items over that base through the library's own `ContosoToolboxItemAttribute`, derived from `ToolboxItemAttribute` as DevExpress's `DXToolboxItem` is, in the library's `[ToolboxTabName]` tab, `ContosoSpinEdit` with its own `[ToolboxBitmap]` picture, `ToolboxIcons/SpinEdit.bmp`), and what the Toolbox doesn't list: `ContosoPopupEdit` and `ContosoInnerEdit` (the library's attribute with its `Hidden` kind, and with `false`), `ContosoEditHelper` (inherits `[ToolboxItem(false)]`), `ContosoHiddenControl` (`[ToolboxItem(false)]`), `ContosoInvisibleComponent` (`[DesignTimeVisible(false)]`), an abstract, an internal and a parameterized control, and `ContosoDialog` (a form). `ContosoForm` and `ContosoButton` name designers in `Contoso.WinForms.Design`, which isn't shipped, as a vendor's controls do when its design-time package isn't referenced. |
| `src/Contoso.WinForms.Grid` | Package `Contoso.WinForms.Grid` 1.0.0, which depends on `Contoso.WinForms.Controls`: the `ContosoGrid` control. |
| `WinFormsPackageApp` | References only `Contoso.WinForms.Grid` (`Contoso.WinForms.Controls` comes with it). `MainForm` derives from `Contoso.WinForms.ContosoForm` and has a `ContosoButton`; the app's code doesn't use `ContosoGrid`. `WinFormsPackages.slnx` has this project only. |

`nuget.config` restores from `feed/` only, into `.packages/`, so nothing comes from nuget.org and the machine's package cache never holds the stand-in packages. The tests pack the libraries into `feed/` first (`WinFormsDesignerPackageTests.cs` in `tests/Codalus.LanguageServices.Host.Tests`); to open the app by hand, do the same:

```bash
dotnet pack src/Contoso.WinForms.Controls -o feed
dotnet pack src/Contoso.WinForms.Grid -o feed
```

Used by `WinFormsDesignerPackageHostTests` (the designer host, Windows) and `WinFormsToolboxPackageTests` (the Toolbox through the real build and C# hosts, Windows).
