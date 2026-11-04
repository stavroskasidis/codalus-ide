# 0091. Default tool window locations: pages among the documents

- **Status:** accepted (2026-11-02)
- **Date:** 2026-11-02
- **Deciders:** maintainer (the review of every tool window's default location of 2026-11-02, and its decisions: the first batch of moves; one Plugins page instead of the Plugins window and the Plugin Gallery; saved layouts re-home the moved windows once)
- **Builds on:** [ADR 0060](0060-plugin-distribution.md) (the gallery), [ADR 0061](0061-components-and-plugin-performance.md) (Components)

## Context

A tool window opens where its descriptor's `ToolWindowLocation` says the first time it's shown, and in the group it was last in after that. The locations were left, right and bottom, and 38 of the 60 built-in windows opened in the bottom group with the Output and the Error List. Several of them are pages the user reads with the whole window (the Plugin Gallery, Components, Aspire's resources and console side by side, Benchmarks' results table) or narrow lists that belong with the right side's windows (Notifications' cards, Worktrees beside Git Changes, the Task Runner Explorer's scripts, .NET Counters beside Diagnostic Tools). The HTTP Response's body wants width and height, and the classic IDE shows it beside the `.http` file.

## Options

| Question | Options | Verdict |
|---|---|---|
| A page in the window's middle | Make each a `ShellDocument`; a tool window location among the documents | **A location**: `ToolWindowLocation.Document` (a tab of the active document group) and `DocumentBeside` (a group of its own to the right of the active one, or the other group when the user has split them). The windows stay tool windows: the View menu, their commands, their content and the user's drags work as before. Shell windows only: a plugin manifest's `location` stays `left`, `right` or `bottom`. |
| Shown without the focus | Take the group's front; join it behind | **Behind the document the user is in**, when that's the group's active tab: a run that brings up Aspire or benchmarks that finish don't take the editor away. Shown with the focus (its command, View → Other Windows), it comes to the front. |
| The active document | Whatever is the group's active tab; the last document | **The last document.** A tool window among the documents isn't one: going to it leaves `ActiveDocument` and the group files open in as they were, as going to a side's window does. |
| The Plugins window and the Plugin Gallery | Keep both; one page | **One page**, the Plugins window's id, among the documents: an Installed tab (the old window's list; Uninstall on a gallery plugin's row) and a Browse tab (the gallery: search, install, updates, the changes waiting for a restart). The Plugin Gallery's window is gone; Browse Plugins… opens the Browse tab, the slow-plugin notification's Show Plugins the Installed one. |
| Saved layouts | New layouts only; re-home the moved windows once | **Once.** `LayoutSnapshot` version 2. A version 1 layout keeps everything but the moved windows (`RehomedInVersion2`), which open in their new places when they're next shown. |

## Decision

Moved: the Plugins page (with the gallery), Components, Aspire and Benchmarks among the documents; the HTTP Response beside them; Notifications, Worktrees, the Task Runner Explorer and .NET Counters on the right. The rest stay where they were, the debugger's windows included (a bottom group split in two, as the classic IDE's, is a change of its own).

A layout saves the tool windows in each document group (a `documents` node's `tools` and `active`), so a page among the documents comes back after a restart like any other tool window; the documents themselves are still the solution session's. The group `DocumentBeside` makes isn't the documents' own (`BesideDocuments`), so a restore and Reset Layout put the documents back in theirs.

### The HTTP Response beside its file, narrow (2026-11-02, the maintainer's review of the first CI pictures)

At the default 1280 × 800 window the group beside the file is about 380 px wide. The response's toolbar drew over its status (the time and size under Send Again, the chip gone) and its history took two fifths of it.

| Question | Options | Verdict |
|---|---|---|
| The place | Beside, the view made to fit; back to the bottom group; a tab among the documents | **Beside, the view made to fit.** Narrower than 720 px the history goes under the response (at most 220 px, or two fifths of the height, its splitter across); the width the user dragged it to comes back with a wider window. A response narrower than 520 px shows its version, time and size on a line under the status chip, and the toolbar's buttons go into its overflow menu. A failed request's error has a line of its own, wrapped. |
| The website's Aspire picture | As the smoke test leaves the window; HTTP Response closed first | **Closed first**: the smoke step closes web.http's HTTP Response before Aspire's picture, so the page has the documents' width. |

## Consequences

- The website's Aspire and HTTP pictures (`aspire.png`, `web-http.png`) show the new places once a CI run's smoke pictures are copied in.
- Users who had moved one of these windows find it in its new default place once; everything else they arranged stays.
