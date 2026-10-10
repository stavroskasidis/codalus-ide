using Avalonia.Headless.XUnit;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Workbench.Node;

namespace DaedalusStudio.Shell.Tests;

/// <summary>
/// M16b (ADR 0024): Node.js and the web language servers are installed on demand (into a cache shared by test runs, so
/// only the first downloads), then serve TypeScript, HTML and CSS files.
/// </summary>
public sealed class WebLanguagesTests
{
    /// <summary>A cache shared by test runs on this machine.</summary>
    private static string SharedCache => Path.Combine(Path.GetTempPath(), "daedalus-test-node-cache");

    [AvaloniaFact]
    public async Task The_web_language_servers_install_and_serve_typescript_html_and_css()
    {
        Assert.SkipWhen(NodeRuntime.Platform is null, "No Node.js build for this platform.");
        Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, SharedCache);
        try
        {
            await using var f = ShellFixture.Create();
            var install = f.Shell.InstallWebLanguagesAsync();
            await ShellFixture.PumpUntil(() => install.IsCompleted, seconds: 600);
            await install;
            Assert.True(f.Shell.WebServers.IsInstalled, "The servers were not installed: " + f.Shell.LanguageLog.GetText());

            var folder = Path.Combine(f.UserData, "site");
            Directory.CreateDirectory(folder);
            var script = Path.Combine(folder, "app.ts");
            await File.WriteAllTextAsync(script, "function greet(name: string): string {\n    return \"Hello, \" + name;\n}\n\nconst count: number = \"three\";\nconsole.log(greet(\"web\"));\n", TestContext.Current.CancellationToken);
            var page = Path.Combine(folder, "index.html");
            await File.WriteAllTextAsync(page, "<!DOCTYPE html>\n<html>\n<body>\n<di\n</body>\n</html>\n", TestContext.Current.CancellationToken);
            var style = Path.Combine(folder, "site.css");
            await File.WriteAllTextAsync(style, "body {\n    col\n}\n", TestContext.Current.CancellationToken);

            // TypeScript: a type error, hover and member completion.
            var editor = await f.Shell.OpenFileAsync(script, preview: false, null);
            try
            {
                await ShellFixture.PumpUntil(() => editor?.Controller is not null && f.Shell.Languages.RunningClient("typescript") is not null, seconds: 120);
            }
            catch (Xunit.Sdk.XunitException)
            {
                Assert.Fail("The TypeScript server didn't start: " + f.Shell.LanguageLog.GetText());
            }

            try
            {
                await ShellFixture.PumpUntil(() => f.Shell.Languages.Diagnostics.For(script).Any(d => d.Code == "2322"), seconds: 120);
            }
            catch (Xunit.Sdk.XunitException)
            {
                Assert.Fail("No TS2322: " + string.Join("; ", f.Shell.Languages.Diagnostics.For(script).Select(d => $"{d.Code} {d.Message}")) + " LOG: " + f.Shell.LanguageLog.GetText());
            }

            var ts = f.Shell.Languages.RunningClient("typescript")!;
            var text = editor!.Model.Text.Text;
            var hover = await ts.HoverAsync(editor.Model.Uri, editor.Model.PositionAt(text.IndexOf("greet(\"web", StringComparison.Ordinal) + 2), CancellationToken.None);
            Assert.Contains("greet(name: string): string", hover?.Contents.Value, StringComparison.Ordinal);
            var members = await ts.CompletionAsync(editor.Model.Uri, editor.Model.PositionAt(text.IndexOf("console.log", StringComparison.Ordinal) + "console.".Length),
                new CompletionContext(CompletionTriggerKind.Invoked, null), CancellationToken.None);
            Assert.Contains(members!.Items, i => i.Label == "log");

            // HTML: element completion.
            var html = await f.Shell.OpenFileAsync(page, preview: false, null);
            await ShellFixture.PumpUntil(() => html?.Controller is not null && f.Shell.Languages.RunningClient("html") is not null, seconds: 120);
            var htmlClient = f.Shell.Languages.RunningClient("html")!;
            var elements = await htmlClient.CompletionAsync(html!.Model.Uri, html.Model.PositionAt(html.Model.Text.Text.IndexOf("<di", StringComparison.Ordinal) + 3),
                new CompletionContext(CompletionTriggerKind.Invoked, null), CancellationToken.None);
            Assert.Contains(elements!.Items, i => i.Label == "div");

            // CSS: property completion.
            var css = await f.Shell.OpenFileAsync(style, preview: false, null);
            await ShellFixture.PumpUntil(() => css?.Controller is not null && f.Shell.Languages.RunningClient("css") is not null, seconds: 120);
            var cssClient = f.Shell.Languages.RunningClient("css")!;
            var properties = await cssClient.CompletionAsync(css!.Model.Uri, css.Model.PositionAt(css.Model.Text.Text.IndexOf("col", StringComparison.Ordinal) + 3),
                new CompletionContext(CompletionTriggerKind.Invoked, null), CancellationToken.None);
            Assert.Contains(properties!.Items, i => i.Label == "color");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, null);
        }
    }
}
