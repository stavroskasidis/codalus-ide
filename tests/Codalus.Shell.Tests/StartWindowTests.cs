using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Tests;

/// <summary>The start window (M38): recent entries in groups, search, pins, and opening a solution from it.</summary>
public sealed class StartWindowTests
{
    [AvaloniaFact]
    public async Task The_start_window_lists_recent_solutions_searches_pins_and_opens_one()
    {
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        var gone = Path.Combine(f.UserData, "gone", "Gone.sln");
        f.Shell.Recent.Add(gone);
        f.Shell.Recent.Add(solution);

        f.Shell.Run("workbench.showStartWindow");
        await ShellFixture.PumpUntil(() => f.Shell.IsStartWindowOpen);
        var model = f.Shell.StartWindowModel!;
        Assert.Equal(["Today", "ProjectSystem.slnx", "Gone.sln"], model.Rows.Select(r => r is StartGroupRowViewModel g ? g.Title : ((StartEntryRowViewModel)r).Name));
        Assert.False(model.Rows.OfType<StartEntryRowViewModel>().Single(r => r.Name == "Gone.sln").Exists);

        // Search, then pin: a Pinned group first.
        model.Search = "projectsys";
        Assert.Equal("ProjectSystem.slnx", Assert.Single(model.Rows.OfType<StartEntryRowViewModel>()).Name);
        model.Search = "nothing like it";
        Assert.True(model.IsEmpty);
        model.Search = string.Empty;
        model.Rows.OfType<StartEntryRowViewModel>().Single(r => r.Name == "Gone.sln").PinCommand.Execute(null);
        await ShellFixture.PumpUntil(() => model.Rows.FirstOrDefault() is StartGroupRowViewModel { Title: "Pinned" });
        model.Rows.OfType<StartEntryRowViewModel>().Single(r => r.Name == "Gone.sln").RemoveCommand.Execute(null);
        await ShellFixture.PumpUntil(() => model.Rows.OfType<StartEntryRowViewModel>().All(r => r.Name != "Gone.sln"));
        Assert.DoesNotContain(gone, f.Shell.Recent.Items);

        // Opening an entry closes the window and opens the solution.
        model.Rows.OfType<StartEntryRowViewModel>().Single().OpenCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Solution?.Path == solution, seconds: 120);

        // Continue without code just closes it.
        f.Shell.ShowStartWindow();
        await ShellFixture.PumpUntil(() => f.Shell.IsStartWindowOpen);
        f.Shell.StartWindowModel!.ContinueCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);
    }
}
