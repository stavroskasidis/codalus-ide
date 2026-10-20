# UI guidelines

How Codalus IDE's surfaces look and behave: the checklist every panel, window and dialog meets (D25), the tokens views use, and the icon rules (D24). It is for the shell's own views and for plugin authors whose views and contributions sit next to them. M34 (UI polish) wrote it down; M34a and M34d applied it.

## The checklist

Review a surface against each line, in Classic and every theme family (Harbor, Graphite, Daylight and Ember, M45), in Light, Dark and High contrast, at its narrowest and widest, before calling it done. Every milestone's definition of done includes this review of its own panels ([roadmap](roadmap.md#m34--ui-polish)).

- **Layout**
  - Spacing comes from the 4 px grid (`Shell.Space.*`, `Shell.Padding.*`); no one-off margins.
  - Lists and trees use rows of `Shell.Size.Row` (22 px) with `Shell.Padding.Row`.
  - Tables keep their column widths: in a narrow pane the header and the rows scroll sideways together (a `ScrollViewer` with horizontal scrolling around a `DockPanel` with a `MinWidth`), never squeezed into each other.
  - The in-window dialog sizes to its content, except a preview of changes: three quarters of the window, at least 640 × 400 where the window has room (`DialogView.PreviewSize`, M34h), so the diff is readable.
  - Code shown outside an editor (a template's preview, a definition) is a `CodeViewer`, in the editor's colors, never plain text in a text box.
- **Toolbars**
  - A tool window's toolbar is one `controls:ToolbarPanel`: one row, 24 px buttons (`Button.toolbar`, `Shell.Size.ToolbarButton`), 16 px icons, inputs of `Shell.Size.Input`. It never wraps: what doesn't fit goes into its overflow menu (»), buttons by their `AutomationProperties.Name`, toggle buttons as checkable entries, combo boxes as submenus of their choices.
  - A search or filter box takes the rest of the row, or a row of its own under the toolbar when the toolbar is long (the Solution Explorer, the Test Explorer).
  - A `Border.toolbarSpacer` takes what is left of the row instead, down to nothing, so the items after it sit at the right end (AI Chat's Agent switch, Request Log and Configure, M34h).
  - Every icon-only button has a tooltip with its name and, when it has one, its shortcut (the menus' format: `Run All Tests (Ctrl+R, A)`), and an `AutomationProperties.Name`.
  - Group with `Border.toolbarSeparator`; labels between toolbar items are tooltips instead (labels can't overflow).
  - A status at the toolbar's end ("Docker · Linux") is a `TextBlock.muted.small` with `HorizontalAlignment="Right"`: it sits at the right end when there is room and is the first thing to go when there isn't (it never moves into the overflow menu; M34h).
  - Each icon takes its command family's color (below), so a toolbar's icons aren't all one color (M34h, the maintainer's
    review of Solution Explorer's): a refresh or sync is green, navigation blue, a folder's files tan, the solution's
    tree purple, Git's commands the Git family's orange, as in the menus. Where an icon's kind would give the wrong family in
    a toolbar, its `Icon` takes an `accent-*` class.
- **Typography**
  - UI text is 9 pt: `Shell.FontSize.Body` (12 px), the window's default; don't set it again.
  - Secondary text (details, counts, dates) is `Shell.FontSize.Small` (11 px), usually with `TextBlock.muted`.
  - Two heading sizes: `TextBlock.heading` (`Shell.FontSize.Title`, 18 px, page titles) and `TextBlock.subheading` (`Shell.FontSize.Heading`, 14 px, sections, detail panes and the in-window dialog's title, M34h).
  - Titles are sentence case except command and window names, which are title case as in the menus. Use the product's terms: solution, project, tool window, document.
- **Text that doesn't fit**
  - Nothing is cut off without an ellipsis: single-line text in a constrained space has `TextTrimming="CharacterEllipsis"` and a tooltip with the full text (on the text or its row).
  - Messages wrap (`TextWrapping="Wrap"`).
  - `PolishTests` walks every tool window at 280 px (`Shell.Size.ToolWindowMinWidth`), the documents that work as dialogs at 640 px and the in-window dialog's forms at 440 px. It fails on text cut off without an ellipsis or a tooltip.
  - Inputs keep room to type: a text box or combo box squeezed under 48 px by the columns around it fails `PolishTests` (M34g).
  - Text never runs under other text: a text in a horizontal stack panel isn't constrained by its cell, so put a row's texts in a `DockPanel` or `Grid` where they trim. `PolishTests` fails on text drawn over text (M34g, found in the quick pick's rows). Rows of a form that show different controls share their fixed columns (`Grid.IsSharedSizeScope`, `SharedSizeGroup`), so the values line up.
  - Columns: give the main column `*` and the others proportional (`2*,1.5*`) or modest fixed widths. Fixed columns that add up to most of 640 px squeeze the main one to nothing (the Keyboard Shortcuts header and Add New Item's list did, until M34's final pass).
- **Controls** (M34h): buttons, fields, choices, tabs and rows come from [the control set](#the-control-set): pick the kind of button by the rules there, never restyle one in a view.
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
  - Error and warning text uses `TextBlock.error` and `TextBlock.warning` (`Shell.Error.Foreground`, `Shell.Warning.Foreground`), never `Shell.Error` or `Shell.Warning`: those are graphics tokens, held to 3:1 only.
  - Logs color their lines the same way (M34h, `Controls/OutputPresenter`, the Output window and the Debug Console): an error line (`error CS1002:`, *Build failed*, an exception and its stack trace, `fail:`) and standard error in `Shell.Error.Foreground`, a warning line in `Shell.Warning.Foreground`, a success (*Build succeeded*, `0 Error(s)`, *Passed!*) in `Shell.Diff.Added.Foreground`. A process's ANSI colors are the terminal's palette raised to 4.5:1 (7:1 in High Contrast) against the editor's background, so they keep their hue and stay readable; other escape sequences are dropped, never shown. `OutputClassifier` (the line kinds) and `AnsiDecoder` are in `Workbench/Output`. Likewise the added and removed lines of a diff use `Shell.Diff.Added.Foreground` and `Shell.Diff.Removed.Foreground`, not `Shell.Diff.Added` and `.Removed`.
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
| `Shell.Merge.Conflict`, `.Conflict.Border`, `.Resolved.Border` | The merge editor's conflict blocks: an unresolved block's muted red fill (opaque, under the code; none in High Contrast) and dashed outline, and a resolved block's solid outline |
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
| `Shell.CornerRadius`, `Shell.CornerRadius.Overlay` | 3, 6 | Toolbar chrome from before M34h; popups and dialogs (controls take `Shell.Control.CornerRadius`, below) |

### Classes

`ShellStyles.axaml` applies the tokens through classes, which views use instead of setting the values themselves: `Button.toolbar`, `ToggleButton.toolbar`, `ComboBox.toolbar`, `controls:ToolbarPanel`, `Border.toolbarSeparator`, `Border.toolbarSpacer`, `TextBlock.muted`, `Run.muted`, `Run.code` (inline code in rendered Markdown), `TextBlock.small`, `TextBlock.heading`, `TextBlock.subheading`, `TextBlock.emptyState`, `TextBlock.error`, `TextBlock.warning`, `Button.link`, `Border.overlay`, `Border.intelliSense` (the editor's IntelliSense popups and Quick Info), `Border.gridHeader`, `Border.attached`, the control set's classes ([below](#the-control-set)), and the icon classes below.

## The control set

One look for every button, field, choice, tab and row (M34h, D33): the design and its pictures are [`design/controls.md`](design/controls.md), approved on 2026-10-18. The shell's control themes (`Theming/Controls.axaml`: `Button`, `ToggleButton`, `DropDownButton`, `CheckBox`, `RadioButton`, `ToggleSwitch`, `SegmentedControl`) and `ShellStyles.axaml` (text and combo boxes, split buttons, tabs, rows, chips) draw the set, so a plain control has the look already: a view picks a kind with a class and never sets colors, heights or corners on a control itself. `ControlSetTests` checks the states, and the smoke test's gallery photographs the whole set in every theme (`controls-*`, `Infrastructure/ControlGallery.cs`).

### Which button

| Kind | When | How |
|---|---|---|
| Primary | The one main action of a dialog, page or form (OK, Invoke, Add Reference, Send); at most one in view. A destructive primary keeps the accent and names what it does (Remove, not OK). | `<Button Classes="accent" …/>` (the dialog's default button is drawn so on its own) |
| Default | Every other text button. | `<Button Content="{l:T …}"/>` |
| Subtle | A secondary action in a crowded row (Reset beside Invoke): flat until hovered. | `Classes="subtle"` |
| Icon only | A familiar command with a standard glyph (refresh, start, stop, delete, open, copy, move up and down, clear, filter, pin, collapse). A tooltip with its name and shortcut in the menus' format (`Refresh (F5)`) and an `AutomationProperties.Name`: a tooltip isn't a name. | `<Button Classes="icon" controls:ButtonIcon.Kind="refresh" ToolTip.Tip="{l:T …}" AutomationProperties.Name="{l:T …}"/>` |
| Icon + text | A tool window's main command (Run All, Add…, Modify Find) and commands an icon can't name alone (Test Client, Make Active, Discover); one or two in a toolbar. The text stays the button's name. | `<Button Classes="iconText" controls:ButtonIcon.Kind="add" Content="{l:T …}"/>` |
| Text only | A dialog's or page's button row (OK, Cancel, Apply…). | `Classes="dialog"` (80 px at least) |
| Drop-down | Opens a choice (Running Services, the AI provider); never runs anything itself. | `<DropDownButton Content="…"><DropDownButton.Flyout>…` (its 12 px chevron is drawn for it) |
| Split | Runs its last choice; its arrow opens the others (Export CSV, then JSON). | `<SplitButton Content="…" Command="…"><SplitButton.Flyout>…` |
| Link | Navigates (Edit in settings.json, a file and line); never runs a command. | `Classes="link"` (underlined under the pointer) |
| Toolbar | 24 px flat buttons in a tool window's toolbar. | `Classes="toolbar icon"` or `"toolbar iconText"`; `ToggleButton.toolbar` stays checked as a tint with an accent edge (Keep Results, In process, the Error List's filters); `SplitButton.toolbar`, `DropDownButton.toolbar` |

Never: an icon-only button in a dialog's button row, two primaries in view, a check box in a toolbar (it becomes a `ToggleButton.toolbar`), a label between toolbar items (it becomes the tooltip, or a muted prefix inside the combo box it names).

Patterns the views use (M34h):

- **A muted prefix inside a combo box** (Benchmarks' `Job Short`): its `SelectionBoxItemTemplate` draws the label muted before the value. **Inside a text box** (Counters' `Every 1 s`): muted `TextBlock`s as its `InnerLeftContent` and `InnerRightContent`.
- **Buttons attached to their field** (Configuration Manager's New… and Remove on their combo box): `<Border Classes="attached"><StackPanel Orientation="Horizontal"><ComboBox …/><Button Classes="icon" …/></StackPanel></Border>`, one shape with the set's corners and square joints.
- **A table's column headers**: `Border.gridHeader` (24 px on `Shell.Grid.Header`, a `Shell.Grid.Rule` under it, the labels muted and semibold).
- **Commands on the selected row** (Service References' Test Client, Update from Source, Open Document, Remove): compact icon buttons at the row's end, shown while it is selected (`IsVisible="{Binding $parent[ListBoxItem].IsSelected}"`), instead of a row of text buttons under the list.
- **A state in a word on a row**: a chip (`locked` with its lock icon, `current`, `missing (prunable)` as a warning), or a dot and the word (a container's filled dot while it runs, its status in the detail line), never color alone.
- **A tree view's title commands** from `toolWindow/title` are icon buttons when the command has an icon in `CommandIcons` (the built-in plugins' are mapped there); a third-party command without one keeps its title.

`controls:ButtonIcon.Kind` works on `Button`, `ToggleButton` and `DropDownButton`; the icon takes its kind's palette color like any icon (the accent's text on a primary button, the checked text in High contrast).

### Sizes, spacing and order

- One height per context: **28 px** (`Shell.Size.Control`, the default) for buttons, text boxes and combo boxes in dialogs, documents and pages; **24 px** in tool windows, toolbars, form grids and dense rows (`Classes="compact"` on a button; inside a `controls:ToolbarPanel` buttons, text boxes and combo boxes are 24 px on their own).
- Inside a button: 12 px on each side (`Shell.Padding.Button`), 10 before an icon, 6 between the icon and the text (`Shell.Space.ButtonIcon`); 8 at 24 px (`Shell.Padding.ButtonCompact`). Icons 16 px, chevrons 12 px.
- Corners: `Shell.Control.CornerRadius` (Classic 2, Harbor and Graphite 3, Daylight and Ember 6); dialogs and popups `Shell.CornerRadius.Overlay`.
- A dialog's buttons are right-aligned in its footer, the primary is the default (Enter) and Cancel answers Escape. **Windows and Linux** put the primary first (`OK` `Cancel`); **macOS** puts it last (`Cancel` `OK`). A request lists its buttons primary first; `DialogViewModel` reverses them on macOS (`PrimaryLast`).

### Focus

- Buttons, toggles, segments, tabs, check boxes, radio buttons and switches: a **2 px ring in `Shell.Focus`, 1 px outside the control** (`Shell.Focus.Thickness`, `.Offset`, the ring's `Shell.Focus.Margin` and `.CornerRadius`), drawn by the control themes when the keyboard brings the focus.
- Text and combo boxes: their border becomes a 2 px `Shell.Focus` edge.
- Rows: a 1 px outline in `Shell.Focus` on the keyboard's row.

### Fields and choices

- Text boxes: `Shell.Input` with a 1 px `Shell.Input.Border` that darkens under the pointer (`.Hover`). An error (a binding's validation error, or `Classes="invalid"`) draws the border in `Shell.Error`, and the message goes under the box with the error icon in `TextBlock.error` (validation errors are drawn so on their own). Read-only is the panel's color; disabled is `Shell.Disabled`.
- Placeholders are `Shell.Input.Placeholder`, the muted text at full opacity; a placeholder names what goes in the box.
- Search boxes: `<TextBox Classes="search" PlaceholderText="{l:T …}"/>` has its search icon, and a clear button once there is text; in a toolbar it takes the rest of the row. A match count goes in its `InnerRightContent`.
- Combo boxes: the field with a 12 px chevron; focused from the keyboard or open, the focus edge.
- **Check box** in lists, forms and dialogs that commit with OK, and for Options' booleans: the title is the check box's own label, once. **Radio buttons** for one of a few choices that need descriptions. **Switch** (`ToggleSwitch`, its label as `Content`) only for an on/off that takes effect at once and has its own place: AI Chat's Agent mode.
- **Chip toggles** for the parts that go with something, each on or off, in a wrapping row (AI Chat's context: Selection, Current file, Errors…): `<ToggleButton Classes="chip" controls:ButtonIcon.Kind="document-text" Content="{l:T …}"/>`, a 22 px pill with a 12 px icon, checked in the toolbar's checked tint with its accent edge (M34h).
- **A composer** (`Border.composer`, M34h): one field that holds a multi-line text box and its own row of actions under the text (a hint, the primary Send), with the text box's border and its focus edge around both; the text box inside is bare.
- **Segmented control** for two to four views of the same content (Form or XML, Tree or List, Pretty or Raw, User or Workspace), instead of a `TabControl` used as a view switch or a pair of toggle buttons:

  ```xml
  <controls:SegmentedControl SelectedIndex="{Binding ViewIndex}" AutomationProperties.Name="{l:T findResults.view}">
    <controls:SegmentedControlItem Icon="type-hierarchy" Content="{l:T findResults.tree}" />
    <controls:SegmentedControlItem Icon="list" Content="{l:T findResults.list}" />
  </controls:SegmentedControl>
  ```

  It is a list underneath: Tab reaches the chosen segment, the arrow keys, Home and End move the choice, and there is always one. It can bind `ItemsSource` and `SelectedItem` too.
- **Tabs** inside a view (`TabControl`, 28 px) switch between different content (Body, Headers, Request): muted until selected, the selected one bold with a 2 px accent underline; a count goes in a `Border.badge` in the header (`<StackPanel Orientation="Horizontal" Spacing="6"><TextBlock …/><Border Classes="badge"><TextBlock Text="{Binding Count}"/></Border></StackPanel>`), which takes the accent on the selected tab.

### Lists, grids and states

- Rows of 22 px (`Shell.Size.Row`; `<ListBox Classes="rows">` sets them), headers of 24 px on `Shell.Grid.Header`, separators in `Shell.Grid.Rule`, numbers right-aligned in tabular figures.
- Every list and tree: `Shell.Row.Hover` under the pointer (quieter than a control's hover, so syntax-colored text stays AA; outlined in High contrast), `Shell.Selection`, and `Shell.Selection.Inactive` when the list hasn't the focus. The muted and matched text on a selected row follows on its own.
- A find match in result text is `Shell.Match.Background` with bold `Shell.Match.Background.Foreground`, underlined on a selected row.
- On a selected row, an icon under 3:1 takes the selection's text color (in practice the orange icons on Classic Blue's and Harbor Light's selections); in High Contrast every icon on a selection is black.
- **Chips** say a state in a word (`200 OK`, `Fault s:Client`, `GET`): `<Border Classes="chip success"><TextBlock Text="200 OK"/></Border>` with `success`, `error`, `warning` or `info` (none: neutral; `square` for a method). **Badges** are counts: `<Border Classes="badge"><TextBlock Text="3"/></Border>`. Color never carries the meaning alone; High contrast outlines both.
- Empty and error states: an icon, one sentence and the next step, centered: `<StackPanel Classes="emptyState"><controls:Icon Kind="…"/><TextBlock Text="{l:T …}"/><Button Classes="iconText" …/></StackPanel>`.

### Dialogs

The in-window dialog (`DialogView`, from `DialogViewModel.ShowAsync(new DialogRequest(…))`):

- Header: a 28 px tile with the request's `Icon` in its color (a dangerous request gets the warning icon), the title at 14 px semibold, a muted line with its `Context` (the type and file it acts on), and a close button (Escape).
- Body: `Shell.Padding.Dialog` (20 px at the sides). A checklist is a bordered list with a header row (Select all, `2 of 4`), rows of 30 px with each item's `Icon`, its label in the editor's font and its detail; Space checks the selected row, and a reorderable one has Move Up and Move Down as icon buttons beside it (Alt+Up, Alt+Down).
- Footer: `Border.dialogFooter` (`Shell.Dialog.Footer`, a top separator, `Shell.Padding.DialogFooter`), a keyboard hint on the left (`Border.kbd` keys), the buttons on the right in the OS's order, the default one the only primary.
- Width 520 px (`Shell.Size.Dialog.Width`), growing with its content up to 720 (`Shell.Size.Dialog.MaxWidth`; 960 with notes), narrower only in a smaller window. A preview of changes takes three quarters of the window instead (`DialogView.PreviewSize`: at least 640 × 400 where there is room, at most the window less 16 px on each side), its changes on the editor's background filling what the header, message and footer leave, each added or removed line marked in the diff's color at its left edge. The window behind is dimmed by `Shell.Dialog.Scrim`.

### Tokens of the control set

Colors (every theme; the values are in [`design/controls.md`](design/controls.md#colors)):

| Token | Use |
|---|---|
| `Shell.Button`, `.Foreground`, `.Border`, `.Hover`, `.Hover.Border`, `.Pressed`, `.Pressed.Border` | The default button in its states |
| `Shell.Accent.Hover`, `Shell.Accent.Pressed` | The primary button (and checked boxes, switches) under the pointer and pressed |
| `Shell.Toolbar.Hover`, `.Hover.Border`, `.Pressed`, `.Checked`, `.Checked.Border`, `.Checked.Foreground` | Toolbar and subtle buttons; a checked toolbar toggle |
| `Shell.Input.Border`, `.Border.Hover`, `.Placeholder` | Text and combo boxes |
| `Shell.Disabled`, `.Border`, `.Foreground` | A disabled control (its text exempt from AA, about 3:1) |
| `Shell.Check.Border` | An unchecked check box's, radio button's and switch's outline (3:1) |
| `Shell.Segment.Selected`, `.Foreground` | The chosen segment |
| `Shell.Selection.Inactive`, `.Foreground` | A list's selection without the focus |
| `Shell.Row.Hover` (and `Shell.Row.Hover.Outline`, High contrast's) | A row under the pointer |
| `Shell.Grid.Header`, `Shell.Grid.Rule` | Column headers; row separators |
| `Shell.Match.Background`, `.Foreground` | A find match in result text |
| `Shell.Badge`, `.Foreground` | Count badges, neutral chips |
| `Shell.Status.Success`, `.Error`, `.Warning`, `.Info` (each with `.Foreground`) | Chips |
| `Shell.Dialog.Footer`, `Shell.Dialog.Scrim` | A dialog's button row; the window behind it |
| `Shell.Chat.User`, `.Foreground`; `Shell.CodeBlock.Header` | AI Chat's own messages; a code block's header row |

Sizes and shapes:

| Token | Value | Use |
|---|---|---|
| `Shell.Size.Control` | 28 | Buttons, text boxes and combo boxes in dialogs, documents and pages |
| `Shell.Size.Button.MinWidth` | 80 | A button row's text buttons (`Button.dialog`) |
| `Shell.Padding.Button`, `Shell.Padding.ButtonCompact` | 12,0; 8,0 | Inside a button at 28 and 24 px |
| `Shell.Space.ButtonIcon` | 6 | Between a button's icon and its text |
| `Shell.Control.CornerRadius` | Classic 2, Harbor 3, Graphite 3, Daylight 6, Ember 6 | Buttons, fields, segments (per family; `Shell.Check.CornerRadius` for check boxes) |
| `Shell.Focus.Thickness`, `Shell.Focus.Offset` (`Shell.Focus.Margin`, `.CornerRadius`) | 2, 1 (−3) | The focus ring |
| `Shell.Padding.Dialog`, `Shell.Padding.DialogFooter` | 20,16; 20,12 | A dialog's header and body; its footer |
| `Shell.Size.Dialog.Width`, `Shell.Size.Dialog.MaxWidth` | 520, 720 | The in-window dialog |
| `Shell.Size.Segment`, `Shell.Size.Tab` | 24, 28 | A segmented control; a tab strip inside a view |
| `Shell.Chip.BorderThickness` | 0 (1 in High contrast) | Chips' and badges' outline |

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
- **Code symbols** (the completion list, Quick Info, the completion filters): our own 16 px pixel drawings, one per kind (`symbol-class`, `symbol-method`, `symbol-extension-method`, `symbol-property`…) and the target type filter's target (`symbol-target-type`), drawn by `eng/icons/symbol-icons.py` into `Icons/Symbols/`. Each takes its kind's color from `CommandIcons` (types orange, enums and events yellow, methods and delegates purple, data, variables and the rest blue); a part drawn with a fill (the extension method's arrow) takes the palette's nearest. Change a drawing there, run it and commit the SVGs. Other servers' LSP completion kinds map onto them (`Editor/IntelliSense/SymbolKinds.cs`).
- **The Windows Forms Toolbox:** every control and component has an icon of its own (M34h), `control-*` names in `IconSet` (Fluent icons, and stroke drawings for the combo box, the progress bar and the group box, which Fluent has no picture of), mapped by type in `ToolboxViewModel.IconFor`: inputs, lists and menus blue, containers purple, pictures and progress green, data yellow, the error provider red, folders tan. A derived or vendor's control takes the icon of the control its name ends with (`RadButton` a button's); anything else is `control-generic` or `control-component`.
- **Our own drawings:** icons that resemble another product's are drawn from scratch, never traced, and never use its logo.
- **Compiled at build time:** `eng/icons/CompileIcons.cs` turns the SVGs into drawing operations; nothing parses SVG at runtime, and the shell stays Native AOT clean. Only `<path d>` is supported; anything else fails the build.
- **Color by family:** a command's icon takes its family's token through an `accent-*` class (`Controls/CommandIcons.cs`): run green, stop red, save and navigation blue, open folder tan, build purple, Git orange, tests green. Other icons take their kind's palette color. States use `info`, `warning`, `error`, and the test outcome classes (`passed`, `failed`, `skipped`, `running`, `none`).
- **High Contrast:** every icon is drawn in the foreground; shape carries the meaning.
- **One size:** 16 px in toolbars, menus, lists and tabs (14 px in document tabs); 12 px only for chevrons and inline badges. Disabled commands dim their icons (40 %).
- **Plugins** refer to icons by name (ADR 0003): any name in `IconSet` (`Controls/Icon.cs`), or `kind@Family` to color one with a family's token (`docs/plugins.md`).

## The gallery

`eng/smoke-test.sh` runs the real app through the smoke test, whose `shell.galleryEmpty` and `shell.gallery` steps photograph every registered tool window, the main documents (Options, Keyboard Shortcuts, Project Properties, NuGet, Configuration Manager, Add New Item, the Template Manager, Check Environment), the in-window dialog's forms (a preview of changes, a checklist, a name with an error) and the window's chrome, empty, with data and in an error state, in seven themes: Classic's Blue, Light, Dark and High Contrast and Harbor's Light, Dark and High contrast (M34h; Harbor is the family new installs start with, [ADR 0077](adr/0077-theme-families.md)). `CODALUS_GALLERY_THEMES=all` adds the other families' nine variants. Since M34g they also photograph Code Search's floating window (empty, nothing found, Feature Search, results with the preview), the WCF Test Client (empty, a missing address, a WSDL file's form), the first-run setup's four steps, Options' color theme cards and the Service References document's WCF page; `wcf.testClient` adds the test client with a response and a fault, and on Windows `winforms.designer` adds the Windows Forms designer, the Toolbox and the Properties window, and `wpf.preview` the WPF preview; `netfx.projects` adds Solution Explorer with a project that can't build, Convert to SDK-Style's preview and the Error List's reason, and on Windows the Call Stack and Modules of a desktop-CLR session. The pictures, `{surface}-{state}-{theme}.png` by the theme's id (`nuget-data-harbor.dark.png`), and an index (`index.md`, a column per theme by its name) are in `gallery/` next to the report (`artifacts/smoke-test/gallery` by default; CI uploads them with the smoke test's artifact). AI Chat is photographed with the `ai.chat` step's conversation (a question, and an agent's turn with a step) with AI on, docked and at its design's size in a window of its own (`aichat-design-data-*`, M34h). Review a milestone's surfaces there against the checklist; the maintainer approves the gallery at M34's checkpoints (D25).
