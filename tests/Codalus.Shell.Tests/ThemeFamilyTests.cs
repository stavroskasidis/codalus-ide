using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Codalus.Shell.Editor;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// M45 (D32, ADR 0077): the theme families Harbor, Graphite, Daylight and Ember, each Light, Dark and High contrast, with
/// Classic (Blue, Light, Dark, High Contrast) kept for saved settings. The colors are checked against the design's
/// tables (docs/design/shell-themes.md), which the tests read.
/// </summary>
public partial class ThemeFamilyTests
{
    private static readonly string[] s_families = ["harbor", "graphite", "daylight", "ember"];
    private static readonly string[] s_variants = ["light", "dark", "highContrast"];

    private static IEnumerable<string> FamilyThemes => s_families.SelectMany(f => s_variants.Select(v => $"{f}.{v}"));

    // ------------------------------------------------------------------ the setting

    [Fact]
    public void The_setting_reads_a_family_a_variant_and_todays_values_as_Classic()
    {
        Assert.Equal(("harbor", (string?)null), ThemeService.Parse("harbor"));
        Assert.Equal(("ember", "dark"), ThemeService.Parse("ember.dark"));
        Assert.Equal(("graphite", "highContrast"), ThemeService.Parse("graphite.highContrast"));
        Assert.Equal(("classic", "blue"), ThemeService.Parse("classic.blue"));
        Assert.Equal(("classic", (string?)null), ThemeService.Parse("classic"));

        // D32's mapping: the values written before the families.
        Assert.Equal(("classic", (string?)null), ThemeService.Parse("followSystem"));
        Assert.Equal(("classic", "blue"), ThemeService.Parse("blue"));
        Assert.Equal(("classic", "light"), ThemeService.Parse("light"));
        Assert.Equal(("classic", "dark"), ThemeService.Parse("dark"));
        Assert.Equal(("classic", "highContrast"), ThemeService.Parse("highContrast"));
        Assert.Equal("classic.dark", ThemeService.Normalize("dark"));
        Assert.Equal("classic", ThemeService.Normalize("followSystem"));

        // Not a built-in theme: a plugin's, or nothing.
        Assert.Null(ThemeService.Parse("harbor.blue")); // Blue is Classic's only
        Assert.Null(ThemeService.Parse("midnight"));
        Assert.Null(ThemeService.Parse("harbor.dark.extra"));
        Assert.Null(ThemeService.Parse(""));
    }

    [Fact]
    public void A_family_follows_the_systems_light_dark_and_high_contrast()
    {
        var light = new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Light };
        var dark = new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Dark };
        var high = new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Light, ContrastPreference = ColorContrastPreference.High };
        Assert.Equal("harbor.light", ThemeService.Resolve("harbor", light));
        Assert.Equal("harbor.dark", ThemeService.Resolve("harbor", dark));
        Assert.Equal("harbor.highContrast", ThemeService.Resolve("harbor", high));
        Assert.Equal("ember.highContrast", ThemeService.Resolve("ember", high));

        // A pinned variant doesn't follow.
        Assert.Equal("daylight.dark", ThemeService.Resolve("daylight.dark", light));
        Assert.Equal("daylight.dark", ThemeService.Resolve("daylight.dark", high));

        // Classic, as Follow System did.
        Assert.Equal("light", ThemeService.Resolve("followSystem", light));
        Assert.Equal("highContrast", ThemeService.Resolve("classic", high));
        Assert.Equal("blue", ThemeService.Resolve("blue", dark));

        // Unknown values and no system information: the default (D36), Classic, dark.
        Assert.Equal("light", ThemeService.Resolve("neon", light));
        Assert.Equal("highContrast", ThemeService.Resolve("neon", high));
        Assert.Equal("harbor.dark", ThemeService.Resolve("harbor", null));
        Assert.Equal("dark", ThemeService.Resolve(ThemeService.DefaultSetting, null));
    }

    [Fact]
    public void The_default_is_Classic_following_each_mode_of_the_system()
    {
        // D36 (the maintainer, 2026-10-19): Classic is the default, so a profile without the setting follows the OS
        // within Classic, high contrast first.
        Assert.Equal("classic", ThemeService.DefaultSetting);
        Assert.Equal("light", ThemeService.Resolve(ThemeService.DefaultSetting, new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Light }));
        Assert.Equal("dark", ThemeService.Resolve(ThemeService.DefaultSetting, new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Dark }));
        Assert.Equal("highContrast", ThemeService.Resolve(ThemeService.DefaultSetting, new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Light, ContrastPreference = ColorContrastPreference.High }));
        Assert.Equal("highContrast", ThemeService.Resolve(ThemeService.DefaultSetting, new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Dark, ContrastPreference = ColorContrastPreference.High }));
        Assert.Equal(ThemeService.HighContrast, ThemeService.VariantOf("highContrast"));

        // Classic comes first wherever the families are listed: the families, the quick pick's choices, the setting's values.
        Assert.Equal(["classic", "harbor", "graphite", "daylight", "ember"], ThemeService.Families.Select(f => f.Id));
        Assert.Equal(["classic", "classic.blue", "classic.light", "classic.dark", "classic.highContrast", "harbor"], ThemeService.Choices.Take(6).Select(c => c.Id));
        Assert.Equal("classic", BuiltIns.ColorThemeSetting(ThemeService.Choices).Enum![0]);
    }

    [Fact]
    public void The_setting_defaults_to_Classic_and_offers_every_family_variant_and_todays_values()
    {
        var descriptor = BuiltIns.ColorThemeSetting(ThemeService.Choices);
        Assert.Equal("classic", descriptor.Default.GetString());
        Assert.Contains("The families are Classic (Blue, Light, Dark and High Contrast; the default), Harbor, Graphite, Daylight and Ember.", descriptor.Description, StringComparison.Ordinal);
        Assert.Equal(21, ThemeService.Choices.Count); // four families and Classic, each following the system or pinned
        foreach (var theme in FamilyThemes.Concat(s_families).Concat(["classic", "classic.blue", "classic.light", "classic.dark", "classic.highContrast"]))
        {
            Assert.Contains(theme, descriptor.Enum!);
        }

        // Today's values stay valid: a saved setting isn't reported as an error.
        foreach (var legacy in new[] { "followSystem", "blue", "light", "dark", "highContrast" })
        {
            Assert.Null(descriptor.Validate(System.Text.Json.JsonSerializer.SerializeToElement(legacy)));
        }

        Assert.Equal(descriptor.Enum!.Count, descriptor.EnumDescriptions!.Count);
        Assert.Equal("Harbor (Follow System)", ThemeService.Title("harbor"));
        Assert.Equal("Ember High Contrast", ThemeService.Title("ember.highContrast"));
        Assert.Equal("Classic Blue", ThemeService.Title("blue"));
        Assert.Equal("High Contrast", ThemeService.CustomizationTitle("highContrast")); // Classic's customizations keep their names
        Assert.Equal("Graphite Dark", ThemeService.CustomizationTitle("graphite.dark"));
    }

    [Fact]
    public void Kinds_families_and_variants_of_the_resolved_themes()
    {
        Assert.Equal(16, ThemeService.Themes.Count);
        Assert.Equal(16, ThemeService.Themes.Select(t => t.Variant).Distinct().Count());
        foreach (var (id, variant) in ThemeService.Themes)
        {
            Assert.Equal(variant, ThemeService.VariantOf(id));
            Assert.Equal(id.EndsWith("highContrast", StringComparison.Ordinal), ThemeService.IsHighContrast(variant));
            var kind = ThemeService.KindOf(id);
            var root = variant;
            while (root.InheritVariant is { } parent)
            {
                root = parent;
            }

            Assert.Equal(kind == "light" ? ThemeVariant.Light : ThemeVariant.Dark, root); // Fluent's palette follows the kind
        }

        Assert.Equal("harbor", ThemeService.FamilyOf("harbor.dark"));
        Assert.Equal("classic", ThemeService.FamilyOf("blue"));
        Assert.Equal("light", ThemeService.KindOf("blue"));
        Assert.True(ThemeService.IsLight("daylight.light"));
        Assert.False(ThemeService.IsLight("daylight.dark"));
    }

    // ------------------------------------------------------------------ the design's colors

    /// <summary>A family's table in docs/design/shell-themes.md: token (or <c>editor:keyword</c>) → light, dark, high contrast.</summary>
    internal static Dictionary<string, string[]> DesignTable(string family)
    {
        var text = File.ReadAllText(System.IO.Path.Combine(ShellFixture.RepoRoot, "docs", "design", "shell-themes.md"));
        var section = text.Split($"### {char.ToUpperInvariant(family[0])}{family[1..]} colors")[1].Split("\n## ")[0].Split("\n### ")[0];
        var table = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in section.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("| Role", StringComparison.Ordinal)))
        {
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            var values = cells[2..5].Select(c => ColorPattern().Matches(c).Select(m => m.Value.ToUpperInvariant()).ToArray()).ToArray();
            if (cells[1].StartsWith("editor:", StringComparison.Ordinal))
            {
                table["editor:" + cells[1]["editor:".Length..].Trim()] = [.. values.Select(v => v[0])];
                continue;
            }

            var names = TokenPattern().Matches(cells[1]).Select(m => m.Groups[1].Value).ToList();
            var full = names.Select(n => n.StartsWith("Shell.", StringComparison.Ordinal) ? n : names[0][..names[0].LastIndexOf('.')] + n).ToList();
            for (var i = 0; i < full.Count; i++)
            {
                table[full[i]] = [.. values.Select(v => v.Length == 1 ? v[0] : v[i])];
            }
        }

        return table;
    }

    [Fact]
    public void The_design_has_a_table_for_every_family()
    {
        foreach (var family in s_families)
        {
            var table = DesignTable(family);
            Assert.True(table.Count > 40, $"{family}: {table.Count} rows");
            Assert.Contains("Shell.Background", table.Keys);
            Assert.Contains("editor:keyword", table.Keys);
            Assert.All(table.Values, v => Assert.Equal(3, v.Length));
        }
    }

    [Fact]
    public void Each_family_variant_has_the_designs_editor_colors_and_they_are_readable()
    {
        foreach (var family in s_families)
        {
            var table = DesignTable(family);
            for (var v = 0; v < 3; v++)
            {
                var id = $"{family}.{s_variants[v]}";
                var theme = EditorTheme.For(id);
                Assert.Equal(id, theme.Id);
                uint Design(string key) => 0xFF000000 | uint.Parse(table[key][v].AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                Assert.Equal(Design("editor:keyword"), theme.TokenColor("keyword"));
                Assert.Equal(Design("editor:keyword"), theme.TokenColor("modifier"));
                Assert.Equal(Design("editor:type"), theme.TokenColor("class"));
                Assert.Equal(Design("editor:type"), theme.TokenColor("interface"));
                Assert.Equal(Design("editor:method"), theme.TokenColor("method"));
                Assert.Equal(Design("editor:string"), theme.TokenColor("string"));
                Assert.Equal(Design("editor:number"), theme.TokenColor("number"));
                Assert.Equal(Design("editor:comment"), theme.TokenColor("comment"));
                Assert.Equal(Design("Shell.Editor.CurrentLine"), theme.ElementColor("editor.lineHighlightBackground"));

                // The design's syntax colors are text: 4.5:1 on the editor (7:1 in High contrast), on its current line too.
                var background = table.TryGetValue("Shell.Editor.Background", out var editor) ? editor[v] : table["Shell.Panel"][v];
                var minimum = v == 2 ? 7 : 4.5;
                foreach (var key in table.Keys.Where(k => k.StartsWith("editor:", StringComparison.Ordinal)))
                {
                    foreach (var surface in new[] { background, table["Shell.Editor.CurrentLine"][v] })
                    {
                        var ratio = PolishTests.Contrast(Color.Parse(table[key][v]), Color.Parse(surface));
                        Assert.True(ratio >= minimum, $"{id}: {key} {table[key][v]} on {surface} is {ratio:0.00}:1");
                    }
                }
            }
        }

        // Classic's are as they were.
        Assert.Same(EditorTheme.Light, EditorTheme.For("blue"));
        Assert.Same(EditorTheme.Dark, EditorTheme.For("dark"));
        Assert.Same(EditorTheme.HighContrast, EditorTheme.For("highContrast"));
    }

    [AvaloniaFact]
    public void Every_family_variant_has_the_designs_colors()
    {
        var failures = new List<string>();
        foreach (var family in s_families)
        {
            var table = DesignTable(family);
            for (var v = 0; v < 3; v++)
            {
                var variant = ThemeService.VariantOf($"{family}.{s_variants[v]}");
                foreach (var (key, values) in table.Where(t => t.Key.StartsWith("Shell.", StringComparison.Ordinal)))
                {
                    if (!Application.Current!.TryGetResource(key, variant, out var value) || value is not ISolidColorBrush brush)
                    {
                        failures.Add($"{variant}: {key} is missing");
                    }
                    else if (brush.Color != Color.Parse(values[v]))
                    {
                        failures.Add($"{variant}: {key} is {brush.Color}, the design's {values[v]}");
                    }
                }
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>The keys of each theme dictionary in Tokens.axaml, by its key's text (<c>Light</c>, <c>{x:Static …HarborDark}</c>).</summary>
    private static Dictionary<string, HashSet<string>> TokenDictionaries()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(System.IO.Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "Theming", "Tokens.axaml"));
        var themes = document.Root!.Elements().Single(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries");
        return themes.Elements().ToDictionary(
            d => (string)d.Attribute(x + "Key")!,
            d => d.Elements().Select(e => (string?)e.Attribute(x + "Key")).OfType<string>().ToHashSet(StringComparer.Ordinal));
    }

    [AvaloniaFact]
    public void Every_family_variant_sets_every_token_the_shell_uses_itself()
    {
        var dictionaries = TokenDictionaries();
        Assert.Equal(16, dictionaries.Count);

        // Every Shell.* key Classic's Light has (every token the shell uses, M34d's test), set in each family variant's own
        // dictionary: none of Classic's leaks into a family through the variant it inherits.
        var classic = dictionaries["Light"].Where(k => k.StartsWith("Shell.", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        foreach (var key in new[] { "Dark", "{x:Static theming:ThemeService.HighContrast}" }) // Blue takes Light's editor colors
        {
            Assert.Empty(classic.Except(dictionaries[key]));
        }

        foreach (var family in new[] { "Harbor", "Graphite", "Daylight", "Ember" })
        {
            foreach (var variant in new[] { "Light", "Dark", "HighContrast" })
            {
                var keys = dictionaries[$"{{x:Static theming:ThemeService.{family}{variant}}}"];
                Assert.True(classic.IsSubsetOf(keys), $"{family} {variant} lacks {string.Join(", ", classic.Except(keys))}");
                Assert.Contains("DockTabActiveBackgroundBrush", keys);
                Assert.Contains("Shell.Gutter", keys);
            }
        }

        // Every token a view or the code asks for resolves in every theme.
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(System.IO.Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell"), "*.*", SearchOption.AllDirectories)
                     .Where(p => (p.EndsWith(".axaml", StringComparison.Ordinal) || p.EndsWith(".cs", StringComparison.Ordinal))
                                 && !p.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            used.UnionWith(UsedToken().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value).Where(k => !k.EndsWith('.')));
        }

        Assert.Contains("Shell.Editor.Breakpoint.Line", used);
        foreach (var (id, variant) in ThemeService.Themes)
        {
            var missing = used.Where(k => !Application.Current!.TryGetResource(k, variant, out _)).ToList();
            Assert.True(missing.Count == 0, $"{id}: {string.Join(", ", missing)}");
        }
    }

    // ------------------------------------------------------------------ switching

    [AvaloniaFact]
    public async Task A_family_and_its_variant_switch_live_with_the_theme_and_themeFamily_keys()
    {
        await using var f = ShellFixture.Create();
        var settings = f.Shell.Workbench.Settings;
        f.Shell.Theme.SystemColors = () => new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Dark };
        f.Shell.Theme.Apply();

        // A new install: Classic, following the system (D36).
        Assert.Equal("classic", f.Shell.Theme.Setting);
        Assert.Equal("dark", f.Shell.Theme.ActiveTheme);
        Assert.Equal("classic", f.Shell.Theme.ActiveFamily);
        Assert.Equal(ThemeVariant.Dark, Application.Current!.ActualThemeVariant);

        await settings.SetAsync(ThemeService.SettingKey, "graphite.light", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "graphite.light");
        Assert.Equal(ThemeService.GraphiteLight, Application.Current.ActualThemeVariant);
        Assert.Equal("graphite", f.Shell.Theme.ActiveFamily);
        Assert.Equal("light", f.Shell.Workbench.Context.GetValue("theme"));
        Assert.Equal("graphite", f.Shell.Workbench.Context.GetValue("themeFamily"));
        Assert.Equal(EditorTheme.For("graphite.light").TokenColor("keyword"), f.Shell.EditorTheme.TokenColor("keyword"));
        Assert.True(f.Window.TryFindResource("Shell.StatusBar", f.Window.ActualThemeVariant, out var bar));
        Assert.Equal(Color.Parse("#0F766E"), ((ISolidColorBrush)bar!).Color);

        // A plugin's `when` clause sees the family.
        Assert.True(Workbench.Context.WhenClause.Parse("themeFamily == graphite && theme == light").Evaluate(f.Shell.Workbench.Context));

        await settings.SetAsync(ThemeService.SettingKey, "ember.highContrast", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "ember.highContrast");
        Assert.True(ThemeService.IsHighContrast(Application.Current.ActualThemeVariant));
        Assert.Equal("highContrast", f.Shell.Workbench.Context.GetValue("theme"));

        // The terminal takes the family's editor colors.
        Assert.Equal(Colors.Black, f.Shell.TerminalLook.Palette!.Background);
    }

    [AvaloniaFact]
    public async Task A_family_that_follows_the_system_follows_its_changes_high_contrast_included()
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "daylight", SettingScope.User, CancellationToken.None);
        var system = new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Light };
        f.Shell.Theme.SystemColors = () => system;
        f.Shell.Theme.Apply();
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "daylight.light");

        system = new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Dark };
        f.Shell.Theme.Apply(); // what the OS's ColorValuesChanged does
        Assert.Equal("daylight.dark", f.Shell.Theme.ActiveTheme);

        system = new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Dark, ContrastPreference = ColorContrastPreference.High };
        f.Shell.Theme.Apply();
        Assert.Equal("daylight.highContrast", f.Shell.Theme.ActiveTheme);
        Assert.Equal(ThemeService.DaylightHighContrast, Application.Current!.ActualThemeVariant);

        // A pinned variant stays.
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "daylight.light", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "daylight.light");
        f.Shell.Theme.Apply();
        Assert.Equal("daylight.light", f.Shell.Theme.ActiveTheme);
    }

    [AvaloniaFact]
    public async Task A_setting_saved_before_the_families_keeps_its_look()
    {
        var userData = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codalus-shell-" + Guid.NewGuid().ToString("N"));
        var settingsFile = Codalus.Platform.AppPaths.Resolve(Codalus.Platform.PlatformInfo.Current, _ => null, userData).SettingsFile;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsFile)!);
        await File.WriteAllTextAsync(settingsFile, "{ \"workbench.colorTheme\": \"blue\" }", TestContext.Current.CancellationToken);
        await using var f = ShellFixture.Create(userData);

        Assert.Equal("blue", f.Shell.Theme.ActiveTheme);
        Assert.Equal(ThemeService.Blue, Application.Current!.ActualThemeVariant);
        Assert.Equal("classic", f.Shell.Workbench.Context.GetValue("themeFamily"));
        Assert.Equal("blue", f.Shell.Workbench.Context.GetValue("theme"));
        Assert.True(f.Window.TryFindResource("Shell.Tab.Active", f.Window.ActualThemeVariant, out var tab));
        Assert.Equal(Color.Parse("#F5CC84"), ((ISolidColorBrush)tab!).Color); // Blue's tabs, as before

        // The file isn't rewritten: the value stays readable by an older version.
        Assert.Contains("\"blue\"", await File.ReadAllTextAsync(settingsFile, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        foreach (var (legacy, resolved, variant) in new[] { ("dark", "dark", ThemeVariant.Dark), ("highContrast", "highContrast", ThemeService.HighContrast), ("light", "light", ThemeVariant.Light) })
        {
            await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, legacy, SettingScope.User, CancellationToken.None);
            await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == resolved);
            Assert.Equal(variant, Application.Current.ActualThemeVariant);
            Assert.Same(resolved == "light" ? EditorTheme.Light : resolved == "dark" ? EditorTheme.Dark : EditorTheme.HighContrast, EditorTheme.For(f.Shell.Theme.ActiveTheme));
        }

        // Classic following the system, as Follow System did.
        f.Shell.Theme.SystemColors = () => new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Light, ContrastPreference = ColorContrastPreference.High };
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "followSystem", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "highContrast");
        Assert.Equal("classic", f.Shell.Theme.ActiveFamily);
    }

    [AvaloniaFact]
    public async Task Color_customizations_apply_by_family_and_by_variant()
    {
        await using var f = ShellFixture.Create();
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync(ThemeService.SettingKey, "ember.dark", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "ember.dark");
        var tokens = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["[Ember]"] = new Dictionary<string, string> { ["comments"] = "#111111", ["strings"] = "#222222" },
            ["[Ember Dark]"] = new Dictionary<string, string> { ["strings"] = "#333333" },
        });
        await settings.SetAsync(ColorCustomizations.TokensKey, tokens, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.EditorTheme.TokenColor("comment") == 0xFF111111u);
        Assert.Equal(0xFF333333u, f.Shell.EditorTheme.TokenColor("string")); // the variant's over the family's

        await settings.SetAsync(ThemeService.SettingKey, "harbor.dark", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "harbor.dark");
        Assert.Equal(EditorTheme.For("harbor.dark").TokenColor("comment"), f.Shell.EditorTheme.TokenColor("comment"));
    }

    // ------------------------------------------------------------------ choosing

    [AvaloniaFact]
    public async Task The_theme_picker_lists_every_family_and_variant_and_previews_before_it_saves()
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "dark", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "dark");
        var picking = f.Shell.SelectThemeAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen && f.Shell.Palette.Rows.Count > 0);
        var labels = f.Shell.Palette.Rows.Select(r => r.Label).ToList();
        foreach (var family in new[] { "Harbor", "Graphite", "Daylight", "Ember" })
        {
            Assert.Contains($"{family} (Follow System)", labels);
            Assert.Contains($"{family} Light", labels);
            Assert.Contains($"{family} Dark", labels);
            Assert.Contains($"{family} High Contrast", labels);
        }

        Assert.Contains("Classic Blue", labels);
        Assert.Equal("Classic Dark", f.Shell.Palette.Selected!.Label); // a saved "dark" is Classic's Dark

        // Moving previews; the setting doesn't change until a theme is picked.
        f.Shell.Palette.SelectedIndex = labels.IndexOf("Ember Light");
        Assert.Equal("ember.light", f.Shell.Theme.ActiveTheme);
        Assert.Equal("dark", f.Shell.Workbench.Settings.GetString(ThemeService.SettingKey));
        f.Shell.Palette.Accept();
        await picking;
        Assert.Equal("ember.light", f.Shell.Workbench.Settings.GetString(ThemeService.SettingKey));
        Assert.Equal("ember.light", f.Shell.Theme.ActiveTheme);
    }

    [AvaloniaFact]
    public async Task Options_shows_the_families_as_cards_with_pictures_and_a_mode()
    {
        await using var f = ShellFixture.Create();
        f.Shell.OpenSettings(ThemeService.SettingKey);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId) is not null);
        var document = f.Shell.Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId)!;
        var picker = Assert.IsType<ColorThemeSettingViewModel>(document.ViewModel.Items.Single(i => i.Key == ThemeService.SettingKey));
        // Classic first, and chosen: the default (D36), following the system, with Classic's Blue among its modes.
        Assert.Equal(["classic", "harbor", "graphite", "daylight", "ember"], picker.Families.Select(c => c.Id));
        Assert.Equal("classic", picker.SelectedFamily?.Id);
        Assert.Null(picker.SelectedMode?.Variant); // Follow System
        Assert.Equal(["Follow System", "Blue", "Light", "Dark", "High Contrast"], picker.Modes.Select(m => m.Label));
        List<string?> Shown() => [.. f.Window.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Classes.Contains("themeCard")).Select(Avalonia.Automation.AutomationProperties.GetName)];
        await ShellFixture.PumpUntil(() => Shown().Count == 5);
        var shown = Shown();
        Assert.Equal(["Classic", "Harbor", "Graphite", "Daylight", "Ember"], shown);
        Assert.StartsWith("The classic IDE's familiar look", picker.Families[0].Description, StringComparison.Ordinal);

        // A card, then a mode: each writes the setting, and the IDE follows.
        picker.Families.Single(c => c.Id == "ember").SelectCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.GetString(ThemeService.SettingKey) == "ember");
        picker.SelectedMode = picker.Modes.Single(m => m.Variant == "dark");
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "ember.dark");
        Assert.All(picker.Families.Where(c => c.Id != "classic"), c => Assert.Equal(ThemeService.VariantOf(c.Id + ".dark"), c.PreviewVariant));

        // Another family keeps the mode; Classic adds Blue; a family without Blue follows the system.
        picker.Families.Single(c => c.Id == "classic").SelectCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "dark");
        Assert.Contains(picker.Modes, m => m.Variant == "blue");
        picker.SelectedMode = picker.Modes.Single(m => m.Variant == "blue");
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.GetString(ThemeService.SettingKey) == "classic.blue");
        picker.Families.Single(c => c.Id == "graphite").SelectCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.GetString(ThemeService.SettingKey) == "graphite");

        // A value from before the families shows as Classic.
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "highContrast", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => picker.SelectedFamily?.Id == "classic" && picker.SelectedMode?.Variant == "highContrast");

        // Each card's picture is drawn in its family's own tokens, the chosen one's too when it's the window's theme
        // (opened afresh: a ThemeVariantScope in that case drew nothing).
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "harbor.light", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "harbor.light");
        f.Shell.Layout.CloseDocument(document);
        f.Shell.OpenSettings(ThemeService.SettingKey);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId) is not null);
        ShellFixture.Pump();
        ShellFixture.Pump();
        picker = Assert.IsType<ColorThemeSettingViewModel>(f.Shell.Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId)!.ViewModel.Items.Single(i => i.Key == ThemeService.SettingKey));
        var ember = picker.Families.Single(c => c.Id == "ember");
        Assert.Equal(Color.Parse("#EDE8E3"), ((ISolidColorBrush)ember.Picture.Background).Color);
        Assert.Equal(10, ember.Picture.PanelCorners.BottomLeft);
        Assert.Equal(8, ember.Picture.Gutter);
        var cards = f.Window.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Classes.Contains("themeCard")).ToList();
        Assert.Equal(5, cards.Count);
        var harbor = cards.Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Harbor");
        Assert.True(harbor.IsChecked);
        // The picture is the shell's chrome at half size (M34h): the window's color, the title bar's.
        var picture = harbor.GetVisualDescendants().OfType<Border>().First(b => b.Width == 356);
        Assert.Equal(Color.Parse("#D5DDEF"), ((ISolidColorBrush)picture.Background!).Color);
        var title = picture.GetVisualDescendants().OfType<Border>().First(b => b.Height == 24);
        Assert.Equal(Color.Parse("#1B2645"), ((ISolidColorBrush)title.Background!).Color);
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(f.Window);
            frame!.Save(System.IO.Path.Combine(snapshots, "theme-families-options.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    // ------------------------------------------------------------------ shapes

    [AvaloniaFact]
    public async Task Each_family_draws_its_shapes_without_changing_the_views()
    {
        await using var f = ShellFixture.Create();
        var settings = f.Shell.Workbench.Settings;
        f.Shell.Layout.ShowToolWindow(BuiltIns.OutputWindow, focus: true);
        var file = System.IO.Path.Combine(f.UserData, "Notes.txt");
        await File.WriteAllTextAsync(file, "text", TestContext.Current.CancellationToken);
        await f.Shell.OpenFileAsync(file, preview: false);
        f.Window.Width = 1200;
        f.Window.Height = 800;

        async Task Use(string theme)
        {
            await settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None);
            await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == theme);
            ShellFixture.Pump();
            ShellFixture.Pump();
        }

        double Gutter() => f.Window.GetVisualDescendants().OfType<Dock.Controls.ProportionalStackPanel.ProportionalStackPanelSplitter>().First(s => s.IsEffectivelyVisible).Thickness;
        bool Dot() => f.Window.GetVisualDescendants().OfType<Ellipse>().Single(e => e.Classes.Contains("statusDot")).IsVisible;
        IBrush? Start() => f.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StartButton").Background;

        await Use("harbor.light");
        Assert.Equal(6, Gutter());
        Assert.False(Dot());
        var titleBar = f.Window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TitleBar");
        Assert.Equal(Color.Parse("#1B2645"), ((ISolidColorBrush)titleBar.Background!).Color); // the navy title bar
        var menu = f.Window.GetVisualDescendants().OfType<Menu>().Single(m => m.Name == "MenuBar");
        if (OperatingSystem.IsMacOS())
        {
            Assert.False(menu.IsVisible); // the menus are the system's menu bar, outside the window (MenuBuilder)
        }
        else
        {
            Assert.Equal(Color.Parse("#E8ECF7"), ((ISolidColorBrush)menu.GetVisualDescendants().OfType<MenuItem>().First().Foreground!).Color); // readable on it
        }

        await Use("graphite.dark");
        Assert.Equal(1, Gutter());

        await Use("daylight.light");
        Assert.Equal(1, Gutter());
        Assert.Equal(Color.Parse("#6B3FC4"), ((ISolidColorBrush)Start()!).Color); // Start filled with the accent

        await Use("ember.dark");
        Assert.Equal(8, Gutter());
        Assert.True(Dot());
        var chrome = f.Window.GetVisualDescendants().OfType<Dock.Avalonia.Controls.ToolChromeControl>().First(c => c.IsEffectivelyVisible);
        var caption = chrome.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Border" && b.TemplatedParent == chrome);
        Assert.Equal(new CornerRadius(10, 10, 0, 0), caption.CornerRadius);

        await Use("dark");
        Assert.Equal(4, Gutter()); // Classic keeps Dock's
        Assert.False(Dot());
        Assert.Equal(new CornerRadius(0), caption.CornerRadius);
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            foreach (var theme in FamilyThemes)
            {
                await Use(theme);
                using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(f.Window);
                frame!.Save(System.IO.Path.Combine(snapshots, $"theme-{theme}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
    }

    [GeneratedRegex("#[0-9A-Fa-f]{6}")]
    private static partial Regex ColorPattern();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("[\"{ ](Shell\\.[A-Za-z0-9.]+)[\"}]")]
    private static partial Regex UsedToken();
}
