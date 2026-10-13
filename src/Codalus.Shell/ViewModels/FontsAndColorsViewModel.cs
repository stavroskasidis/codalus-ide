using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Theming;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.ViewModels;

/// <summary>Tools → Fonts and Colors (M23a) as a document.</summary>
public sealed class FontsAndColorsDocument : ShellDocument
{
    public const string DocumentId = "fontsAndColors";

    public FontsAndColorsDocument(FontsAndColorsViewModel viewModel)
    {
        Id = DocumentId;
        Title = "Fonts and Colors";
        ViewModel = viewModel;
    }

    public override string Kind => "fontsAndColors";

    public FontsAndColorsViewModel ViewModel { get; }
}

/// <summary>What a color item colors: a token type, an editor element, or a UI token.</summary>
public enum ColorItemKind
{
    Syntax,
    Editor,
    Window,
}

/// <summary>One item of Fonts and Colors: its color now, and whether the user set it (for this theme or every theme).</summary>
public sealed class ColorItemViewModel(FontsAndColorsViewModel owner, ColorItemKind kind, string key, string title) : ObservableObject
{
    private string _hex = string.Empty;
    private uint _argb;
    private bool _isCustom;
    private string? _error;

    public ColorItemKind Kind { get; } = kind;

    /// <summary>The token type, the element's VS Code id, or the resource key.</summary>
    public string Key { get; } = key;

    public string Title { get; } = title;

    public string Group => Kind switch { ColorItemKind.Syntax => "Code", ColorItemKind.Editor => "Editor", _ => "Window" };

    public IBrush Swatch => new SolidColorBrush(_argb);

    public bool IsCustom
    {
        get => _isCustom;
        private set => SetField(ref _isCustom, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    /// <summary>The color as <c>#RRGGBB</c> (or <c>#RRGGBBAA</c>); typing a new one sets it.</summary>
    public string Hex
    {
        get => _hex;
        set
        {
            if (!SetField(ref _hex, value ?? string.Empty))
            {
                return;
            }

            if (ColorCustomizations.Parse(_hex.Trim()) is { } argb)
            {
                Error = null;
                if (argb != _argb || !IsCustom)
                {
                    _ = owner.SetAsync(this, argb);
                }
            }
            else
            {
                Error = "A color is #RGB, #RRGGBB or #RRGGBBAA.";
            }
        }
    }

    public RelayCommand ResetCommand => new(_ => _ = owner.SetAsync(this, null));

    internal void Show(uint argb, bool custom)
    {
        _argb = argb;
        _hex = ColorCustomizations.Format(argb);
        IsCustom = custom;
        Error = null;
        OnPropertyChanged(nameof(Hex));
        OnPropertyChanged(nameof(Swatch));
    }
}

/// <summary>
/// Tools → Fonts and Colors (M23a): the colors of kinds of code, of the editor and of the window, over the active
/// theme. A color typed here goes into <c>editor.tokenColorCustomizations</c> or <c>workbench.colorCustomizations</c>,
/// for the active theme only or for every theme (<see cref="ForAllThemes"/>); Reset removes it. Fonts are the editor
/// settings (<c>editor.fontFamily</c>, <c>editor.fontSize</c>), linked from here.
/// </summary>
public sealed class FontsAndColorsViewModel : ObservableObject
{
    /// <summary>The window's colors offered here (the theme has more; settings take any <c>Shell.*</c> key).</summary>
    public static IReadOnlyList<(string Key, string Title)> WindowColors { get; } =
    [
        ("Shell.Background", "Window background"), ("Shell.Foreground", "Text"), ("Shell.MutedForeground", "Muted text"),
        ("Shell.Panel", "Panels"), ("Shell.Border", "Borders"), ("Shell.Accent", "Accent"), ("Shell.Selection", "Selection"),
        ("Shell.Hover", "Hover"), ("Shell.Link", "Links"), ("Shell.TitleBar", "Title bar"), ("Shell.Toolbar", "Toolbar"),
        ("Shell.Tab.Active", "Active tab"), ("Shell.Tab.Inactive", "Other tabs"), ("Shell.StatusBar", "Status bar"),
        ("Shell.StatusBar.Debugging", "Status bar while debugging"), ("Shell.Editor.Background", "Editor background"),
        ("Shell.Editor.Foreground", "Editor text"), ("Shell.Editor.LineNumbers", "Line numbers"), ("Shell.Error", "Errors"),
        ("Shell.Warning", "Warnings"), ("Shell.Info", "Information"),
    ];

    /// <summary>The editor's elements, by their VS Code ids.</summary>
    public static IReadOnlyDictionary<string, string> EditorColors { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["editor.selectionBackground"] = "Selection",
        ["editor.lineHighlightBackground"] = "Current line",
        ["editor.wordHighlightBackground"] = "Highlighted reference (read)",
        ["editor.wordHighlightStrongBackground"] = "Highlighted reference (write)",
        ["editorBracketMatch.background"] = "Matching bracket",
        ["editor.findMatchHighlightBackground"] = "Find matches",
        ["editorError.foreground"] = "Error squiggles",
        ["editorWarning.foreground"] = "Warning squiggles",
        ["editorInfo.foreground"] = "Information squiggles",
        ["editorHint.foreground"] = "Hints",
    };

    private readonly ShellHost _shell;
    private bool _forAllThemes;
    private string _filter = string.Empty;

    public FontsAndColorsViewModel(ShellHost shell)
    {
        _shell = shell;
        foreach (var type in TokenTypes.All)
        {
            All.Add(new ColorItemViewModel(this, ColorItemKind.Syntax, type, Humanize(type)));
        }

        foreach (var element in Editor.EditorTheme.Elements)
        {
            All.Add(new ColorItemViewModel(this, ColorItemKind.Editor, element, EditorColors.GetValueOrDefault(element, element)));
        }

        foreach (var (key, title) in WindowColors)
        {
            All.Add(new ColorItemViewModel(this, ColorItemKind.Window, key, title));
        }

        shell.ThemeChanged += (_, _) => Ui.Post(Refresh);
        OpenFontSettingsCommand = new RelayCommand(_ => shell.OpenSettings("editor.font"));
        Refresh();
        Fill();
    }

    public List<ColorItemViewModel> All { get; } = [];

    public ObservableCollection<ColorItemViewModel> Items { get; } = [];

    public RelayCommand OpenFontSettingsCommand { get; }

    public string ThemeTitle => ThemeService.Choices.FirstOrDefault(c => c.Id == _shell.Theme.ActiveTheme)?.Title ?? _shell.Theme.ActiveTheme;

    /// <summary>Colors set here apply to every theme (off: to the active one, under its <c>"[Title]"</c>).</summary>
    public bool ForAllThemes
    {
        get => _forAllThemes;
        set => SetField(ref _forAllThemes, value);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                Fill();
            }
        }
    }

    private void Fill()
    {
        Items.Clear();
        foreach (var item in All.Where(i => Filter.Length == 0 || i.Title.Contains(Filter, StringComparison.OrdinalIgnoreCase) || i.Key.Contains(Filter, StringComparison.OrdinalIgnoreCase)))
        {
            Items.Add(item);
        }
    }

    /// <summary>Shows every item's color now (the theme's, or the user's over it).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(ThemeTitle));
        var theme = _shell.EditorTheme;
        var overrides = _shell.ColorOverrides;
        foreach (var item in All)
        {
            switch (item.Kind)
            {
                case ColorItemKind.Syntax:
                    item.Show(theme.TokenColor(item.Key) ?? (theme.ElementColor("editorHint.foreground") | 0xFF000000), overrides.Tokens.ContainsKey(item.Key));
                    break;
                case ColorItemKind.Editor:
                    item.Show(theme.ElementColor(item.Key), overrides.Elements.ContainsKey(item.Key));
                    break;
                default:
                    var color = Avalonia.Application.Current is { } app && app.TryGetResource(item.Key, app.ActualThemeVariant, out var resource) && resource is ISolidColorBrush brush
                        ? brush.Color.ToUInt32() : 0xFF808080;
                    item.Show(color, overrides.Ui.ContainsKey(item.Key));
                    break;
            }
        }
    }

    /// <summary>Sets an item's color (null: removes the user's) in the setting it belongs to, for this theme or all.</summary>
    internal async Task SetAsync(ColorItemViewModel item, uint? argb)
    {
        var key = item.Kind == ColorItemKind.Syntax ? ColorCustomizations.TokensKey : ColorCustomizations.WorkbenchKey;
        var settings = _shell.Workbench.Settings;
        var current = settings.GetValue(key);
        var root = current.ValueKind == JsonValueKind.Object ? JsonNode.Parse(current.GetRawText())!.AsObject() : [];
        var themeScope = "[" + ThemeTitle + "]";
        if (argb is null)
        {
            // Reset: the theme's color again, whichever scope the user's was in.
            foreach (var scope in new[] { root, root[themeScope] as JsonObject })
            {
                Write(item.Kind == ColorItemKind.Syntax ? scope?["semanticTokenColors"] as JsonObject : scope, item.Key, null);
            }
        }
        else
        {
            var scope = root;
            if (!ForAllThemes)
            {
                scope = root[themeScope] as JsonObject ?? [];
                root[themeScope] = scope;
            }

            if (item.Kind == ColorItemKind.Syntax)
            {
                var semantic = scope["semanticTokenColors"] as JsonObject ?? [];
                scope["semanticTokenColors"] = semantic;
                scope = semantic;
            }

            Write(scope, item.Key, argb);
        }

        using var document = JsonDocument.Parse(root.ToJsonString());
        await settings.SetAsync(key, document.RootElement.Clone(), SettingScope.User, CancellationToken.None).ConfigureAwait(true);
    }

    private static void Write(JsonObject? target, string key, uint? argb)
    {
        if (target is null)
        {
            return;
        }

        if (argb is { } value)
        {
            target[key] = ColorCustomizations.Format(value);
        }
        else
        {
            target.Remove(key);
        }
    }

    private static string Humanize(string type)
    {
        var words = new System.Text.StringBuilder();
        foreach (var c in type)
        {
            if (char.IsUpper(c) && words.Length > 0)
            {
                words.Append(' ');
            }

            words.Append(words.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }

        return words.ToString();
    }
}
