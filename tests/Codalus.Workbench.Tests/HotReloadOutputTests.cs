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
}
