using Avalonia;
using Avalonia.Media;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Theming;

/// <summary>
/// The UI's font (#556): <c>workbench.fontFamily</c> (empty: the system's UI font) and <c>workbench.fontSize</c> (the body
/// text's size; 12, a classic IDE's 9 pt). They set the app's <c>Shell.FontFamily</c> resource, which windows and Fluent's
/// controls draw with, and the four <c>Shell.FontSize</c> tokens, the others in proportion to the body's (11, 14 and 18
/// at 12), and the rows that hold a line of it (<c>Shell.Size.Row</c>, <c>Shell.Size.TreeRow</c>). Views use those
/// resources dynamically, so a change shows at once. The editor's font is its own
/// (<c>editor.fontFamily</c>, <see cref="EditorFont"/>).
/// </summary>
public sealed class UiFont : IDisposable
{
    public const string FamilySetting = "workbench.fontFamily";
    public const string SizeSetting = "workbench.fontSize";

    public const string FamilyResource = "Shell.FontFamily";

    /// <summary>Fluent's controls' font: the same family, so a button's text isn't another font.</summary>
    public const string FluentFamilyResource = "ContentControlThemeFontFamily";

    public const double DefaultSize = 12;
    public const double MinSize = 8;
    public const double MaxSize = 24;

    /// <summary>Each size token, as its size at the default body size.</summary>
    public static IReadOnlyList<(string Key, double Size)> SizeTokens { get; } =
    [
        ("Shell.FontSize.Small", 11),
        ("Shell.FontSize.Body", 12),
        ("Shell.FontSize.Heading", 14),
        ("Shell.FontSize.Title", 18),
    ];

    /// <summary>The rows that hold a line of UI text, as their height at the default body size: they grow and shrink with it.</summary>
    public static IReadOnlyList<(string Key, double Size)> RowTokens { get; } =
    [
        ("Shell.Size.Row", 22),
        ("Shell.Size.TreeRow", 24),
    ];

    private readonly Application _app;
    private readonly SettingsService _settings;
    private (string? Family, double Size)? _preview;

    public UiFont(Application app, SettingsService settings)
    {
        _app = app;
        _settings = settings;
        Update();
        settings.Changed += OnSettingsChanged;
    }

    public FontFamily Family { get; private set; } = FontFamily.Default;

    public double Size { get; private set; } = DefaultSize;

    /// <summary>The family for the setting's value: the system's UI font when empty.</summary>
    public static FontFamily FamilyOf(string? setting) =>
        string.IsNullOrWhiteSpace(setting) ? FontFamily.Default : FontFamily.Parse(setting.Trim());

    /// <summary>The body size for the setting's value, kept to what the UI's rows hold.</summary>
    public static double SizeOf(double setting) =>
        double.IsFinite(setting) && setting > 0 ? Math.Clamp(setting, MinSize, MaxSize) : DefaultSize;

    /// <summary>
    /// A token's size at body size <paramref name="body"/>: in proportion to its default, to the half pixel (the default
    /// body size gives each token its own default exactly).
    /// </summary>
    public static double TokenSize(double defaultTokenSize, double body) =>
        Math.Round(defaultTokenSize * body / DefaultSize * 2, MidpointRounding.AwayFromZero) / 2;

    /// <summary>A row's height at body size <paramref name="body"/>: in proportion to its default, to the whole pixel.</summary>
    public static double RowSize(double defaultRowSize, double body) =>
        Math.Round(defaultRowSize * body / DefaultSize, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Shows a family and size without saving them (the first-run setup's appearance step, as it previews a theme);
    /// null goes back to the settings'.
    /// </summary>
    public void Preview((string? Family, double Size)? preview)
    {
        _preview = preview;
        Update();
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Affects(FamilySetting) || e.Affects(SizeSetting))
        {
            Ui.Post(Update);
        }
    }

    private void Update()
    {
        Family = FamilyOf(_preview is { } preview ? preview.Family : _settings.GetString(FamilySetting));
        Size = SizeOf(_preview?.Size ?? _settings.GetDouble(SizeSetting, DefaultSize));
        var resources = _app.Resources;
        resources[FamilyResource] = Family;
        resources[FluentFamilyResource] = Family;
        foreach (var (key, size) in SizeTokens)
        {
            resources[key] = TokenSize(size, Size);
        }

        foreach (var (key, size) in RowTokens)
        {
            resources[key] = RowSize(size, Size);
        }
    }
}
