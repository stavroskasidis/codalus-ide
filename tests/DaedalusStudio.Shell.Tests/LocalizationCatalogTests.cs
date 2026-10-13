using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using DaedalusStudio.Workbench.Commands;

namespace DaedalusStudio.Shell.Tests;

/// <summary>
/// The English catalogue (M23b): it holds every translatable text of the shell, generated from the code (commands,
/// settings, menus, tool windows) and the views' own strings, and no view gains a hard-coded string. Run with
/// <c>DAEDALUS_UPDATE_LOCALIZATION=1</c> to rewrite the catalogue and the literal baseline after changing UI text.
/// </summary>
public sealed partial class LocalizationCatalogTests
{
    private static readonly string[] s_generatedPrefixes = ["command.", "category.", "setting.", "settingCategory.", "toolWindow.", "menu."];

    private static readonly JsonSerializerOptions s_catalogJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static bool Update => Environment.GetEnvironmentVariable("DAEDALUS_UPDATE_LOCALIZATION") == "1";

    private static string ShellSource => Path.Combine(ShellFixture.RepoRoot, "src", "DaedalusStudio.Shell");

    private static string CatalogPath => Path.Combine(ShellSource, "Localization", "en.json");

    private static string BaselinePath => Path.Combine(ShellFixture.RepoRoot, "tests", "DaedalusStudio.Shell.Tests", "localization-literals.txt");

    [GeneratedRegex("""(?<![\w.])(?:Text|Content|Header|Title|ToolTip\.Tip|PlaceholderText|Watermark|AutomationProperties\.Name)="([^"{][^"]*)" """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Literal();

    [GeneratedRegex(@"\{l:T ([A-Za-z0-9_.]+)\}")]
    private static partial Regex ViewKey();

    private static IEnumerable<string> Views() =>
        Directory.EnumerateFiles(ShellSource, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    private static string Relative(string path) => Path.GetRelativePath(ShellFixture.RepoRoot, path).Replace('\\', '/');

    [AvaloniaFact]
    public async Task The_english_catalogue_has_every_text_of_the_code_and_the_views()
    {
        await using var f = ShellFixture.Create();
        var workbench = f.Shell.Workbench;
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var command in workbench.Commands.Descriptors.Where(c => c.Source == ContributionSource.Shell))
        {
            expected[$"command.{command.Id}"] = command.Title;
            if (command.Category is { Length: > 0 } category)
            {
                expected[$"category.{category}"] = category;
            }
        }

        foreach (var setting in workbench.Settings.Descriptors.Where(s => s.Source == ContributionSource.Shell))
        {
            Put($"setting.{setting.Key}.title", setting.Title);
            Put($"setting.{setting.Key}.description", setting.Description);
            if (setting.Category is { } category)
            {
                expected[$"settingCategory.{category}"] = category;
            }

            if (setting is { Enum: { } values, EnumDescriptions: { } names } && values.Count == names.Count)
            {
                for (var i = 0; i < values.Count; i++)
                {
                    expected[$"setting.{setting.Key}.enum.{values[i]}"] = names[i];
                }
            }
        }

        foreach (var window in workbench.ToolWindows.All.Where(w => w.Source == ContributionSource.Shell))
        {
            expected[$"toolWindow.{window.Id}"] = window.Title;
        }

        foreach (var menu in workbench.Menus.MenuBar)
        {
            expected[$"menu.{menu.Id}"] = menu.Title;
        }

        foreach (var rule in workbench.Menus.Rules.Where(r => r is { Submenu: not null, Title: not null } && r.Source == ContributionSource.Shell))
        {
            expected[$"menu.{rule.Submenu}"] = rule.Title!;
        }

        // The views' strings live only in the catalogue.
        var catalog = JsonNode.Parse(await File.ReadAllTextAsync(CatalogPath, TestContext.Current.CancellationToken))!.AsObject();
        var used = Views().SelectMany(v => ViewKey().Matches(File.ReadAllText(v)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, value) in catalog.Where(p => !s_generatedPrefixes.Any(prefix => p.Key.StartsWith(prefix, StringComparison.Ordinal))))
        {
            if (used.Contains(key))
            {
                expected[key] = value!.GetValue<string>();
            }
        }

        var missingViewKeys = used.Where(k => !catalog.ContainsKey(k)).ToList();
        Assert.True(missingViewKeys.Count == 0, $"Views use keys missing from en.json: {string.Join(", ", missingViewKeys.Take(20))}");

        var json = JsonSerializer.Serialize(expected, s_catalogJson) + "\n";
        if (Update)
        {
            await File.WriteAllTextAsync(CatalogPath, json.Replace("\r\n", "\n", StringComparison.Ordinal), TestContext.Current.CancellationToken);
            return;
        }

        var actual = catalog.ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal);
        var missing = expected.Where(e => !actual.TryGetValue(e.Key, out var v) || v != e.Value).Select(e => e.Key).ToList();
        var extra = actual.Keys.Where(k => !expected.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"src/DaedalusStudio.Shell/Localization/en.json is out of date ({missing.Count} new or changed, {extra.Count} no longer used: "
            + $"{string.Join(", ", missing.Concat(extra).Take(12))}). Run the shell tests with DAEDALUS_UPDATE_LOCALIZATION=1 to regenerate it.");

        void Put(string key, string? value)
        {
            if (value is not null)
            {
                expected[key] = value;
            }
        }
    }

    [Fact]
    public void No_view_gains_a_hard_coded_string()
    {
        // Each view's literal UI strings may only go down (eng/localize-xaml.py moves them into the catalogue).
        var counts = Views()
            .Select(v => (File: Relative(v), Count: Literal().Matches(File.ReadAllText(v)).Count(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Any(char.IsLetter))))
            .Where(c => c.Count > 0)
            .ToList();
        if (Update)
        {
            File.WriteAllLines(BaselinePath, counts.Select(c => $"{c.File} {c.Count}"));
            return;
        }

        var baseline = File.ReadAllLines(BaselinePath).Where(l => l.Length > 0).Select(l => l.Split(' ')).ToDictionary(p => p[0], p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var grown = counts.Where(c => c.Count > baseline.GetValueOrDefault(c.File)).Select(c => $"{c.File}: {c.Count} (was {baseline.GetValueOrDefault(c.File)})").ToList();
        Assert.True(grown.Count == 0, "Views with new hard-coded strings; use {l:T key} with the text in Localization/en.json (eng/localize-xaml.py does it): " + string.Join("; ", grown));
        var shrunk = counts.Where(c => c.Count < baseline.GetValueOrDefault(c.File)).Select(c => c.File).Concat(baseline.Keys.Where(k => counts.All(c => c.File != k))).ToList();
        Assert.True(shrunk.Count == 0, $"Fewer hard-coded strings in {string.Join(", ", shrunk)}: lower the baseline with DAEDALUS_UPDATE_LOCALIZATION=1.");
    }
}
