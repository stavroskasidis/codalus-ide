using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench;

namespace DaedalusStudio.Shell.Theming;

public sealed record ThemeChoice(string Id, string Title);

/// <summary>
/// Applies <c>workbench.colorTheme</c>: Light, Dark, High Contrast, or Follow System, which tracks the OS
/// light/dark preference and its high-contrast setting. Switching is live. The resolved theme is exposed as
/// the <c>theme</c> context key.
/// </summary>
public sealed class ThemeService
{
    public const string SettingKey = "workbench.colorTheme";
    public const string FollowSystem = "followSystem";

    /// <summary>
    /// A custom variant; anything not overridden for it falls back to Dark. Fluent palettes only support Light and
    /// Dark, so the high-contrast Fluent colors are overridden in Theming/Tokens.axaml instead.
    /// </summary>
    public static readonly ThemeVariant HighContrast = new("HighContrast", ThemeVariant.Dark);

    public static IReadOnlyList<ThemeChoice> Choices { get; } =
    [
        new(FollowSystem, "Follow System"),
        new("light", "Light"),
        new("dark", "Dark"),
        new("highContrast", "High Contrast"),
    ];

    private readonly Application _app;
    private readonly WorkbenchServices _workbench;
    private string? _preview;

    public ThemeService(Application app, WorkbenchServices workbench)
    {
        _app = app;
        _workbench = workbench;
        workbench.Settings.Changed += (_, e) =>
        {
            if (e.Affects(SettingKey))
            {
                Ui.Post(Apply);
            }
        };
        if (app.PlatformSettings is { } platform)
        {
            platform.ColorValuesChanged += (_, _) => Ui.Post(Apply);
        }

        Apply();
    }

    /// <summary>The resolved theme: <c>light</c>, <c>dark</c> or <c>highContrast</c>.</summary>
    public string ActiveTheme { get; private set; } = string.Empty;

    /// <summary>The configured choice (may be <see cref="FollowSystem"/>).</summary>
    public string Setting => _workbench.Settings.GetString(SettingKey, FollowSystem);

    public event EventHandler? Changed;

    /// <summary>Shows <paramref name="themeId"/> without saving it (theme picker); null ends the preview.</summary>
    public void Preview(string? themeId)
    {
        _preview = themeId;
        Apply();
    }

    public void Apply()
    {
        var resolved = Resolve(_preview ?? Setting, _app.PlatformSettings?.GetColorValues());
        _app.RequestedThemeVariant = resolved switch
        {
            "light" => ThemeVariant.Light,
            "highContrast" => HighContrast,
            _ => ThemeVariant.Dark,
        };

        if (resolved != ActiveTheme)
        {
            ActiveTheme = resolved;
            _workbench.Context.Set("theme", resolved);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal static string Resolve(string setting, PlatformColorValues? system) => setting switch
    {
        "light" or "dark" or "highContrast" => setting,
        _ when system?.ContrastPreference == ColorContrastPreference.High => "highContrast",
        _ when system?.ThemeVariant == PlatformThemeVariant.Light => "light",
        _ => "dark",
    };
}
