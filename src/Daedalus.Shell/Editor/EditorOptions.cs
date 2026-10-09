using System.Globalization;
using Daedalus.LanguageServices.Client;
using Daedalus.LanguageServices.Protocol;
using Daedalus.Shell.Infrastructure;
using Daedalus.Workbench.Languages;
using Daedalus.Workbench.Settings;

namespace Daedalus.Shell.Editor;

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

    private readonly SettingsService _settings;

    public EditorOptions(SettingsService settings)
    {
        _settings = settings;
        Read();
        settings.Changed += (_, e) =>
        {
            if (e.Affects("editor"))
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

    private void Read()
    {
        TabSize = Math.Clamp(_settings.GetInt32(TabSizeKey, 4), 1, 16);
        InsertSpaces = _settings.GetBoolean(InsertSpacesKey, true);
        WordWrap = _settings.GetBoolean(WordWrapKey);
        Minimap = _settings.GetBoolean(MinimapKey, true);
        LineNumbers = _settings.GetBoolean(LineNumbersKey, true);
        RenderWhitespace = _settings.GetString(RenderWhitespaceKey, "none") == "all";
        HighlightCurrentLine = _settings.GetBoolean(HighlightCurrentLineKey, true);
        FormatOnType = _settings.GetBoolean(FormatOnTypeKey, true);
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
    Daedalus.Workbench.Debugging.DebugService Debugger { get; }
}
