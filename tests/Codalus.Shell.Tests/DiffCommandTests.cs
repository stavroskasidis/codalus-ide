using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary><c>codalus diff left right</c>: parsing, the hand-off to a running shell, and the file comparison.</summary>
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
        var name = SingleInstance.PipeName(Path.Combine(Path.GetTempPath(), "codalus-instance-" + Guid.NewGuid().ToString("N")));

        // No shell running: the request is refused at once (a connect where nothing listens waits for its whole
        // timeout, half a second before every codalus <solution> started an IDE).
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(SingleInstance.MayBeListening(name));
        Assert.False(SingleInstance.TrySend(name, new InstanceRequest("diff", ["a", "b"])));
        Assert.True(started.ElapsedMilliseconds < 250, $"refused after {started.ElapsedMilliseconds} ms");

        // The pipe is looked up by its name, not found among all of the machine's (on Windows each listing took 185 ms on
        // a loaded runner, CI run 41233191844; this machine's 1,600 pipes, 12 ms).
        started.Restart();
        for (var i = 0; i < 50; i++)
        {
            Assert.False(SingleInstance.MayBeListening(name));
        }

        Assert.True(started.ElapsedMilliseconds < 250, $"50 lookups took {started.ElapsedMilliseconds} ms");

        var received = new TaskCompletionSource<InstanceRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = SingleInstance.Listen(name, request =>
        {
            received.TrySetResult(request);
            return Task.FromResult(true);
        });
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
        Assert.True(SingleInstance.MayBeListening(name));
        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("diff", request.Command);
        Assert.Equal(["left.txt", "right.txt"], request.Arguments);
        Assert.NotEqual(name, SingleInstance.PipeName(Path.GetTempPath())); // one pipe per user-data folder
    }

    [Fact]
    public void Merge_and_wait_parse_their_files()
    {
        var merge = ShellOptions.Parse(["merge", "l.cs", "r.cs", "b.cs", "m.cs"]);
        Assert.Equal((Path.GetFullPath("l.cs"), Path.GetFullPath("r.cs"), Path.GetFullPath("b.cs"), Path.GetFullPath("m.cs")), merge.Merge);
        Assert.Single(ShellOptions.Parse(["merge", "l.cs", "r.cs"]).Errors);
        Assert.Equal(Path.GetFullPath("COMMIT_EDITMSG"), ShellOptions.Parse(["--wait", "COMMIT_EDITMSG"]).WaitFile);
    }

    [Fact]
    public async Task A_waiting_request_is_answered_when_the_work_is_done()
    {
        var name = SingleInstance.PipeName(Path.Combine(Path.GetTempPath(), "codalus-instance-" + Guid.NewGuid().ToString("N")));
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = SingleInstance.Listen(name, request => request.Wait ? finish.Task : Task.FromResult(true));
        for (var i = 0; i < 50 && !SingleInstance.TrySend(name, new InstanceRequest("ping", [])); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        // Connecting can time out on a loaded machine (the listener takes the next client between requests): retry, as the
        // ping above does, so what's tested is the waiting.
        var waiting = Task.Run(() =>
        {
            bool? answer = null;
            for (var i = 0; i < 20 && answer is null; i++)
            {
                answer = SingleInstance.Send(name, new InstanceRequest("merge", ["l", "r", "b", "m"], Wait: true));
            }

            return answer;
        }, TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        // Meanwhile other requests are still served.
        Assert.True(SingleInstance.TrySend(name, new InstanceRequest("diff", ["a", "b"])));

        finish.SetResult(false);
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task Merge_tool_and_wait_finish_when_the_merge_is_accepted_or_the_tab_closed()
    {
        await using var f = ShellFixture.Create();
        string Write(string name, string text)
        {
            var path = Path.Combine(f.UserData, name);
            File.WriteAllText(path, text);
            return path;
        }

        var local = Write("LOCAL.txt", "a\nmine\nz\n");
        var remote = Write("REMOTE.txt", "a\ntheirs\nz\n");
        var @base = Write("BASE.txt", "a\nbase\nz\n");
        var merged = Write("merged.txt", "a\n<<<<<<< LOCAL\nmine\n=======\ntheirs\n>>>>>>> REMOTE\nz\n");

        var merging = f.Shell.MergeFilesAsync(local, remote, @base, merged);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<MergeEditorView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<MergeEditorView>().Single();
        view.Accept("current");
        await view.AcceptMergeAsync();
        Assert.True(await merging);
        Assert.Equal("a\nmine\nz\n", File.ReadAllText(merged));

        // Closing the tab without accepting: not resolved.
        File.WriteAllText(merged, "a\n<<<<<<< LOCAL\nmine\n=======\ntheirs\n>>>>>>> REMOTE\nz\n");
        merging = f.Shell.MergeFilesAsync(local, remote, @base, merged);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.OfType<MergeEditorDocument>().Any());
        f.Shell.Layout.CloseDocument(f.Shell.Layout.Documents.OfType<MergeEditorDocument>().Single());
        Assert.False(await merging);

        // --wait: done when the file's tab closes.
        var message = Write("COMMIT_EDITMSG", "subject\n");
        var editing = f.Shell.EditAndWaitAsync(message);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == message);
        Assert.False(editing.IsCompleted);
        f.Shell.CloseActiveDocument();
        Assert.True(await editing);
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
