using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Tests;

/// <summary>Plugins' source control providers in the Source Control Providers window (plugin API 1.0, M22a).</summary>
public sealed class SourceControlProviderTests
{
    [AvaloniaFact]
    public async Task The_window_shows_a_providers_files_commits_and_opens_a_files_diff()
    {
        await using var f = ShellFixture.Create(builtInPlugins: Path.Combine(ShellFixture.RepoRoot, "samples", "plugins", "HelloWorld", "bin", ShellFixture.Configuration, "net10.0"));
        var folder = Path.Combine(f.UserData, "tracked");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "the new a\n");
        File.WriteAllText(Path.Combine(folder, "b.txt"), "b\n");

        f.Shell.Workbench.Commands.ExecuteAsync("hello.startScm", [JsonSerializer.SerializeToElement(folder)], CancellationToken.None).GetAwaiter();
        await ShellFixture.PumpUntil(() => f.Shell.Plugins.SourceControls.Any(s => s.Groups.Count == 1), seconds: 60);
        f.Shell.Layout.ShowToolWindow(ShellHost.SourceControlProvidersWindow);

        string Text()
        {
            var root = f.Shell.SourceControlView();
            static IEnumerable<string> Walk(Codalus.Plugins.Protocol.ViewNodeDto node) =>
                new[] { node.Text, node.Value }.Where(t => t is not null).Select(t => t!).Concat((node.Rows ?? []).SelectMany(r => r)).Concat((node.Children ?? []).SelectMany(Walk));
            return string.Join(" | ", Walk(root));
        }

        Assert.Contains("Hello Source Control", Text(), StringComparison.Ordinal);
        Assert.Contains("Changes (2)", Text(), StringComparison.Ordinal);
        Assert.Contains("a.txt", Text(), StringComparison.Ordinal);

        // The commit box and button: the provider records the commit and says so.
        await f.Shell.OnSourceControlEventAsync("accept:hello-scm", "click", null, null);
        Assert.Contains("no commits", Text(), StringComparison.Ordinal); // no message, no commit
        await f.Shell.OnSourceControlEventAsync("message:hello-scm", "change", "fix the thing", null);
        await f.Shell.OnSourceControlEventAsync("accept:hello-scm", "click", null, null);
        await ShellFixture.PumpUntil(() => Text().Contains("last: fix the thing", StringComparison.Ordinal), seconds: 30);

        // Opening a file compares it with the provider's original.
        await f.Shell.OnSourceControlEventAsync("group:hello-scm:changes", "activate", null, 0);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is DiffDocument, seconds: 30);
        Assert.Contains("a.txt", f.Shell.Layout.ActiveDocument!.Title, StringComparison.Ordinal);
    }
}
