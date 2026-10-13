using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench;

namespace Codalus.Shell.Theming;

public sealed record ThemeChoice(string Id, string Title);

/// <summary>
/// Applies <c>workbench.colorTheme</c>: Visual Studio's Blue, Light and Dark (M29), High Contrast, or Follow System, which
/// tracks the OS light/dark preference and its high-contrast setting. Switching is live. The resolved theme is exposed as
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

    /// <summary>Visual Studio's Blue theme: Light's Fluent colors, its own chrome tokens.</summary>
    public static readonly ThemeVariant Blue = new("Blue", ThemeVariant.Light);

    public static IReadOnlyList<ThemeChoice> Choices { get; } =
    [
        new(FollowSystem, "Follow System"),
        new("blue", "Blue"),
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

    /// <summary>The resolved theme: <c>blue</c>, <c>light</c>, <c>dark</c> or <c>highContrast</c>.</summary>
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
            "blue" => Blue,
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

    /// <summary>Whether a resolved theme has light surfaces (Blue and Light): the editor, grammar and terminal colors follow.</summary>
    public static bool IsLight(string theme) => theme is "light" or "blue";

    internal static string Resolve(string setting, PlatformColorValues? system) => setting switch
    {
        "blue" or "light" or "dark" or "highContrast" => setting,
        _ when system?.ContrastPreference == ColorContrastPreference.High => "highContrast",
        _ when system?.ThemeVariant == PlatformThemeVariant.Light => "light",
        _ => "dark",
    };
}
