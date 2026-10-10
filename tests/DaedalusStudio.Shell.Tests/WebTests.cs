using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;

namespace DaedalusStudio.Shell.Tests;

/// <summary>M15a in the shell: the Endpoints Explorer, .http files against the running fixture app, generated requests and the environment.</summary>
public sealed class WebTests
{
    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    [AvaloniaFact]
    public async Task Endpoints_http_files_and_the_running_app()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Web");
        var project = Path.Combine(folder, "WebApi");
        var port = FreePort();
        foreach (var file in new[] { Path.Combine(project, "Properties", "launchSettings.json"), Path.Combine(project, "WebApi.http") })
        {
            await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Replace("5248", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), TestContext.Current.CancellationToken);
        }

        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Web.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 120);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: LanguageServices.Protocol.WorkspaceStates.Ready, Projects: 1 }, seconds: 180);

        // The Endpoints Explorer lists the routes by project.
        await f.Shell.ShowEndpointsAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Endpoints.Rows.Any(r => r.Route == "/api/todos/{id:int}"), seconds: 60);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<EndpointsExplorerView>().Any());
        Assert.Equal("WebApi", f.Shell.Endpoints.Rows[0].ProjectHeader);
        f.Shell.Endpoints.Filter = "orders";
        Assert.Equal(5, f.Shell.Endpoints.Rows.Count); // the header and four actions
        f.Shell.Endpoints.Filter = string.Empty;

        // The .http file: its language, grammar and send glyphs.
        var http = Path.Combine(project, "WebApi.http");
        var editor = await f.Shell.OpenFileAsync(http, preview: false, null);
        await ShellFixture.PumpUntil(() => editor?.Controller is not null);
        Assert.Equal(ShellHost.HttpLanguageId, editor!.Model.LanguageId);
        Assert.Equal("source.http", f.Shell.TextMate.ScopeFor(ShellHost.HttpLanguageId));
        Assert.Equal([3, 8, 14], editor.Controller!.HttpMargin.RequestLines);

        // Start the app, then send requests.
        await f.Shell.StartWithoutDebuggingAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Count == 1, seconds: 180);
        try
        {
            await ShellFixture.PumpUntil(() => f.Shell.Runs.Running[0].Channel?.GetText().Contains("Now listening on", StringComparison.Ordinal) == true, seconds: 120);
            await f.Shell.SendHttpRequestAsync(http, editor.Model.Text.Text, 3);
            await ShellFixture.PumpUntil(() => f.Shell.HttpResponse.Selected is not null);
            Assert.Equal(200, f.Shell.HttpResponse.Selected!.Exchange.Response?.StatusCode);
            Assert.Equal("Hello from the Web fixture", f.Shell.HttpResponse.Selected.Exchange.Response?.Body);

            await f.Shell.SendHttpRequestAsync(http, editor.Model.Text.Text, 14);
            Assert.Equal(201, f.Shell.HttpResponse.Selected!.Exchange.Response?.StatusCode);
            Assert.Contains("\"title\": \"From the .http file\"", f.Shell.HttpResponse.Body.Text, StringComparison.Ordinal); // indented JSON
            Assert.Equal("json", f.Shell.HttpResponse.Body.LanguageId);
            Assert.Equal(2, f.Shell.HttpResponse.History.Count);
            f.Shell.Layout.ShowToolWindow(HttpResponseViewModel.ToolWindowId);
            await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<HttpResponseView>().Any(v => v.BodyText.Contains("From the .http file", StringComparison.Ordinal)));
        }
        finally
        {
            f.Shell.StopRunning();
        }

        // Generate Request appends to the project's .http file.
        var todo = f.Shell.Endpoints.Endpoints.Single(e => e.Route == "/api/todos/{id:int}");
        await f.Shell.GenerateRequestAsync(todo);
        await ShellFixture.PumpUntil(() => File.ReadAllText(http).Contains("GET {{WebApi_HostAddress}}/api/todos/{{id}}", StringComparison.Ordinal));
        Assert.Contains("@WebApi_HostAddress", File.ReadAllText(http), StringComparison.Ordinal);
        var post = f.Shell.Endpoints.Endpoints.Single(e => e is { HttpMethod: "POST", Route: "/api/todos" });
        Assert.Equal("POST {{h}}/api/todos\nAccept: application/json\nContent-Type: application/json\n\n{\n}\n", ShellHost.GeneratedRequest(post.HttpMethod, "{{h}}", post.Route));

        // The environment override reaches the plan.
        await f.Shell.SelectAspNetEnvironmentAsync("Staging");
        var (plan, _, _) = await f.Shell.Runs.PlanAsync(Path.Combine(project, "WebApi.csproj"), CancellationToken.None);
        Assert.Equal("Staging", plan.Environment["ASPNETCORE_ENVIRONMENT"]);
        await f.Shell.SelectAspNetEnvironmentAsync(string.Empty);
        Assert.Null(f.Shell.Runs.EnvironmentOverride);
    }
}
