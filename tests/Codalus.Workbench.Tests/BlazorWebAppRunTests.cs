using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Output;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Running a Blazor Web App (task #90, found reproducing #75 on the maintainer's app upgraded to the Web App layout):
/// the server is the default startup project, a missing browser opener doesn't end the IDE, and Hot Reload doesn't
/// report "applied" for an update the browser refused.
/// </summary>
public sealed class BlazorWebAppRunTests
{
    private static ProjectInfo Project(string path, string? outputType, params ReferenceInfo[] references) =>
        new(path, Path.GetFileNameWithoutExtension(path), ProjectStates.Loaded, null, ["net10.0"], outputType, [], references, [], true);

    private static ReferenceInfo Package(string id) => new(ReferenceKinds.Package, id, "10.0.13", null, "net10.0", true);

    private static ReferenceInfo ProjectReference(string path) => new(ReferenceKinds.Project, Path.GetFileNameWithoutExtension(path), null, path, "net10.0", true);

    private const string Client = "/src/Presentation/Client/App.Client.csproj";
    private const string Server = "/src/Presentation/Server/App.Server.csproj";
    private const string Engine = "/src/Engine/Engine.csproj";

    [Fact]
    public void A_blazor_web_apps_server_is_the_default_startup_project_not_its_webassembly_client()
    {
        // The solution lists the client first (BlazorSudoku.sln does): its WasmAppHost serves 404 at "/".
        var projects = new Dictionary<string, ProjectInfo>
        {
            [Client] = Project(Client, "Exe", Package("Microsoft.AspNetCore.Components.WebAssembly"), ProjectReference(Engine)),
            [Engine] = Project(Engine, "Library"),
            [Server] = Project(Server, "Exe", Package("Microsoft.AspNetCore.Components.WebAssembly.Server"), ProjectReference(Client), ProjectReference(Engine)),
        };
        Assert.Equal(Server, RunService.DefaultStartupProject([Client, Engine, Server], projects));

        // A standalone WebAssembly app (no server references it) and other solutions keep the first executable project.
        projects.Remove(Server);
        Assert.Equal(Client, RunService.DefaultStartupProject([Client, Engine], projects));
        var console = "/src/Tool/Tool.csproj";
        projects[console] = Project(console, "Exe");
        Assert.Equal(console, RunService.DefaultStartupProject([Engine, console, Client], projects));
    }

    [Fact]
    public async Task A_browser_that_cant_open_is_reported_in_the_run_output_instead_of_ending_the_ide()
    {
        // No xdg-open (a container, a minimal desktop): opening the page threw on the output reader's thread.
        var output = new OutputService();
        var runs = new RunService(null!, output, _ => throw new System.ComponentModel.Win32Exception(2, "An error occurred trying to start process 'xdg-open'."));
        var (shell, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c echo Now listening on: http://localhost:5299 & ping -n 2 127.0.0.1 > nul")
            : ("/bin/sh", "-c \"echo 'Now listening on: http://localhost:5299'; sleep 1\"");
        var plan = new LaunchPlan(shell, arguments, Path.GetTempPath(), new Dictionary<string, string>(), "http://localhost:5299/");
        var program = runs.StartPlan("server", "Server", plan);
        await program.Exited.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var text = program.Channel!.GetText();
        Assert.Contains("http://localhost:5299/", text, StringComparison.Ordinal);
        Assert.Contains("xdg-open", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_update_the_browser_refused_isnt_reported_as_applied()
    {
        // SDK 10.0.401: the browser's error comes as a "[Browser #1]" block whose "Error:" line has no prefix, and dotnet
        // watch then prints "changes applied" anyway (seen with build output left by another SDK).
        var session = new HotReloadSession();
        foreach (var line in new[]
                 {
                     "dotnet watch : Waiting for changes",
                     "dotnet watch : File updated: /src/Presentation/Client/Components/GameBoard.razor",
                     "dotnet watch : [Browser #1]    at Microsoft.DotNet.HotReload.WebAssembly.Browser.WebAssemblyHotReload.GetRequiredAgent()",
                     "   at Microsoft.DotNet.HotReload.WebAssembly.Browser.WebAssemblyHotReload.ApplyHotReloadDeltas(Delta[] deltas, Int32 loggingLevel)",
                     "Error: Unable to apply managed code updates because the app was not started by 'dotnet watch'.",
                     "    at un (http://localhost:5004/_framework/dotnet.runtime.v06hirbjsv.js:3:27548)",
                     "dotnet watch : C# and Razor changes applied in 1447ms.",
                 })
        {
            session.OnLine(line);
        }

        Assert.NotEqual(HotReloadState.Watching, session.State);
        Assert.Contains("Unable to apply managed code updates", session.Message, StringComparison.Ordinal);
        Assert.Equal(0, session.Applied);

        // The next update that works is applied again.
        session.OnLine("dotnet watch : File updated: /src/Presentation/Client/Components/GameBoard.razor");
        session.OnLine("dotnet watch : C# and Razor changes applied in 252ms.");
        Assert.Equal(HotReloadState.Watching, session.State);
        Assert.Equal(1, session.Applied);
    }

    [Theory]
    [InlineData("dotnet watch : Restart is needed to apply the changes.", HotReloadState.Restarting)]
    [InlineData("dotnet watch : Waiting for a file to change before restarting ...", HotReloadState.Exited)]
    [InlineData("dotnet watch : [BlazorSudoku.Server (net10.0)] Exited with error code 134", HotReloadState.Exited)]
    public void Restarts_and_exits_are_told_apart(string line, HotReloadState state) =>
        Assert.Equal(state, HotReloadOutput.Parse(line)?.State);
}
