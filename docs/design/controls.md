# Controls: one look for buttons, fields and rows

The control set every view uses: buttons, toolbar buttons and toggles, text, search and combo boxes, check boxes, radio buttons and switches, segmented controls, tabs inside a view, and list and grid rows, each in every state. The maintainer asked for it after reviewing the M34 gallery on 2026-10-18 ("All buttons and inputs should have a common look and feel. Most buttons are generic/bland. Most buttons that can be icon buttons should be converted or have them as Icon + text if needed."), and it is to be approved before the app changes. Nothing here is built yet: these are designs, and the views keep their current look until the set is applied (`TODO(issue)`: apply the control set, starting with the shared styles in `ShellStyles.axaml`, then the views in the findings table).

The designs are drawn in the Classic family (Blue, Light, Dark, High Contrast) and Harbor (Light, Dark, High contrast), with their real tokens ([`ui-guidelines.md`](../ui-guidelines.md#tokens), [`shell-themes.md`](shell-themes.md#harbor-colors)). Graphite, Daylight and Ember follow the same rules; their values for the new tokens are mixed from their own colors when the set is applied, as M45 did for the tokens the design left out.

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

More pictures: the control set in [Classic Blue](controls/previews/controls-classic-blue.png), [Classic High Contrast](controls/previews/controls-classic-high-contrast.png) and [Harbor High contrast](controls/previews/controls-harbor-high-contrast.png); the WCF Test Client with a fault ([Harbor Light](controls/previews/wcf-test-client-fault-harbor-light.png), [Classic Dark](controls/previews/wcf-test-client-fault-classic-dark.png)); the checklist dialog's buttons on [macOS](controls/previews/checklist-dialog-macos-harbor-light.png).

## The design files

`controls/` holds the designs as HTML mockups, the same kind as the theme families' ([`shell-themes.md`](shell-themes.md#the-design-files)): open a file in a browser. They use `../shell-themes/support.js` to render, `controls.js` for the palettes, the icon drawings and a small C# colorizer, and `controls.css` for the controls themselves, so every mockup draws the same set.

- `Controls.dc.html`: the control set in every state, the sizes, the dialog button order, and the proposed tokens with their values in the theme shown.
- `ToolWindowToolbars.dc.html`: Containers (with data and empty), Exception Settings, Benchmarks, Counters, Configuration Manager, the Service References document's WCF page and Git Worktrees, each now and proposed.
- `FindResults.dc.html`, `HttpResponse.dc.html`, `WcfTestClient.dc.html`, `ChecklistDialog.dc.html`, `AiChat.dc.html`, `Options.dc.html`: the redesigned views, with the content, commands and labels of their `.axaml` and `en.json`, and the Codalus samples' code (`samples/Wcf`, `samples/Web/WebApi`, `samples/Diagnostics/Benchmarks`).

The query string picks the theme: `Controls.dc.html?theme=Classic%20Dark`. The themes are `Classic Blue`, `Classic Light`, `Classic Dark`, `Classic High Contrast`, `Harbor Light` (the default), `Harbor Dark` and `Harbor High contrast`. `WcfTestClient.dc.html?mode=Fault` shows a fault, and `ChecklistDialog.dc.html?os=macOS` the macOS button order. The sheets link every theme at their top.

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
- Combo boxes: the field with a 12 px chevron; open, the field keeps the focus edge and the list shows the chosen item with a check.
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

Classic's values keep its look: Blue's yellow hover and border for buttons and toolbars, Light's and Dark's blues. When the set is applied, Fluent's own keys for buttons, toggle buttons and text boxes (`ButtonBackground`, `ButtonBackgroundPointerOver`, `TextControlBorderBrush`…) take these values in each theme, as M45 did for check boxes and accent buttons ([ADR 0077](../adr/0077-theme-families.md)), so a plain `Button` gets the look without a class.

### Sizes and shapes

| Token | Value | Use |
|---|---|---|
| `Shell.Size.Control` | 28 | Buttons, text boxes and combo boxes in dialogs, documents and pages |
| `Shell.Size.ToolbarButton`, `Shell.Size.Input` | 24 (existing) | The same controls in tool windows, toolbars, form grids and dense rows |
| `Shell.Size.Button.MinWidth` | 80 | Text buttons in a dialog's or page's button row |
| `Shell.Padding.Button` | 12,0 | Inside a text button (10 before an icon) |
| `Shell.Padding.ButtonCompact` | 8,0 | Inside a 24 px text or icon + text button |
| `Shell.Space.ButtonIcon` | 6 | Between a button's icon and its text |
| `Shell.Control.CornerRadius` | Classic 2, Harbor 3 | Buttons, fields, check boxes, segments; a family value (Graphite, Daylight and Ember to choose) |
| `Shell.Focus.Thickness`, `Shell.Focus.Offset` | 2, 1 | The focus ring, in `Shell.Focus`, 1 px outside the control |
| `Shell.Padding.Dialog` | 20,16 | Around a dialog's header and body; its footer is 20,12 on `Shell.Dialog.Footer` |
| `Shell.Size.Dialog.Width` | 520 | An in-window dialog's width; it grows with its content up to 720 |
| `Shell.Size.Segment` | 24 | A segmented control's height |
| `Shell.Size.Tab` | 28 | A tab strip inside a tool window or document |

### Classes

The styles become classes in `ShellStyles.axaml`, next to today's `Button.toolbar` and `Button.link`: `Button.accent` (Fluent's, restyled), `Button.subtle`, `Button.icon`, `Button.compact` (24 px), `ToggleButton.toolbar` checked, `SplitButton.toolbar`, `TextBox.search`, a segmented control (a `ListBox` or `RadioButton`s with a `segment` class; to decide when built), `TabStrip.view`, `Border.chip` with `.success`, `.error`, `.warning`, `.info`, `Border.badge`, and `Border.dialogFooter`.

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

Find Results follows Visual Studio's Find Results window (grouped by file, a header row per file with its count, line and column, the match highlighted, an icon toolbar), as the finding asks; nothing in the UI says "Visual Studio".

## Contrast

Every text and background pair meets WCAG AA, 4.5:1, in Classic Blue, Light and Dark and Harbor Light and Dark, and 7:1 in both High Contrast themes; icons, focus rings and the outlines of unchecked check boxes, radio buttons and switches meet 3:1. The check was made on the pages as rendered, not on a list of pairs: `render.cjs` opens every mockup in all seven themes in Chromium, takes each visible text's color and the color under it (its ancestors' backgrounds, composited, with opacity), and fails under the minimum. It checked 10,927 pairs with none below. Disabled controls are exempt (WCAG 1.4.3) and draw about 3:1; the "Now" pictures of the current views and the dimmed window behind the dialog aren't checked. The checks found and fixed, while designing: syntax colors on a control's hover (hence `Shell.Row.Hover`), line numbers on a hovered row, orange icons on light selections, and a link on Classic High Contrast's hover (hence the outline). When the set is applied, its pairs join `tests/Codalus.Shell.Tests/TestData/contrast-pairs.txt`, so `PolishTests` keeps checking them in all sixteen themes.

## To decide

- **Dialog titles:** 14 px with an icon tile and a close button, instead of the guidelines' 18 px heading for dialogs, for the in-window dialog.
- **Switches:** only for AI Chat's Agent mode; Options keeps check boxes, as the classic IDE does.
- **Segmented controls** replace the `TabControl`s used as view switches (the WCF Test Client's Request and XML, its Response and XML) and Options' User and Workspace toggle buttons; tabs stay for different content.
- **Classic's corners** go to 2 px (`Shell.Control.CornerRadius`) from today's 3 px, closer to the classic IDE; Harbor keeps 3 px.
- **Classic Dark's selection** stays its gray `#3F3F46`; the classic IDE's focused selection is blue, which would be a change to an existing token.
- **Counters** merges its process id box and process list into one editable combo box (a behavior change, not only a style).
- **Find Results** gets a Tree or List segmented control in place of the List View toggle, and colorized code.
