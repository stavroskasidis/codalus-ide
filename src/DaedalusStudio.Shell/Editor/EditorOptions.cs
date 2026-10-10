using System.Globalization;
using DaedalusStudio.LanguageServices.Client;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Languages;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell.Editor;

/// <summary>The <c>editor.*</c> settings, refreshed on the UI thread when they change.</summary>
public sealed class EditorOptions : ObservableObject
{
    public const string TabSizeKey = "editor.tabSize";
    public const string InsertSpacesKey = "editor.insertSpaces";
    public const string WordWrapKey = "editor.wordWrap";
    public const string MinimapKey = "editor.minimap.enabled";
    public const string LineNumbersKey = "editor.lineNumbers";
    public const string RenderWhitespaceKey = "editor.renderWhitespace";
    public const string RulersKey = "editor.rulers";
    public const string HighlightCurrentLineKey = "editor.highlightCurrentLine";
    public const string FormatOnTypeKey = "editor.formatOnType";
    public const string AutoClosingBracketsKey = "editor.autoClosingBrackets";
    public const string InlayParameterNamesKey = "editor.inlayHints.parameterNames";
    public const string InlayTypesKey = "editor.inlayHints.types";
    public const string CodeLensKey = "editor.codeLens";
    public const string InlineDiagnosticsKey = "editor.inlineDiagnostics";
    public const string InheritanceMarginKey = "editor.inheritanceMargin";
    public const string IndentGuidesKey = "editor.guides.indentation";
    public const string BracketPairColorizationKey = "editor.bracketPairColorization";
    public const string StickyScrollKey = "editor.stickyScroll";
    public const string OverviewRulerKey = "editor.overviewRuler";
    public const string NavigationBarKey = "editor.navigationBar";
    public const string PerfTipsKey = "debug.perfTips";

    private readonly SettingsService _settings;

    public EditorOptions(SettingsService settings)
    {
        _settings = settings;
        Read();
        settings.Changed += (_, e) =>
        {
            if (e.Affects("editor") || e.Affects(PerfTipsKey))
            {
                Ui.Post(() =>
                {
                    Read();
                    OnPropertyChanged(string.Empty);
                });
            }
        };
    }

    public int TabSize { get; private set; }

    public bool InsertSpaces { get; private set; }

    public bool WordWrap { get; private set; }

    public bool Minimap { get; private set; }

    public bool LineNumbers { get; private set; }

    public bool RenderWhitespace { get; private set; }

    public bool HighlightCurrentLine { get; private set; }

    public IReadOnlyList<int> Rulers { get; private set; } = [];

    public bool FormatOnType { get; private set; }

    /// <summary>Insert the closing bracket or quote of the language's pairs (ADR 0016).</summary>
    public bool AutoClosingBrackets { get; private set; }

    // M12a: inline information and structure.
    public bool InlayParameterNames { get; private set; }

    public bool InlayTypes { get; private set; }

    public bool CodeLens { get; private set; }

    public bool InlineDiagnostics { get; private set; }

    public bool InheritanceMargin { get; private set; }

    public bool IndentGuides { get; private set; }

    public bool BracketPairColorization { get; private set; }

    public bool StickyScroll { get; private set; }

    public bool OverviewRuler { get; private set; }

    public bool NavigationBar { get; private set; }

    /// <summary>Show the elapsed time at the current statement while debugging (ADR 0021).</summary>
    public bool PerfTips { get; private set; }

    private void Read()
    {
        InlayParameterNames = _settings.GetBoolean(InlayParameterNamesKey, true);
        InlayTypes = _settings.GetBoolean(InlayTypesKey, false);
        CodeLens = _settings.GetBoolean(CodeLensKey, true);
        InlineDiagnostics = _settings.GetBoolean(InlineDiagnosticsKey, false);
        InheritanceMargin = _settings.GetBoolean(InheritanceMarginKey, true);
        IndentGuides = _settings.GetBoolean(IndentGuidesKey, true);
        BracketPairColorization = _settings.GetBoolean(BracketPairColorizationKey, true);
        StickyScroll = _settings.GetBoolean(StickyScrollKey, true);
        OverviewRuler = _settings.GetBoolean(OverviewRulerKey, true);
        NavigationBar = _settings.GetBoolean(NavigationBarKey, true);
        PerfTips = _settings.GetBoolean(PerfTipsKey, true);
        TabSize = Math.Clamp(_settings.GetInt32(TabSizeKey, 4), 1, 16);
        InsertSpaces = _settings.GetBoolean(InsertSpacesKey, true);
        WordWrap = _settings.GetBoolean(WordWrapKey);
        Minimap = _settings.GetBoolean(MinimapKey, true);
        LineNumbers = _settings.GetBoolean(LineNumbersKey, true);
        RenderWhitespace = _settings.GetString(RenderWhitespaceKey, "none") == "all";
        HighlightCurrentLine = _settings.GetBoolean(HighlightCurrentLineKey, true);
        FormatOnType = _settings.GetBoolean(FormatOnTypeKey, true);
        AutoClosingBrackets = _settings.GetBoolean(AutoClosingBracketsKey, true);
        Rulers = [.. _settings.GetStrings(RulersKey).Select(r => int.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out var column) ? column : 0).Where(c => c > 0)];
    }
}

/// <summary>The project a file belongs to, when it has several target frameworks (the editor's project-context picker).</summary>
public sealed record ProjectContext(string ProjectPath, string ProjectName, IReadOnlyList<string> Frameworks, string Active);

/// <summary>What an editor view needs from the shell.</summary>
public interface IEditorServices
{
    EditorOptions Options { get; }

    EditorTheme Theme { get; }

    DiagnosticsStore Diagnostics { get; }

    /// <summary>TextMate grammars and language configurations (ADR 0016).</summary>
    TextMateSupport TextMate { get; }

    /// <summary>The running language service client of a document's language, or null.</summary>
    LanguageHostClient? Client(CodeDocument document);

    /// <summary>The theme changed (token and highlight colors).</summary>
    event EventHandler? ThemeChanged;

    /// <summary>Opens a location (Go to Definition, Ctrl+Click).</summary>
    void OpenLocations(IReadOnlyList<Location> locations, string title);

    /// <summary>The project of a multi-targeted file: its frameworks and the active one (null otherwise).</summary>
    ProjectContext? ProjectContextOf(CodeDocument document);

    Task SetActiveFrameworkAsync(string projectPath, string targetFramework);

    /// <summary>Projects or their active frameworks changed.</summary>
    event EventHandler? ProjectContextsChanged;

    void ShowMessage(string message);

    void OpenLink(string target);

    /// <summary>Writes to the Language Services log.</summary>
    void Log(string message);

    /// <summary>The lightbulb: code fixes and refactorings at the active editor's caret.</summary>
    Task ShowCodeActionsAsync();

    /// <summary>Breakpoints, the current statement and data tips (ADR 0007).</summary>
    DaedalusStudio.Workbench.Debugging.DebugService Debugger { get; }

    /// <summary>QuickWatch on an expression (the exception helper's View Details, ADR 0021).</summary>
    void ShowQuickWatch(string expression);

    /// <summary>Whether an exception type breaks when thrown (the exception helper's check box).</summary>
    void SetBreakOnException(string type, bool value);

    /// <summary>Run to Click: runs the paused program to a line.</summary>
    Task RunToLineAsync(string path, int line);

    /// <summary>The breakpoint glyph's context menu.</summary>
    void ShowBreakpointMenu(string path, int line, Avalonia.Controls.Control anchor);

    /// <summary>Gutter change markers and blame (ADR 0008).</summary>
    DaedalusStudio.Workbench.Git.GitService Git { get; }

    /// <summary>Tests and their outcomes, for the test margin (ADR 0010).</summary>
    DaedalusStudio.Workbench.Testing.TestService Tests { get; }

    /// <summary>The test margin's menu for a test (Run, Debug, Run with Coverage, Show in Test Explorer).</summary>
    void ShowTestMenu(DaedalusStudio.Workbench.Testing.TestNode test, Avalonia.Controls.Control anchor);

    /// <summary>The editor's context menu (the <c>editor/context</c> menu location) at the caret.</summary>
    void ShowEditorContextMenu(CodeEditorController editor, Avalonia.Controls.Control anchor);

    /// <summary>Runs a command on the active editor (CodeLens).</summary>
    void RunCommand(string commandId);

    /// <summary>Bookmarks (M12b): the editor's margin shows and moves them.</summary>
    DaedalusStudio.Workbench.Bookmarks.BookmarkService Bookmarks { get; }

    /// <summary>Code snippets by language (M12b).</summary>
    DaedalusStudio.Workbench.Snippets.SnippetRegistry Snippets { get; }
}
