using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench;

namespace Codalus.Shell.Theming;

/// <summary>A value of <c>workbench.colorTheme</c> and its title, in the theme picker's order.</summary>
public sealed record ThemeChoice(string Id, string Title)
{
    /// <summary>The family the choice belongs to (<c>harbor</c>…, <c>classic</c>), or null for a plugin's theme.</summary>
    public string? Family { get; init; }

    /// <summary>The variant it pins (<c>light</c>, <c>dark</c>, <c>highContrast</c>, Classic's <c>blue</c>), or null when it follows the system.</summary>
    public string? Variant { get; init; }
}

/// <summary>A theme family (M45, ADR 0077): its id in <c>workbench.colorTheme</c>, its title and a line about it.</summary>
public sealed record ThemeFamily(string Id, string Title, string Description)
{
    /// <summary>The variants it has: Light, Dark and High contrast; Classic also has Blue.</summary>
    public IReadOnlyList<string> Variants => Id == ThemeService.Classic ? ["blue", "light", "dark", "highContrast"] : ["light", "dark", "highContrast"];
}

/// <summary>
/// Applies <c>workbench.colorTheme</c> (M45, D32, ADR 0077): a theme family (Classic, the Blue, Light, Dark and High
/// Contrast from before M45, then Harbor, Graphite, Daylight and Ember) and a variant. <c>classic</c> follows the system's
/// light, dark and high-contrast settings within the family; <c>harbor.dark</c> pins the variant. The values written
/// before M45 (<c>blue</c>, <c>light</c>, <c>dark</c>, <c>highContrast</c>, <c>followSystem</c>) read as Classic, so a
/// saved setting keeps its look; a profile without the setting gets Classic following the system (D36, the maintainer's
/// choice on 2026-10-19; it was Harbor in 0.3.0). Switching is live, and the picker previews before it
/// saves. The resolved theme is the <c>theme</c> context key (its kind: <c>light</c>, <c>dark</c> or <c>highContrast</c>;
/// Classic's Blue stays <c>blue</c>) and its family the <c>themeFamily</c> key.
/// </summary>
public sealed class ThemeService : IDisposable
{
    public const string SettingKey = "workbench.colorTheme";

    /// <summary>Classic following the system, as written before M45.</summary>
    public const string FollowSystem = "followSystem";

    public const string Classic = "classic";
    public const string Harbor = "harbor";
    public const string Graphite = "graphite";
    public const string Daylight = "daylight";
    public const string Ember = "ember";

    /// <summary>The setting's default (D36, 2026-10-19; Harbor under D32): Classic, following the system.</summary>
    public const string DefaultSetting = Classic;

    /// <summary>
    /// Classic's High Contrast; anything not overridden for it falls back to Dark. Fluent palettes only support Light and
    /// Dark, so the high-contrast Fluent colors are overridden in Theming/Tokens.axaml instead. The families' High
    /// contrast variants inherit it.
    /// </summary>
    public static readonly ThemeVariant HighContrast = new("HighContrast", ThemeVariant.Dark);

    /// <summary>Classic's Blue: Light's Fluent colors, its own chrome tokens.</summary>
    public static readonly ThemeVariant Blue = new("Blue", ThemeVariant.Light);

    public static readonly ThemeVariant HarborLight = new("Harbor.Light", ThemeVariant.Light);
    public static readonly ThemeVariant HarborDark = new("Harbor.Dark", ThemeVariant.Dark);
    public static readonly ThemeVariant HarborHighContrast = new("Harbor.HighContrast", HighContrast);
    public static readonly ThemeVariant GraphiteLight = new("Graphite.Light", ThemeVariant.Light);
    public static readonly ThemeVariant GraphiteDark = new("Graphite.Dark", ThemeVariant.Dark);
    public static readonly ThemeVariant GraphiteHighContrast = new("Graphite.HighContrast", HighContrast);
    public static readonly ThemeVariant DaylightLight = new("Daylight.Light", ThemeVariant.Light);
    public static readonly ThemeVariant DaylightDark = new("Daylight.Dark", ThemeVariant.Dark);
    public static readonly ThemeVariant DaylightHighContrast = new("Daylight.HighContrast", HighContrast);
    public static readonly ThemeVariant EmberLight = new("Ember.Light", ThemeVariant.Light);
    public static readonly ThemeVariant EmberDark = new("Ember.Dark", ThemeVariant.Dark);
    public static readonly ThemeVariant EmberHighContrast = new("Ember.HighContrast", HighContrast);

    /// <summary>The families, the default (Classic) first (D36), in every list that offers them.</summary>
    public static IReadOnlyList<ThemeFamily> Families { get; } =
    [
        new(Classic, "Classic", "The classic IDE's familiar look, in Blue, Light, Dark and High Contrast: the themes from before 0.3."),
        new(Harbor, "Harbor", "The closest to the classic IDE: a deep navy title bar, docked tool windows with gutters, a cobalt accent."),
        new(Graphite, "Graphite", "Compact and dense, for more code on the screen: hairlines instead of gutters, a teal accent."),
        new(Daylight, "Daylight", "Flat and airy: rounded tabs, the Start button filled with a violet accent."),
        new(Ember, "Ember", "Warm, with floating panels: rounded cards on the window's background, a copper accent."),
    ];

    /// <summary>Every resolved built-in theme with its variant: Classic's four, then each family's three.</summary>
    public static IReadOnlyList<(string Id, ThemeVariant Variant)> Themes { get; } =
    [
        ("blue", Blue), ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark), ("highContrast", HighContrast),
        ("harbor.light", HarborLight), ("harbor.dark", HarborDark), ("harbor.highContrast", HarborHighContrast),
        ("graphite.light", GraphiteLight), ("graphite.dark", GraphiteDark), ("graphite.highContrast", GraphiteHighContrast),
        ("daylight.light", DaylightLight), ("daylight.dark", DaylightDark), ("daylight.highContrast", DaylightHighContrast),
        ("ember.light", EmberLight), ("ember.dark", EmberDark), ("ember.highContrast", EmberHighContrast),
    ];

    /// <summary>The values written before M45, each read as Classic.</summary>
    public static IReadOnlyList<ThemeChoice> LegacyChoices { get; } =
    [
        new(FollowSystem, "Classic (follows the system; the value before 0.3)") { Family = Classic },
        new("blue", "Classic Blue (the value before 0.3)") { Family = Classic, Variant = "blue" },
        new("light", "Classic Light (the value before 0.3)") { Family = Classic, Variant = "light" },
        new("dark", "Classic Dark (the value before 0.3)") { Family = Classic, Variant = "dark" },
        new("highContrast", "Classic High Contrast (the value before 0.3)") { Family = Classic, Variant = "highContrast" },
    ];

    /// <summary>The built-in choices, family by family: following the system, then each variant.</summary>
    public static IReadOnlyList<ThemeChoice> Choices { get; } =
    [
        .. Families.SelectMany(f => f.Variants.Select(v => new ThemeChoice($"{f.Id}.{v}", Title($"{f.Id}.{v}")) { Family = f.Id, Variant = v })
            .Prepend(new ThemeChoice(f.Id, $"{f.Title} (Follow System)") { Family = f.Id })),
    ];

    private readonly Application _app;
    private readonly WorkbenchServices _workbench;
    private readonly Func<IReadOnlyList<Workbench.Plugins.ContributedTheme>> _contributed;
    private string? _preview;
    private Avalonia.Platform.IPlatformSettings? _platform;

    public ThemeService(Application app, WorkbenchServices workbench, Func<IReadOnlyList<Workbench.Plugins.ContributedTheme>>? contributed = null)
    {
        _app = app;
        _workbench = workbench;
        _contributed = contributed ?? (() => []);
        SystemColors = () => app.PlatformSettings?.GetColorValues();
        workbench.Settings.Changed += (_, e) =>
        {
            if (e.Affects(SettingKey))
            {
                Ui.Post(Apply);
            }
        };
        if (app.PlatformSettings is { } platform)
        {
            // The OS switching light and dark, or high contrast on and off: a family that follows the system follows it.
            _platform = platform;
            platform.ColorValuesChanged += OnColorValuesChanged;
        }

        Apply();
    }

    /// <summary>Stops following the OS's colors: the platform's settings outlive a shell, and would hold it.</summary>
    public void Dispose()
    {
        if (_platform is { } platform)
        {
            platform.ColorValuesChanged -= OnColorValuesChanged;
            _platform = null;
        }
    }

    private void OnColorValuesChanged(object? sender, Avalonia.Platform.PlatformColorValues e) => Ui.Post(Apply);

    /// <summary>
    /// The resolved theme: Classic's <c>blue</c>, <c>light</c>, <c>dark</c> or <c>highContrast</c> (also a plugin's theme's
    /// base), or a family's variant such as <c>harbor.dark</c>.
    /// </summary>
    public string ActiveTheme { get; private set; } = string.Empty;

    /// <summary>The resolved theme's family: <c>harbor</c>, <c>graphite</c>, <c>daylight</c>, <c>ember</c> or <c>classic</c>.</summary>
    public string ActiveFamily { get; private set; } = string.Empty;

    /// <summary>The plugin's theme in use over <see cref="ActiveTheme"/>, or null (API 1.0, M22a).</summary>
    public Workbench.Plugins.ContributedTheme? ActiveContributed { get; private set; }

    /// <summary>The built-in themes, then the plugins' (by label).</summary>
    public IReadOnlyList<ThemeChoice> AllChoices =>
        [.. Choices, .. _contributed().OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase).Select(t => new ThemeChoice(t.Id, t.Label))];

    /// <summary>The plugins' themes.</summary>
    public IReadOnlyList<Workbench.Plugins.ContributedTheme> Contributed => _contributed();

    /// <summary>The system's light, dark and high-contrast settings (the tests stand in for the OS here).</summary>
    internal Func<PlatformColorValues?> SystemColors { get; set; }

    /// <summary>The configured choice (may follow the system).</summary>
    public string Setting => _workbench.Settings.GetString(SettingKey, DefaultSetting);

    public event EventHandler? Changed;

    /// <summary>Shows <paramref name="themeId"/> without saving it (theme picker, Options); null ends the preview.</summary>
    public void Preview(string? themeId)
    {
        _preview = themeId;
        Apply();
    }

    public void Apply()
    {
        var setting = _preview ?? Setting;
        var contributed = _contributed().FirstOrDefault(t => t.Id == setting);
        // A plugin's theme applies over Classic's variant for its base (M22a), as it did before the families.
        var resolved = contributed?.Base ?? Resolve(setting, SystemColors());
        _app.RequestedThemeVariant = VariantOf(resolved);
        if (resolved != ActiveTheme || contributed != ActiveContributed)
        {
            ActiveTheme = resolved;
            ActiveFamily = FamilyOf(resolved);
            ActiveContributed = contributed;
            _workbench.Context.Set("theme", resolved == "blue" ? resolved : KindOf(resolved));
            _workbench.Context.Set("themeFamily", ActiveFamily);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The family and pinned variant a setting names (D32): <c>harbor</c> → (harbor, null), <c>harbor.dark</c> → (harbor,
    /// dark), today's values as Classic (<c>dark</c> → (classic, dark), <c>followSystem</c> → (classic, null)). Null for
    /// anything else (a plugin's theme).
    /// </summary>
    public static (string Family, string? Variant)? Parse(string? setting)
    {
        if (string.IsNullOrEmpty(setting))
        {
            return null;
        }

        if (setting == FollowSystem)
        {
            return (Classic, null);
        }

        if (setting is "blue" or "light" or "dark" or "highContrast")
        {
            return (Classic, setting);
        }

        var dot = setting.IndexOf('.', StringComparison.Ordinal);
        var familyId = dot < 0 ? setting : setting[..dot];
        if (Families.FirstOrDefault(f => f.Id == familyId) is not { } family)
        {
            return null;
        }

        if (dot < 0)
        {
            return (family.Id, null);
        }

        var variant = setting[(dot + 1)..];
        return family.Variants.Contains(variant) ? (family.Id, variant) : null;
    }

    /// <summary>
    /// The theme a setting resolves to with the system's colors: a pinned variant as it is, or the system's high contrast,
    /// then its light or dark, within the family. An unknown value resolves as the default.
    /// </summary>
    internal static string Resolve(string setting, PlatformColorValues? system)
    {
        var (family, variant) = Parse(setting) ?? (DefaultSetting, null);
        variant ??= system?.ContrastPreference == ColorContrastPreference.High ? "highContrast"
            : system?.ThemeVariant == PlatformThemeVariant.Light ? "light"
            : "dark";
        return family == Classic ? variant : $"{family}.{variant}";
    }

    /// <summary>The Avalonia theme variant of a resolved theme.</summary>
    public static ThemeVariant VariantOf(string theme) =>
        Themes.FirstOrDefault(t => t.Id == theme).Variant ?? ThemeVariant.Dark;

    /// <summary>The family of a resolved theme (<c>classic</c> for Classic's and a plugin's base).</summary>
    public static string FamilyOf(string theme)
    {
        var dot = theme.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? Classic : theme[..dot];
    }

    /// <summary>A resolved theme's kind: <c>light</c> (Classic's Blue too), <c>dark</c> or <c>highContrast</c>.</summary>
    public static string KindOf(string theme)
    {
        var variant = theme[(theme.IndexOf('.', StringComparison.Ordinal) + 1)..];
        return variant switch
        {
            "light" or "blue" => "light",
            "highContrast" => "highContrast",
            _ => "dark",
        };
    }

    /// <summary>Whether a resolved theme has light surfaces (Blue and every Light): the editor, grammar and terminal colors follow.</summary>
    public static bool IsLight(string theme) => KindOf(theme) == "light";

    /// <summary>Whether a theme variant is a High Contrast one (Classic's or a family's): icons are monochrome there.</summary>
    public static bool IsHighContrast(ThemeVariant? variant)
    {
        for (var v = variant; v is not null; v = v.InheritVariant)
        {
            if (v == HighContrast)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A resolved theme's or a setting's title: "Harbor Dark", "Classic Blue", "Ember (Follow System)".</summary>
    public static string Title(string theme)
    {
        if (Parse(theme) is not var (family, variant))
        {
            return theme;
        }

        var familyTitle = Families.First(f => f.Id == family).Title;
        return variant switch
        {
            null => $"{familyTitle} (Follow System)",
            _ => $"{familyTitle} {VariantTitle(variant)}",
        };
    }

    /// <summary>
    /// The name color customizations take for a resolved theme (<c>"[Harbor Dark]"</c>): a family's title and variant, or
    /// Classic's variant alone (<c>"[Dark]"</c>, <c>"[High Contrast]"</c>), as before the families.
    /// </summary>
    public static string CustomizationTitle(string theme) => FamilyOf(theme) == Classic ? VariantTitle(theme) : Title(theme);

    /// <summary>A variant's title: Light, Dark, High Contrast, Blue.</summary>
    public static string VariantTitle(string variant) => variant switch
    {
        "light" => "Light",
        "dark" => "Dark",
        "highContrast" => "High Contrast",
        "blue" => "Blue",
        _ => variant,
    };

    /// <summary>A setting in its M45 form: today's values as Classic's (<c>dark</c> → <c>classic.dark</c>, <c>followSystem</c> → <c>classic</c>); others as they are.</summary>
    public static string Normalize(string setting) => Parse(setting) is var (family, variant) ? SettingFor(family, variant) : setting;

    /// <summary>The setting's value for a family and a pinned variant (null follows the system).</summary>
    public static string SettingFor(string family, string? variant) => variant is null ? family : $"{family}.{variant}";
}
