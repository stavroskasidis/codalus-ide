using System.Globalization;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell;

/// <summary>Editor settings, commands, key bindings and menus (ADR 0005).</summary>
internal static partial class BuiltIns
{
    /// <summary>Editor key bindings; they apply while an editor has focus (<c>editorTextFocus</c>).</summary>
    public const string EditorKeybindings = """
        [
          { "key": "ctrl+space", "command": "editor.action.triggerSuggest", "when": "editorTextFocus" },
          { "key": "ctrl+shift+space", "command": "editor.action.triggerParameterHints", "when": "editorTextFocus" },
          { "key": "primary+k primary+i", "command": "editor.action.showHover", "when": "editorTextFocus" },
          { "key": "f12", "command": "editor.action.revealDefinition", "when": "editorTextFocus" },
          { "key": "primary+f12", "command": "editor.action.goToImplementation", "when": "editorTextFocus" },
          { "key": "primary+m primary+m", "command": "editor.action.toggleFold", "when": "editorTextFocus" },
          { "key": "primary+m primary+o", "command": "editor.action.foldAll", "when": "editorTextFocus" },
          { "key": "primary+m primary+l", "command": "editor.action.unfoldAll", "when": "editorTextFocus" },
          { "key": "primary+f", "command": "editor.action.find", "when": "editorTextFocus" },
          { "key": "primary+h", "mac": "cmd+alt+f", "command": "editor.action.replace", "when": "editorTextFocus" },
          { "key": "f3", "command": "editor.action.findNext", "when": "editorTextFocus" },
          { "key": "shift+f3", "command": "editor.action.findPrevious", "when": "editorTextFocus" },
          { "key": "primary+g", "mac": "ctrl+g", "command": "editor.action.gotoLine", "when": "editorTextFocus" },
          { "key": "ctrl+alt+up", "command": "editor.action.insertCursorAbove", "when": "editorTextFocus" },
          { "key": "ctrl+alt+down", "command": "editor.action.insertCursorBelow", "when": "editorTextFocus" },
          { "key": "shift+alt+.", "command": "editor.action.addSelectionToNextFindMatch", "when": "editorTextFocus" },
          { "key": "shift+alt+;", "command": "editor.action.selectHighlights", "when": "editorTextFocus" },
          { "key": "primary+shift+s", "command": "file.saveAll" },
          { "key": "ctrl+-", "mac": "ctrl+-", "command": "workbench.navigateBack" },
          { "key": "ctrl+shift+-", "mac": "ctrl+shift+-", "command": "workbench.navigateForward" },
          { "key": "ctrl+tab", "command": "workbench.nextDocument", "when": "activeDocumentKind" },
          { "key": "ctrl+shift+tab", "command": "workbench.previousDocument", "when": "activeDocumentKind" },
          { "key": "primary+\\", "command": "workbench.splitEditorRight", "when": "activeDocumentKind == textFile" },
          { "key": "primary+k primary+\\", "command": "workbench.splitEditorDown", "when": "activeDocumentKind == textFile" },
          { "key": "primary+\\ primary+e", "command": "workbench.view.errorList" },
          { "key": "shift+f12", "command": "editor.action.referenceSearch", "when": "editorTextFocus" },
          { "key": "f2", "command": "editor.action.rename", "when": "editorTextFocus" },
          { "key": "primary+r primary+r", "command": "editor.action.rename", "when": "editorTextFocus" },
          { "key": "primary+.", "command": "editor.action.quickFix", "when": "editorTextFocus" },
          { "key": "alt+enter", "command": "editor.action.quickFix", "when": "editorTextFocus" },
          { "key": "primary+k primary+d", "command": "editor.action.formatDocument", "when": "editorTextFocus" },
          { "key": "primary+k primary+c", "command": "editor.action.addCommentLine", "when": "editorTextFocus" },
          { "key": "primary+k v", "command": "markdown.showPreviewToSide", "when": "activeDocumentKind == textFile" },
          { "key": "primary+k primary+u", "command": "editor.action.removeCommentLine", "when": "editorTextFocus" },
          { "key": "primary+/", "command": "editor.action.commentLine", "when": "editorTextFocus" },
          { "key": "primary+alt+space", "command": "editor.action.triggerWordCompletion", "when": "editorTextFocus" },
          { "key": "shift+alt+f", "command": "editor.action.formatDocument", "when": "editorTextFocus" },
          { "key": "primary+k primary+f", "command": "editor.action.formatSelection", "when": "editorTextFocus" },
          { "key": "primary+t", "command": "workbench.action.gotoAll" },
          { "key": "alt+home", "command": "editor.action.goToBase", "when": "editorTextFocus" },
          { "key": "primary+k primary+x", "command": "editor.action.insertSnippet", "when": "editorTextFocus" },
          { "key": "primary+d", "command": "editor.action.duplicateSelection", "when": "editorTextFocus" },
          { "key": "primary+k primary+e", "command": "editor.action.codeCleanup", "when": "editorTextFocus" },
          { "key": "primary+k primary+k", "command": "bookmarks.toggle", "when": "editorTextFocus" },
          { "key": "primary+k primary+n", "command": "bookmarks.next" },
          { "key": "primary+k primary+p", "command": "bookmarks.previous" },
          { "key": "primary+k primary+l", "command": "bookmarks.clearAll" },
          { "key": "alt+up", "command": "editor.action.moveLinesUpAction", "when": "editorTextFocus" },
          { "key": "alt+down", "command": "editor.action.moveLinesDownAction", "when": "editorTextFocus" },
          { "key": "shift+alt+=", "command": "editor.action.smartSelect.expand", "when": "editorTextFocus" },
          { "key": "shift+alt+-", "command": "editor.action.smartSelect.shrink", "when": "editorTextFocus" },
          { "key": "primary+shift+v", "command": "editor.action.pasteFromClipboardRing", "when": "editorTextFocus" },
          { "key": "primary+shift+.", "command": "editor.action.fontZoomIn", "when": "editorTextFocus" },
          { "key": "primary+shift+,", "command": "editor.action.fontZoomOut", "when": "editorTextFocus" },
          { "key": "primary+k primary+shift+s", "command": "editor.action.surroundWithSnippet", "when": "editorTextFocus" },
          { "key": "alt+f12", "command": "editor.action.peekDefinition", "when": "editorTextFocus" },
          { "key": "shift+alt+h", "command": "editor.action.showCallHierarchy", "when": "editorTextFocus" },
          { "key": "shift+f1", "command": "editor.action.showHelp", "when": "editorTextFocus" },
          { "key": "f8", "command": "editor.action.marker.next", "when": "editorTextFocus" },
          { "key": "shift+f8", "command": "editor.action.marker.prev", "when": "editorTextFocus" },
          { "key": "primary+shift+backspace", "command": "workbench.action.navigateToLastEditLocation" },
          { "key": "primary+shift+f", "command": "workbench.action.findInFiles" }
        ]
        """;

    public static IReadOnlyList<SettingDescriptor> EditorSettings()
    {
        var shell = ContributionSource.Shell;
        SettingDescriptor Setting(string key, string json, SettingType type, string title, string description, int order) =>
            SettingDescriptor.Create(key, json, type, shell) with { Title = title, Category = "Text Editor", Description = description, Order = order };
        return
        [
            Setting(EditorOptions.TabSizeKey, "4", SettingType.Integer, "Tab Size", "Columns per indentation level. An .editorconfig file overrides it.", 3) with { Minimum = 1, Maximum = 16 },
            Setting(EditorOptions.InsertSpacesKey, "true", SettingType.Boolean, "Insert Spaces", "Indent with spaces instead of tabs. An .editorconfig file overrides it.", 4),
            Setting(EditorOptions.WordWrapKey, "false", SettingType.Boolean, "Word Wrap", "Wrap long lines in the editor.", 5),
            Setting(EditorOptions.MinimapKey, "true", SettingType.Boolean, "Minimap", "Show a miniature of the document beside the editor.", 6),
            Setting(EditorOptions.LineNumbersKey, "true", SettingType.Boolean, "Line Numbers", "Show line numbers.", 7),
            Setting(EditorOptions.RenderWhitespaceKey, "\"none\"", SettingType.String, "Render Whitespace", "Show spaces and tabs.", 8) with { Enum = ["none", "all"], EnumDescriptions = ["Hidden", "Visible"] },
            Setting(EditorOptions.RulersKey, "[]", SettingType.StringArray, "Rulers", "Columns at which to draw vertical rulers, e.g. 120.", 9),
            Setting(EditorOptions.HighlightCurrentLineKey, "true", SettingType.Boolean, "Highlight Current Line", "Highlight the line with the caret.", 10),
            Setting(EditorOptions.FormatOnTypeKey, "true", SettingType.Boolean, "Format on Type", "Format the statement or block after typing ; or }.", 11),
            SettingDescriptor.Create(ShellHost.SchemaDownloadSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
            {
                Title = "Download JSON Schemas",
                Category = "JSON",
                Description = "Download schemas from SchemaStore.org (cached for a week) for JSON and YAML files without a bundled schema. Takes effect after a restart.",
            },
            Setting(EditorOptions.AutoClosingBracketsKey, "true", SettingType.Boolean, "Auto Closing Brackets", "Insert the closing bracket or quote after typing an opening one, as the language defines them.", 12),
            Setting(EditorOptions.InlayParameterNamesKey, "true", SettingType.Boolean, "Inlay Hints: Parameter Names", "Show parameter names before literal and new arguments.", 13),
            Setting(EditorOptions.InlayTypesKey, "false", SettingType.Boolean, "Inlay Hints: Types", "Show the inferred types of var declarations and lambda parameters.", 14),
            Setting(EditorOptions.CodeLensKey, "true", SettingType.Boolean, "CodeLens", "Show reference and implementation counts at the end of declarations.", 15),
            Setting(EditorOptions.InlineDiagnosticsKey, "false", SettingType.Boolean, "Inline Diagnostics", "Show error and warning messages at the end of their line.", 16),
            Setting(EditorOptions.InheritanceMarginKey, "true", SettingType.Boolean, "Inheritance Margin", "Show glyphs for members that implement, override or are overridden, with navigation.", 17),
            Setting(EditorOptions.IndentGuidesKey, "true", SettingType.Boolean, "Indentation Guides", "Draw vertical lines at indentation levels.", 18),
            Setting(EditorOptions.BracketPairColorizationKey, "true", SettingType.Boolean, "Bracket Pair Colorization", "Color matching brackets by nesting depth.", 19),
            Setting(EditorOptions.StickyScrollKey, "true", SettingType.Boolean, "Sticky Scroll", "Keep the headers of the enclosing scopes at the top of the editor while scrolling.", 20),
            Setting(EditorOptions.OverviewRulerKey, "true", SettingType.Boolean, "Overview Ruler", "Mark errors, warnings, changes, breakpoints, find results and the caret beside the scroll bar.", 21),
            Setting(EditorOptions.NavigationBarKey, "true", SettingType.Boolean, "Navigation Bar", "Show the type and member drop-downs above the editor.", 22),
            Setting(ShellHost.FormatOnSaveSetting, "false", SettingType.Boolean, "Format on Save", "Format the document with its language service before saving.", 23),
            SettingDescriptor.Create(ShellHost.CodeCleanupFixesSetting, "[]", SettingType.StringArray, shell) with
            {
                Title = "Code Cleanup Fixes",
                Category = "Code Cleanup",
                Description = "Diagnostic ids whose fixes Code Cleanup applies (empty: unused usings, simplified names, braces, accessibility modifiers, readonly fields, new(), file-scoped namespaces). Then it sorts usings and formats.",
            },
            SettingDescriptor.Create(ShellHost.CodeCleanupOnSaveSetting, "false", SettingType.Boolean, shell) with
            {
                Title = "Run Code Cleanup on Save",
                Category = "Code Cleanup",
                Description = "Run Code Cleanup on C# files before they are saved.",
            },
            SettingDescriptor.Create(ShellHost.TaskListTokensSetting, "[\"TODO\", \"HACK\", \"UNDONE\"]", SettingType.StringArray, shell) with
            {
                Title = "Task List Tokens",
                Category = "Task List",
                Description = "Comment tokens the Task List collects (a comment starting with one of them).",
            },
            SettingDescriptor.Create(ShellHost.AutoRecoverSetting, "true", SettingType.Boolean, shell) with
            {
                Title = "AutoRecover",
                Category = "Files",
                Description = "Back up unsaved changes every 30 seconds and offer to restore them after a crash.",
            },
            SettingDescriptor.Create("navigation.sourceLink", "false", SettingType.Boolean, shell) with
            {
                Title = "Source Link",
                Category = "Navigation",
                Description = "Download the original source of library code from its Source Link URL (GitHub and the like) on Go to Definition. Embedded sources are always used; otherwise the code is decompiled.",
            },
            SettingDescriptor.Create("languageServices.analysisScope", "\"openDocuments\"", SettingType.String, shell) with
            {
                Title = "Background Analysis Scope",
                Category = "Language Services",
                Description = "Where analyzers run in the background. Compiler errors are always reported for the whole solution.",
                Enum = ["openDocuments", "solution"],
                EnumDescriptions = ["Open documents", "Entire solution"],
            },
        ];
    }

    public static void RegisterEditorCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, string category, CommandHandler handler, string? enablement = null, bool palette = true) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell)
                {
                    Category = category,
                    Enablement = WhenClause.Parse(enablement),
                    ShowInPalette = palette,
                },
                handler);

        CommandHandler OnEditor(Action<CodeEditorController> action) => Ui(() =>
        {
            if (shell.ActiveEditor?.Controller is { } controller)
            {
                action(controller);
            }
        });

        CommandHandler OnEditorAsync(Func<CodeEditorController, Task> action) => Ui(() =>
            shell.ActiveEditor?.Controller is { } controller ? action(controller) : Task.CompletedTask);

        const string Editor = "activeDocumentKind == textFile";
        Add("editor.action.triggerSuggest", "Trigger Suggest", "Editor", OnEditor(c => c.ShowCompletion()), Editor);
        Add("editor.action.triggerParameterHints", "Parameter Info", "Editor", OnEditor(c => c.ShowSignatureHelp()), Editor);
        Add("editor.action.showHover", "Quick Info", "Editor", OnEditor(c => c.ShowQuickInfoAtCaret()), Editor);
        Add("editor.action.revealDefinition", "Go to Definition", "Editor", OnEditorAsync(c => c.GoToDefinitionAsync()), Editor);
        Add("editor.action.goToImplementation", "Go to Implementation", "Editor", OnEditorAsync(c => c.GoToImplementationAsync()), Editor);
        Add("editor.action.goToDerived", "Go to Derived Types", "Editor", OnEditorAsync(c => c.GoToImplementationAsync()), Editor);
        Add("editor.action.goToBase", "Go to Base", "Editor", Ui(shell.GoToBaseAsync), Editor);
        Add("editor.action.insertSnippet", "Insert Snippet", "Snippets", Ui(shell.InsertSnippetAsync), Editor);
        Add("bookmarks.toggle", "Toggle Bookmark", "Bookmarks", Ui(shell.ToggleBookmark), Editor);
        Add("bookmarks.next", "Next Bookmark", "Bookmarks", Ui(() => shell.NextBookmarkAsync(1)));
        Add("bookmarks.previous", "Previous Bookmark", "Bookmarks", Ui(() => shell.NextBookmarkAsync(-1)));
        Add("bookmarks.clearAll", "Clear All Bookmarks", "Bookmarks", Ui(() => shell.Bookmarks.Clear()));
        Add("editor.action.codeCleanup", "Code Cleanup", "Editor", Ui(shell.CodeCleanupAsync), Editor);
        Add("editor.action.duplicateSelection", "Duplicate Selection or Line", "Editor", OnEditor(c => c.DuplicateLines()), Editor);
        Add("editor.action.moveLinesUpAction", "Move Line Up", "Editor", OnEditor(c => c.MoveLines(-1)), Editor);
        Add("editor.action.moveLinesDownAction", "Move Line Down", "Editor", OnEditor(c => c.MoveLines(1)), Editor);
        Add("editor.action.smartSelect.expand", "Expand Selection", "Editor", OnEditorAsync(c => c.ExpandSelectionAsync()), Editor);
        Add("editor.action.smartSelect.shrink", "Shrink Selection", "Editor", OnEditor(c => c.ShrinkSelection()), Editor);
        Add("editor.action.pasteFromClipboardRing", "Paste from Clipboard Ring", "Editor", Ui(shell.PasteFromClipboardRingAsync), Editor);
        Add("editor.action.pasteJsonAsClasses", "Paste JSON as Classes", "Editor", Ui(shell.PasteJsonAsClassesAsync), Editor);
        Add("editor.action.fontZoomIn", "Zoom In (Editor)", "Editor", OnEditor(c => c.Zoom *= 1.1), Editor);
        Add("editor.action.fontZoomOut", "Zoom Out (Editor)", "Editor", OnEditor(c => c.Zoom /= 1.1), Editor);
        Add("editor.action.fontZoomReset", "Reset Zoom (Editor)", "Editor", OnEditor(c => c.Zoom = 1), Editor);
        Add("workbench.action.localHistory", "Show Local History", "File", Ui(shell.ShowLocalHistoryAsync), Editor);
        Add("editor.action.surroundWithSnippet", "Surround With", "Snippets", Ui(shell.SurroundWithAsync), Editor);
        Add("workbench.action.openSnippets", "Configure User Snippets", "Snippets", Ui(shell.OpenUserSnippetsAsync));
        Add("snippets.importVisualStudio", "Import Snippets (.snippet)…", "Snippets", Ui(shell.ImportVisualStudioSnippetsAsync));
        Add("editor.action.peekDefinition", "Peek Definition", "Editor", Ui(shell.PeekDefinitionAsync), Editor);
        Add("editor.action.showCallHierarchy", "Show Call Hierarchy", "Editor", Ui(shell.ShowCallHierarchyAsync), Editor);
        Add("editor.action.showTypeHierarchy", "Show Type Hierarchy", "Editor", Ui(shell.ShowTypeHierarchyAsync), Editor);
        Add("editor.action.showHelp", "Help on Symbol (F1 Help)", "Help", Ui(shell.ShowHelpAsync), Editor);
        Add("editor.action.marker.next", "Go to Next Problem", "Editor", Ui(() => shell.GoToProblem(1)), Editor);
        Add("editor.action.marker.prev", "Go to Previous Problem", "Editor", Ui(() => shell.GoToProblem(-1)), Editor);
        Add("workbench.action.navigateToLastEditLocation", "Go to Last Edit Location", "Go", Ui(shell.GoToLastEditAsync));
        Add("editor.action.toggleFold", "Toggle Outlining Expansion", "Editor", OnEditor(c => c.ToggleFoldAtCaret()), Editor);
        Add("editor.action.foldAll", "Collapse to Definitions", "Editor", OnEditor(c => c.SetAllFolded(true)), Editor);
        Add("editor.action.unfoldAll", "Expand All Outlining", "Editor", OnEditor(c => c.SetAllFolded(false)), Editor);
        Add("editor.action.find", "Find", "Edit", OnEditor(c => OpenSearch(c, replace: false)), Editor);
        Add("editor.action.replace", "Replace", "Edit", OnEditor(c => OpenSearch(c, replace: true)), Editor);
        Add("editor.action.findNext", "Find Next", "Edit", OnEditor(c => c.Search.FindNext()), Editor);
        Add("editor.action.findPrevious", "Find Previous", "Edit", OnEditor(c => c.Search.FindPrevious()), Editor);
        Add("editor.action.gotoLine", "Go to Line…", "Edit", Ui(() => GoToLineAsync(shell)), Editor);
        Add("editor.action.insertCursorAbove", "Add Caret Above", "Edit", OnEditor(c => c.MultiCaret.AddCaretVertically(-1)), Editor);
        Add("editor.action.insertCursorBelow", "Add Caret Below", "Edit", OnEditor(c => c.MultiCaret.AddCaretVertically(+1)), Editor);
        Add("editor.action.addSelectionToNextFindMatch", "Insert Next Matching Caret", "Edit", OnEditor(c => c.MultiCaret.SelectNextOccurrence()), Editor);
        Add("editor.action.selectHighlights", "Insert Carets at All Matches", "Edit", OnEditor(c => c.MultiCaret.SelectNextOccurrence(all: true)), Editor);
        Add("editor.action.referenceSearch", "Find All References", "Editor", Ui(shell.FindReferencesAsync), Editor);
        Add("editor.action.rename", "Rename…", "Editor", Ui(shell.RenameSymbolAsync), Editor);
        Add("editor.action.quickFix", "Quick Actions and Refactorings…", "Editor", Ui(shell.ShowCodeActionsAsync), Editor);
        Add("editor.action.formatDocument", "Format Document", "Editor", Ui(() => shell.FormatAsync(selectionOnly: false)), Editor);
        Add("editor.action.commentLine", "Toggle Line Comment", "Editor", OnEditor(c => c.Comment(CommentAction.Toggle)), Editor);
        Add("markdown.showPreviewToSide", "Markdown: Open Preview to the Side", "Markdown", Ui(() => shell.ShowMarkdownPreview()), Editor);
        Add("editor.action.addCommentLine", "Comment Selection", "Editor", OnEditor(c => c.Comment(CommentAction.Comment)), Editor);
        Add("editor.action.removeCommentLine", "Uncomment Selection", "Editor", OnEditor(c => c.Comment(CommentAction.Uncomment)), Editor);
        Add("editor.action.triggerWordCompletion", "Complete Word", "Editor", OnEditor(c => c.CompleteWords()), Editor);
        Add("editor.action.formatSelection", "Format Selection", "Editor", Ui(() => shell.FormatAsync(selectionOnly: true)), Editor);
        Add("workbench.action.gotoAll", "Go To All…", "Edit", Ui(shell.GoToAllAsync));
        Add("workbench.action.findInFiles", "Find in Files", "Edit", Ui(shell.FindInFiles));
        Add("editor.action.changeEndOfLine", "Change Line Endings…", "Edit", Ui(shell.ChangeEndOfLineAsync), Editor);

        Add("file.saveAll", "Save All", "File", Ui(shell.SaveAllAsync));
        Add("workbench.navigateBack", "Navigate Backward", "View", Ui(shell.NavigateBackAsync));
        Add("workbench.navigateForward", "Navigate Forward", "View", Ui(shell.NavigateForwardAsync));
        Add("workbench.nextDocument", "Next Document", "Window", Ui(() => shell.CycleDocument(+1)), "activeDocumentKind");
        Add("workbench.previousDocument", "Previous Document", "Window", Ui(() => shell.CycleDocument(-1)), "activeDocumentKind");
        Add("workbench.keepEditor", "Keep Open (Promote Preview Tab)", "Window", Ui(() =>
        {
            if (shell.Layout.ActiveDocument is { } document)
            {
                document.IsPreview = false;
            }
        }), "activeDocumentKind");
        Add("workbench.closeAllDocuments", "Close All Documents", "Window", Ui(() => shell.CloseAllDocuments(exceptActive: false)), "activeDocumentKind");
        Add("workbench.closeOtherDocuments", "Close All But This", "Window", Ui(() => shell.CloseAllDocuments(exceptActive: true)), "activeDocumentKind");
        Add("workbench.closeDocumentsToTheRight", "Close All to the Right", "Window", Ui(shell.CloseDocumentsToTheRight), "activeDocumentKind");
        Add("workbench.compareFiles", "Compare Files…", "File", Ui(() => shell.CompareFilesAsync(withActive: false)));
        Add("workbench.compareActiveWith", "Compare Active File With…", "File", Ui(() => shell.CompareFilesAsync(withActive: true)), Editor);
        Add("analysis.syntaxVisualizer", "Syntax Visualizer", "View", Ui(shell.ShowSyntaxVisualizer));
        Add("analysis.codeMetrics.solution", "Calculate Code Metrics for Solution", "Analyze", Ui(() => shell.CalculateCodeMetricsAsync(projectOnly: false)));
        Add("analysis.codeMetrics.project", "Calculate Code Metrics for Project", "Analyze", Ui(() => shell.CalculateCodeMetricsAsync(projectOnly: true)), Editor);
        Add("workbench.splitEditorRight", "Split Editor Right", "Window", Ui(() => shell.SplitActiveEditor(below: false)), Editor);
        Add("workbench.splitEditorDown", "Split Editor Down", "Window", Ui(() => shell.SplitActiveEditor(below: true)), Editor);
        Add("workbench.toggleEditorSplit", "Split", "Window", Ui(shell.ToggleEditorSplit), Editor);
    }

    private static void OpenSearch(CodeEditorController controller, bool replace)
    {
        var selection = controller.Editor.TextArea.Selection;
        if (!selection.IsEmpty && !selection.IsMultiline)
        {
            controller.Search.SearchPattern = selection.GetText();
        }

        controller.Search.IsReplaceMode = replace;
        controller.Search.Open();
        controller.Search.Reactivate();
    }

    private static async Task GoToLineAsync(ShellHost shell)
    {
        if (shell.ActiveEditor is not { Controller: { } controller } editor)
        {
            return;
        }

        var lines = editor.Model.Text.LineCount;
        var result = await shell.Dialogs.ShowAsync(new DialogRequest("Go to Line", $"Line number (1–{lines}):", ["Go", "Cancel"])
        {
            Input = controller.Caret.Line.ToString(CultureInfo.InvariantCulture),
            Validate = text => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= lines ? null : $"Enter a number from 1 to {lines}.",
        }).ConfigureAwait(true);
        if (result.Button == "Go" && int.TryParse(result.Input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line))
        {
            controller.Navigate(new LanguageServices.Protocol.Position(line - 1, 0));
        }
    }

    public static void RegisterEditorMenus(MenuRegistry menus)
    {
        menus.AddMenuBarMenu(new MenuBarMenu("edit", "_Edit", 20));
        menus.AddMenuBarMenu(new MenuBarMenu("window", "_Window", 80));
        menus.AddMenuBarMenu(new MenuBarMenu("analyze", "_Analyze", 67));
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0) =>
            new(location, command, shell) { Group = group, Order = order };
        string Edit = MenuLocations.MenuBar("edit"), Window = MenuLocations.MenuBar("window"), File = MenuLocations.MenuBar("file");
        menus.Add(
        [
            Item(File, "file.saveAll", "save", 2),
            Item(Edit, "editor.action.find", "1-find", 1),
            Item(Edit, "editor.action.replace", "1-find", 2),
            Item(Edit, "editor.action.findNext", "1-find", 3),
            Item(Edit, "editor.action.findPrevious", "1-find", 4),
            Item(Edit, "workbench.action.findInFiles", "1-find", 5),
            Item(Edit, "workbench.action.gotoAll", "2-go", 0),
            Item(Edit, "editor.action.referenceSearch", "2-go", 6),
            Item(Edit, "editor.action.gotoLine", "2-go", 1),
            Item(Edit, "editor.action.revealDefinition", "2-go", 2),
            Item(Edit, "editor.action.goToImplementation", "2-go", 3),
            Item(Edit, "editor.action.peekDefinition", "2-go", 3),
            Item(Edit, "editor.action.goToBase", "2-go", 3),
            Item(Edit, "editor.action.showCallHierarchy", "2-go", 7),
            Item(Edit, "editor.action.showTypeHierarchy", "2-go", 8),
            Item(Edit, "workbench.action.navigateToLastEditLocation", "2-go", 9),
            Item(Edit, "editor.action.marker.next", "2-go", 10),
            Item(Edit, "editor.action.marker.prev", "2-go", 11),
            // The editor's context menu: navigation first, then refactoring (M12a).
            Item(MenuLocations.EditorContext, "editor.action.revealDefinition", "1-navigation", 1),
            Item(MenuLocations.EditorContext, "editor.action.peekDefinition", "1-navigation", 2),
            Item(MenuLocations.EditorContext, "editor.action.goToImplementation", "1-navigation", 3),
            Item(MenuLocations.EditorContext, "editor.action.goToBase", "1-navigation", 4) with { When = WhenClause.Parse("editorLanguageId == csharp") },
            Item(MenuLocations.EditorContext, "editor.action.referenceSearch", "1-navigation", 5),
            Item(MenuLocations.EditorContext, "editor.action.showCallHierarchy", "1-navigation", 6) with { When = WhenClause.Parse("editorLanguageId == csharp") },
            Item(MenuLocations.EditorContext, "editor.action.rename", "2-refactor", 1),
            Item(MenuLocations.EditorContext, "editor.action.quickFix", "2-refactor", 2),
            Item(MenuLocations.EditorContext, "editor.action.formatDocument", "2-refactor", 3),
            Item(Edit, "workbench.navigateBack", "2-go", 4),
            Item(Edit, "workbench.navigateForward", "2-go", 5),
            Item(Edit, "editor.action.triggerSuggest", "3-intellisense", 1),
            Item(Edit, "editor.action.triggerParameterHints", "3-intellisense", 2),
            Item(Edit, "editor.action.showHover", "3-intellisense", 3),
            Item(Edit, "editor.action.quickFix", "3-intellisense", 4),
            Item(Edit, "editor.action.rename", "3-intellisense", 5),
            Item(Edit, "editor.action.formatDocument", "3-intellisense", 6),
            Item(Edit, "editor.action.formatSelection", "3-intellisense", 7),
            Item(Edit, "editor.action.insertCursorAbove", "4-carets", 1),
            Item(Edit, "editor.action.insertCursorBelow", "4-carets", 2),
            Item(Edit, "editor.action.addSelectionToNextFindMatch", "4-carets", 3),
            Item(Edit, "editor.action.selectHighlights", "4-carets", 4),
            Item(Edit, "editor.action.toggleFold", "5-outlining", 1),
            Item(Edit, "editor.action.foldAll", "5-outlining", 2),
            Item(Edit, "editor.action.unfoldAll", "5-outlining", 3),
            Item(Edit, "editor.action.changeEndOfLine", "6-advanced", 1),
            Item(Edit, "editor.action.codeCleanup", "3-intellisense", 8),
            Item(Edit, "editor.action.duplicateSelection", "6-advanced", 2),
            Item(Edit, "editor.action.moveLinesUpAction", "6-advanced", 3),
            Item(Edit, "editor.action.moveLinesDownAction", "6-advanced", 4),
            Item(Edit, "editor.action.smartSelect.expand", "6-advanced", 5),
            Item(Edit, "editor.action.smartSelect.shrink", "6-advanced", 6),
            Item(Edit, "editor.action.pasteFromClipboardRing", "7-paste", 1),
            Item(Edit, "editor.action.pasteJsonAsClasses", "7-paste", 2),
            Item(Edit, "editor.action.insertSnippet", "8-snippets", 1),
            Item(Edit, "editor.action.surroundWithSnippet", "8-snippets", 2),
            Item(File, "workbench.action.localHistory", "history", 1),
            Item(File, "workbench.compareFiles", "history", 2),
            Item(File, "workbench.compareActiveWith", "history", 3),
            Item(Window, "workbench.closeDocumentsToTheRight", "3-close", 3),
            Item(MenuLocations.MenuBar("analyze"), "analysis.codeMetrics.solution", "1-metrics", 1),
            Item(MenuLocations.MenuBar("analyze"), "analysis.codeMetrics.project", "1-metrics", 2),
            Item(MenuLocations.MenuBar("analyze"), "editor.action.codeCleanup", "2-cleanup", 1),
            Item(MenuLocations.MenuBar("analyze"), "analysis.syntaxVisualizer", "3-roslyn", 1),
            Item(Window, "workbench.toggleEditorSplit", "1-split", 1),
            Item(Window, "workbench.splitEditorRight", "1-split", 2),
            Item(Window, "workbench.splitEditorDown", "1-split", 3),
            Item(Window, "workbench.keepEditor", "2-documents", 1),
            Item(Window, "workbench.nextDocument", "2-documents", 2),
            Item(Window, "workbench.previousDocument", "2-documents", 3),
            Item(Window, "workbench.closeOtherDocuments", "3-close", 1),
            Item(Window, "workbench.closeAllDocuments", "3-close", 2),
            Item(Window, "workbench.resetLayout", "4-layout", 1),
        ]);
    }
}
