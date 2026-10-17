using System.Text.Json;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Theming;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// The colors and shapes of a theme variant that a family card's picture draws, read from its tokens. (A
/// <c>ThemeVariantScope</c> drew nothing when its variant was the window's: the picture takes the values instead.)
/// </summary>
public sealed class ThemePicture(ThemeVariant variant)
{
    private static readonly IBrush s_none = Brushes.Transparent;

    public IBrush Background { get; } = Brush("Shell.Background", variant);

    public IBrush Border { get; } = Brush("Shell.Border", variant);

    public IBrush TitleBar { get; } = Brush("Shell.TitleBar", variant);

    public IBrush Search { get; } = Brush("Shell.Search", variant);

    public IBrush SearchBorder { get; } = Brush("Shell.Search.Border", variant);

    public IBrush Toolbar { get; } = Brush("Shell.Toolbar", variant);

    public IBrush ToolbarBorder { get; } = Brush("Shell.Toolbar.Border", variant);

    public IBrush IconBlue { get; } = Brush("Shell.Icon.Blue", variant);

    public IBrush IconFolder { get; } = Brush("Shell.Icon.Folder", variant);

    public IBrush Start { get; } = Brush("Shell.Toolbar.Start", variant);

    public IBrush StartIcon { get; } = Brush("Shell.Toolbar.Start.Icon", variant);

    public IBrush StatusBar { get; } = Brush("Shell.StatusBar", variant);

    public IBrush EditorBackground { get; } = Brush("Shell.Editor.Background", variant);

    public IBrush EditorForeground { get; } = Brush("Shell.Editor.Foreground", variant);

    public IBrush CurrentLine { get; } = Brush("Shell.Editor.CurrentLine", variant);

    public IBrush Accent { get; } = Brush("Shell.Accent", variant);

    public IBrush Link { get; } = Brush("Shell.Link", variant);

    public IBrush Muted { get; } = Brush("Shell.MutedForeground", variant);

    public IBrush Panel { get; } = Brush("Shell.Panel", variant);

    public IBrush Caption { get; } = Brush("Shell.Caption.Active", variant);

    public IBrush Selection { get; } = Brush("Shell.Selection", variant);

    public IBrush Foreground { get; } = Brush("Shell.Foreground", variant);

    public IBrush GutterBackground { get; } = Brush("Shell.Gutter.Background", variant);

    public double Gutter { get; } = Value("Shell.Gutter", variant, 4.0);

    public CornerRadius PanelCorners { get; } = Value("Shell.Panel.CornerRadius.Bottom", variant, default(CornerRadius));

    private static IBrush Brush(string key, ThemeVariant variant) => Lookup(key, variant) as IBrush ?? s_none;

    private static T Value<T>(string key, ThemeVariant variant, T fallback) => Lookup(key, variant) is T value ? value : fallback;

    private static object? Lookup(string key, ThemeVariant variant) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, variant, out var value) ? value : null;
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

    public string Description => Family.Description;

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
        Modes = family is null ? [] : [new ThemeModeOption(null, "Follow System"), .. family.Family.Variants.Select(v => new ThemeModeOption(v, ThemeService.VariantTitle(v)))];
        SetSilently(ref _selectedMode, Modes.FirstOrDefault(m => m.Variant == parsed?.Variant), nameof(SelectedMode));
        SetSilently(ref _selectedPluginTheme, parsed is null ? PluginThemes.FirstOrDefault(p => p.Value == setting) : null, nameof(SelectedPluginTheme));
    }

    private void SetSilently<T>(ref T field, T value, string property)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(property);
        }
    }
}
