using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using DaedalusStudio.LanguageServices.Protocol;

namespace DaedalusStudio.Shell.Tests;

/// <summary>M16a in the shell: Razor files served by the C# host (ADR 0023), and a Blazor app with Hot Reload.</summary>
public sealed class BlazorTests
{
    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    [AvaloniaFact]
    public async Task Razor_editing_and_a_blazor_app_with_hot_reload()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Blazor");
        var app = Path.Combine(folder, "BlazorApp");
        var port = FreePort();
        var settings = Path.Combine(app, "Properties", "launchSettings.json");
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken)).Replace("5280", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), TestContext.Current.CancellationToken);
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Blazor.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 120);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 180);

        // A .razor file is served by the C# host: hover in C#, and an error reaches the Error List at its line.
        var counter = Path.Combine(app, "Components", "Pages", "Counter.razor");
        var editor = await f.Shell.OpenFileAsync(counter, preview: false, null);
        await ShellFixture.PumpUntil(() => editor?.Controller is not null);
        Assert.Equal(ShellHost.RazorLanguageId, editor!.Model.LanguageId);
        var client = f.Shell.Languages.RunningClient(ShellHost.CSharpLanguageId)!;
        var text = editor.Model.Text.Text;
        Hover? hover = null;
        await ShellFixture.PumpUntil(() =>
        {
            hover = client.HoverAsync(editor.Model.Uri, editor.Model.PositionAt(text.IndexOf("@currentCount", StringComparison.Ordinal) + 3), ShellFixture.RequestTimeout()).GetAwaiter().GetResult();
            return hover is not null;
        }, seconds: 60);
        Assert.Contains("currentCount", hover!.Contents.Value, StringComparison.Ordinal);

        var offset = text.IndexOf("currentCount++", StringComparison.Ordinal);
        editor.Model.Text.Replace(offset, "currentCount".Length, "currentCountt");
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Diagnostics.For(counter).Any(d => d.Code == "CS0103"), seconds: 90);
        Assert.Equal(editor.Model.PositionAt(offset).Line, f.Shell.Languages.Diagnostics.For(counter).First(d => d.Code == "CS0103").Range.Start.Line);
        editor.Model.Text.Replace(offset, "currentCountt".Length, "currentCount");
        await ShellFixture.PumpUntil(() => !f.Shell.Languages.Diagnostics.For(counter).Any(d => d.Code == "CS0103"), seconds: 90);

        // Run with Hot Reload; a markup edit, saved, changes the rendered page.
        await f.Shell.StartWithHotReloadAsync();
        await ShellFixture.PumpUntil(() => f.Shell.HotReloadProgram is not null, seconds: 180);
        var program = f.Shell.HotReloadProgram!;
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        try
        {
            await ShellFixture.PumpUntil(() => program.Channel?.GetText().Contains("Now listening on", StringComparison.Ordinal) == true, seconds: 240);
            Assert.Contains("<h1>Counter</h1>", await http.GetStringAsync("/counter", TestContext.Current.CancellationToken), StringComparison.Ordinal);
            var heading = editor.Model.Text.Text.IndexOf("<h1>Counter</h1>", StringComparison.Ordinal);
            editor.Model.Text.Replace(heading, "<h1>Counter</h1>".Length, "<h1>Clicker</h1>");
            f.Shell.Run("run.applyCodeChanges");
            var page = string.Empty;
            await ShellFixture.PumpUntil(() =>
            {
                try
                {
                    page = http.GetStringAsync("/counter").GetAwaiter().GetResult();
                }
                catch (HttpRequestException)
                {
                    // Restarting.
                }

                return page.Contains("<h1>Clicker</h1>", StringComparison.Ordinal);
            }, seconds: 120);
        }
        finally
        {
            f.Shell.StopRunning();
        }
    }
}
