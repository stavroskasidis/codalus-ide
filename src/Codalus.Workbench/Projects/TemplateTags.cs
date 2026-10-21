using Codalus.Workbench.Localization;

namespace Codalus.Workbench.Projects;

/// <summary>
/// New Project's tags (ADR 0082): the platforms and project types of a template, from its classifications and its IDE
/// host file's tags, in the classic IDE's vocabulary, and the icon of its row. The ids stay English (they are the
/// filters' values, remembered across starts); <see cref="PlatformLabel"/>, <see cref="ProjectTypeLabel"/> and
/// <see cref="LanguageLabel"/> give the display language's words.
/// </summary>
public static class TemplateTags
{
    public const string GenericIcon = "template-generic";

    /// <summary>The platform vocabulary, in the filter's order.</summary>
    public static IReadOnlyList<string> Platforms { get; } = ["Windows", "Linux", "macOS", "Android", "iOS", "Mac Catalyst", "tvOS", "Web", "Cloud"];

    /// <summary>The project-type vocabulary, in the filter's order.</summary>
    public static IReadOnlyList<string> ProjectTypes { get; } = ["Console", "Library", "Desktop", "Web", "Test", "Service", "Mobile", "Cloud", "Games", "IoT", "AI", "Other"];

    private static readonly string[] s_crossPlatform = ["Windows", "Linux", "macOS"];

    private static readonly string[] s_maui = ["Android", "iOS", "Mac Catalyst", "Windows"];

    /// <summary>A classification (or an IDE host file's project-type tag) → the project types it means.</summary>
    private static IEnumerable<string> TypesOf(string classification) => classification.Trim().ToLowerInvariant() switch
    {
        "console" => ["Console"],
        "library" or "class library" => ["Library"],
        "desktop" or "wpf" or "winforms" or "windows forms" or "winui" or "avalonia" => ["Desktop"],
        "web" or "blazor" or "mvc" or "razor pages" or "web api" or "api" or "webassembly" or "razor" => ["Web"],
        "test" or "xunit" or "nunit" or "mstest" or "playwright" or "tunit" => ["Test"],
        "service" or "worker" or "grpc" or "wcf" => ["Service"],
        "mobile" or "maui" or "android" or "ios" => ["Mobile"],
        "cloud" or "azure" or "aspire" => ["Cloud"],
        "games" or "game" => ["Games"],
        "iot" => ["IoT"],
        "ai" or "mcp" => ["AI"],
        "other" => ["Other"],
        _ => [],
    };

    /// <summary>The project types of a template: its classifications', minus its host file's removals, plus its additions; else Other.</summary>
    public static IReadOnlyList<string> ProjectTypesOf(IEnumerable<string> classifications, IEnumerable<HostTags> hostTags)
    {
        var types = new HashSet<string>(classifications.SelectMany(TypesOf), StringComparer.Ordinal);
        foreach (var tags in hostTags.Where(t => string.Equals(t.Type, "projectType", StringComparison.OrdinalIgnoreCase)))
        {
            types.ExceptWith(tags.Remove.SelectMany(TypesOf));
            types.UnionWith(tags.Add.SelectMany(TypesOf));
        }

        return types.Count == 0 ? ["Other"] : [.. ProjectTypes.Where(types.Contains)];
    }

    /// <summary>
    /// The platforms of a template: Windows for one whose projects run only there, Android/iOS/Mac Catalyst/Windows for
    /// MAUI, a mobile OS's own, else Windows/Linux/macOS (plus Web for WebAssembly and Cloud for cloud templates); then
    /// its host file's platform tags remove and add.
    /// </summary>
    public static IReadOnlyList<string> PlatformsOf(IEnumerable<string> classifications, IEnumerable<HostTags> hostTags, bool runsOnlyOnWindows)
    {
        var classes = new HashSet<string>(classifications.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);
        var platforms = new HashSet<string>(StringComparer.Ordinal);
        if (runsOnlyOnWindows)
        {
            platforms.Add("Windows");
        }
        else if (classes.Contains("MAUI"))
        {
            platforms.UnionWith(s_maui);
        }
        else if (classes.Overlaps(["Android", "iOS", "tvOS", "Mac Catalyst"]))
        {
            platforms.UnionWith(Platforms.Where(classes.Contains));
        }
        else
        {
            platforms.UnionWith(s_crossPlatform);
            if (classes.Contains("WebAssembly"))
            {
                platforms.Add("Web");
            }

            if (classes.Overlaps(["Cloud", "Azure", "Aspire"]))
            {
                platforms.Add("Cloud");
            }
        }

        foreach (var tags in hostTags.Where(t => string.Equals(t.Type, "platform", StringComparison.OrdinalIgnoreCase)))
        {
            platforms.ExceptWith(tags.Remove.Select(Platform).OfType<string>());
            platforms.UnionWith(tags.Add.Select(Platform).OfType<string>());
        }

        return [.. Platforms.Where(platforms.Contains)];
    }

    /// <summary>A platform tag in any spelling → the vocabulary's id (<c>macos</c> → <c>macOS</c>), or null.</summary>
    private static string? Platform(string tag) =>
        Platforms.FirstOrDefault(p => string.Equals(p, tag.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? (tag.Trim().Equals("MacCatalyst", StringComparison.OrdinalIgnoreCase) ? "Mac Catalyst" : null);

    /// <summary>
    /// The icon of a row: <c>template-test</c>, <c>-library</c>, <c>-mobile</c>, <c>-desktop</c>, <c>-service</c>,
    /// <c>-web</c>, <c>-cloud</c>, <c>-console</c>, else <c>template-generic</c> (the first that applies).
    /// </summary>
    public static string IconKind(IReadOnlyList<string> projectTypes, IEnumerable<string> classifications)
    {
        bool Has(string type) => projectTypes.Contains(type, StringComparer.Ordinal);
        if (Has("Test"))
        {
            return "template-test";
        }

        if (classifications.Any(c => string.Equals(c, "Library", StringComparison.OrdinalIgnoreCase)))
        {
            return "template-library";
        }

        if (classifications.Any(c => string.Equals(c, "Worker", StringComparison.OrdinalIgnoreCase)))
        {
            return "template-service";
        }

        return Has("Mobile") ? "template-mobile"
            : Has("Desktop") ? "template-desktop"
            : Has("Web") ? "template-web"
            : Has("Service") ? "template-service"
            : Has("Cloud") ? "template-cloud"
            : Has("Console") ? "template-console"
            : Has("Library") ? "template-library"
            : GenericIcon;
    }

    /// <summary>A language code as New Project shows it: <c>VB</c> → Visual Basic; C# and F# as they are.</summary>
    public static string LanguageLabel(string language) => language switch
    {
        "VB" => Localizer.Get("templates.language.vb", "Visual Basic"),
        _ => language,
    };

    public static string PlatformLabel(string platform) => platform switch
    {
        "Windows" => Localizer.Get("templates.platform.windows", "Windows"),
        "Linux" => Localizer.Get("templates.platform.linux", "Linux"),
        "macOS" => Localizer.Get("templates.platform.macos", "macOS"),
        "Android" => Localizer.Get("templates.platform.android", "Android"),
        "iOS" => Localizer.Get("templates.platform.ios", "iOS"),
        "Mac Catalyst" => Localizer.Get("templates.platform.macCatalyst", "Mac Catalyst"),
        "tvOS" => Localizer.Get("templates.platform.tvos", "tvOS"),
        "Web" => Localizer.Get("templates.platform.web", "Web"),
        "Cloud" => Localizer.Get("templates.platform.cloud", "Cloud"),
        _ => platform,
    };

    public static string ProjectTypeLabel(string type) => type switch
    {
        "Console" => Localizer.Get("templates.type.console", "Console"),
        "Library" => Localizer.Get("templates.type.library", "Library"),
        "Desktop" => Localizer.Get("templates.type.desktop", "Desktop"),
        "Web" => Localizer.Get("templates.type.web", "Web"),
        "Test" => Localizer.Get("templates.type.test", "Test"),
        "Service" => Localizer.Get("templates.type.service", "Service"),
        "Mobile" => Localizer.Get("templates.type.mobile", "Mobile"),
        "Cloud" => Localizer.Get("templates.type.cloud", "Cloud"),
        "Games" => Localizer.Get("templates.type.games", "Games"),
        "IoT" => Localizer.Get("templates.type.iot", "IoT"),
        "AI" => Localizer.Get("templates.type.ai", "AI"),
        "Other" => Localizer.Get("templates.type.other", "Other"),
        _ => type,
    };

    /// <summary>A row's chips: its language, then its platforms, then its project types, in the display language.</summary>
    public static IReadOnlyList<string> Chips(string language, IEnumerable<string> platforms, IEnumerable<string> projectTypes) =>
        [.. (language.Length > 0 ? [LanguageLabel(language)] : Array.Empty<string>()), .. platforms.Select(PlatformLabel), .. projectTypes.Select(ProjectTypeLabel)];
}
