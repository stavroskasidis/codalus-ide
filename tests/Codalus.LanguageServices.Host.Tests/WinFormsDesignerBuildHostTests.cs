using System.Collections.Concurrent;
using Codalus.Workbench.Previewer;
using Codalus.Workbench.WinForms;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// #66, Windows only: the designer host runs on a copy of the build output, so the app builds while a form is loaded
/// (before, the host loaded bin/ in place and the next build failed with MSB3027). Its own fixture: the rebuild here
/// can't race another class's host.
/// </summary>
public sealed class WinFormsDesignerBuildHostTests(WinFormsHostFixture fixture) : IClassFixture<WinFormsHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_build_while_a_form_is_loaded_succeeds_and_the_host_keeps_running_on_its_copy()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var target = fixture.Target("OrderForm.cs");
        var source = fixture.Read("OrderForm.Designer.cs");
        var host = (WinFormsDesignerProcess)await WinFormsDesignerProcess.StartAsync(target, Ct);
        var log = new ConcurrentQueue<string>();
        host.Log += (_, line) => log.Enqueue(line);
        var copy = host.CopyFolder!;
        try
        {
            // The form loads from the copy: the app's own user control, its satellites and pdb are there.
            var render = await host.LoadAsync(WinFormsDesignerService.LoadParams(target, source.Model, source.Model.Namespace + "." + source.Model.ClassName), Ct);
            Assert.True(render.Errors.Length == 0, string.Join("\n", render.Errors));
            Assert.Equal("WinFormsApp.Controls.CounterControl", render.Components.Single(c => c.Name == "counterControl").Type);
            Assert.StartsWith(BuildOutputShadowCopy.DefaultRoot + Path.DirectorySeparatorChar, copy, StringComparison.OrdinalIgnoreCase);
            var app = Path.Combine(copy, "0");
            Assert.True(File.Exists(Path.Combine(app, "WinFormsApp.dll")));
            Assert.True(File.Exists(Path.Combine(app, "WinFormsApp.pdb")));
            Assert.True(File.Exists(Path.Combine(app, "fr", "WinFormsApp.resources.dll")));
            Assert.Contains(log, l => l.StartsWith("Copied the build output (", StringComparison.Ordinal));
            var loaded = DateTime.UtcNow.AddSeconds(10);
            while (!log.Any(l => l.StartsWith("[designer] WinFormsApp loaded from " + app, StringComparison.OrdinalIgnoreCase)) && DateTime.UtcNow < loaded)
            {
                await Task.Delay(100, Ct);
            }

            Assert.True(log.Any(l => l.StartsWith("[designer] WinFormsApp loaded from " + app, StringComparison.OrdinalIgnoreCase)), "The host didn't load the app from its copy:\n" + string.Join("\n", log));

            // Nothing holds the build output: the build can replace the assembly and its pdb.
            using (File.Open(fixture.AppDll, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }

            using (File.Open(Path.ChangeExtension(fixture.AppDll, ".pdb"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }

            // A build while the form is loaded succeeds, and the host knows it must start again to show it.
            Assert.False(await host.IsOutdatedAsync(Ct));
            await File.AppendAllTextAsync(Path.Combine(fixture.App, "Counter.cs"), "\n// rebuilt while the designer runs\n", Ct);
            var (code, output) = await WinFormsHostFixture.DotnetAsync("build", Path.Combine(fixture.App, "WinFormsApp.csproj"), "-nologo", "-v", "quiet");
            Assert.True(code == 0, output);
            Assert.DoesNotContain("MSB3027", output, StringComparison.Ordinal);
            Assert.DoesNotContain("MSB3026", output, StringComparison.Ordinal);
            Assert.True(await host.IsOutdatedAsync(Ct));

            // The host still runs on its copy.
            var again = await host.RenderAsync(Ct);
            Assert.NotNull(again.Frame);
        }
        finally
        {
            await host.DisposeAsync();
        }

        // The copy goes with the host.
        var until = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists(copy) && DateTime.UtcNow < until)
        {
            await Task.Delay(100, Ct);
        }

        Assert.False(Directory.Exists(copy), $"{copy} is still there");
    }
}
