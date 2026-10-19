using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Codalus.Shell.Controls;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views;

/// <summary>
/// A code block of an answer in AI Chat (M34h, docs/design/controls/AiChat.dc.html): a card whose header row names the
/// language (and the file, when the block names one) and offers Copy, Insert at Cursor and Apply…, over the code in a
/// read-only <see cref="CodeViewer"/> in the editor's colors, as high as the code.
/// </summary>
internal sealed class AiCodeBlockView : Border
{
    private readonly CodeViewer _viewer;
    private readonly TextBlock _language = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold };
    private readonly Button[] _buttons;
    private string? _text;
    private (string Code, string? Language)? _colored;
    private int _version;
    private bool _released;

    public AiCodeBlockView(IEditorServices services, EditorFont font, Action copy, Action insert, Action apply)
    {
        Classes.Add("codeBlock");
        _language.Classes.Add("muted");
        _language.Classes.Add("small");
        _viewer = new CodeViewer(services, font) { EditorColors = true, FitsContent = true };
        _viewer.Editor.FontSize = Math.Max(9, font.Size - 1);
        AutomationProperties.SetName(_viewer.Editor, Localizer.Get("msg.ai.codeReadOnly", "Code (read-only)"));

        // Tab leaves the code for the next control, as in the rest of the chat (the editor keeps Tab for itself).
        _viewer.Editor.TextArea.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Tab && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift && TopLevel.GetTopLevel(this)?.FocusManager is { } focus)
            {
                e.Handled = focus.TryMoveFocus(e.KeyModifiers == KeyModifiers.Shift ? NavigationDirection.Previous : NavigationDirection.Next);
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var copyButton = HeaderButton("copy", null, Localizer.Get("msg.ai.copy", "Copy"), Localizer.Get("msg.ai.copyTheCode", "Copy the code"), copy);
        var insertButton = HeaderButton("insert", null, Localizer.Get("msg.ai.insert", "Insert at Cursor"), Localizer.Get("msg.ai.insertTheCodeAtTheCursor", "Insert the code at the cursor"), insert);
        var applyButton = HeaderButton("document-check", Localizer.Get("msg.ai.apply", "Apply…"), Localizer.Get("msg.ai.applyTheCodeAfterAPreview", "Apply the code after a preview"), Localizer.Get("msg.ai.applyTheCodeAfterAPreview", "Apply the code after a preview"), apply);
        _buttons = [copyButton, insertButton, applyButton];
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.AddRange(_buttons);
        DockPanel.SetDock(actions, Avalonia.Controls.Dock.Right);
        var header = new Border { Child = new DockPanel { Children = { actions, _language } } };
        header.Classes.Add("codeBlockHeader");
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
        Child = new DockPanel { Children = { header, _viewer } };
    }

    /// <summary>The viewer (tests).</summary>
    public CodeViewer Viewer => _viewer;

    /// <summary>Styled as a border (<c>Border.codeBlock</c>).</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>The language as the header shows it (tests).</summary>
    public string? HeaderText => _language.Text;

    /// <summary>The code is in its language's colors (tests: none while it is still written).</summary>
    public bool IsColored => _colored is not null;

    /// <summary>
    /// Shows <paramref name="code"/> in <paramref name="language"/> (the fence's name: <c>csharp</c>, <c>cs</c>, <c>json</c>…)
    /// with the file it names, if any; its buttons wait while the answer is still being written. While the block itself
    /// is still written (<paramref name="streaming"/> and not <paramref name="closed"/>), it is plain text that grows at
    /// its end; it is colored once it is complete, after the layout (tokenizing a long block on every render of a
    /// streaming answer would hold up the UI thread).
    /// </summary>
    public void Show(string language, string? path, string code, bool streaming, bool closed)
    {
        var id = LanguageId(language);
        var label = LanguageLabel(language);
        _language.Text = path is { Length: > 0 } ? $"{label} · {Path.GetFileName(path)}" : label;
        ToolTip.SetTip(_language, path is { Length: > 0 } ? $"{label} · {path}" : label);
        foreach (var button in _buttons)
        {
            button.IsEnabled = !streaming;
        }

        var complete = !streaming || closed;
        if (complete && _colored == (code, id))
        {
            return;
        }

        if (_colored is not null && _colored != (code, id))
        {
            _colored = null;
            _viewer.ShowPlain(code);
            _text = code;
        }
        else if (_text is { } shown && code.Length > shown.Length && code.StartsWith(shown, StringComparison.Ordinal))
        {
            _viewer.Append(code[shown.Length..]);
            _text = code;
        }
        else if (_text != code)
        {
            _viewer.ShowPlain(code);
            _text = code;
        }

        var version = ++_version;
        if (complete)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (version == _version && !_released)
                {
                    _viewer.Show(code, id, null);
                    _colored = (code, id);
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>Stops following the theme (the turn left the screen or the chat was cleared).</summary>
    public void Release()
    {
        _released = true;
        _version++;
        _viewer.Release();
    }

    /// <summary>The editor's language for a fence's name, or null for plain text (names it doesn't know stay plain).</summary>
    public static string? LanguageId(string fence) => fence.ToLowerInvariant() switch
    {
        "csharp" or "cs" or "c#" => "csharp",
        "vb" or "vbnet" or "vb.net" or "visualbasic" => "vb",
        "fsharp" or "fs" or "f#" => "fsharp",
        "json" or "jsonc" => "json",
        "xml" or "xaml" or "axaml" or "csproj" or "props" or "targets" or "msbuild" or "resx" => "xml",
        "html" or "htm" => "html",
        "razor" or "cshtml" => "razor",
        "css" => "css",
        "scss" => "scss",
        "js" or "javascript" or "jsx" => "javascript",
        "ts" or "typescript" or "tsx" => "typescript",
        "sh" or "bash" or "shell" or "zsh" or "console" => "shellscript",
        "ps" or "ps1" or "powershell" or "pwsh" => "powershell",
        "yaml" or "yml" => "yaml",
        "sql" => "sql",
        "md" or "markdown" => "markdown",
        "py" or "python" => "python",
        "diff" or "patch" => "diff",
        "ini" or "editorconfig" => "ini",
        "dockerfile" or "docker" => "dockerfile",
        _ => null,
    };

    /// <summary>A fence's language as people name it (<c>C#</c>, <c>JSON</c>), or the fence's own name.</summary>
    public static string LanguageLabel(string fence) => fence.ToLowerInvariant() switch
    {
        "" => Localizer.Get("msg.ai.code", "Code"),
        "csharp" or "cs" or "c#" => "C#",
        "vb" or "vbnet" or "vb.net" or "visualbasic" => "Visual Basic",
        "fsharp" or "fs" or "f#" => "F#",
        "json" or "jsonc" => "JSON",
        "xml" or "xaml" or "axaml" or "csproj" or "props" or "targets" or "msbuild" or "resx" => fence.Length <= 5 ? fence.ToUpperInvariant() : "XML",
        "html" or "htm" => "HTML",
        "razor" or "cshtml" => "Razor",
        "css" or "scss" or "sql" or "yaml" or "ini" => fence.ToUpperInvariant(),
        "yml" => "YAML",
        "js" or "javascript" or "jsx" => "JavaScript",
        "ts" or "typescript" or "tsx" => "TypeScript",
        "sh" or "bash" or "shell" or "zsh" or "console" => "Shell",
        "ps" or "ps1" or "powershell" or "pwsh" => "PowerShell",
        "md" or "markdown" => "Markdown",
        "py" or "python" => "Python",
        "diff" or "patch" => "Diff",
        "dockerfile" or "docker" => "Dockerfile",
        _ => fence,
    };

    private static Button HeaderButton(string icon, string? text, string tooltip, string name, Action click)
    {
        var button = new Button { Content = text };
        button.Classes.Add("toolbar");
        button.Classes.Add(text is null ? "icon" : "iconText");
        ButtonIcon.SetKind(button, icon);
        ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }
}
