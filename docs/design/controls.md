# Controls: one look for buttons, fields and rows

The control set every view uses: buttons, toolbar buttons and toggles, text, search and combo boxes, check boxes, radio buttons and switches, segmented controls, tabs inside a view, and list and grid rows, each in every state. The maintainer asked for it after reviewing the M34 gallery on 2026-10-18 ("All buttons and inputs should have a common look and feel. Most buttons are generic/bland. Most buttons that can be icon buttons should be converted or have them as Icon + text if needed."), and the maintainer approved it on 2026-10-18 with every open question as proposed ([D33](../roadmap.md#decision-register), [below](#decided)). M34h builds it: first the foundation (the tokens in all sixteen themes, the control themes in `Theming/Controls.axaml`, the classes in `Theming/ShellStyles.axaml`, the segmented control, the in-window dialog's frame and the checklist dialog; [`ui-guidelines.md`](../ui-guidelines.md#the-control-set) says how a view uses them), then the views in the findings table on top of it.

The designs are drawn in the Classic family (Blue, Light, Dark, High Contrast) and Harbor (Light, Dark, High contrast), with their real tokens ([`ui-guidelines.md`](../ui-guidelines.md#tokens), [`shell-themes.md`](shell-themes.md#harbor-colors)). Graphite, Daylight and Ember follow the same rules; their values for the new tokens are mixed from their own colors ([the derived families](#graphite-daylight-and-ember)), as M45 did for the tokens the design left out.

| | Classic Light | Classic Dark | Harbor Light | Harbor Dark |
|---|---|---|---|---|
| **Control set** | ![Controls, Classic Light](controls/previews/controls-classic-light.png) | ![Controls, Classic Dark](controls/previews/controls-classic-dark.png) | ![Controls, Harbor Light](controls/previews/controls-harbor-light.png) | ![Controls, Harbor Dark](controls/previews/controls-harbor-dark.png) |
| **Toolbars, now and proposed** | ![Toolbars, Classic Light](controls/previews/tool-window-toolbars-classic-light.png) | ![Toolbars, Classic Dark](controls/previews/tool-window-toolbars-classic-dark.png) | ![Toolbars, Harbor Light](controls/previews/tool-window-toolbars-harbor-light.png) | ![Toolbars, Harbor Dark](controls/previews/tool-window-toolbars-harbor-dark.png) |
| **Find Results** | ![Find Results, Classic Light](controls/previews/find-results-classic-light.png) | ![Find Results, Classic Dark](controls/previews/find-results-classic-dark.png) | ![Find Results, Harbor Light](controls/previews/find-results-harbor-light.png) | ![Find Results, Harbor Dark](controls/previews/find-results-harbor-dark.png) |
| **HTTP Response** | ![HTTP Response, Classic Light](controls/previews/http-response-classic-light.png) | ![HTTP Response, Classic Dark](controls/previews/http-response-classic-dark.png) | ![HTTP Response, Harbor Light](controls/previews/http-response-harbor-light.png) | ![HTTP Response, Harbor Dark](controls/previews/http-response-harbor-dark.png) |
| **WCF Test Client** | ![WCF Test Client, Classic Light](controls/previews/wcf-test-client-classic-light.png) | ![WCF Test Client, Classic Dark](controls/previews/wcf-test-client-classic-dark.png) | ![WCF Test Client, Harbor Light](controls/previews/wcf-test-client-harbor-light.png) | ![WCF Test Client, Harbor Dark](controls/previews/wcf-test-client-harbor-dark.png) |
| **Checklist dialog** | ![Checklist dialog, Classic Light](controls/previews/checklist-dialog-classic-light.png) | ![Checklist dialog, Classic Dark](controls/previews/checklist-dialog-classic-dark.png) | ![Checklist dialog, Harbor Light](controls/previews/checklist-dialog-harbor-light.png) | ![Checklist dialog, Harbor Dark](controls/previews/checklist-dialog-harbor-dark.png) |
| **AI Chat** | ![AI Chat, Classic Light](controls/previews/ai-chat-classic-light.png) | ![AI Chat, Classic Dark](controls/previews/ai-chat-classic-dark.png) | ![AI Chat, Harbor Light](controls/previews/ai-chat-harbor-light.png) | ![AI Chat, Harbor Dark](controls/previews/ai-chat-harbor-dark.png) |
| **Options** | ![Options, Classic Light](controls/previews/options-classic-light.png) | ![Options, Classic Dark](controls/previews/options-classic-dark.png) | ![Options, Harbor Light](controls/previews/options-harbor-light.png) | ![Options, Harbor Dark](controls/previews/options-harbor-dark.png) |
| **Windows Forms designer menus** | ![Designer menus, Classic Light](controls/previews/winforms-designer-menus-classic-light.png) | ![Designer menus, Classic Dark](controls/previews/winforms-designer-menus-classic-dark.png) | ![Designer menus, Harbor Light](controls/previews/winforms-designer-menus-harbor-light.png) | ![Designer menus, Harbor Dark](controls/previews/winforms-designer-menus-harbor-dark.png) |
| **Properties window** | ![Properties window, Classic Light](controls/previews/properties-window-classic-light.png) | ![Properties window, Classic Dark](controls/previews/properties-window-classic-dark.png) | ![Properties window, Harbor Light](controls/previews/properties-window-harbor-light.png) | ![Properties window, Harbor Dark](controls/previews/properties-window-harbor-dark.png) |
| **Git Changes** | ![Git Changes, Classic Light](controls/previews/git-changes-classic-light.png) | ![Git Changes, Classic Dark](controls/previews/git-changes-classic-dark.png) | ![Git Changes, Harbor Light](controls/previews/git-changes-harbor-light.png) | ![Git Changes, Harbor Dark](controls/previews/git-changes-harbor-dark.png) |
| **NuGet's verified owner** | ![Package manager, Classic Light](controls/previews/package-manager-classic-light.png) | ![Package manager, Classic Dark](controls/previews/package-manager-classic-dark.png) | ![Package manager, Harbor Light](controls/previews/package-manager-harbor-light.png) | ![Package manager, Harbor Dark](controls/previews/package-manager-harbor-dark.png) |

More pictures: the control set in [Classic Blue](controls/previews/controls-classic-blue.png), [Classic High Contrast](controls/previews/controls-classic-high-contrast.png) and [Harbor High contrast](controls/previews/controls-harbor-high-contrast.png); the WCF Test Client with a fault ([Harbor Light](controls/previews/wcf-test-client-fault-harbor-light.png), [Classic Dark](controls/previews/wcf-test-client-fault-classic-dark.png)); the checklist dialog's buttons on [macOS](controls/previews/checklist-dialog-macos-harbor-light.png); the Windows Forms designer's menus in [Classic High Contrast](controls/previews/winforms-designer-menus-classic-high-contrast.png); the Properties window in [Classic High Contrast](controls/previews/properties-window-classic-high-contrast.png), and in the whole window with BackColor's drop-down ([Classic Light](controls/previews/properties-window-color-classic-light.png), [Harbor Dark](controls/previews/properties-window-color-harbor-dark.png)), the font editor ([Classic Dark](controls/previews/properties-window-font-classic-dark.png), [Harbor Light](controls/previews/properties-window-font-harbor-light.png)) and the invalid-value dialog ([Classic Light](controls/previews/properties-window-invalid-classic-light.png), [Harbor Dark](controls/previews/properties-window-invalid-harbor-dark.png)); Git Changes in [Classic High Contrast](controls/previews/git-changes-classic-high-contrast.png), and in the whole window with a folder's menu ([Classic Light](controls/previews/git-changes-folder-classic-light.png), [Harbor Dark](controls/previews/git-changes-folder-harbor-dark.png), [on macOS](controls/previews/git-changes-folder-macos-harbor-light.png)), a branch's menu ([Classic Dark](controls/previews/git-changes-branch-menu-classic-dark.png), [Harbor Light](controls/previews/git-changes-branch-menu-harbor-light.png)) and the unmerged branch's dialog ([Classic Light](controls/previews/git-changes-delete-unmerged-classic-light.png)); NuGet's verified owner in [Classic Blue](controls/previews/package-manager-classic-blue.png), [Classic High Contrast](controls/previews/package-manager-classic-high-contrast.png) and [Harbor High contrast](controls/previews/package-manager-harbor-high-contrast.png).

## The design files

`controls/` holds the designs as HTML mockups, the same kind as the theme families' ([`shell-themes.md`](shell-themes.md#the-design-files)): open a file in a browser. They use `../shell-themes/support.js` to render, `controls.js` for the palettes, the icon drawings and a small C# colorizer, and `controls.css` for the controls themselves, so every mockup draws the same set.

- `Controls.dc.html`: the control set in every state, the sizes, the dialog button order, and the proposed tokens with their values in the theme shown.
- `ToolWindowToolbars.dc.html`: Containers (with data and empty), Exception Settings, Benchmarks, Counters, Configuration Manager, the Service References document's WCF page and Git Worktrees, each now and proposed.
- `FindResults.dc.html`, `HttpResponse.dc.html`, `WcfTestClient.dc.html`, `ChecklistDialog.dc.html`, `AiChat.dc.html`, `Options.dc.html`: the redesigned views, with the content, commands and labels of their `.axaml` and `en.json`, and the Codalus samples' code (`samples/Wcf`, `samples/Web/WebApi`, `samples/Diagnostics/Benchmarks`).
- `WinFormsDesignerMenus.dc.html` (#67, part 2, a design before it is built): the Windows Forms designer's context menus as the classic IDE's, in fifteen panels on `samples/WindowsDesktop/WinFormsApp`'s OrderForm (400 px panels) and InventoryForm (600 px, so the whole form shows). The surface's menu: a control in a split container's panel (its verb, Select 'notesSplit.Panel1', no Type Here, Paste grayed with nothing copied, today's "Dock: Fill"), a control on the form (Select 'OrderForm', Paste enabled after Copy), the form itself (only View Code, Lock Controls and Properties enabled), a TabControl's verbs (Add Tab, Remove Tab) and the same with no tab pages (Remove Tab grayed), a control with a tray component (no verbs; z-order, Align to Grid and Lock Controls grayed), and a tool strip item (File: Select 'mainMenuStrip', its drop-down beneath). The tray: a component's menu, the background's (Paste, Line Up Icons) and an ImageList's verb (Choose images) with Show Smart Tag, the tray wrapping as its WrapPanel does. A menu strip that keeps Type Here with today's "Dock: Top" over File. Locked controls: the strip docked at the top, a TextBox's menu with Lock Controls checked, a split container beside the Format menu (Align's entries grayed for one control, all with icons, Lock Controls checked), and the form itself, each with no grab handles and the lock's tile on the frame's corner. Its last card lists what the maintainer decides ([below](#the-windows-forms-designers-menus-06)). The icons are Fluent's own paths (MIT) from the pinned `@fluentui/svg-icons`, as in the package manager's design, so the shapes are the ones the shell would compile. The forms stand in for the bitmaps the designer host draws, in Windows' own colors in every theme, so the contrast check skips them (`data-nocheck`), and today's Dock text, drawn in the accent over them; the menus and the other adorners are checked.
- `GitChanges.dc.html` (#23, a design before it is built): the Git Changes window's folder menu and the branch drop-down with each branch's menu, on `samples/BuildAndRun` with ten changes and two staged files, Git Changes docked right at 400 px, in 26 panels side by side. The change tree: a folder's hover buttons with Discard, the folder menu in Changes, a staged folder, a folder whose files are all deleted, a folder of untracked files (Add to .gitignore) and the filter ("2 of 5 files"). The confirmations: Discard with and without the filter, Rename to a name that exists, Delete, an unmerged branch (by git's rule) and Delete from origin…, and the Compare with Current Branch document. The branch drop-down: the list, search, no match, a branch's menu, the current branch, a remote branch, a branch in another worktree (its chip, and Enter on it), a detached HEAD, a merge in progress, a repository with no commits, no remote, and the 280 px pane. Its last card lists what the maintainer decides ([below](#git-changes-06)). The classic file and folder icons are the app's own pixel drawings.
- `PropertiesWindow.dc.html` (#68, a design before it is built): the Windows Forms designer's Properties window as the classic IDE's, at the tool window's 300 px, for OrderForm's placeOrderButton with the values the designer host reports for the fixture. Sixteen panels: Categorized with Font open to its nine parts; the other categories with (DataBindings) open and a row's menu (Reset grayed for (Name), Description); BackColor's drop-down on its Web tab and the Custom (48 basic colors, 16 slots, Define Color) and System tabs; the ContentAlignment picker; the multi-line text drop-down; ShortcutKeys on InventoryForm's newToolStripMenuItem; Cursor, a choice that takes typing; the font editor, the String Collection Editor (notesTextBox.Lines) and Select Resource as panels over the designer surface; the invalid-value dialog; a file and a project from the Solution Explorer (Property Pages grayed, then enabled); the component selector open; Alphabetical. Its last card lists what the maintainer decides ([below](#the-properties-window-06)). `?state=` shows one state in the whole window. The forms are skipped by the contrast check as in the designer's menus; the preview's cart.png stands in for an imported image.
- `PackageManager.dc.html` (#78, item 4, a design before it is built): the NuGet package manager's verified-owner badge, on `samples/Packages`' Plain project with made-up Contoso and Fabrikam packages, in six panels at the document's real sizes. Browse at 1240 px: a verified short ID, selected, with the badge in the details' heading; a verified 70-character ID that trims and keeps its badge, under the pointer with the tooltip; a verified installed package with an update, beside its installed badge; two packages without a reserved prefix. Where it doesn't show: the Installed list and its details; another source (a mirror of nuget.org) that sends the same field; All sources when a newer copy in the local feed wins. At the document's 640 px minimum: the long ID trimmed in the list and wrapped in the heading. Then the glyph beside the ones it was chosen over, and a last card with what the maintainer decides ([below](#nugets-verified-owner-06)). The glyphs are Fluent's own paths (MIT) from the pinned `@fluentui/svg-icons`, so the shape is the one the shell would compile. The initials' tiles are today's large text (19 px bold, held to 3:1 as `PolishTests` holds them), so the check skips them, and it skips today's Refresh in Classic Blue, whose green on the window is 2.96:1, a gap of the current view.

The query string picks the theme: `Controls.dc.html?theme=Classic%20Dark`. The themes are `Classic Blue`, `Classic Light`, `Classic Dark`, `Classic High Contrast`, `Harbor Light` (the default), `Harbor Dark` and `Harbor High contrast`. `WcfTestClient.dc.html?mode=Fault` shows a fault, `ChecklistDialog.dc.html?os=macOS` the macOS button order, `PropertiesWindow.dc.html?state=Color` one of the Properties window's states in the whole window (`Color`, `Alignment`, `Font`, `Strings`, `Resource`, `Invalid`, `File`), and `GitChanges.dc.html?state=branch-menu` one of Git Changes' 26 states in the whole window, with `os=macOS` or `os=Linux` for that system's paths, Reveal title, shortcuts and button order. The sheets link every theme at their top.

`node docs/design/controls/render.cjs` renders `previews/` (1280 px wide: the sheets at their full height, the views at their natural size, 540 to 800 px) and checks the contrast as rendered (below); `--check` only checks. It needs Playwright's Chromium. The committed previews were then reduced to 256 colors to keep them small; the HTML is the source of every color.

The mockups draw text in Inter as a stand-in for the system UI font (Segoe UI on Windows) at the shell's 12 px, and code in the system's monospace font as a stand-in for Cascadia Mono. The icons are our own 16 px drawings in the style of Fluent UI System Icons (regular), standing in for the ones the app compiles from `eng/icons`; their colors follow [the icon rule](../ui-guidelines.md#icons).

## The rules

### Which button

- **Primary** (the accent): the one main action of a dialog, page or form (OK, Invoke, Add Reference, Send). At most one in view. A destructive primary keeps the accent and names what it does (Remove, not OK).
- **Default**: every other text button.
- **Subtle**: a secondary action in a crowded row (Reset beside Invoke); flat until hovered.
- **Icon only**: a familiar command with a standard glyph: refresh, start, stop, restart, delete, open, open in browser, copy, insert, move up and down, clear, filter, pin, collapse. Toolbars and row actions use them. Each has a tooltip with its name and shortcut in the menus' format (`Refresh (F5)`) and an `AutomationProperties.Name`; a tooltip isn't a name.
- **Icon + text**: a tool window's main command (Run All, Add…, Modify Find, Send Again), and commands an icon can't name alone (Test Client, Make Active, Install, Discover). At most one or two in a toolbar, so the row stays scannable.
- **Text only**: the buttons in a dialog's or page's button row (OK, Cancel, Apply…, Remove).
- **Drop-down button**: opens a choice of commands or a list (Running Services, the AI provider); a 12 px chevron after the label, and it never runs anything itself. **Split button**: runs its last choice, its arrow opens the others (Export CSV, then JSON).
- **Link**: navigates (Edit in settings.json, a file and line); it never runs a command.
- **Toolbar toggle**: a 24 px toolbar button that stays checked (Keep Results, In process, the Error List's filters): a tint and an accent edge, not a solid accent block.
- Never: an icon-only button in a dialog's button row, two primaries in view, a check box in a toolbar (it becomes a toggle), a label between toolbar items (it becomes the tooltip, or a muted prefix inside the combo box it names, as `Job Short`).

### Sizes, spacing and order

- One height per context: **28 px** (`Shell.Size.Control`) for buttons, text boxes and combo boxes in dialogs, documents and pages; **24 px** (`Shell.Size.ToolbarButton`, `Shell.Size.Input`) in tool windows, toolbars, form grids and dense rows. Text buttons in a button row are at least 80 px wide.
- Inside a button: 12 px on each side, 10 px before an icon, 6 px between the icon and the text; 8 px at 24 px. Icons are 16 px, chevrons 12 px.
- A dialog's buttons are right-aligned in its footer. The primary is the default (Enter) and Cancel answers Escape. **Windows and Linux** put the primary first (`OK` `Cancel`); **macOS** puts it last (`Cancel` `OK`). A hint or secondary link goes on the left of the footer.
- Corners: `Shell.Control.CornerRadius` (Classic 2 px, Harbor 3 px); overlays and dialogs keep `Shell.CornerRadius.Overlay`.

### Focus

- Buttons, toggles, segments, tabs, check boxes and switches: a **2 px ring in `Shell.Focus`, 1 px outside the control**, so it reads on any fill.
- Text and combo boxes: their border becomes a 2 px accent edge.
- Rows: a 1 px inset outline when the keyboard is on a row that isn't selected.
- The ring meets 3:1 against the surface in every theme (checked below); High Contrast's is its focus color (Classic's orange, Harbor's cyan).

### Fields and choices

- Text boxes: `Shell.Input` with a 1 px `Shell.Input.Border` that darkens under the pointer. An error draws the border in `Shell.Error` and says what to do under the box, with the error icon, in `TextBlock.error`. Read-only is a dashed border on the panel color; disabled is `Shell.Disabled`.
- Search boxes: a search (or filter) icon, a placeholder naming what is searched, a match count and a clear button once there is text. In a toolbar the box takes the rest of the row.
- Placeholders are the muted text at full opacity (`Shell.Input.Placeholder`).
- Combo boxes: the field with a 12 px chevron; open, the field keeps the focus edge and the list shows the chosen item with a check. An editable one is one field: one border around its text and chevron under the pointer and with the focus, as a text box's, and its text and placeholder in the middle of the field like every combo box's.
- **Check box** in lists, forms and dialogs that commit with OK, and for Options' booleans: the title is the check box's own label, once. **Radio buttons** for one of a few choices that need their descriptions. **Switch** only for an on/off that takes effect at once and has its own place (AI Chat's Agent mode).
- **Segmented control** (24 px) for two to four views of the same content: Form or XML, Tree or List, Pretty or Raw, User or Workspace. It replaces a `TabControl` used as a view switch and pairs of `ToggleButton`s.
- **Tabs** inside a view (28 px) switch between different content (Body, Headers, Request): muted until selected, the selected one bold with a 2 px accent underline, counts as badges, kept pages with their own close button.

### Lists, grids and states

- Rows of 22 px (`Shell.Size.Row`), 24 px for headers on `Shell.Grid.Header`, separators in `Shell.Grid.Rule`, numbers right-aligned in tabular figures.
- Under the pointer a row takes `Shell.Row.Hover`, quieter than a control's hover so syntax-colored text stays AA; in High Contrast it is a dashed outline.
- The selection is `Shell.Selection`; a list without the focus keeps it as `Shell.Selection.Inactive`. A match is `Shell.Match.Background` with bold text, and underlined on a selected row.
- On a selected row, an icon that falls under 3:1 takes the selection's text color: in practice the orange icons on Classic Blue's and Harbor Light's selections. In High Contrast every icon on a selection is black.
- **Chips** say a state in a word (`200 OK`, `Fault s:Client`, `GET`) on a `Shell.Status.*` tint; **badges** are counts. Color never carries the meaning alone, and in High Contrast chips are outlined.
- Empty and error states: an icon, one sentence, and the next step as a button or link.

### Dialogs

The in-window dialog (`DialogView`) gets a header, a body and a footer:

- Header: a 28 px icon tile in the command's color, the title at 14 px semibold (`TextBlock.subheading`), a muted line with the context (the type and file it acts on), and a close button (Esc).
- Body: 20 px at the sides; a list in a bordered box with its own header row (Select all, `2 of 4`), rows of 30 px with the member's icon, its signature in the editor font and its kind; reordering as icon buttons beside the list.
- Footer: `Shell.Dialog.Footer` with a top separator, a keyboard hint on the left and the buttons on the right in the OS's order.
- Width 520 px (`Shell.Size.Dialog.Width`), growing with its content up to 720 px; the window behind is dimmed by `Shell.Dialog.Scrim`.

## Tokens

The set uses the existing tokens for everything they already name (`Shell.Accent`, `Shell.Focus`, `Shell.Input`, `Shell.Selection`, `Shell.Link`, the icon palette, the editor's colors). No existing value changes. These are new.

### Colors

| Token | Classic Blue | Classic Light | Classic Dark | Classic HC | Harbor Light | Harbor Dark | Harbor HC | Use |
|---|---|---|---|---|---|---|---|---|
| `Shell.Button` | `#FFFFFF` | `#FDFDFD` | `#333337` | `#000000` | `#FFFFFF` | `#1F2840` | `#000000` | Default button fill |
| `Shell.Button.Foreground` | `#1E1E1E` | `#1E1E1E` | `#F1F1F1` | `#FFFFFF` | `#1B2645` | `#E3E8F5` | `#FFFFFF` | Default button text |
| `Shell.Button.Border` | `#8E9BBC` | `#C2C4D0` | `#46464C` | `#FFFFFF` | `#AEBBD9` | `#34416A` | `#FFFFFF` | Default button border |
| `Shell.Button.Hover` | `#FDF4BF` | `#EAF3FC` | `#3E3E42` | `#1F1F1F` | `#EDF1FA` | `#273350` | `#1A323D` | Default button fill under the pointer |
| `Shell.Button.Hover.Border` | `#E5C365` | `#7EB4EA` | `#5C5C63` | `#F38518` | `#8FA0C8` | `#4C5D8E` | `#6CD2FF` | Its border |
| `Shell.Button.Pressed` | `#FFE8A6` | `#CCE4F7` | `#2A4A6B` | `#0E4A7B` | `#DCE4F6` | `#1A2238` | `#1A323D` | Default button fill while pressed |
| `Shell.Button.Pressed.Border` | `#E5C365` | `#569DE5` | `#3794FF` | `#F38518` | `#6F84B8` | `#6E98FF` | `#6CD2FF` | Its border |
| `Shell.Accent.Hover` | `#0C62A8` | `#0068B0` | `#5AA8FF` | `#7CF3FF` | `#244CA8` | `#8AACFF` | `#AEE6FF` | Primary button under the pointer |
| `Shell.Accent.Pressed` | `#0A5591` | `#005A9E` | `#2B7FE0` | `#1AEBFF` | `#1E408F` | `#5A86F0` | `#6CD2FF` | Primary button while pressed |
| `Shell.Toolbar.Hover` | `#FDF4BF` | `#DCEAF8` | `#3E3E40` | `#1F1F1F` | `#E0E6F6` | `#273350` | `#1A323D` | Toolbar and subtle buttons under the pointer |
| `Shell.Toolbar.Hover.Border` | `#E5C365` | `#B3D3F2` | `#55555B` | `#F38518` | `#C3CDE6` | `#384670` | `#6CD2FF` | Its border |
| `Shell.Toolbar.Pressed` | `#FFE8A6` | `#C3DBF4` | `#2A4A6B` | `#0E4A7B` | `#CFD9F1` | `#1E2A47` | `#1A323D` | Toolbar and subtle buttons while pressed |
| `Shell.Toolbar.Checked` | `#FFEFBB` | `#D5E8FA` | `#1E3D5C` | `#0E4A7B` | `#D9E3F8` | `#22345F` | `#6CD2FF` | A checked toolbar toggle |
| `Shell.Toolbar.Checked.Border` | `#E5C365` | `#007ACC` | `#3794FF` | `#1AEBFF` | `#2B59C3` | `#6E98FF` | `#6CD2FF` | Its border |
| `Shell.Toolbar.Checked.Foreground` | `#1E1E1E` | `#1E1E1E` | `#F1F1F1` | `#FFFFFF` | `#0E1735` | `#F1F4FB` | `#000000` | Its text |
| `Shell.Input.Border` | `#8E9BBC` | `#BDBFCC` | `#4A4A50` | `#FFFFFF` | `#AEBBD9` | `#34416A` | `#FFFFFF` | Text box and combo box border |
| `Shell.Input.Border.Hover` | `#5B6E99` | `#8D90A0` | `#6A6A72` | `#F38518` | `#7F90BC` | `#4C5D8E` | `#6CD2FF` | Its border under the pointer |
| `Shell.Input.Placeholder` | `#4D5A73` | `#646464` | `#A6A6A6` | `#FFFFFF` | `#4E5873` | `#9AA5C2` | `#D6D6D6` | Placeholder text (the muted text at full opacity) |
| `Shell.Disabled.Foreground` | `#7D8496` | `#8A8A8A` | `#7A7A7A` | `#A0A0A0` | `#7F889F` | `#6F7993` | `#9A9A9A` | Text of a disabled control (exempt from AA; about 3:1) |
| `Shell.Disabled` | `#E9ECF4` | `#EDEDEF` | `#2A2A2B` | `#000000` | `#EBEFF8` | `#161C2C` | `#000000` | Fill of a disabled button or field |
| `Shell.Disabled.Border` | `#C3CADB` | `#D7D8DF` | `#38383C` | `#7A7A7A` | `#D2DAEC` | `#262F48` | `#6E6E6E` | Its border |
| `Shell.Check.Border` | `#5B6E99` | `#6E7080` | `#9A9AA2` | `#FFFFFF` | `#5D6A8C` | `#8A96B6` | `#FFFFFF` | Unchecked check box, radio button and switch outline (3:1) |
| `Shell.Segment.Selected` | `#0E70C0` | `#007ACC` | `#3794FF` | `#1AEBFF` | `#2B59C3` | `#6E98FF` | `#6CD2FF` | The selected segment of a segmented control |
| `Shell.Segment.Selected.Foreground` | `#FFFFFF` | `#FFFFFF` | `#000000` | `#000000` | `#FFFFFF` | `#0A1124` | `#000000` | Its text |
| `Shell.Selection.Inactive` | `#DDE2EF` | `#E4E6EE` | `#323236` | `#1F1F1F` | `#E3E8F5` | `#232D45` | `#1A323D` | Selected row of a list without the focus |
| `Shell.Selection.Inactive.Foreground` | `#1E1E1E` | `#1E1E1E` | `#F1F1F1` | `#FFFFFF` | `#0E1735` | `#E3E8F5` | `#FFFFFF` | Its text |
| `Shell.Row.Hover` | `#FEF7D9` | `#E8F1FB` | `#2A2D2E` | `#000000` | `#EBF0FA` | `#1E2740` | `#1A323D` | A list or grid row under the pointer (quieter than a control's hover, so colored text stays AA; High contrast adds a dashed outline) |
| `Shell.Grid.Header` | `#EAEDF5` | `#F0F0F3` | `#2A2A2C` | `#000000` | `#EEF1F9` | `#1C2436` | `#000000` | Column headers of tables and grouped lists |
| `Shell.Grid.Rule` | `#E2E6F0` | `#EBEBEF` | `#2E2E31` | `#3A3A3A` | `#E8ECF6` | `#1F2840` | `#5C5C5C` | Row separators |
| `Shell.Match.Background` | `#FFE58F` | `#FFE58F` | `#653A0B` | `#000000` | `#FFE7A3` | `#5A4410` | `#000000` | A find match in result text (High contrast: an outline) |
| `Shell.Match.Background.Foreground` | `#1E1E1E` | `#1E1E1E` | `#FFFFFF` | `#1AEBFF` | `#0E1735` | `#FFF4D6` | `#FFE66D` | Text on it |
| `Shell.Badge` | `#DDE2EF` | `#E4E6EE` | `#3F3F46` | `#000000` | `#E1E7F5` | `#273350` | `#000000` | Count badges (matches per file, headers, unread) |
| `Shell.Badge.Foreground` | `#1E1E1E` | `#1E1E1E` | `#F1F1F1` | `#FFFFFF` | `#1B2645` | `#E3E8F5` | `#FFFFFF` | Their text |
| `Shell.Status.Success` | `#E3F2E6` | `#E3F2E6` | `#1F3A28` | `#000000` | `#E2F1E6` | `#173326` | `#000000` | Success chip (HTTP 2xx, passed) |
| `Shell.Status.Success.Foreground` | `#116329` | `#116329` | `#8FDBA8` | `#00FF00` | `#116329` | `#8FDBA8` | `#00FF00` | Its text |
| `Shell.Status.Error` | `#FBE7E3` | `#FBE7E3` | `#4A2420` | `#000000` | `#FBE6E2` | `#3E2025` | `#000000` | Error chip (HTTP 4xx and 5xx, a SOAP fault) |
| `Shell.Status.Error.Foreground` | `#9A220B` | `#9A220B` | `#F8A08F` | `#F48771` | `#9A220B` | `#F8A08F` | `#F48771` | Its text |
| `Shell.Status.Warning` | `#FFF2CC` | `#FFF2CC` | `#3D3412` | `#000000` | `#FDF0CF` | `#352E13` | `#000000` | Warning chip (HTTP 3xx) |
| `Shell.Status.Warning.Foreground` | `#6E4B00` | `#6E4B00` | `#E5C24A` | `#FFD700` | `#6B4900` | `#E5C24A` | `#FFD700` | Its text |
| `Shell.Status.Info` | `#E1ECF8` | `#E1EEF9` | `#1B3550` | `#000000` | `#E3EAFA` | `#1B2A4D` | `#000000` | Neutral chip (a method, a kind) |
| `Shell.Status.Info.Foreground` | `#0054A0` | `#005A9E` | `#8CC8FF` | `#75BEFF` | `#1D49B0` | `#A9C6FF` | `#7CC7FF` | Its text |
| `Shell.Dialog.Footer` | `#EEF0F6` | `#F2F2F5` | `#2A2A2C` | `#000000` | `#EBEFF8` | `#161D2C` | `#000000` | The button row of a dialog |
| `Shell.Dialog.Scrim` | `rgba(31,45,84,.45)` | `rgba(30,30,30,.38)` | `rgba(0,0,0,.55)` | `rgba(0,0,0,.7)` | `rgba(27,38,69,.42)` | `rgba(5,8,16,.6)` | `rgba(0,0,0,.72)` | The window behind an in-window dialog |
| `Shell.Chat.User` | `#DCE6F6` | `#E1EEF9` | `#1E3A5C` | `#000000` | `#E1E9FA` | `#21345F` | `#000000` | Your messages in AI Chat (High contrast: outlined) |
| `Shell.Chat.User.Foreground` | `#1E1E1E` | `#1E1E1E` | `#F1F1F1` | `#FFFFFF` | `#0E1735` | `#F1F4FB` | `#FFFFFF` | Their text |
| `Shell.CodeBlock.Header` | `#EEF0F6` | `#F2F2F5` | `#2A2A2C` | `#000000` | `#EEF1F9` | `#1C2436` | `#000000` | The header row of a code block (language, Copy, Insert at Cursor, Apply…) |

Classic's values keep its look: Blue's yellow hover and border for buttons and toolbars, Light's and Dark's blues. As built (M34h), the shell's own control themes for `Button`, `ToggleButton`, `DropDownButton`, `CheckBox`, `RadioButton` and `ToggleSwitch` (`Theming/Controls.axaml`, in the application's resources, so they replace Fluent's) draw these tokens, and `ShellStyles.axaml` restyles Fluent's text boxes, combo boxes, split buttons, tabs and rows with them, so a plain `Button` gets the look without a class. That replaced the plan of setting Fluent's keys (`ButtonBackground`…) in each of the sixteen themes: one place per state instead of sixteen.

#### Graphite, Daylight and Ember

Mixed from each variant's own colors (its window, panel, text, muted text, border, hover, selection, accent, input, link and overlay) by the rules that give Harbor its values, then checked against the contrast pairs:

- **Light:** buttons white with the border 10 % toward the text, their hover 60 % of the hover color, pressed 30 % toward the selection, its borders toward the accent; the accent's hover and pressed 12 % and 25 % darker; the toolbar's hover the hover color, checked a 15 % tint of the accent with the accent's edge; quiet surfaces (an inactive selection, badges, grid headers and rules, disabled fills, the dialog's footer) 6–18 % toward the muted text, so they show on Daylight's white too; a row's hover 80 % of the hover color; chips Harbor's tints, information a 12 % tint of the accent with the link color made AA on it; the scrim the text color at 42 %.
- **Dark:** buttons halfway between the panel and the hover color, the text 20 % toward white, the border 12 % toward the accent, pressed toward the window; the accent's hover 20 % lighter and pressed 10 % darker; checked and your chat messages the selection 30 % toward the panel; quiet surfaces toward the hover color and the border; matches and chips Harbor's dark tints; the scrim the window darkened, at 60 %.
- **High contrast:** black controls with white borders, the family's hover and accent for hover and focus, a checked toolbar toggle filled with the accent, Harbor's chip colors on black, the scrim at 72 %.
- **Corners** (`Shell.Control.CornerRadius`): Graphite 3 px, Daylight and Ember 6 px (their rounded tabs and cards); a check box's corner (`Shell.Check.CornerRadius`) stays at 3 and 4 px, so it doesn't read as a radio button.

| Token | Graphite Light | Graphite Dark | Graphite HC | Daylight Light | Daylight Dark | Daylight HC | Ember Light | Ember Dark | Ember HC |
|---|---|---|---|---|---|---|---|---|---|
| `Shell.Button` | `#FFFFFF` | `#202C2F` | `#000000` | `#FFFFFF` | `#2D283A` | `#000000` | `#FFFFFF` | `#2A2119` | `#000000` |
| `Shell.Button.Foreground` | `#1F2226` | `#DDE0E5` | `#FFFFFF` | `#1F1F24` | `#E9E8F0` | `#FFFFFF` | `#2A211B` | `#EBE5E0` | `#FFFFFF` |
| `Shell.Button.Border` | `#C1C4C9` | `#2A3D41` | `#FFFFFF` | `#CDCDD3` | `#423C56` | `#FFFFFF` | `#CBC3BB` | `#413327` | `#FFFFFF` |
| `Shell.Button.Hover` | `#ECF3F3` | `#213839` | `#0F3A32` | `#F6F4FB` | `#38314B` | `#322B3D` | `#F8F2EC` | `#3A2A1C` | `#3D2B11` |
| `Shell.Button.Hover.Border` | `#9BAEB0` | `#2A5C5B` | `#3FF2D2` | `#B2ABC4` | `#5D5082` | `#D2B3FF` | `#BDA896` | `#6A4A2C` | `#FFB547` |
| `Shell.Button.Pressed` | `#DAECEA` | `#1B2326` | `#0F3A32` | `#EFEBF9` | `#262330` | `#322B3D` | `#F5E7DA` | `#1F1914` | `#3D2B11` |
| `Shell.Button.Pressed.Border` | `#759294` | `#2BB3A3` | `#3FF2D2` | `#9288AD` | `#A98BFF` | `#D2B3FF` | `#A78A72` | `#E08A3C` | `#FFB547` |
| `Shell.Accent.Hover` | `#0D6D64` | `#55C2B5` | `#8CF7E4` | `#5E37AC` | `#BAA2FF` | `#E4D1FF` | `#A24F10` | `#E6A163` | `#FFD391` |
| `Shell.Accent.Pressed` | `#0B5D56` | `#27A193` | `#3FF2D2` | `#502F93` | `#987DE6` | `#D2B3FF` | `#8A440E` | `#CA7C36` | `#FFB547` |
| `Shell.Toolbar.Hover` | `#E0EBEB` | `#213839` | `#0F3A32` | `#F0ECF9` | `#38314B` | `#322B3D` | `#F4E9E0` | `#3A2A1C` | `#3D2B11` |
| `Shell.Toolbar.Hover.Border` | `#BBCBCE` | `#224A49` | `#3FF2D2` | `#D2CDE2` | `#493E66` | `#D2B3FF` | `#D9C6B7` | `#533821` | `#FFB547` |
| `Shell.Toolbar.Pressed` | `#D5ECE9` | `#1D2E2F` | `#0F3A32` | `#EFEAFA` | `#302A3F` | `#322B3D` | `#F6E4D3` | `#2D2218` | `#3D2B11` |
| `Shell.Toolbar.Checked` | `#D4E5E4` | `#1F3332` | `#3FF2D2` | `#E9E2F6` | `#332A4E` | `#D2B3FF` | `#F1E1D5` | `#30251A` | `#FFB547` |
| `Shell.Toolbar.Checked.Border` | `#0F7C72` | `#2BB3A3` | `#3FF2D2` | `#6B3FC4` | `#A98BFF` | `#D2B3FF` | `#B85A12` | `#E08A3C` | `#FFB547` |
| `Shell.Toolbar.Checked.Foreground` | `#062E2A` | `#E8F4F2` | `#000000` | `#2A1660` | `#F1EBFF` | `#000000` | `#4A2508` | `#F7E7D6` | `#000000` |
| `Shell.Input.Border` | `#C1C4C9` | `#2A3D41` | `#FFFFFF` | `#CDCDD3` | `#423C56` | `#FFFFFF` | `#CBC3BB` | `#413327` | `#FFFFFF` |
| `Shell.Input.Border.Hover` | `#94979C` | `#2A5C5B` | `#3FF2D2` | `#9C9CA2` | `#5D5082` | `#D2B3FF` | `#9E968F` | `#6A4A2C` | `#FFB547` |
| `Shell.Input.Placeholder` | `#555A63` | `#9A9FAA` | `#D6D6D6` | `#5C5C66` | `#A7A3B8` | `#D6D6D6` | `#6B5E53` | `#A39A90` | `#D6D6D6` |
| `Shell.Disabled.Foreground` | `#868990` | `#6F737B` | `#9A9A9A` | `#8D8D94` | `#787586` | `#9A9A9A` | `#968C84` | `#736C65` | `#9A9A9A` |
| `Shell.Disabled` | `#EDEEEF` | `#1A1B1E` | `#000000` | `#F5F5F6` | `#1F1E26` | `#000000` | `#F2F0ED` | `#141312` | `#000000` |
| `Shell.Disabled.Border` | `#DADBDD` | `#26282E` | `#6E6E6E` | `#E2E2E3` | `#2D2A36` | `#6E6E6E` | `#E1DDD9` | `#24211E` | `#6E6E6E` |
| `Shell.Check.Border` | `#696E76` | `#888C96` | `#FFFFFF` | `#707078` | `#938FA3` | `#FFFFFF` | `#7D7168` | `#8E867E` | `#FFFFFF` |
| `Shell.Segment.Selected` | `#0F7C72` | `#2BB3A3` | `#3FF2D2` | `#6B3FC4` | `#A98BFF` | `#D2B3FF` | `#B85A12` | `#E08A3C` | `#FFB547` |
| `Shell.Segment.Selected.Foreground` | `#FFFFFF` | `#0B1F1C` | `#000000` | `#FFFFFF` | `#160A33` | `#000000` | `#FFFFFF` | `#1A0F05` | `#000000` |
| `Shell.Selection.Inactive` | `#E4E4E6` | `#203133` | `#0F3A32` | `#EBEBED` | `#312C41` | `#322B3D` | `#EAE6E3` | `#30251A` | `#3D2B11` |
| `Shell.Selection.Inactive.Foreground` | `#062E2A` | `#DDE0E5` | `#FFFFFF` | `#2A1660` | `#E9E8F0` | `#FFFFFF` | `#4A2508` | `#EBE5E0` | `#FFFFFF` |
| `Shell.Row.Hover` | `#E5EDEE` | `#202A2D` | `#0F3A32` | `#F3F0FA` | `#2B2737` | `#322B3D` | `#F5ECE5` | `#271F18` | `#3D2B11` |
| `Shell.Grid.Header` | `#EDEEEF` | `#1F2629` | `#000000` | `#F5F5F6` | `#262330` | `#000000` | `#F2F0ED` | `#201C17` | `#000000` |
| `Shell.Grid.Rule` | `#EAEAEC` | `#23262B` | `#5C5C5C` | `#F2F2F3` | `#292732` | `#5C5C5C` | `#EFEDEA` | `#211E1C` | `#5C5C5C` |
| `Shell.Match.Background` | `#FFE7A3` | `#5A4410` | `#000000` | `#FFE7A3` | `#5A4410` | `#000000` | `#FFE7A3` | `#5A4410` | `#000000` |
| `Shell.Match.Background.Foreground` | `#1F2226` | `#FFF4D6` | `#FFE66D` | `#1F1F24` | `#FFF4D6` | `#FFE66D` | `#2A211B` | `#FFF4D6` | `#FFE66D` |
| `Shell.Badge` | `#E4E4E6` | `#213839` | `#000000` | `#EBEBED` | `#38314B` | `#000000` | `#EAE6E3` | `#3A2A1C` | `#000000` |
| `Shell.Badge.Foreground` | `#1F2226` | `#DDE0E5` | `#FFFFFF` | `#1F1F24` | `#E9E8F0` | `#FFFFFF` | `#2A211B` | `#EBE5E0` | `#FFFFFF` |
| `Shell.Status.Success` | `#E2F1E6` | `#173326` | `#000000` | `#E2F1E6` | `#173326` | `#000000` | `#E2F1E6` | `#173326` | `#000000` |
| `Shell.Status.Success.Foreground` | `#116329` | `#8FDBA8` | `#00FF00` | `#116329` | `#8FDBA8` | `#00FF00` | `#116329` | `#8FDBA8` | `#00FF00` |
| `Shell.Status.Error` | `#FBE6E2` | `#3E2025` | `#000000` | `#FBE6E2` | `#3E2025` | `#000000` | `#FBE6E2` | `#3E2025` | `#000000` |
| `Shell.Status.Error.Foreground` | `#9A220B` | `#F8A08F` | `#F48771` | `#9A220B` | `#F8A08F` | `#F48771` | `#9A220B` | `#F8A08F` | `#F48771` |
| `Shell.Status.Warning` | `#FDF0CF` | `#352E13` | `#000000` | `#FDF0CF` | `#352E13` | `#000000` | `#FDF0CF` | `#352E13` | `#000000` |
| `Shell.Status.Warning.Foreground` | `#6B4900` | `#E5C24A` | `#FFD700` | `#6B4900` | `#E5C24A` | `#FFD700` | `#6B4900` | `#E5C24A` | `#FFD700` |
| `Shell.Status.Info` | `#E2EFEE` | `#213738` | `#000000` | `#EDE8F8` | `#363049` | `#000000` | `#F6EBE3` | `#38291C` | `#000000` |
| `Shell.Status.Info.Foreground` | `#0B5CAD` | `#7CC4FF` | `#7CC7FF` | `#5A2FB0` | `#C4AEFF` | `#D2B3FF` | `#A04E10` | `#F2B27A` | `#7CF0FF` |
| `Shell.Dialog.Footer` | `#EDEEEF` | `#1A1B1E` | `#000000` | `#F5F5F6` | `#1F1E26` | `#000000` | `#F2F0ED` | `#141312` | `#000000` |
| `Shell.Dialog.Scrim` | `rgba(31,34,38,.42)` | `rgba(8,8,10,.60)` | `rgba(0,0,0,.72)` | `rgba(31,31,36,.42)` | `rgba(11,11,14,.60)` | `rgba(0,0,0,.72)` | `rgba(42,33,27,.42)` | `rgba(6,6,5,.60)` | `rgba(0,0,0,.72)` |
| `Shell.Chat.User` | `#DBE8E8` | `#1F3332` | `#000000` | `#EDE8F8` | `#332A4E` | `#000000` | `#F3E6DC` | `#30251A` | `#000000` |
| `Shell.Chat.User.Foreground` | `#062E2A` | `#E8F4F2` | `#FFFFFF` | `#2A1660` | `#F1EBFF` | `#FFFFFF` | `#4A2508` | `#F7E7D6` | `#FFFFFF` |
| `Shell.CodeBlock.Header` | `#EDEEEF` | `#1F2629` | `#000000` | `#F5F5F6` | `#262330` | `#000000` | `#F2F0ED` | `#201C17` | `#000000` |


### Sizes and shapes

| Token | Value | Use |
|---|---|---|
| `Shell.Size.Control` | 28 | Buttons, text boxes and combo boxes in dialogs, documents and pages |
| `Shell.Size.ToolbarButton`, `Shell.Size.Input` | 24 (existing) | The same controls in tool windows, toolbars, form grids and dense rows |
| `Shell.Size.Button.MinWidth` | 80 | Text buttons in a dialog's or page's button row |
| `Shell.Padding.Button` | 12,0 | Inside a text button (10 before an icon) |
| `Shell.Padding.ButtonCompact` | 8,0 | Inside a 24 px text or icon + text button |
| `Shell.Space.ButtonIcon` | 6 | Between a button's icon and its text |
| `Shell.Control.CornerRadius` | Classic 2, Harbor 3, Graphite 3, Daylight 6, Ember 6 | Buttons, fields, segments; a family value (`Shell.Check.CornerRadius`: check boxes, at most 4; `Shell.Focus.CornerRadius`: the ring) |
| `Shell.Focus.Thickness`, `Shell.Focus.Offset` | 2, 1 | The focus ring, in `Shell.Focus`, 1 px outside the control |
| `Shell.Padding.Dialog` | 20,16 | Around a dialog's header and body; its footer is 20,12 on `Shell.Dialog.Footer` |
| `Shell.Size.Dialog.Width` | 520 | An in-window dialog's width; it grows with its content up to 720 |
| `Shell.Size.Segment` | 24 | A segmented control's height |
| `Shell.Size.Tab` | 28 | A tab strip inside a tool window or document |

### Classes

As built (M34h; [`ui-guidelines.md`](../ui-guidelines.md#the-control-set) shows each in use): `Button.accent`, `Button.subtle`, `Button.icon` and `Button.iconText` with `controls:ButtonIcon.Kind`, `Button.compact` (24 px), `Button.dialog` (80 px), `Button.link`, `DropDownButton` (its own chevron), `SplitButton`, `Button.toolbar`, `ToggleButton.toolbar` (checked: a tint with an accent edge), `SplitButton.toolbar`, `DropDownButton.toolbar`, `TextBox.search`, `TextBox.invalid`, `controls:SegmentedControl` with `controls:SegmentedControlItem` (a `ListBox` underneath: Tab and the arrow keys, list and item names for screen readers), `TabItem` (every tab inside a view; counts in a `Border.badge`), `ListBox.rows` (22 px rows), `Border.chip` with `.success`, `.error`, `.warning`, `.info` and `.square`, `Border.badge`, `StackPanel.emptyState`, `Border.dialogFooter` and `Border.kbd`; with AI Chat (M34h), `ToggleButton.chip` (its context toggles), `Border.composer`, `Run.code` and `Border.toolbarSpacer`.

Where the build differs from the pictures: Avalonia's borders draw no dashes, so High contrast's row under the pointer is outlined solid (`Shell.Row.Hover.Outline`, an inset shadow) and a read-only field is the panel's color with a solid border; a combo box's list marks the chosen item with the selection's colors, not yet with a check (`TODO(issue)`); a tab strip has no hairline under it yet (`TODO(issue)`); the view tabs' underline is the accent in every theme (Classic Blue's mockup draws its gold).

## The findings, and what answers them

| View | The finding (2026-10-18) | Answered in |
|---|---|---|
| All | "All buttons and inputs should have a common look and feel. Most buttons are generic/bland. Most buttons that can be icon buttons should be converted or have them as Icon + text if needed." | [The rules](#the-rules), `Controls.dc.html` |
| Configuration Manager | Generic default buttons and drop-downs with no icons | `ToolWindowToolbars.dc.html`: New… and Remove as icon buttons attached to their combo box, Make Active with an icon, a chip for the active pair, compact combo boxes in the grid |
| Containers (data and empty) | Generic default buttons and drop-downs with no icons | `ToolWindowToolbars.dc.html`: nine text buttons become a 24 px icon toolbar in three groups; the state is a dot and a word; the empty state gets an icon and Refresh |
| Exception Settings | Generic default buttons and drop-downs with no icons | `ToolWindowToolbars.dc.html`: Break on All, Break on None and Restore Defaults as icons, the search box takes the row, Add with an icon, module conditions as compact fields |
| Diagnostics: Benchmarks and Counters | Generic default buttons and drop-downs with no icons | `ToolWindowToolbars.dc.html`: Run All keeps its label, the rest are icons; the job's label moves into its combo box; In process becomes a toggle; Counters' process box and list become one combo box, and Export a split button |
| Service References, WCF page | "UI looks default, like its missing its style (buttons, inputs, dropdowns)" | `ToolWindowToolbars.dc.html`: the kind as a segmented control, Browse on its field, Discover as the drop-down Running Services, the reference's commands on the selected reference, one primary button |
| Git Worktrees | "This window menu buttons does not match the rest of the app" | `ToolWindowToolbars.dc.html`: the text links become the standard toolbar, Add… labelled, locked as a chip |
| In-window checklist dialog | "This window needs a total redesign. Looks bad in all aspects (height/buttons/spacing/title etc)" | `ChecklistDialog.dc.html`: header with icon, title, context and close; a bordered list with Select all and a count; move up and down as icon buttons; a footer with the buttons in the OS's order |
| HTTP Response | "This window looks broken, needs love and redesign to make it prettier" | `HttpResponse.dc.html`: the status as a chip with the version, time and size; the request line with a link to its `.http` file; Body, Headers and Request as tabs, Pretty or Raw; history rows with method and status chips |
| WCF Test Client | "This UI looks broken, needs redesign" | `WcfTestClient.dc.html`: one address bar, operations with their signatures, the request as a typed grid (required marks, enum combo box, list items with add and remove), Invoke as the only primary, the response tree with the fault as a chip |
| Find Results | "This results window needs work. Compare this look with the VS look." | `FindResults.dc.html`: what was searched as a header line, results grouped by folder and file with match counts, line and column columns, colorized code with the match highlighted, an icon toolbar, the summary as a status line |
| AI Chat | "The chat itself needs to be better looking, its just plain text now" | `AiChat.dc.html`: your messages as bubbles with what was sent, the assistant's turns with an avatar, its tool steps, code blocks with a header (Copy, Insert at Cursor, Apply…), and a composer with context toggles and a send button |
| Options | "Needs colour/icons maybe?" | `Options.dc.html`: the category tree with palette-colored icons, a page header, User or Workspace as a segmented control, modified settings marked with the accent and a chip, booleans as one check box |

As built (M34h, [checkpoint](../checkpoints/m34h.md#the-views-toolbars-and-options)): the views of `ToolWindowToolbars.dc.html` and Options, and the sweep of the other views' text buttons; the differences from the pictures are listed there.

As built (M34h): Find Results, the HTTP Response and the WCF Test Client follow their mockups; [the checkpoint](../checkpoints/m34h.md#the-control-sets-views-find-results-http-response-and-the-wcf-test-client) lists where they differ.

Find Results follows Visual Studio's Find Results window (grouped by file, a header row per file with its count, line and column, the match highlighted, an icon toolbar), as the finding asks; nothing in the UI says "Visual Studio".

## Contrast

Every text and background pair meets WCAG AA, 4.5:1, in Classic Blue, Light and Dark and Harbor Light and Dark, and 7:1 in both High Contrast themes; icons, focus rings and the outlines of unchecked check boxes, radio buttons and switches meet 3:1. The check was made on the pages as rendered, not on a list of pairs: `render.cjs` opens every mockup in all seven themes in Chromium, takes each visible text's color and the color under it (its ancestors' backgrounds, composited, with opacity), and fails under the minimum. It checked 83,652 pairs with none below. Disabled controls are exempt (WCAG 1.4.3) and draw about 3:1; the "Now" pictures of the current views and the dimmed window behind the dialog aren't checked. The checks found and fixed, while designing: syntax colors on a control's hover (hence `Shell.Row.Hover`), line numbers on a hovered row, orange icons on light selections, and a link on Classic High Contrast's hover (hence the outline). When the set is applied, its pairs join `tests/Codalus.Shell.Tests/TestData/contrast-pairs.txt`, so `PolishTests` keeps checking them in all sixteen themes.

## Decided

The maintainer approved these on 2026-10-18, all as proposed ([D33](../roadmap.md#decision-register)):

- **Dialog titles:** 14 px with an icon tile and a close button, instead of the guidelines' 18 px heading for dialogs, for the in-window dialog.
- **Switches:** only for AI Chat's Agent mode; Options keeps check boxes, as the classic IDE does.
- **Segmented controls** replace the `TabControl`s used as view switches (the WCF Test Client's Request and XML, its Response and XML) and Options' User and Workspace toggle buttons; tabs stay for different content.
- **Classic's corners** go to 2 px (`Shell.Control.CornerRadius`) from today's 3 px, closer to the classic IDE; Harbor keeps 3 px.
- **Classic Dark's selection** stays its gray `#3F3F46`; the classic IDE's focused selection is blue, which would be a change to an existing token.
- **Counters** merges its process id box and process list into one editable combo box (a behavior change, not only a style).
- **Find Results** gets a Tree or List segmented control in place of the List View toggle, and colorized code.

## The Windows Forms designer's menus (0.6)

`WinFormsDesignerMenus.dc.html` is step 1 of #67's part 2: the context menus, Lock Controls, the context keys and the verbs are built only once the maintainer approves it, and the decision goes into the roadmap's [decision register](../roadmap.md#decision-register). Part 1, Type Here only on tool strips, is a fix that doesn't wait for it. Questions for the maintainer, each as drawn and with what was rejected:

1. **Show Smart Tag stays**, the one entry the classic IDE's menu doesn't have: the panel's property tasks have no menu form, and the designer's verbs come first either way. It follows the primary selection and opens the menu alone when there are no verbs (a TextBox, whose smart tag has only Multiline). Rejected: dropping it for exact parity.
2. **Align, Make Same Size and Tab Order leave the surface menu**, as in the classic IDE; they stay in Format and View. Rejected: keeping them where the classic IDE's users won't look for them.
3. **A component's verbs and Show Smart Tag share the first group**, with no separator, on the surface and in the tray. Verbs keep the designer's text ("Edit Items...", "Choose images"); a disabled verb is grayed (Remove Tab with no tab pages); more than one component selected has none, as the classic IDE lists verbs for one. Rejected: a separator after the verbs, which the plan's `BuildMenuAsync` would add; hiding a disabled verb.
4. **A locked control keeps its smart tag glyph** and its Show Smart Tag: locking stops moving and resizing, not the control's tasks. Rejected: hiding the glyph while locked.
5. **Lock Controls is a check entry with no icon**, by the menus' rule ([`ui-guidelines.md`](../ui-guidelines.md): a check takes the icon column), so the yellow lock shows only on the adorner. The plan changes to match: `MenuBuilder.Entry` gives no icon to an entry whose `IsChecked` is set, checked or not, so the plan's `winforms.lockControls`→lock in `CommandIcons.s_icons` never reaches a menu. Rejected: the classic IDE's lock icon on a checked tile, a second check style for one entry.
6. **The lock adorner is a 12 px tile centered on the frame's top-left corner**, where the grab handle it replaces would be, the panel color with an accent edge like the smart tag's 12 px glyph, with Fluent's `lock_closed_16_filled` at 10 px in it (a new kind, `locked`, Yellow), close to the classic IDE's 7 × 9 px glyph, which also straddles the corner. The plan's `IconSet.GetFilled("lock")` at 12 px at the bounds' −14, −14 changes to this. It reaches 6 px past the frame: the form's sits in the surface's 16 px padding, and a strip docked at the top covers the window's edge, not the caption. Rejected: the first draft's 16 px tile 17 px outside the corner (clipped for the form, over the caption for a docked strip, over any neighbor within 17 px); the bare glyph (the form is drawn in Windows' light colors in every theme, and the dark themes' yellow, #FFCC00, is about 1.3:1 on it); the regular lock that `GetFilled("lock")` compiles (`lock_closed_16_regular`, whose outline is 0.6 px at 10 px).
7. **Commands that act only on unlocked controls stay enabled when everything selected is locked** (Align to Grid, Size to Grid, Center in Form), as in the classic IDE, and then do nothing; Bring to Front, Send to Back, Cut, Copy and Delete act on locked controls, as there. Rejected: graying them when everything selected is locked, an enablement the classic IDE doesn't have and one more context key.
8. **Align's seven entries all get icons**, in the icon palette's Blue: Fluent's `align_left`, `align_center_vertical` (Centers), `align_right`, `align_top`, `align_center_horizontal` (Middles) and `align_bottom` 16 px regular, six new kinds in `fluent-icons.txt`, `Icon.cs` and `CommandIcons.s_kindColors`, mapped from `winforms.align.lefts`…`bottoms`, beside to Grid's `grid_16_regular`. A submenu has icons on all its entries or none: Order keeps Bring to Front and Send to Back; Make Same Size, Horizontal and Vertical Spacing and Center in Form keep none in this task, TODO(issue). Rejected: to Grid's icon alone in Align (one icon in seven looks accidental); icons for all of Format now (Fluent has only 20 px candidates for sizing, spacing and centering, still to be picked).
9. **A tool strip item gets the surface menu until the classic item menu is built**: the classic IDE's Convert To, Insert, Set Image… and Edit DropDownItems… are deferred (TODO(issue), in the plan's Gaps); meanwhile an item has View Code, Select its strip, the clipboard and Properties, with Bring to Front, Send to Back, Align to Grid and Lock Controls grayed, since an item isn't a control. Rejected: no menu on an item until then.
10. **The tray's background menu is Paste, then Line Up Icons**, in the item menu's group order. Rejected: today's Line Up Icons alone.
11. **Deliberate differences from the classic IDE:** Align to Grid stays enabled (Codalus has snap lines and an 8 px grid but no SnapToGrid layout mode, which is what enables it there); the tray keeps Rename (F2); Lock Controls is left out of the tray menu: the classic IDE disables it for anything but controls, and whether its tray menu lists it at all hasn't been verified. Shortcuts are the key bindings' labels: Delete, not Del.
12. **Today's Dock text stays as it is in this task**: `RenderLayoutAdorners` draws "Dock: Fill" or "Dock: Top" in 10 px accent text inside a docked control's bottom left, so on a menu strip it sits over File, and on the form's light colors the accent is under 4.5:1 outside Classic Blue and Light and Harbor Light (Classic Dark 3.1, Harbor Dark 2.8, Classic High Contrast 1.5, Harbor High contrast 1.7; Classic Light 4.2 on the strip). A known overlap and contrast gap of the current surface, TODO(issue): leave it out on strips that take Type Here, and draw it on a panel-colored tile like the lock. Rejected: changing the layout adorners in this task.

The icons are Fluent's own paths (MIT) from the pinned `@fluentui/svg-icons` 1.1.344: the files the plan adds (`position_to_front_20_regular` and `position_to_back_20_regular`, drawn at 16 px as the shell scales a 20 px design, since 1.1.344 has no 16 px ones, and `grid_16_regular`), the ones proposed above, and the ones the shell already compiles for these menus, so the shapes are the ones that would ship.

## The Properties window (0.6)

`PropertiesWindow.dc.html` is step 1 of #68: the shell, the protocol and the designer host are built only once the maintainer approves it, and the decision goes into the roadmap's [decision register](../roadmap.md#decision-register). Questions for the maintainer, each as drawn and with what was rejected:

1. **Sub-properties are sent eagerly, two levels deep**, with the properties (Font's nine parts, Padding's five, FlatAppearance's colors). Rejected: a request per expansion now; deeper levels would need it later.
2. **Modal editors are panels over the designer surface**, placed as the M41b collection editor is ([ADR 0073](../adr/0073-windows-forms-designer.md)), in the in-window dialog's frame: the title, the property they edit as the context line, the buttons in the OS's order. The collection editor takes the same frame later. Rejected: the collection editor's plain panel; a dialog over the whole window.
3. **Chevrons rather than +/- boxes** for categories and expandable rows, in the text color (chrome glyphs), in a 16 px gutter of the categories' color (`Shell.Toolbar`) that runs down the rows. Rejected: the classic IDE's boxes; rows without the gutter.
4. **Categorized and Alphabetical for every source**, the Solution Explorer's files and projects and plugins' rows included. Alphabetical keeps today's sort, which ignores a leading "(", so (DataBindings) sorts under D and (Name) under N. Rejected: the buttons only for a designer; the parenthesized names first, as the classic IDE sorts them.
5. **The component selector** replaces the title for a designer: the form first, then its components by name, each with its type muted. Rejected: the title text alone.
6. **(DataBindings) is an expandable row in Data**, as in the classic IDE, with (Advanced) left out (it opens a Windows Forms dialog). Rejected: today's flat "(DataBindings)" category.
7. **Property Pages is enabled only for project and solution nodes** (Project Properties, Configuration Manager); grayed for files, folders and designer components. Rejected: enabled everywhere.
8. **Editors that are Windows Forms dialogs (Mask, a grid's cell styles…) get no "…"**, only the inline text, and the description says why. Rejected: a "…" that only says the editor is unavailable.
9. **Modifiers, GenerateMember and Locked are deferred** (TODO(issue)); Design shows (Name) only. Rejected: building them in this task.
10. **The arrow and the "…" show on the selected row and under the pointer**, as in the classic IDE; F4 and Alt+Down open the drop-down. Rejected: a button on every row.
11. **Set values are bold in the value cell**, as the classic IDE marks them, and read-only values are muted. Rejected: today's bold names.
12. **Rows of 22 px** (`Shell.Size.Row`) with `Shell.Grid.Rule` lines, a line between names and values that drags the 45 % split, and a 5 px splitter above a 76 px description (a title and three lines). Rejected: today's 25 px rows with `Shell.Border` lines.
13. **Cursor is in Appearance**, where `Control.Cursor`'s category puts it (the plan's text had Behavior, whose choice lists are ContextMenuStrip and DialogResult). Rejected: overriding the host's category.
14. **The invalid-value dialog has the error tile and the property as its context** (`DialogRequest.Icon = "error"`, `Context = "placeOrderButton · Location.X"`) and the converter's message; the cell keeps what was typed until OK, then shows the old value. Rejected: the plan's plain request, and putting the value back before the dialog shows.

## Git Changes (0.6)

`GitChanges.dc.html` is step 2 of #23, the maintainer's follow-up after M34h: "Git panel folder actions + branch dropdown with context menu". The Git Changes window's folders get the menu a classic IDE has (Stage, Unstage, Discard, Stash, Add to .gitignore, View History, Reveal, Open in Terminal, Copy Path, Copy Relative Path, Expand and Collapse All), and its branch button becomes a drop-down that lists the branches, each with its own menu (Checkout, New Local Branch From…, Merge, Rebase, Push, Rename…, Delete, Compare with Current Branch, View History, Copy Branch Name). The titles, icons and keys are the ones the plan registers and `CommandIcons` and the key bindings give (Reveal in File Explorer, Finder or File Manager, as `explorer.reveal` names it). Nothing is built until the maintainer approves it, and the decision goes into the roadmap's [decision register](../roadmap.md#decision-register) as D38.

**The spike** (plan step 1) comes before the approval: a context menu opened inside the branch flyout, checked headless through Avalonia's input pipeline and with real clicks on Linux, macOS and Windows. Its results per OS go here. Not run yet; if it fails on any OS, the design changes before D38 to one popup: right-click or ⋯ swaps the flyout's list for that branch's commands, and Back returns to the list.

The rules it follows:

- **Entries that don't apply are hidden, never grayed**, and the row or a banner says why: the worktree chip, the merge banner, "No commits yet". The menus are built from the registered locations `gitChanges/context` (a file or folder: its path, its section and `shownOnly`) and `gitChanges/branch` (a branch: its full ref), with keys scoped to that menu only.
- **Folder actions from the menu act on the files the window shows**: all of them with no filter, only the shown ones with a filter. A command called with no third argument acts on the whole folder.
- **Discard and Delete always confirm**, with Enter on Cancel and the action's button named for it; Discard lists exactly the files it acts on and how many untracked files it deletes, and Delete names what git compares with when the branch isn't merged.

Questions for the maintainer, each as drawn and with what was rejected:

1. **Discard and Delete keep Enter on Cancel**, which carries the accent as the dialog's default; Discard and Delete are plain buttons named for the action, first on Windows and Linux, last on macOS, as Drop Stash and Reset are today. Rejected: the accent on Discard or Delete while Enter stays on Cancel (`DialogView` draws the default as its primary); a red button, a new token.
2. **Menu folder actions act on the files shown**; the confirmation counts the ones the filter hides. Rejected: the whole folder from the menu too.
3. **Hidden, with the reason on the row or a banner.** Rejected: grayed entries with a reason (a menu rule can only hide; per-rule enablement would be a plugin-manifest change).
4. **Branch names stay whole**, one row each, the current branch first, then by name; remote branches under their remote's header without the prefix, those with a local branch included. Rejected: folders for each "/", as the classic IDE nests them; hiding remote branches that have a local one, as the Switch Branch quick pick does.
5. **Titles name the branches**, as the classic IDE's do ("Merge 'feature/sign-in' into 'main'", "Rebase 'main' onto 'feature/sign-in'"), HEAD when detached, and Compare with HEAD then. Rejected: static titles.
6. **A ⋯ button** on the row under the pointer or selected opens the same menu as right-click, Shift+F10 and the Menu key. Rejected: no button.
7. **Checkout of a remote branch that has a local branch switches to the local one**; without one it creates a local branch that tracks it. Rejected: offering to reset the local branch.
8. **Deleting the upstream is offered once, in the first confirmation**, as an unchecked check box whenever the branch has an upstream; the second dialog, for an unmerged branch, only warns. Rejected: the plan's choice in the second dialog only, which a merged branch never sees; `DialogView`'s checklist (Select all, 0 of 1) for one choice, so a single choice needs its own look.
9. **Compare is tip to tip, from the current branch to the other**: a file new on feature/sign-in is A, and its diff shows main on the left. Rejected: from the merge base; the plan's HEAD on the right, which marks the branch's new files D.
10. **Two entries get icons the code doesn't give them**: Add to .gitignore an eye-off in blue (`explorer.addToGitignore` has none, so it would be the menu's only entry without one; the Solution Explorer's Git entry would take it too), and Rebase the ⋯ menu's arrow-sort in Git orange (`git.rebase` has no `CommandIcons` entry). Rejected: no icons.
11. **A repository with no commits shows only Copy Branch Name**: the plan's rules would also show New Local Branch From…, Rename… and View History, so they need a key for an unborn HEAD (`gitHeadUnborn`). Rejected: showing them and letting git fail.
12. **Copy Path stays on a folder that's no longer on disk**; only Reveal and Open in Terminal need it to exist. Rejected: hiding Copy Path with them, as the plan's rule (`gitChangeExists` on all three) would.
13. **View History keeps `git.history`'s shortcut label** (Ctrl+0 Ctrl+H), as `MenuBuilder` shows a command's binding, though the shortcut opens the repository's history and not the branch's; the Delete key reads "Delete", not "Del". Rejected: no label on entries that pass arguments (a `MenuBuilder` change).
14. **The flyout is 340 px, right-aligned to the button**, over the editor at 280 px; the button stays today's 28 px, with the branch icon in Git orange. Rejected: a 24 px compact button; a flyout as wide as the pane.
15. **A folder row shows "2 of 5 files" only while a filter hides some**; every folder's accessible name has its count. Rejected: a count on every folder row.

The new 16 px drawings (merge, compare, rename, stash, upload, mention, expand-all, eye-off) are in the page's own sprite, as the designer menus' and the Properties window's are, so the other mockups don't change; discard, subtract, sync, pull-request and arrow-up-down are the shell's own stroke drawings (`Controls/Icon.cs`).

## NuGet's verified owner (0.6)

`PackageManager.dc.html` is the design step of #78's item 4. nuget.org's search already says when a package's ID prefix is reserved for its owner (its `verified` field, which NuGet.Protocol reads as `PrefixReserved`); the build host will pass it on as `PackageSummary.Verified` only for results from https://api.nuget.org, and the package manager will show a badge after the ID. The protocol and host parts don't wait for this; the badge's UI is built only once the maintainer approves the design, and the decision goes into the roadmap's [decision register](../roadmap.md#decision-register) as D39.

Questions for the maintainer, each as drawn and with what was rejected:

1. **The glyph is Fluent's `checkmark_starburst_16_filled`**, the kind `verified` in `Icon.cs` and `fluent-icons.txt`: a badge with a check, a shape nothing else in the shell uses, filled because it is a mark (as `play`, `stop` and `breakpoint` are). Rejected: `shield_checkmark` (already the shell's `shield`, Check Dev Certificate), `checkmark_circle` (reads as installed or passed, beside the installed badge's check), the regular starburst (its outline and check blur at 14 px), a "verified" chip (wider, and it competes with the version badges).
2. **14 px, 4 px after the ID**, centered on the 12 px ID's line, in the row and in the details' 18 px heading alike. Rejected: 12 px (`Shell.Size.IconSmall`; the check closes up), 16 px (taller than the ID it follows); 16 px in the heading only.
3. **The icon palette's Blue** (`["verified"] = Blue` in `CommandIcons.s_kindColors`), on a selected row too, where it keeps 3:1 in every theme; in High contrast the text's color, so black on Harbor's selection. Rejected: the accent (the update badge's fill, and the color of what can be clicked); green (installed, passed).
4. **Right after the ID, before the byline**: the ID and the badge share a panel docked left, so a long ID trims and keeps its badge and the byline gives way first. Rejected: before the ID (the IDs stop lining up); on the initial's tile (its five colors); in the versions column (it would read as a version's state).
5. **In the details, after the heading, on its first line**: a wrapped ID keeps the badge at the end of its first line (docked right and top, with a 5 px top margin that centers it on the heading's 24 px line). Rejected: after the last character (an `InlineUIContainer` in the heading's inlines instead of its `Text`); a "Verified owner" row with the metadata.
6. **The tooltip "Verified owner: nuget.org reserved this ID prefix for the package's owner"** on both badges; the icon's automation name is "verified owner" and the row's accessible name ends ", verified owner". Rejected: no tooltip (the shape alone doesn't say who verified what); a link to nuget.org's prefix reservation page (a tooltip can't hold one).
7. **Only from nuget.org**: an https source whose host is exactly `api.nuget.org`. Mirrors, upstreams such as Azure Artifacts, private feeds and local folders never show it, even when they send `verified: true`. Rejected: any source's `verified` (a private feed could badge `Microsoft.*`); a per-source option to trust its verified owners.
8. **Browse only**: Installed, Updates and Consolidate show none, nor the details opened from them, because the installed data has no such field. Rejected: remembering the mark from an earlier search (stale, and only for packages searched for); a nuget.org request per installed package.
9. **In All sources the copy that wins decides**: the highest version's copy is kept, so a newer copy from another source or a local folder shows none, and the details' Source names it. Rejected: the badge when any source's copy is verified (the package installed would be the unverified one).
