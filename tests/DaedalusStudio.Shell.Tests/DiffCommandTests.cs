using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.Views;

namespace DaedalusStudio.Shell.Tests;

/// <summary><c>daedalus diff left right</c>: parsing, the hand-off to a running shell, and the file comparison.</summary>
public sealed class DiffCommandTests
{
    [Fact]
    public void Diff_takes_two_files()
    {
        var options = ShellOptions.Parse(["diff", "a.txt", "b.txt", "--user-data-dir", "x"]);
        Assert.Equal((Path.GetFullPath("a.txt"), Path.GetFullPath("b.txt")), options.Diff);
        Assert.Equal("x", options.UserDataDir);
        Assert.Null(options.Path);
        Assert.Empty(options.Errors);

        var missing = ShellOptions.Parse(["diff", "a.txt"]);
        Assert.Null(missing.Diff);
        Assert.Single(missing.Errors);

        // A solution called "diff" is still a path when it isn't first.
        Assert.Equal("diff", ShellOptions.Parse(["--dev", "diff"]).Path);
    }

    [Fact]
    public async Task A_second_process_hands_its_request_to_the_running_one()
    {
        var name = SingleInstance.PipeName(Path.Combine(Path.GetTempPath(), "daedalus-instance-" + Guid.NewGuid().ToString("N")));
        Assert.False(SingleInstance.TrySend(name, new InstanceRequest("diff", ["a", "b"])));

        var received = new TaskCompletionSource<InstanceRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = SingleInstance.Listen(name, request => received.TrySetResult(request));
        var sent = false;
        for (var i = 0; i < 50 && !sent; i++)
        {
            sent = SingleInstance.TrySend(name, new InstanceRequest("diff", ["left.txt", "right.txt"]));
            if (!sent)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        Assert.True(sent);
        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("diff", request.Command);
        Assert.Equal(["left.txt", "right.txt"], request.Arguments);
        Assert.NotEqual(name, SingleInstance.PipeName(Path.GetTempPath())); // one pipe per user-data folder
    }

    [AvaloniaFact]
    public async Task Two_files_open_side_by_side()
    {
        await using var f = ShellFixture.Create();
        var left = Path.Combine(f.UserData, "verified.txt");
        var right = Path.Combine(f.UserData, "received.txt");
        await File.WriteAllTextAsync(left, "one\ntwo\nthree\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(right, "one\n2\nthree\nfour\n", TestContext.Current.CancellationToken);

        var document = await f.Shell.OpenFileDiffAsync(left, right);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DiffView>().Any());

        Assert.Equal("verified.txt ↔ received.txt", document.Title);
        Assert.False(document.ViewModel.CanStage);
        Assert.Equal("+2 −1", document.ViewModel.Summary);
        var view = f.Window.GetVisualDescendants().OfType<DiffView>().Single();
        Assert.Contains("two", view.LeftText, StringComparison.Ordinal);
        Assert.Contains("four", view.RightText, StringComparison.Ordinal);

        // Opening the same pair again reuses the document.
        Assert.Same(document, await f.Shell.OpenFileDiffAsync(left, right));
    }
}
