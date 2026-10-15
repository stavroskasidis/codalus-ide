# UI guidelines

How Codalus IDE's surfaces look and behave: the checklist every panel, window and dialog meets (D25), the tokens views use, and the icon rules (D24). It is for the shell's own views and for plugin authors whose views and contributions sit next to them. M34 (UI polish) wrote it down; M34a and M34d applied it.

## The checklist

Review a surface against each line, in all four themes, at its narrowest and widest, before calling it done. Every milestone's definition of done includes this review of its own panels ([roadmap](roadmap.md#m34--ui-polish)).

- **Layout**
  - Spacing comes from the 4 px grid (`Shell.Space.*`, `Shell.Padding.*`); no one-off margins.
  - Lists and trees use rows of `Shell.Size.Row` (22 px) with `Shell.Padding.Row`.
  - Tables keep their column widths: in a narrow pane the header and the rows scroll sideways together (a `ScrollViewer` with horizontal scrolling around a `DockPanel` with a `MinWidth`), never squeezed into each other.
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
  - `PolishTests` walks every tool window at 280 px (`Shell.Size.ToolWindowMinWidth`) and fails on text cut off without an ellipsis or a tooltip.
- **States**
  - Every surface has an empty state, a loading state and an error state, each with a next step: what to do (`Open Solution…`, `Press F9 in an editor to add one`), not only what is missing.
  - Empty and error messages use `TextBlock.emptyState` (muted, wrapped, `Shell.Padding.EmptyState` around).
  - Loading shows progress where it happens (an indeterminate bar under the toolbar, a status line), not a modal.
- **Keyboard**
  - Everything is reachable with the keyboard in reading order; focus is visible (`Shell.Focus`).
  - Shortcuts show in menus and tooltips, per OS (`docs/user-guide/keyboard.md`).
  - Lists and trees have context menus where users expect them (`MenuBuilder.Context`, menu locations in `BuiltIns.Menus.cs`).
- **Themes**
  - Colors come from tokens only (`{DynamicResource Shell.*}`), never literals, so Blue, Light, Dark and High Contrast switch live.
  - Text meets WCAG AA (4.5:1) on what it is drawn on; icons, focus rings and other graphics 3:1. `tests/Codalus.Shell.Tests/TestData/contrast-pairs.txt` lists every pair; `PolishTests` checks it in every theme.
  - In High Contrast, color never carries meaning alone: icons are monochrome (shape carries it), and states also have a shape or a word.
- **Operating systems and scales:** check Windows, Linux and macOS (Cmd shortcuts, the global menu) at 100 % to 200 %.
- **Accessibility:** every control has a name (`AutomationProperties.Name`), live regions announce status (`AutomationProperties.LiveSetting`), and rows have an accessible name that reads like a sentence.

## Tokens

All tokens are in `src/Codalus.Shell/Theming/Tokens.axaml`. Colors differ per theme; spacing, sizes and type are the same in every theme.

### Colors

| Token | Use |
|---|---|
| `Shell.Background`, `Shell.Foreground` | The window, and its text |
| `Shell.Panel` | Tool window and document surfaces |
| `Shell.MutedForeground` | Secondary text (4.5:1 on every surface) |
| `Shell.Border` | Borders and separators |
| `Shell.Hover`, `Shell.Selection` | Pointer over, and selected rows |
| `Shell.Accent`, `Shell.AccentForeground` | Accent graphics; text on an accent fill |
| `Shell.Focus` | Focus rings and outlines |
| `Shell.Overlay`, `Shell.Overlay.Border` | Popups, the command palette, toasts |
| `Shell.Link`, `Shell.Match` | Links; matched characters in pickers |
| `Shell.Info`, `Shell.Warning`, `Shell.Error`, `Shell.Modified` | States (icons and borders) |
| `Shell.Diff.Added`, `Shell.Diff.Removed` | Diffs, and passed tests |
| `Shell.Toolbar`, `Shell.Toolbar.Border`, `Shell.TitleBar*`, `Shell.Search*` | The title row and the standard toolbar |
| `Shell.StatusBar*` | The status bar: no solution (`.Empty`), a solution, debugging (`.Debugging`), hover |
| `Shell.Tab.Active`, `.Selected`, `.Inactive`, `.Hover`, `.Preview` (each with `.Foreground`) | Document tabs: the active tab, the selected tab of a pane without the focus, other tabs, pointer over, the preview tab |
| `Shell.TabColor.1`–`8` | Tabs colored by project (`workbench.editor.colorTabsByProject`) |
| `Shell.Caption.Active`, `Shell.Caption.Inactive` (with `.Foreground`) | Tool window captions with and without the focus |
| `Shell.Editor.*` | The editor: text, line numbers, inlay hints, guides, brackets, folding |
| `Shell.Icon.Run`, `.Stop`, `.Save`, `.Open`, `.Build`, `.Git`, `.Test`, `.Navigate` | Command icon families (white in High Contrast) |
| `Shell.Icon.Outline` | The gray of the classic file icons' pages and outlines |

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

`ShellStyles.axaml` applies the tokens through classes, which views use instead of setting the values themselves: `Button.toolbar`, `ToggleButton.toolbar`, `ComboBox.toolbar`, `controls:ToolbarPanel`, `Border.toolbarSeparator`, `TextBlock.muted`, `TextBlock.small`, `TextBlock.heading`, `TextBlock.subheading`, `TextBlock.emptyState`, `Button.link`, `Border.overlay`, and the icon classes below.

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
otherwise). File, folder and project icons take the palette color nearest each of their fills by hue, folders tan;
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

`eng/smoke-test.sh` runs the real app through the smoke test, whose `shell.galleryEmpty` and `shell.gallery` steps photograph every registered tool window, the main documents (Options, Keyboard Shortcuts, Project Properties, NuGet, Configuration Manager) and the window's chrome in each theme, empty, with data and in an error state. The pictures and an index (`index.md`) are in `gallery/` next to the report (`artifacts/smoke-test/gallery` by default; CI uploads them with the smoke test's artifact). Review a milestone's surfaces there against the checklist; the maintainer approves the gallery at M34's checkpoints (D25).
