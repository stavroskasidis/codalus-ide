using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Daedalus.LanguageServices.Protocol;

[assembly: AvaloniaTestApplication(typeof(Daedalus.Spike.Avalonia.Tests.TestAppBuilder))]

namespace Daedalus.Spike.Avalonia.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>Headless UI test of prototype A against the real language service host.</summary>
public class ShellHeadlessTests
{
    private static string Metadata(string key) =>
        typeof(ShellHeadlessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static string RepoRoot => Metadata("DaedalusRepoRoot");

    [AvaloniaFact]
    public async Task Shell_loads_solution_tree_and_highlights_the_open_document()
    {
        Environment.SetEnvironmentVariable("DAEDALUS_LSHOST", Path.Combine(
            RepoRoot, "src", "Daedalus.LanguageServices.Host", "bin", Metadata("DaedalusConfiguration"), "net10.0", "daedalus-lshost.dll"));
        var solution = Path.Combine(RepoRoot, "samples", "SpikeSolution", "SpikeSolution.slnx");
        var file = Path.Combine(RepoRoot, "samples", "SpikeSolution", "src", "Spike.Core", "Inventory", "Product.cs");
        var shell = new ShellViewModel(new SpikeOptions(solution, file, null, null));
        var window = new MainWindow(shell);
        window.Show();

        try
        {
            var init = shell.InitializeAsync(CancellationToken.None);
            await PumpUntil(() => init.IsCompleted, TimeSpan.FromMinutes(2));
            await init;

            var root = Assert.Single(shell.Explorer.Roots);
            Assert.Equal(SolutionNodeKind.Solution, root.Node.Kind);
            Assert.Equal(["Spike.App", "Spike.Core", "Spike.Utilities"], root.Children.Select(c => c.Name));

            await PumpUntil(() => window.GetVisualDescendants().OfType<EditorView>().Any(), TimeSpan.FromSeconds(30));
            var editor = window.GetVisualDescendants().OfType<EditorView>().Single();
            Assert.Equal("Product.cs", editor.Document.Title);

            await PumpUntil(() => editor.Document.Controller.Colorizer.TokenCount > 0, TimeSpan.FromMinutes(1));
            Assert.True(editor.Document.Controller.Colorizer.TokenCount > 20);
            Assert.Equal("Ready", shell.Status);
        }
        finally
        {
            window.Close();
            await shell.DisposeAsync();
        }
    }

    /// <summary>Runs dispatcher jobs and render ticks until <paramref name="condition"/> holds.</summary>
    private static async Task PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the shell.");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        }
    }
}
