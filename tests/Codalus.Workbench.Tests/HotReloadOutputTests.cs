using Codalus.Workbench.Build;

namespace Codalus.Workbench.Tests;

/// <summary>Following <c>dotnet watch</c> from its output (M15b, ADR 0022), as the SDKs word it.</summary>
public sealed class HotReloadOutputTests
{
    [Theory]
    [InlineData("dotnet watch : C# and Razor changes applied in 1203ms.", HotReloadState.Watching, 1203)]
    [InlineData("dotnet watch 🔥 C# and Razor changes applied in 128ms.", HotReloadState.Watching, 128)]
    [InlineData("dotnet watch : Hot reload of changes succeeded.", HotReloadState.Watching, null)]
    [InlineData("dotnet watch : Unable to apply changes due to compilation errors.", HotReloadState.Errors, null)]
    [InlineData(@"dotnet watch : C:\app\Program.cs(9,48): error CS1525: Invalid expression term ';'", HotReloadState.Errors, null)]
    [InlineData("dotnet watch : [hr (net10.0)] Restarting application ...", HotReloadState.Restarting, null)]
    [InlineData("dotnet watch : File updated: .\\Program.cs", HotReloadState.Applying, null)]
    [InlineData("dotnet watch : Waiting for changes", HotReloadState.Watching, null)]
    [InlineData("dotnet watch : [hr (net10.0)] Exited", HotReloadState.Exited, null)]
    public void Watch_lines_become_states(string line, HotReloadState state, int? milliseconds)
    {
        var e = Assert.IsType<HotReloadEvent>(HotReloadOutput.Parse(line));
        Assert.Equal(state, e.State);
        Assert.Equal(milliseconds is { } ms ? TimeSpan.FromMilliseconds(ms) : null, e.Applied);
    }

    [Theory]
    [InlineData("info: Microsoft.Hosting.Lifetime[14]")]
    [InlineData("Now listening on: http://localhost:5000")]
    [InlineData("  Determining projects to restore...")]
    [InlineData("dotnet watch : Hot reload enabled. For a list of supported edits, see https://aka.ms/dotnet/hot-reload.")]
    public void Other_lines_are_ignored(string line) => Assert.Null(HotReloadOutput.Parse(line));

    [Fact]
    public void A_session_counts_updates_and_restarts()
    {
        var session = new HotReloadSession();
        var events = new List<HotReloadState>();
        session.Changed += (_, e) => events.Add(e.State);
        foreach (var line in new[]
                 {
                     "dotnet watch : Waiting for changes",
                     "dotnet watch : File updated: .\\Program.cs",
                     "dotnet watch : C# and Razor changes applied in 90ms.",
                     "dotnet watch : [app (net10.0)] Restarting application ...",
                 })
        {
            session.OnLine(line);
        }

        Assert.Equal([HotReloadState.Watching, HotReloadState.Applying, HotReloadState.Watching, HotReloadState.Restarting], events);
        Assert.Equal((1, 1), (session.Applied, session.Restarts));
        Assert.Equal(TimeSpan.FromMilliseconds(90), session.LastApplied);
        Assert.Equal(HotReloadState.Restarting, session.State);
    }

    [Fact]
    public void A_session_has_started_once_dotnet_watch_watches_not_when_the_app_listens()
    {
        // dotnet watch drops the changes saved before it watches; the app can already listen by then.
        var session = new HotReloadSession();
        foreach (var line in new[]
                 {
                     "dotnet watch : Hot reload enabled. For a list of supported edits, see https://aka.ms/dotnet/hot-reload.",
                     "dotnet watch : Press Ctrl+R to restart.",
                     "Build succeeded.",
                     "dotnet watch : Loading projects ...",
                     "dotnet watch : Loaded 1 project(s) in 1.1s.",
                     "      Now listening on: http://localhost:5280",
                 })
        {
            session.OnLine(line);
        }

        Assert.False(session.Started.IsCompleted);
        session.OnLine("dotnet watch : Waiting for changes");
        Assert.True(session.Started.IsCompleted);
        Assert.Equal(HotReloadState.Watching, session.State);

        // A start that fails or ends doesn't keep anyone waiting.
        var failed = new HotReloadSession();
        failed.OnLine("dotnet watch : Build failed.");
        Assert.True(failed.Started.IsCompleted);
    }

    [Theory]
    [InlineData("[codalus-hot-reload-agent] Loaded into process 6568: /app/bin/Debug/net10.0/BlazorSudoku.Server.dll (/sdk/hotreload/net10.0/Microsoft.Extensions.DotNetDeltaApplier.dll)", 6568)]
    [InlineData("\u001b[2m[BlazorHosted (net10.0)] [codalus-hot-reload-agent] Loaded into process 42: /app/BlazorHosted.dll\u001b[0m", 42)]
    [InlineData("[codalus-hot-reload-agent] Loaded into process: /app/bin/Debug/net10.0/BlazorSudoku.Server.dll (/sdk/10.0.401/...)", null)]
    public void The_agents_line_says_the_app_started_with_its_id_from_sdk_11_on(string line, int? processId)
    {
        line = System.Text.RegularExpressions.Regex.Unescape(line);
        Assert.True(HotReloadOutput.IsAgentLoaded(line, out var id));
        Assert.Equal(processId, id);

        var session = new HotReloadSession();
        var started = new List<int?>();
        session.AppStarted += started.Add;
        session.OnLine(line);
        session.OnLine("[codalus-hot-reload-agent] Posix signal handlers registered.");
        Assert.Equal([processId], started);
        Assert.Equal(processId, session.AppProcessId);
    }

    [Fact]
    public void The_servers_refusal_under_the_debugger_is_a_refusal_not_an_error()
    {
        // SDK 11 RC2 (and 10.0.401) with netcoredbg attached to a Blazor Web App's server: the browser applies the update.
        var session = new HotReloadSession("/src/App/Presentation/Server");
        var refused = 0;
        var files = new List<string>();
        session.Refused += () => refused++;
        session.FilesUpdated += f => files.AddRange(f);
        foreach (var line in new[]
                 {
                     "dotnet watch : Waiting for changes",
                     "dotnet watch : File updated: ../Client/Components/GameBoard.razor",
                     "dotnet watch : [BlazorSudoku.Server (net10.0)] The runtime failed to applying the change: Assembly updates cannot be applied while a debugger is attached.",
                     "dotnet watch : [BlazorSudoku.Server (net10.0)] Further changes won't be applied to this process.",
                     "dotnet watch : [BlazorSudoku.Server (net10.0)] Further changes won't be applied to this process.",
                     "dotnet watch : Files updated: /src/App/Presentation/Client/Pages/Index.razor, Layout/NavMenu.razor",
                 })
        {
            session.OnLine(line);
        }

        Assert.True(session.UpdatesRefused);
        Assert.Equal(1, refused);
        Assert.NotEqual(HotReloadState.Errors, session.State);
        Assert.Equal(
            [Path.GetFullPath("/src/App/Presentation/Client/Components/GameBoard.razor"), Path.GetFullPath("/src/App/Presentation/Client/Pages/Index.razor"), Path.GetFullPath("/src/App/Presentation/Server/Layout/NavMenu.razor")],
            files);
    }

    [Fact]
    public void A_restart_and_the_apps_end_are_announced_and_its_own_output_is_told_from_dotnet_watchs()
    {
        var session = new HotReloadSession();
        var events = new List<string>();
        session.RestartAnnounced += () => events.Add("restart");
        session.AppEnded += () => events.Add("ended");
        session.AppOutput += line => events.Add("app: " + line.Trim());
        foreach (var line in new[]
                 {
                     "  BlazorSudoku.Server -> /app/bin/Debug/net10.0/BlazorSudoku.Server.dll", // the build's, before the app
                     "[codalus-hot-reload-agent] Loaded into process 7594: /app/bin/Debug/net10.0/BlazorSudoku.Server.dll",
                     "dotnet watch : Waiting for changes",
                     "info: Microsoft.Hosting.Lifetime[14]",
                     "      Now listening on: http://localhost:5004",
                     "dotnet watch : File updated: /app/Client/Components/GameBoard.razor",
                     "dotnet watch : Restart is needed to apply the changes.",
                     "dotnet watch : [BlazorSudoku.Server (net10.0)] Exited",
                     "  BlazorSudoku.Server -> /app/bin/Debug/net10.0/BlazorSudoku.Server.dll",
                     "dotnet watch : [BlazorSudoku.Server (net10.0)] Exited with error code 134",
                     "dotnet watch : Waiting for a file to change before restarting ...",
                 })
        {
            session.OnLine(line);
        }

        Assert.Equal(["app: info: Microsoft.Hosting.Lifetime[14]", "app: Now listening on: http://localhost:5004", "restart", "ended"], events);
        Assert.Equal(1, session.ListeningCount);
        Assert.Equal(1, session.Restarts);
        Assert.Equal(HotReloadState.Exited, session.State);
    }

    [Fact]
    public void A_path_dotnet_watch_cut_its_working_directory_off_as_text_is_put_back_together()
    {
        // BlazorHosted/BlazorHosted runs dotnet watch; BlazorHosted/BlazorHosted.Client/Pages/Counter.razor changed.
        var root = Path.Combine(Path.GetTempPath(), "codalus-hr-" + Guid.NewGuid().ToString("N"));
        var server = Path.Combine(root, "BlazorHosted", "BlazorHosted");
        var counter = Path.Combine(root, "BlazorHosted", "BlazorHosted.Client", "Pages", "Counter.razor");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(Path.GetDirectoryName(counter)!);
        File.WriteAllText(counter, string.Empty);
        try
        {
            var session = new HotReloadSession(server);
            var files = new List<string>();
            session.FilesUpdated += files.AddRange;
            session.OnLine("dotnet watch : File updated: ./.Client/Pages/Counter.razor");
            Assert.Equal([counter], files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
