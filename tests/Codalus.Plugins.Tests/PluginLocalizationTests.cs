namespace Codalus.Plugins.Tests;

/// <summary><see cref="PluginLocalization"/> (API 0.2.9): a plugin's own text from its nls files in the display language.</summary>
public sealed class PluginLocalizationTests
{
    private sealed class Context(string installPath) : IPluginContext
    {
        public PluginInfo Plugin { get; } = new("codalus.test", "1.0.0", installPath);

        public ICommands Commands => throw new NotSupportedException();

        public IWindow Window => throw new NotSupportedException();

        public ISettings Settings => throw new NotSupportedException();

        public IPluginLog Log => throw new NotSupportedException();

        public IList<IDisposable> Subscriptions { get; } = [];
    }

    [Fact]
    public void A_plugins_text_follows_the_display_language_and_falls_back_to_english()
    {
        var folder = Directory.CreateTempSubdirectory("codalus-plugin-nls-").FullName;
        var before = Environment.GetEnvironmentVariable(PluginLocalization.LocaleVariable);
        try
        {
            File.WriteAllText(Path.Combine(folder, "codalus-plugin.nls.de.json"), """{ "start.title": "Startseite", "saved": "{0} gespeichert" }""");
            var context = new Context(folder);

            Environment.SetEnvironmentVariable(PluginLocalization.LocaleVariable, "en");
            Assert.Equal("Start Page", context.Localize("start.title", "Start Page"));

            Environment.SetEnvironmentVariable(PluginLocalization.LocaleVariable, "de-AT");
            Assert.Equal("Startseite", context.Localize("start.title", "Start Page"));
            Assert.Equal("3 gespeichert", context.Localize("saved", "{0} saved", 3));
            Assert.Equal("Untranslated", context.Localize("other", "Untranslated"));

            Environment.SetEnvironmentVariable(PluginLocalization.LocaleVariable, "qps-ploc");
            var pseudo = context.Localize("saved", "{0} saved");
            Assert.StartsWith("[{0} šåvéð", pseudo, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PluginLocalization.LocaleVariable, before);
            Directory.Delete(folder, recursive: true);
        }
    }
}
