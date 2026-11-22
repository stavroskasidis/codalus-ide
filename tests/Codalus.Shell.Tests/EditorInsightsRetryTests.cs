using Avalonia.Headless.XUnit;

namespace Codalus.Shell.Tests;

/// <summary>The editor's inline information asks again after a request its language service failed (a server still loading).</summary>
public sealed class EditorInsightsRetryTests
{
    [AvaloniaFact]
    public async Task Document_symbols_come_after_the_server_failed_the_first_requests()
    {
        // A failed request was asked again on another kind's timer: the symbols' on the inlay hints', the hints' on the
        // CodeLens', the lenses' on the symbols'. So the symbols FsAutoComplete couldn't list while it loaded a project
        // never came until an edit (F# shell test, Windows, stress run 45019776994: "no symbols for Shapes.fs" for 240 s).
        await using var f = ShellFixture.Create();
        var log = Path.Combine(f.UserData, "conformance.log");
        f.Shell.Workbench.LanguageDefinitions.Register(new Workbench.Languages.LanguageDefinition("conf", "Conformance") { Extensions = [".conf"] });
        f.Shell.Workbench.LanguageServers.Register(new Workbench.Languages.LanguageServiceDescriptor("conformance", "Conformance", [], null,
            () => Rpc.ProcessLaunchInfo.FromPath(EditorLargeTextTests.ConformanceServer, ["--log", log, "--fail-symbols", "2"]))
        {
            Languages = ["conf"],
        });

        var path = Path.Combine(f.UserData, "settings.conf");
        await File.WriteAllTextAsync(path, "greeting = hello\n", TestContext.Current.CancellationToken);
        await f.Shell.OpenFileAsync(path, preview: false, null);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == path && f.Shell.ActiveEditor.Controller is not null);
        var editor = f.Shell.ActiveEditor!.Controller!;

        await ShellFixture.PumpUntil(() => editor.Insights.SymbolsVersion == editor.Model.Version, seconds: 30,
            () => $"no symbols at version {editor.Model.Version} (listed at {editor.Insights.SymbolsVersion}); the server saw: "
                  + (File.Exists(log) ? string.Join(", ", File.ReadAllLines(log).Where(l => l.StartsWith("documentSymbol", StringComparison.Ordinal))) : "nothing"));
        Assert.Equal("greeting", Assert.Single(editor.Insights.Symbols).Name);
    }
}
