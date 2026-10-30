using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
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

        await f.RunAsync("workbench.showStartWindow");
        await ShellFixture.PumpUntil(() => f.Shell.IsStartWindowOpen);
        var model = f.Shell.StartWindowModel!;
        Assert.Equal(["Today", "ProjectSystem.slnx", "Gone.sln"], model.Rows.Select(r => r is StartGroupRowViewModel g ? g.Title : ((StartEntryRowViewModel)r).Name));
        await ShellFixture.PumpUntil(() => !model.Rows.OfType<StartEntryRowViewModel>().Single(r => r.Name == "Gone.sln").Exists); // checked off the UI thread (M56)
        Assert.True(model.Rows.OfType<StartEntryRowViewModel>().Single(r => r.Name == "ProjectSystem.slnx").Exists);

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

    /// <summary>
    /// The start window lists recent entries at once, their icons from their names, and checks them off the UI thread: one
    /// on a share that doesn't answer shows as not found once its check's time is up, and stays in the list.
    /// </summary>
    [AvaloniaFact]
    public async Task Recent_entries_show_at_once_and_one_that_doesnt_answer_shows_as_not_found()
    {
        await using var f = ShellFixture.Create();
        var folder = Directory.CreateDirectory(Path.Combine(f.UserData, "work", "Folder")).FullName;
        var offline = Path.Combine(f.UserData, "offline", "Offline.slnx");
        var gone = Path.Combine(f.UserData, "gone", "Gone.sln");
        using var release = new ManualResetEventSlim();
        var onUiThread = 0;
        f.Shell.RecentProbe = new PathProbe(path =>
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Interlocked.Increment(ref onUiThread);
            }

            if (path == offline)
            {
                release.Wait(TimeSpan.FromSeconds(30));
            }

            return PathProbe.Kind(path);
        });
        f.Shell.Recent.Add(gone);
        f.Shell.Recent.Add(offline);
        f.Shell.Recent.Add(folder);
        try
        {
            f.Shell.ShowStartWindow();
            StartEntryRowViewModel Row(string path) => f.Shell.StartWindowModel!.Rows.OfType<StartEntryRowViewModel>().Single(r => r.Entry.Path == path);
            Assert.Equal(FileIcons.ForFolder("Folder", open: false), Row(folder).Icon);
            Assert.Equal(FileIcons.ForFile("Offline.slnx"), Row(offline).Icon);

            await ShellFixture.PumpUntil(() => Row(gone).Kind is not null && Row(folder).Kind is not null);
            Assert.False(Row(gone).Exists);
            Assert.Equal(PathState.Folder, Row(folder).Kind);

            await ShellFixture.PumpUntil(() => Row(offline).Kind is not null, seconds: 10);
            Assert.Equal(PathState.Unknown, Row(offline).Kind);
            Assert.False(Row(offline).Exists);
            Assert.Equal(FileIcons.ForFile("Offline.slnx"), Row(offline).Icon);
            Assert.Contains(offline, f.Shell.Recent.Items);
            Assert.Equal(0, onUiThread);
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>
    /// The last-solution startup opens the first recent entry that is there, in order: one that doesn't answer counts as not
    /// there once its check's time is up, and so does one that's gone.
    /// </summary>
    [AvaloniaFact]
    public async Task The_last_solution_startup_skips_entries_that_arent_there_or_dont_answer()
    {
        await using var f = ShellFixture.Create();
        var folder = Directory.CreateDirectory(Path.Combine(f.UserData, "work", "Folder")).FullName;
        var offline = Path.Combine(f.UserData, "offline", "Offline.slnx");
        using var release = new ManualResetEventSlim();
        f.Shell.RecentProbe = new PathProbe(path =>
        {
            if (path == offline)
            {
                release.Wait(TimeSpan.FromSeconds(30));
            }

            return PathProbe.Kind(path);
        });
        f.Shell.Recent.Add(folder);
        f.Shell.Recent.Add(Path.Combine(f.UserData, "gone", "Gone.sln"));
        f.Shell.Recent.Add(offline);
        try
        {
            Assert.Equal((folder, PathState.Folder), await f.Shell.FirstRecentThereAsync());
        }
        finally
        {
            release.Set();
        }
    }
}
