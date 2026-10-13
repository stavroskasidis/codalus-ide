using System.Text;
using DaedalusStudio.Workbench.Localization;
using DaedalusStudio.Workbench.Plugins;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>Catalogues, fallbacks, pseudo-languages and plugin manifest placeholders (M23b).</summary>
public sealed class LocalizationTests
{
    private static Func<string, Stream?> Catalogs(params (string Locale, string Json)[] catalogs) =>
        locale => catalogs.FirstOrDefault(c => c.Locale == locale) is { Json: { } json } ? new MemoryStream(Encoding.UTF8.GetBytes(json)) : null;

    private static readonly Func<string, Stream?> s_catalogs = Catalogs(
        ("en", """{ "view.title": "Fonts and Colors", "command.x": "Old English" }"""),
        ("de", """{ "view.title": "Schriftarten und Farben", "command.x": "Befehl" }"""),
        ("pt", """{ "view.title": "Fontes e cores" }"""));

    [Fact]
    public void Translations_fall_back_to_the_language_then_to_the_codes_english()
    {
        var english = LocalizationCatalog.Load("en", s_catalogs);
        Assert.Equal("Fonts and Colors", english.Get("view.title"));
        Assert.Equal("New English", english.Get("command.x", "New English")); // the code's text wins over a stale catalogue
        Assert.Equal("missing.key", english.Get("missing.key"));

        var german = LocalizationCatalog.Load("de-AT", s_catalogs);
        Assert.Equal("Schriftarten und Farben", german.Get("view.title"));
        Assert.Equal("Befehl", german.Get("command.x", "New English"));
        Assert.Equal("Untranslated", german.Get("command.y", "Untranslated"));

        var portuguese = LocalizationCatalog.Load("pt-BR", s_catalogs);
        Assert.Equal("Fontes e cores", portuguese.Get("view.title"));
        Assert.False(portuguese.IsRightToLeft);
        Assert.True(LocalizationCatalog.Load("ar", s_catalogs).IsRightToLeft);
        Assert.True(LocalizationCatalog.Load("he-IL", s_catalogs).IsRightToLeft);
    }

    [Fact]
    public void Pseudo_languages_accent_and_pad_every_string_but_keep_placeholders_and_icons()
    {
        var pseudo = LocalizationCatalog.Load(Localizer.PseudoLocale, s_catalogs);
        var text = pseudo.Get("view.title");
        Assert.StartsWith("[Ƒöñţš åñð Çöļöŕš", text, StringComparison.Ordinal);
        Assert.EndsWith("]", text, StringComparison.Ordinal);
        Assert.True(text.Length > "Fonts and Colors".Length * 1.25);
        Assert.Contains("{0}", Localizer.Pseudo("Saved {0} files"), StringComparison.Ordinal);
        Assert.Contains("$(bell)", Localizer.Pseudo("$(bell) Notifications"), StringComparison.Ordinal);
        Assert.False(pseudo.IsRightToLeft);
        Assert.True(LocalizationCatalog.Load(Localizer.PseudoMirroredLocale, s_catalogs).IsRightToLeft);
    }

    [Fact]
    public void A_manifests_placeholders_take_the_language_files_text()
    {
        var folder = Directory.CreateTempSubdirectory("daedalus-nls-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, ManifestLocalization.DefaultFile), """{ "displayName": "Welcome", "command.show": "Show Start Page" }""");
            File.WriteAllText(Path.Combine(folder, "daedalus-plugin.nls.de.json"), """{ "displayName": "Willkommen" }""");
            const string Manifest = """{ "id": "x", "displayName": "%displayName%", "contributes": { "commands": [ { "id": "s", "title": "%command.show%" }, { "id": "t", "title": "%unknown%" } ] } }""";

            var english = ManifestLocalization.Apply(Manifest, folder, LocalizationCatalog.Load("en", s_catalogs));
            Assert.Contains("\"displayName\":\"Welcome\"", english, StringComparison.Ordinal);
            Assert.Contains("\"title\":\"Show Start Page\"", english, StringComparison.Ordinal);
            Assert.Contains("\"title\":\"%unknown%\"", english, StringComparison.Ordinal);

            var german = ManifestLocalization.Apply(Manifest, folder, LocalizationCatalog.Load("de-DE", s_catalogs));
            Assert.Contains("\"displayName\":\"Willkommen\"", german, StringComparison.Ordinal);
            Assert.Contains("\"title\":\"Show Start Page\"", german, StringComparison.Ordinal); // not translated: English

            var pseudo = ManifestLocalization.Apply(Manifest, folder, LocalizationCatalog.Load(Localizer.PseudoLocale, s_catalogs));
            Assert.Contains("[Ŵéļçöɱé", pseudo, StringComparison.Ordinal);
            Assert.Equal("""{ "id": "x" }""", ManifestLocalization.Apply("""{ "id": "x" }""", folder, LocalizationCatalog.Load("de", s_catalogs)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
