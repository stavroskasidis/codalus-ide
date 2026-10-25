using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Debugging;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Debugging;

namespace Codalus.Shell.Tests;

/// <summary>
/// A Break All where no thread has managed frames (a program paused before its Main runs, as the packaged WinUI app
/// attached to at its start can be): the Call Stack shows [External Code], as the classic IDE does, not an empty window.
/// The adapter is the scripted one (<c>Codalus.LspConformance.Server --dap</c>).
/// </summary>
public sealed class CallStackExternalCodeTests
{
    private static string ScriptedServer => Path.Combine(ShellFixture.RepoRoot, "tests", "Codalus.LspConformance.Server", "bin",
        typeof(CallStackExternalCodeTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusConfiguration").Value!, Codalus.Tests.RepoBuild.TargetFramework, "codalus-lsp-conformance.dll");

    [AvaloniaFact]
    public async Task A_stop_without_frames_shows_external_code_that_selects_nothing()
    {
        await using var debug = new DebugService(() => null);
        var opened = new List<string>();
        var callStack = new CallStackViewModel(debug, (path, _) =>
        {
            opened.Add(path);
            return Task.CompletedTask;
        });
        var launch = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["threads"] = new object[] { new { id = 1, name = "<No name>", frames = Array.Empty<object>() }, new { id = 2, name = "Main Thread", frames = "error" } },
        });
        await debug.LaunchAdapterAsync("Scripted", new DebugAdapterStartInfo("dotnet", [ScriptedServer, "--dap"], AppContext.BaseDirectory), launch, ShellFixture.RequestTimeout(60));
        await debug.PauseAsync();
        await ShellFixture.PumpUntil(() => debug.State == DebugState.Stopped && callStack.Frames.Count > 0, seconds: 60, describe: () => $"Debugger: {debug.State}, frames {debug.Frames.Count}");

        var row = Assert.Single(callStack.Frames);
        Assert.True(row.IsExternalCode);
        Assert.True(row.IsTop);
        Assert.Equal(FrameRow.ExternalCode, row.Name);
        Assert.Equal(string.Empty, row.Location);
        Assert.False(callStack.IsEmpty);
        Assert.Equal(1, callStack.SelectedThread?.Id);

        // Not a frame of the adapter's: activating it selects no frame (Locals would ask the adapter for its scopes) and opens nothing.
        await callStack.ActivateAsync(row);
        Assert.Null(debug.SelectedFrame);
        Assert.Empty(opened);

        // Running again, it goes.
        await debug.ContinueAsync();
        await ShellFixture.PumpUntil(() => callStack.Frames.Count == 0);
    }
}
