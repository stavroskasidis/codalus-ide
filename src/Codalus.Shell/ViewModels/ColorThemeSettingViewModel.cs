using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Codalus.Shell.Docking;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Theming;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// A theme variant's preview (M34h): what <c>ThemePictureView</c> shows in its <see cref="Variant"/>, a scaled-down sample
/// of the shell's chrome. The view draws the chrome with the variant's own resources (a <c>ThemeVariantScope</c>); this
/// gives it the sample's words (translated), the editor's code in the variant's syntax colors (the editor's own theme,
/// <see cref="EditorTheme"/>), and the solution's tree. A few values are read here too, for tests and Options.
/// </summary>
public sealed class ThemePicture
{
    private static readonly FontFamily s_codeFont = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, Noto Sans Mono, Ubuntu Mono, Liberation Mono, Noto Mono, monospace");

    public ThemePicture(ThemeVariant variant)
    {
        Variant = variant;
        ThemeId = ThemeService.Themes.FirstOrDefault(t => t.Variant == variant).Id ?? "dark";
        Background = Brush("Shell.Background", variant);
        Gutter = Value("Shell.Gutter", variant, 4.0);
        PanelCorners = Value("Shell.Panel.CornerRadius.Bottom", variant, default(CornerRadius));

        var editor = EditorTheme.For(ThemeId);
        var plain = Brush("Shell.Editor.Foreground", variant);
        IBrush Token(string type) => editor.TokenColor(type) is { } argb ? new ImmutableSolidColorBrush(argb) : plain;
        ThemePictureSegment Plain(string text) => new(text, plain);
        ThemePictureSegment Keyword(string text) => new(text, Token("keyword"));
        ThemePictureSegment Type(string text) => new(text, Token("class"));
        ThemePictureSegment Method(string text) => new(text, Token("method"));
        ThemePictureSegment String(string text) => new(text, Token("string"));
        ThemePictureSegment Number(string text) => new(text, Token("number"));
        ThemePictureSegment Comment(string text) => new(text, Token("comment"));
        ThemePictureCodeLine Line(int number, params ThemePictureSegment[] segments) => new(number.ToString(CultureInfo.InvariantCulture), number == 4, segments);
        Code =
        [
            Line(1, Keyword("public "), Keyword("class "), Type("Cart")),
            Line(2, Plain("{")),
            Line(3, Comment("    // The total")),
            Line(4, Keyword("    int "), Method("Count"), Plain("()")),
            Line(5, Plain("        => "), Number("42"), Plain(";")),
            Line(6, Keyword("    string "), Plain("Id = "), String("\"A\""), Plain(";")),
            Line(7, Plain("}")),
        ];
    }

    /// <summary>The variant the preview draws in.</summary>
    public ThemeVariant Variant { get; }

    /// <summary>The resolved theme of <see cref="Variant"/> (<c>harbor.dark</c>, <c>light</c>…).</summary>
    public string ThemeId { get; }

    public IBrush Background { get; }

    public double Gutter { get; }

    public CornerRadius PanelCorners { get; }

    public FontFamily CodeFont => s_codeFont;

    /// <summary>The menu bar's first menus, as the menu bar shows them (translated, without their access keys).</summary>
    public IReadOnlyList<string> Menus { get; } =
    [
        Menu("menu.file", "_File"), Menu("menu.edit", "_Edit"), Menu("menu.view", "_View"), Menu("menu.build", "_Build"), Menu("menu.debug", "_Debug"),
    ];

    public string Search { get; } = Localizer.Get("msg.themePicture.search", "Search");

    public string SolutionName => "Shop";

    public string Start { get; } = Localizer.Get("msg.themePicture.start", "Start");

    public string ActiveTab => "Cart.cs";

    public string OtherTab => "Program.cs";

    public string ToolWindow { get; } = Localizer.Get("command.workbench.view.solutionExplorer", "Solution Explorer");

    public string Ready { get; } = Localizer.Get("msg.themePicture.ready", "Ready");

    public string Branch => "main";

    /// <summary>The editor's lines, the fourth the current one.</summary>
    public IReadOnlyList<ThemePictureCodeLine> Code { get; }

    /// <summary>Solution Explorer's rows: the solution, its project, a folder and two files, the first selected.</summary>
    public IReadOnlyList<ThemePictureTreeRow> Tree { get; } =
    [
        new(0, "chevron-down", "cl:solution", "Shop", false),
        new(1, "chevron-down", "cl:project-csharp", "Shop", false),
        new(2, "chevron-right", "cl:folder", "Models", false),
        new(2, null, "cl:csharp", "Cart.cs", true),
        new(2, null, "cl:csharp", "Program.cs", false),
    ];

    private static string Menu(string key, string fallback) => Localizer.Get(key, fallback).Replace("_", string.Empty, StringComparison.Ordinal);

    private static IBrush Brush(string key, ThemeVariant variant) => Lookup(key, variant) as IBrush ?? Brushes.Transparent;

    private static T Value<T>(string key, ThemeVariant variant, T fallback) => Lookup(key, variant) is T value ? value : fallback;

    private static object? Lookup(string key, ThemeVariant variant) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, variant, out var value) ? value : null;
}

/// <summary>A run of the preview's code in its syntax color.</summary>
public sealed record ThemePictureSegment(string Text, IBrush Brush);

/// <summary>A line of the preview's editor: its number, whether it's the current line, its runs.</summary>
public sealed record ThemePictureCodeLine(string Number, bool IsCurrent, IReadOnlyList<ThemePictureSegment> Segments);

/// <summary>A row of the preview's Solution Explorer.</summary>
public sealed record ThemePictureTreeRow(int Level, string? Chevron, string Icon, string Name, bool IsSelected)
{
    public Thickness Indent => new(4 + (Level * 10), 0, 0, 0);
}

/// <summary>A theme family's card in Options: its name, a line about it, and a picture of it in the chosen mode.</summary>
public sealed class ThemeFamilyOption : ObservableObject
{
    private ThemeVariant _previewVariant;
    private ThemePicture _picture;
    private bool _isSelected;

    public ThemeFamilyOption(ThemeFamily family, Action<ThemeFamilyOption> select)
    {
        Family = family;
        _previewVariant = ThemeService.VariantOf(family.Id == ThemeService.Classic ? "light" : family.Id + ".light");
        _picture = new ThemePicture(_previewVariant);
        SelectCommand = new RelayCommand(() => select(this));
    }

    public ThemeFamily Family { get; }

    public string Id => Family.Id;

    public string Title => Family.Title;

    /// <summary>The card's line, translated (M34g; the setup's cards were, Options' weren't).</summary>
    public string Description => Family.Id switch
    {
        ThemeService.Classic => Localizer.Get("msg.colorTheme.classic", "The classic IDE's familiar look, in Blue, Light, Dark and High Contrast: the themes from before 0.3."),
        ThemeService.Harbor => Localizer.Get("msg.colorTheme.harbor", "The closest to the classic IDE: a deep navy title bar, docked tool windows with gutters, a cobalt accent."),
        ThemeService.Graphite => Localizer.Get("msg.colorTheme.graphite", "Compact and dense, for more code on the screen: hairlines instead of gutters, a teal accent."),
        ThemeService.Daylight => Localizer.Get("msg.colorTheme.daylight", "Flat and airy: rounded tabs, the Start button filled with a violet accent."),
        ThemeService.Ember => Localizer.Get("msg.colorTheme.ember", "Warm, with floating panels: rounded cards on the window's background, a copper accent."),
        _ => Family.Description,
    };

    /// <summary>The variant the card's picture shows.</summary>
    public ThemeVariant PreviewVariant
    {
        get => _previewVariant;
        set
        {
            if (SetField(ref _previewVariant, value))
            {
                Picture = new ThemePicture(value);
            }
        }
    }

    /// <summary>The picture's colors and shapes, the variant's tokens.</summary>
    public ThemePicture Picture
    {
        get => _picture;
        private set => SetField(ref _picture, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public ICommand SelectCommand { get; }
}

/// <summary>A mode in Options' theme picker: Follow System (null) or a pinned variant.</summary>
public sealed record ThemeModeOption(string? Variant, string Label);

/// <summary>
/// <c>workbench.colorTheme</c> in Tools → Options → Environment (M45): the families as cards with pictures, and a mode
/// (Follow System, Light, Dark, High Contrast; Classic also Blue). Picking writes the setting, so the IDE switches live.
/// A plugin's theme is chosen from its own list. A value from before the families shows as Classic.
/// </summary>
public sealed class ColorThemeSettingViewModel : SettingItemViewModel
{
    private readonly Func<string, string> _resolve;
    private ThemeFamilyOption? _selectedFamily;
    private ThemeModeOption? _selectedMode;
    private EnumOption? _selectedPluginTheme;
    private IReadOnlyList<ThemeModeOption> _modes = [];

    /// <param name="resolve">The theme a setting resolves to now (with the system's colors), for the cards' pictures.</param>
    public ColorThemeSettingViewModel(SettingDescriptor descriptor, SettingsService settings, Func<SettingScope> scope, Func<string, string>? resolve = null)
        : base(descriptor, settings, scope)
    {
        _resolve = resolve ?? (setting => ThemeService.Resolve(setting, Avalonia.Application.Current?.PlatformSettings?.GetColorValues()));
        Families = [.. ThemeService.Families.Select(f => new ThemeFamilyOption(f, Pick))];
        var builtIn = ThemeService.Choices.Select(c => c.Id).Concat(ThemeService.LegacyChoices.Select(c => c.Id)).ToHashSet(StringComparer.Ordinal);
        PluginThemes = [.. (descriptor.Enum ?? []).Select((value, i) => (value, i)).Where(e => !builtIn.Contains(e.value))
            .Select(e => new EnumOption(e.value, descriptor.EnumDescriptions?.ElementAtOrDefault(e.i) ?? e.value, null))];
        Refresh();
    }

    public IReadOnlyList<ThemeFamilyOption> Families { get; }

    /// <summary>The selected family's modes.</summary>
    public IReadOnlyList<ThemeModeOption> Modes
    {
        get => _modes;
        private set => SetField(ref _modes, value);
    }

    /// <summary>The plugins' themes (the setting's other values).</summary>
    public IReadOnlyList<EnumOption> PluginThemes { get; }

    public bool HasPluginThemes => PluginThemes.Count > 0;

    public ThemeFamilyOption? SelectedFamily => _selectedFamily;

    public ThemeModeOption? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (SetField(ref _selectedMode, value) && !Refreshing && value is not null && _selectedFamily is { } family)
            {
                Write(ThemeService.SettingFor(family.Id, value.Variant));
            }
        }
    }

    public EnumOption? SelectedPluginTheme
    {
        get => _selectedPluginTheme;
        set
        {
            if (SetField(ref _selectedPluginTheme, value) && !Refreshing && value is not null)
            {
                Write(value.Value);
            }
        }
    }

    /// <summary>The value the picker shows, in its M45 form.</summary>
    public string Current { get; private set; } = ThemeService.DefaultSetting;

    private void Pick(ThemeFamilyOption family)
    {
        if (!CanEdit)
        {
            return;
        }

        // A family keeps the chosen mode where it has it (Blue is Classic's only): Follow System otherwise.
        var variant = _selectedPluginTheme is null && family.Family.Variants.Contains(_selectedMode?.Variant ?? string.Empty) ? _selectedMode!.Variant : null;
        Write(ThemeService.SettingFor(family.Id, variant));
    }

    private void Write(string value)
    {
        Show(value);
        _ = WriteAsync(JsonSerializer.SerializeToElement(value, ShellJsonContext.Default.String));
    }

    protected override void Load(JsonElement value) =>
        Show(value.ValueKind == JsonValueKind.String ? value.GetString()! : ThemeService.DefaultSetting);

    private void Show(string setting)
    {
        Current = ThemeService.Normalize(setting);
        OnPropertyChanged(nameof(Current));
        var parsed = ThemeService.Parse(setting);
        var family = parsed is var (id, _) ? Families.FirstOrDefault(f => f.Id == id) : null;
        _selectedFamily = family;
        foreach (var option in Families)
        {
            option.IsSelected = option == family;
            // Each card shows its family in the chosen mode (the system's variant when following it).
            var variant = parsed?.Variant is { } v && option.Family.Variants.Contains(v) ? v : null;
            option.PreviewVariant = ThemeService.VariantOf(_resolve(ThemeService.SettingFor(option.Id, variant)));
        }

        OnPropertyChanged(nameof(SelectedFamily));
        Modes = family is null ? [] : [new ThemeModeOption(null, Localizer.Get("msg.colorTheme.followSystem", "Follow System")), .. family.Family.Variants.Select(v => new ThemeModeOption(v, ModeTitle(v)))];
        SetSilently(ref _selectedMode, Modes.FirstOrDefault(m => m.Variant == parsed?.Variant), nameof(SelectedMode));
        SetSilently(ref _selectedPluginTheme, parsed is null ? PluginThemes.FirstOrDefault(p => p.Value == setting) : null, nameof(SelectedPluginTheme));
    }

    /// <summary>A mode's title, translated (the setting's own names, <see cref="ThemeService.VariantTitle"/>, stay English).</summary>
    private static string ModeTitle(string variant) => variant switch
    {
        "light" => Localizer.Get("msg.colorTheme.light", "Light"),
        "dark" => Localizer.Get("msg.colorTheme.dark", "Dark"),
        "highContrast" => Localizer.Get("msg.colorTheme.highContrast", "High Contrast"),
        "blue" => Localizer.Get("msg.colorTheme.blue", "Blue"),
        _ => ThemeService.VariantTitle(variant),
    };

    private void SetSilently<T>(ref T field, T value, string property)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(property);
        }
    }
}
