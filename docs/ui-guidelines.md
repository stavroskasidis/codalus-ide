# UI guidelines

How Codalus IDE's surfaces look and behave: the checklist every panel, window and dialog meets (D25), the tokens views use, and the icon rules (D24). It is for the shell's own views and for plugin authors whose views and contributions sit next to them. M34 (UI polish) wrote it down; M34a and M34d applied it.

## The checklist

Review a surface against each line, in every theme family (Harbor, Graphite, Daylight and Ember, M45) and Classic, in Light, Dark and High contrast, at its narrowest and widest, before calling it done. Every milestone's definition of done includes this review of its own panels ([roadmap](roadmap.md#m34--ui-polish)).

- **Layout**
  - Spacing comes from the 4 px grid (`Shell.Space.*`, `Shell.Padding.*`); no one-off margins.
  - Lists and trees use rows of `Shell.Size.Row` (22 px) with `Shell.Padding.Row`.
  - Tables keep their column widths: in a narrow pane the header and the rows scroll sideways together (a `ScrollViewer` with horizontal scrolling around a `DockPanel` with a `MinWidth`), never squeezed into each other.
  - The in-window dialog sizes to its content, except a preview of changes: three quarters of the window, at least 640 × 400 where the window has room (`DialogView.PreviewSize`, M34h), so the diff is readable.
  - Code shown outside an editor (a template's preview, a definition) is a `CodeViewer`, in the editor's colors, never plain text in a text box.
- **Toolbars**
  - A tool window's toolbar is one `controls:ToolbarPanel`: one row, 24 px buttons (`Button.toolbar`, `Shell.Size.ToolbarButton`), 16 px icons, inputs of `Shell.Size.Input`. It never wraps: what doesn't fit goes into its overflow menu (»), buttons by their `AutomationProperties.Name`, toggle buttons as checkable entries, combo boxes as submenus of their choices.
  - A search or filter box takes the rest of the row, or a row of its own under the toolbar when the toolbar is long (the Solution Explorer, the Test Explorer).
  - Every icon-only button has a tooltip with its name and, when it has one, its shortcut (the menus' format: `Run All Tests (Ctrl+R, A)`), and an `AutomationProperties.Name`.
  - Group with `Border.toolbarSeparator`; labels between toolbar items are tooltips instead (labels can't overflow).
- **Typography**
  - UI text is 9 pt: `Shell.FontSize.Body` (12 px), the window's default; don't set it again.
  - Secondary text (details, counts, dates) is `Shell.FontSize.Small` (11 px), usually with `TextBlock.muted`.
  - Two heading sizes: `TextBlock.heading` (`Shell.FontSize.Title`, 18 px, page and dialog titles) and `TextBlock.subheading` (`Shell.FontSize.Heading`, 14 px, sections and detail panes).
  - Titles are sentence case except command and window names, which are title case as in the menus. Use the product's terms: solution, project, tool window, document.
- **Text that doesn't fit**
  - Nothing is cut off without an ellipsis: single-line text in a constrained space has `TextTrimming="CharacterEllipsis"` and a tooltip with the full text (on the text or its row).
  - Messages wrap (`TextWrapping="Wrap"`).
  - `PolishTests` walks every tool window at 280 px (`Shell.Size.ToolWindowMinWidth`), the documents that work as dialogs at 640 px and the in-window dialog's forms at 440 px. It fails on text cut off without an ellipsis or a tooltip.
  - Inputs keep room to type: a text box or combo box squeezed under 48 px by the columns around it fails `PolishTests` (M34g).
  - Text never runs under other text: a text in a horizontal stack panel isn't constrained by its cell, so put a row's texts in a `DockPanel` or `Grid` where they trim. `PolishTests` fails on text drawn over text (M34g, found in the quick pick's rows). Rows of a form that show different controls share their fixed columns (`Grid.IsSharedSizeScope`, `SharedSizeGroup`), so the values line up.
  - Columns: give the main column `*` and the others proportional (`2*,1.5*`) or modest fixed widths. Fixed columns that add up to most of 640 px squeeze the main one to nothing (the Keyboard Shortcuts header and Add New Item's list did, until M34's final pass).
- **States**
  - Every surface has an empty state, a loading state and an error state, each with a next step: what to do (`Open Solution…`, `Press F9 in an editor to add one`), not only what is missing.
  - Empty and error messages use `TextBlock.emptyState` (muted, wrapped, `Shell.Padding.EmptyState` around).
  - Loading shows progress where it happens (an indeterminate bar under the toolbar, a status line), not a modal.
- **Keyboard**
  - Everything is reachable with the keyboard in reading order; focus is visible (`Shell.Focus`).
  - Shortcuts show in menus and tooltips, per OS (`docs/user-guide/keyboard.md`).
  - Lists and trees have context menus where users expect them (`MenuBuilder.Context`, menu locations in `BuiltIns.Menus.cs`).
- **Themes**
  - Colors come from tokens only (`{DynamicResource Shell.*}`), never literals, so every theme switches live: the four families' twelve variants and Classic's Blue, Light, Dark and High Contrast.
  - Shapes that differ per family come from tokens too (`Shell.Gutter`, `Shell.Panel.CornerRadius.*`, below), applied by `ShellStyles.axaml`: a view never checks which family is in use.
  - Text drawn on a selection uses `Shell.Selection.Foreground`, on an accent fill `Shell.AccentForeground`: in a family's High contrast variant both fills are the accent, with black text.
  - Text meets WCAG AA (4.5:1) on what it is drawn on, and 7:1 in the families' High contrast variants; icons, focus rings and other graphics 3:1. `tests/Codalus.Shell.Tests/TestData/contrast-pairs.txt` lists every pair; `PolishTests` checks it in all sixteen themes, and since M34g also renders the views and checks each text against the background under it.
  - Error and warning text uses `TextBlock.error` and `TextBlock.warning` (`Shell.Error.Foreground`, `Shell.Warning.Foreground`), never `Shell.Error` or `Shell.Warning`: those are graphics tokens, held to 3:1 only. Likewise the added and removed lines of a diff use `Shell.Diff.Added.Foreground` and `Shell.Diff.Removed.Foreground`, not `Shell.Diff.Added` and `.Removed`.
  - Secondary text on a selected row needs nothing: `ShellStyles.axaml` gives a selected list row's `TextBlock.muted` and `Run.muted` the selection's text, and a selected tree row's header the tree's selected text. A muted run after a name is `<Run Classes="muted" …/>`, never a run with `Foreground="{DynamicResource Shell.MutedForeground}"`, which stays gray on the selection. `HighlightedText`'s matched characters take the selection's text on a selected row too.
  - In High Contrast, color never carries meaning alone: icons are monochrome (shape carries it), and states also have a shape or a word.
- **Operating systems and scales:** check Windows, Linux and macOS (Cmd shortcuts, the global menu) at 100 % to 200 %.
- **Accessibility:** every control has a name (`AutomationProperties.Name`), live regions announce status (`AutomationProperties.LiveSetting`), and rows have an accessible name that reads like a sentence.
  - A tooltip isn't a name: an icon-only button needs `AutomationProperties.Name` too.
  - `PolishTests` fails on a visible button, toggle, combo box, text box, slider, list or tree without a name, its text, or a placeholder, in every tool window, document and dialog form.
  - `UxPassTests` presses Tab through the newer views (M34g) and fails on a control it never reaches.

## Tokens

All tokens are in `src/Codalus.Shell/Theming/Tokens.axaml`. Colors and the families' shapes differ per theme; spacing, sizes and type are the same in every theme. The families' colors are the design's ([`design/shell-themes.md`](design/shell-themes.md)); a family variant sets every token itself and inherits only Fluent's colors of its kind from Classic ([ADR 0077](adr/0077-theme-families.md)).

### Colors

| Token | Use |
|---|---|
| `Shell.Background`, `Shell.Foreground` | The window, and its text |
| `Shell.Panel` | Tool window and document surfaces |
| `Shell.MutedForeground` | Secondary text (4.5:1 on every surface) |
| `Shell.Border` | Borders and separators |
| `Shell.Hover`, `Shell.Selection`, `Shell.Selection.Foreground` | Pointer over, selected rows, and the text on them |
| `Shell.Input` | Text boxes and combo boxes (in the families; Classic keeps Fluent's) |
| `Shell.Accent`, `Shell.AccentForeground` | Accent graphics; text on an accent fill |
| `Shell.Focus` | Focus rings and outlines |
| `Shell.Overlay`, `Shell.Overlay.Border` | Popups, the command palette, toasts |
| `Shell.Link`, `Shell.Match` | Links; matched characters in pickers |
| `Shell.Info`, `Shell.Warning`, `Shell.Error`, `Shell.Modified` | States (icons and borders) |
| `Shell.Error.Foreground`, `Shell.Warning.Foreground` | Error and warning text (4.5:1 on every surface, 7:1 in the families' High contrast; M34g), through `TextBlock.error` and `.warning` |
| `Shell.Diff.Added`, `Shell.Diff.Removed` | Diffs, and passed tests (graphics: gutters, icons) |
| `Shell.Diff.Added.Foreground`, `Shell.Diff.Removed.Foreground` | Added and removed lines as text (a preview of changes; 4.5:1 on every surface, 7:1 in the families' High contrast; M34g) |
| `Shell.Toolbar`, `Shell.Toolbar.Border`, `Shell.TitleBar*`, `Shell.Search*` | The title row and the standard toolbar: `Shell.TitleBar.Hover` for the menu bar's items, `.Chip` behind the solution's name, `Shell.Search.Foreground` for the search box's text |
| `Shell.Toolbar.Start`, `.Foreground`, `.Icon` | The Start button (filled with the accent in Daylight) |
| `Shell.StatusBar*` | The status bar: no solution (`.Empty`), a solution, debugging (`.Debugging`), hover; each state with its `.Foreground` and `.Hover` |
| `Shell.Tab.Active`, `.Selected`, `.Inactive`, `.Hover`, `.Preview` (each with `.Foreground`) | Document tabs: the active tab, the selected tab of a pane without the focus, other tabs, pointer over, the preview tab |
| `Shell.TabColor.1`–`8` | Tabs colored by project (`workbench.editor.colorTabsByProject`) |
| `Shell.Caption.Active`, `Shell.Caption.Inactive` (with `.Foreground`) | Tool window captions with and without the focus |
| `Shell.Editor.*` | The editor: text, line numbers, inlay hints, guides, brackets, folding; the current line, the breakpoint glyph and line, and the current statement's line (`.CurrentLine`, `.Breakpoint`, `.Breakpoint.Line`, `.CurrentStatement`) |
| `Shell.Icon.Run`, `.Stop`, `.Save`, `.Open`, `.Build`, `.Git`, `.Test`, `.Navigate` | Command icon families (white in High Contrast) |
| `Shell.Icon.Outline` | The gray of the classic file icons' pages and outlines |

### Shapes (M45)

The design's layout differences between the families, as values each theme sets ([ADR 0077](adr/0077-theme-families.md)):

| Token | Harbor | Graphite | Daylight | Ember | Classic |
|---|---|---|---|---|---|
| `Shell.Gutter`, `Shell.Gutter.Background` (between docked windows) | 6, window | 1, border | 1, border | 8, window | 4, none |
| `Shell.Panel.CornerRadius.Top`, `.Bottom` (tool windows, the document well) | 0 | 0 | 0 | 10 | 0 |
| `DockDocumentTabItemCornerRadius` (Dock's, document tabs) | 0 | 0 | 6 | 7 | 0 |
| `Shell.Tab.Active.Indicator`, `.IndicatorBrush` (the active tab's border) | 3 on top, accent | 2 below, accent | none | 1 around, accent | none |
| `Shell.Caption.Active.FontWeight`, `.Marker` (the focused tool window's title) | normal (an accent caption) | bold, an accent dot | bold | bold | normal |
| `Shell.StatusBar.ShowDot` (a dot for the state) | no | no | no | yes | no |

### Spacing, sizes and type

| Token | Value | Use |
|---|---|---|
| `Shell.Space.XS`, `S`, `M`, `L`, `XL` | 2, 4, 8, 12, 16 | Spacing between items |
| `Shell.Padding.Toolbar` | 4,2 | Around a toolbar row |
| `Shell.Padding.ToolbarButton` | 4,3 | Inside toolbar buttons |
| `Shell.Padding.Panel` | 8,6 | Around a panel's content |
| `Shell.Padding.EmptyState` | 12 | Around empty and error messages |
| `Shell.Padding.Page` | 16 | Around a document page (Options, properties) |
| `Shell.Padding.Row` | 0,1 | List and tree rows |
| `Shell.Size.Toolbar` | 28 | A toolbar row |
| `Shell.Size.ToolbarButton`, `Shell.Size.Input` | 24 | Toolbar buttons; text boxes and combo boxes in toolbars |
| `Shell.Size.Icon`, `Shell.Size.IconSmall` | 16, 12 | Icons; chevrons and badges |
| `Shell.Size.Row` | 22 | List and tree rows |
| `Shell.Size.ToolWindowMinWidth` | 280 | The narrowest a docked tool window is designed for |
| `Shell.FontSize.Body`, `Small`, `Heading`, `Title` | 12, 11, 14, 18 | Text (see Typography) |
| `Shell.CornerRadius`, `Shell.CornerRadius.Overlay` | 3, 6 | Buttons and inputs; popups |

### Classes

`ShellStyles.axaml` applies the tokens through classes, which views use instead of setting the values themselves: `Button.toolbar`, `ToggleButton.toolbar`, `ComboBox.toolbar`, `controls:ToolbarPanel`, `Border.toolbarSeparator`, `TextBlock.muted`, `Run.muted`, `TextBlock.small`, `TextBlock.heading`, `TextBlock.subheading`, `TextBlock.emptyState`, `TextBlock.error`, `TextBlock.warning`, `Button.link`, `Border.overlay`, and the icon classes below.

## Icons

**The rule (the maintainer's, 2026-10-13): every icon is colored, in the Visual Studio palette.** The palette is the
`Shell.Icon.Blue`, `Green`, `Red`, `Orange`, `Purple`, `Yellow` and `Folder` tokens in `Theming/Tokens.axaml`: Visual
Studio's image colors per theme (light `#00539C`, `#388A34`, `#A1260D`, `#B26B12`, `#652D90`, `#8A6400`, `#8A6320`;
dark `#75BEFF`, `#89D185`, `#F48771`, `#E8AB53`, `#B180D7`, `#FFCC00`, `#DCB67A`), each at least 3:1 against menus,
toolbars and panels. Command families are palette colors (run and tests green, stop red, save and navigation blue,
open folder tan, build purple, Git orange). Every icon kind has its color in `CommandIcons` (`s_kindColors`: blue for
windows, navigation and general actions, green for new, add and run, red for close, delete and errors, yellow for
warnings, locks and databases, orange for Git and history, purple for build and extensions, tan for folders); an
`Icon` that nothing else colors takes its kind's color, so a new kind must be added there (`IconColorTests` fails
otherwise, and `PolishTests` for a kind a view binds at run time). File, folder and project icons take the palette color nearest each of their fills by hue, folders tan;
gray fills (outlines, pages) stay gray, as Visual Studio's do. Only chrome glyphs (chevrons, carets) stay in the text
color, and High Contrast draws every icon in the foreground.

- **Commands and tool windows:** Fluent UI System Icons (Microsoft, MIT), 16 px regular. Add the file name to `src/Codalus.Shell/Icons/fluent-icons.txt`, run `eng/get-fluent-icons.sh`, commit the SVG, and map our name to it in `IconSet` (`Controls/Icon.cs`). A few small glyphs (chevrons, zoom, fit) are 16×16 stroke drawings in the same file.
- **File types, folders and projects:** the classic icons (`cl:` names, our own drawings): 16 px pixel drawings in a classic IDE's style, a gray outline page with a small colored badge in its lower-left corner (`C#`, `YML`, `{}`, `M↓`…; the page's outline is cut around the badge), tan outline folders, and outline project boxes with the language in color. `eng/icons/classic-icons.py` draws them into `Icons/Classic/`: change a drawing there, run it and commit the SVGs. Their unfilled parts take `Shell.Icon.Outline` (the theme's icon gray), their colors the palette's nearest. `Controls/FileIcons.cs` works out a Material name for every file and maps it onto the classic set; a file type with no classic icon is a blank page.
- **Material Icon Theme** (MIT) is the other built-in file icon theme (`workbench.iconTheme`: `material`): `Icons/material-icons.txt` and `eng/get-material-icons.sh`, mapped in `Controls/FileIcons.cs`. Its colors become the palette's nearest (above), except in High Contrast.
- **Our own drawings:** icons that resemble another product's are drawn from scratch, never traced, and never use its logo.
- **Compiled at build time:** `eng/icons/CompileIcons.cs` turns the SVGs into drawing operations; nothing parses SVG at runtime, and the shell stays Native AOT clean. Only `<path d>` is supported; anything else fails the build.
- **Color by family:** a command's icon takes its family's token through an `accent-*` class (`Controls/CommandIcons.cs`): run green, stop red, save and navigation blue, open folder tan, build purple, Git orange, tests green. Other icons take their kind's palette color. States use `info`, `warning`, `error`, and the test outcome classes (`passed`, `failed`, `skipped`, `running`, `none`).
- **High Contrast:** every icon is drawn in the foreground; shape carries the meaning.
- **One size:** 16 px in toolbars, menus, lists and tabs (14 px in document tabs); 12 px only for chevrons and inline badges. Disabled commands dim their icons (40 %).
- **Plugins** refer to icons by name (ADR 0003): any name in `IconSet` (`Controls/Icon.cs`), or `kind@Family` to color one with a family's token (`docs/plugins.md`).

## The gallery

`eng/smoke-test.sh` runs the real app through the smoke test, whose `shell.galleryEmpty` and `shell.gallery` steps photograph every registered tool window, the main documents (Options, Keyboard Shortcuts, Project Properties, NuGet, Configuration Manager, Add New Item, the Template Manager, Check Environment), the in-window dialog's forms (a preview of changes, a checklist, a name with an error) and the window's chrome, empty, with data and in an error state, in seven themes: Classic's Blue, Light, Dark and High Contrast and Harbor's Light, Dark and High contrast (M34h; Harbor is the family new installs start with, [ADR 0077](adr/0077-theme-families.md)). `CODALUS_GALLERY_THEMES=all` adds the other families' nine variants. Since M34g they also photograph Code Search's floating window (empty, nothing found, Feature Search, results with the preview), the WCF Test Client (empty, a missing address, a WSDL file's form), the first-run setup's four steps, Options' color theme cards and the Service References document's WCF page; `wcf.testClient` adds the test client with a response and a fault, and on Windows `winforms.designer` adds the Windows Forms designer, the Toolbox and the Properties window, and `wpf.preview` the WPF preview; `netfx.projects` adds Solution Explorer with a project that can't build, Convert to SDK-Style's preview and the Error List's reason, and on Windows the Call Stack and Modules of a desktop-CLR session. The pictures, `{surface}-{state}-{theme}.png` by the theme's id (`nuget-data-harbor.dark.png`), and an index (`index.md`, a column per theme by its name) are in `gallery/` next to the report (`artifacts/smoke-test/gallery` by default; CI uploads them with the smoke test's artifact). Review a milestone's surfaces there against the checklist; the maintainer approves the gallery at M34's checkpoints (D25).
