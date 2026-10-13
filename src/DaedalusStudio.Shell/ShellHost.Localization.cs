using System.Globalization;
using DaedalusStudio.Workbench.Localization;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell;

/// <summary>
/// The display language (M23b, <see cref="Localizer"/>): <c>--locale</c>, else <c>DAEDALUS_LOCALE</c>, else the setting
/// <see cref="LocaleSetting"/>, else the operating system's language when there is a catalogue for it. The catalogues
/// are <c>Localization/&lt;locale&gt;.json</c>, embedded; <c>en.json</c> is the source translators work from. It is
/// read once, before the built-ins register (their titles are translated as they register); a change applies at the next
/// start. The pseudo-locales <c>qps-ploc</c> and <c>qps-plocm</c> (right to left) are for finding untranslated text.
/// </summary>
public sealed partial class ShellHost
{
    public const string LocaleSetting = "workbench.locale";
    private const string CatalogPrefix = "DaedalusStudio.Shell.Localization.";

    /// <summary>The locales with a catalogue, English first.</summary>
    public static IReadOnlyList<string> AvailableLocales { get; } =
        [.. typeof(ShellHost).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(CatalogPrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[CatalogPrefix.Length..^".json".Length])
            .OrderBy(l => l == "en" ? 0 : 1).ThenBy(l => l, StringComparer.Ordinal)];

    internal static SettingDescriptor LocaleDescriptor() =>
        SettingDescriptor.Create(LocaleSetting, "\"\"", SettingType.String, DaedalusStudio.Workbench.Commands.ContributionSource.Shell) with
        {
            Title = "Display Language",
            Category = "Environment",
            Description = "The language of menus, commands and windows. Empty: the operating system's, when Daedalus Studio has it. qps-ploc and qps-plocm are pseudo-languages for testing translations (the second lays the window out right to left). Applies at the next start.",
            Enum = ["", .. AvailableLocales, Localizer.PseudoLocale, Localizer.PseudoMirroredLocale],
            EnumDescriptions = ["The operating system's", .. AvailableLocales.Select(DisplayName), "Pseudo-language", "Pseudo-language, right to left"],
            Scope = SettingScope.User,
        };

    private void LoadLocalization()
    {
        Workbench.Settings.Register([LocaleDescriptor()]);
        var locale = Options.Locale ?? (Environment.GetEnvironmentVariable("DAEDALUS_LOCALE") is { Length: > 0 } variable ? variable : null);
        locale ??= Workbench.Settings.GetString(LocaleSetting) is { Length: > 0 } chosen ? chosen : null;
        locale ??= OperatingSystemLocale();
        Localizer.Load(locale, Catalog);
        Workbench.Context.Set("locale", Localizer.Locale);

        // The plugin hosts (and every process started from here) learn it from the environment: PluginLocalization (API 0.2.9).
        Environment.SetEnvironmentVariable("DAEDALUS_DISPLAY_LOCALE", Localizer.Locale);
    }

    /// <summary>The OS's language when there's a catalogue for it (or its language without the region), else English.</summary>
    private static string OperatingSystemLocale()
    {
        var culture = CultureInfo.CurrentUICulture;
        return AvailableLocales.FirstOrDefault(l => string.Equals(l, culture.Name, StringComparison.OrdinalIgnoreCase))
            ?? AvailableLocales.FirstOrDefault(l => string.Equals(l, culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
            ?? "en";
    }

    private static Stream? Catalog(string locale) =>
        typeof(ShellHost).Assembly.GetManifestResourceStream(CatalogPrefix + locale + ".json");

    private static string DisplayName(string locale)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(locale);
            return culture.NativeName == culture.EnglishName ? culture.EnglishName : $"{culture.NativeName} ({culture.EnglishName})";
        }
        catch (CultureNotFoundException)
        {
            return locale;
        }
    }
}
