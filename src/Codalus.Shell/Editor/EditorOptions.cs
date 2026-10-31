using System.Globalization;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Languages;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Editor;

/// <summary>
/// The <c>editor.*</c> settings, refreshed on the UI thread when they change. <see cref="For"/> gives a language's
/// options: its <c>"[languageId]"</c> blocks in settings.json over the general values (per-language editor options).
/// </summary>
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
    public const string CommitOnSpaceKey = "editor.completion.commitOnSpace";
    public const string AutoClosingBracketsKey = "editor.autoClosingBrackets";
    public const string InlayParameterNamesKey = "editor.inlayHints.parameterNames";
    public const string InlayTypesKey = "editor.inlayHints.types";
    public const string CodeLensKey = "editor.codeLens";
    public const string GitCodeLensKey = "editor.gitCodeLens";
    public const string TestCodeLensKey = "editor.testCodeLens";
    public const string InlineDiagnosticsKey = "editor.inlineDiagnostics";
    public const string InheritanceMarginKey = "editor.inheritanceMargin";
    public const string IndentGuidesKey = "editor.guides.indentation";
    public const string BracketPairColorizationKey = "editor.bracketPairColorization";
    public const string StickyScrollKey = "editor.stickyScroll";
    public const string OverviewRulerKey = "editor.overviewRuler";
    public const string NavigationBarKey = "editor.navigationBar";
    public const string PerfTipsKey = "debug.perfTips";

    private readonly SettingsService _settings;
    private readonly string? _languageId;
    private readonly Dictionary<string, EditorOptions> _languages = new(StringComparer.Ordinal);

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
                    // The languages' options first: the general options' listeners read them.
                    Read();
                    foreach (var options in _languages.Values)
                    {
                        options.Read();
                    }

                    OnPropertyChanged(string.Empty);
                    foreach (var options in _languages.Values)
                    {
                        options.OnPropertyChanged(string.Empty);
                    }
                });
            }
        };
    }

    private EditorOptions(SettingsService settings, string languageId)
    {
        _settings = settings;
        _languageId = languageId;
        Read();
    }

    /// <summary>The language these options are for; null for the general ones.</summary>
    public string? LanguageId => _languageId;

    /// <summary>
    /// The options of documents of <paramref name="languageId"/> (one instance per language, refreshed with these; the
    /// general options raise <see cref="ObservableObject.PropertyChanged"/> after every language's are read again).
    /// </summary>
    public EditorOptions For(string? languageId)
    {
        if (_languageId is not null || string.IsNullOrEmpty(languageId))
        {
            return this;
        }

        if (!_languages.TryGetValue(languageId, out var options))
        {
            _languages[languageId] = options = new EditorOptions(_settings, languageId);
        }

        return options;
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

    /// <summary>Space commits the completion list's selected item, as the other commit characters do (a classic IDE's default).</summary>
    public bool CommitOnSpace { get; private set; } = true;

    /// <summary>Insert the closing bracket or quote of the language's pairs (ADR 0016).</summary>
    public bool AutoClosingBrackets { get; private set; }

    // M12a: inline information and structure.
    public bool InlayParameterNames { get; private set; }

    public bool InlayTypes { get; private set; }

    public bool CodeLens { get; private set; }

    /// <summary>CodeLens shows each member's last author and its numbers of authors and changes, from Git blame.</summary>
    public bool GitCodeLens { get; private set; }

    /// <summary>CodeLens shows each test's (and test class's) last outcome.</summary>
    public bool TestCodeLens { get; private set; }

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
        var settings = _settings.ForLanguage(_languageId);
        InlayParameterNames = settings.GetBoolean(InlayParameterNamesKey, true);
        InlayTypes = settings.GetBoolean(InlayTypesKey, false);
        CodeLens = settings.GetBoolean(CodeLensKey, true);
        GitCodeLens = settings.GetBoolean(GitCodeLensKey, true);
        TestCodeLens = settings.GetBoolean(TestCodeLensKey, true);
        InlineDiagnostics = settings.GetBoolean(InlineDiagnosticsKey, false);
        InheritanceMargin = settings.GetBoolean(InheritanceMarginKey, true);
        IndentGuides = settings.GetBoolean(IndentGuidesKey, true);
        BracketPairColorization = settings.GetBoolean(BracketPairColorizationKey, true);
        StickyScroll = settings.GetBoolean(StickyScrollKey, true);
        OverviewRuler = settings.GetBoolean(OverviewRulerKey, true);
        NavigationBar = settings.GetBoolean(NavigationBarKey, true);
        PerfTips = settings.GetBoolean(PerfTipsKey, true);
        TabSize = Math.Clamp(settings.GetInt32(TabSizeKey, 4), 1, 16);
        InsertSpaces = settings.GetBoolean(InsertSpacesKey, true);
        WordWrap = settings.GetBoolean(WordWrapKey);
        Minimap = settings.GetBoolean(MinimapKey, true);
        LineNumbers = settings.GetBoolean(LineNumbersKey, true);
        RenderWhitespace = settings.GetString(RenderWhitespaceKey, "none") == "all";
        HighlightCurrentLine = settings.GetBoolean(HighlightCurrentLineKey, true);
        FormatOnType = settings.GetBoolean(FormatOnTypeKey, true);
        CommitOnSpace = settings.GetBoolean(CommitOnSpaceKey, true);
        AutoClosingBrackets = settings.GetBoolean(AutoClosingBracketsKey, true);
        Rulers = [.. settings.GetStrings(RulersKey).Select(r => int.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out var column) ? column : 0).Where(c => c > 0)];
    }
}

/// <summary>The project a file belongs to, when it has several target frameworks (the editor's project-context picker).</summary>
public sealed record ProjectContext(string ProjectPath, string ProjectName, IReadOnlyList<string> Frameworks, string Active);

/// <summary>A language embedded in a document, at a position: the client to ask and the document's URI there (HTML in Razor, ADR 0027).</summary>
public sealed record EmbeddedRequest(LanguageHostClient Client, string Uri);

/// <summary>What an editor view needs from the shell.</summary>
public interface IEditorServices
{
    /// <summary>AI inline suggestions (M28b), or null where there are none.</summary>
    Codalus.Workbench.Ai.AiCompletions? AiCompletions => null;

    /// <summary>AI next-edit suggestions (M28b), or null where there are none.</summary>
    Codalus.Workbench.Ai.AiNextEdit? AiNextEdit => null;

    /// <summary>AI assistance is on: the exception helper offers Ask AI (M28b).</summary>
    bool CanAskAi => false;

    /// <summary>Asks AI Chat about the exception the debugger stopped on (its description with the stack).</summary>
    void AskAiAboutException(string description)
    {
    }

    /// <summary>The key a command is bound to now, as menus show it (<c>Alt+Enter</c>), or null.</summary>
    string? KeyLabel(string commandId) => null;

    /// <summary>The context keys key bindings test while the completion list (<c>suggestWidgetVisible</c>) or signature help (<c>parameterHintsVisible</c>) is open.</summary>
    void SetIntelliSenseContext(bool completionOpen, bool signatureHelpOpen)
    {
    }

    /// <summary>Quick Info's Fix: applies the top code fix (or refactoring) for a diagnostic in an editor.</summary>
    Task ApplyTopFixAsync(CodeEditorController editor, Diagnostic diagnostic) => Task.CompletedTask;

    /// <summary>Quick Info's Other Fixes: the code actions menu at a diagnostic.</summary>
    Task ShowCodeActionsAtAsync(CodeEditorController editor, Diagnostic diagnostic) => Task.CompletedTask;

    /// <summary>Quick Info's Add to Chat: sends a diagnostic and the code around it to AI Chat (AI on only).</summary>
    Task AddDiagnosticToChatAsync(CodeEditorController editor, Diagnostic diagnostic) => Task.CompletedTask;

    EditorOptions Options { get; }

    EditorTheme Theme { get; }

    DiagnosticsStore Diagnostics { get; }

    /// <summary>TextMate grammars and language configurations (ADR 0016).</summary>
    TextMateSupport TextMate { get; }

    /// <summary>The running language service client of a document's language, or null.</summary>
    LanguageHostClient? Client(CodeDocument document);

    /// <summary>
    /// Whether a document's language service will list CodeLens for it, before it answers (a C# file, unless the C#
    /// service couldn't start or has no CodeLens; an F# file once FsAutoComplete serves it, with its CodeLens on): the
    /// editor keeps the rows' room from the first frame.
    /// </summary>
    bool ExpectsCodeLens(CodeDocument document) => false;

    /// <summary>
    /// Whether a document's language service will list its symbols, before it answers (the C# host's languages, F#,
    /// JavaScript and TypeScript, style sheets; not once its service failed or if it has none): the navigation bar keeps
    /// its room from the first frame when the text declares a type.
    /// </summary>
    bool ExpectsDocumentSymbols(CodeDocument document) => false;

    /// <summary>
    /// The language embedded at <paramref name="offset"/> (a caret) of <paramref name="document"/>, to ask too: HTML at
    /// a Razor file's markup. Null elsewhere, and when its server isn't running.
    /// </summary>
    Task<EmbeddedRequest?> EmbeddedAsync(CodeDocument document, int offset);

    /// <summary>Completion items the shell adds at <paramref name="offset"/> (scoped CSS's class names and <c>::deep</c>, ADR 0027).</summary>
    IReadOnlyList<CompletionItem> ExtraCompletions(CodeDocument document, int offset);

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

    /// <summary>How Markdown editors show their preview beside the text (shared by every one), or null for none.</summary>
    MarkdownSplitState? MarkdownSplit => null;

    /// <summary>Writes to the Language Services log.</summary>
    void Log(string message);

    /// <summary>The lightbulb: code fixes and refactorings at the active editor's caret.</summary>
    Task ShowCodeActionsAsync();

    /// <summary>Breakpoints, the current statement and data tips (ADR 0007).</summary>
    Codalus.Workbench.Debugging.DebugService Debugger { get; }

    /// <summary>Sends the request of a .http file at a line (1-based) and shows the response (M15a).</summary>
    Task SendHttpRequestAsync(string path, string text, int line);

    /// <summary>QuickWatch on an expression (the exception helper's View Details, ADR 0021).</summary>
    void ShowQuickWatch(string expression);

    /// <summary>Whether an exception type breaks when thrown (the exception helper's check box).</summary>
    void SetBreakOnException(string type, bool value);

    /// <summary>Run to Click: runs the paused program to a line.</summary>
    Task RunToLineAsync(string path, int line);

    /// <summary>The breakpoint glyph's context menu.</summary>
    void ShowBreakpointMenu(string path, int line, Avalonia.Controls.Control anchor);

    /// <summary>Gutter change markers and blame (ADR 0008).</summary>
    Codalus.Workbench.Git.GitService Git { get; }

    /// <summary>Tests and their outcomes, for the test margin (ADR 0010).</summary>
    Codalus.Workbench.Testing.TestService Tests { get; }

    /// <summary>The test margin's menu for a test (Run, Debug, Run with Coverage, Show in Test Explorer).</summary>
    void ShowTestMenu(Codalus.Workbench.Testing.TestNode test, Avalonia.Controls.Control anchor);

    /// <summary>The editor's context menu (the <c>editor/context</c> menu location) at the caret.</summary>
    void ShowEditorContextMenu(CodeEditorController editor, Avalonia.Controls.Control anchor);

    /// <summary>Runs a command on the active editor (CodeLens).</summary>
    void RunCommand(string commandId);

    /// <summary>The benchmarks of the solution, for the benchmark glyphs (M18b).</summary>
    Codalus.Workbench.Diagnostics.BenchmarkService Benchmarks { get; }

    /// <summary>Runs a benchmark (or a class's) from its glyph in <paramref name="path"/>.</summary>
    Task RunBenchmarkAsync(string path, Codalus.Workbench.Diagnostics.BenchmarkMethod benchmark);

    /// <summary>The profiler's hot lines for the gutter (M18).</summary>
    Codalus.Workbench.Diagnostics.HotLineStore HotLines { get; }

    /// <summary>Bookmarks (M12b): the editor's margin shows and moves them.</summary>
    Codalus.Workbench.Bookmarks.BookmarkService Bookmarks { get; }

    /// <summary>Code snippets by language (M12b).</summary>
    Codalus.Workbench.Snippets.SnippetRegistry Snippets { get; }

    /// <summary>The review threads of the current branch's pull request on a file (M13b), at their lines.</summary>
    IReadOnlyList<Codalus.Workbench.Hosting.EditorReviewThread> ReviewThreadsFor(string path);

    /// <summary>The pull request's review threads changed (raised on any thread).</summary>
    event EventHandler? ReviewThreadsChanged;

    /// <summary>Shows review threads in a flyout at <paramref name="anchor"/> (clicking their note at the end of a line).</summary>
    void ShowReviewThreads(IReadOnlyList<Codalus.Workbench.Hosting.EditorReviewThread> threads, Avalonia.Controls.Control anchor);
}
