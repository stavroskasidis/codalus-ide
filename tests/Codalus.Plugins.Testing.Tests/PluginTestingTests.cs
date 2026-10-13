using Codalus.Plugins;
using Codalus.Plugins.Testing;
using HelloWorld;
using TodoBoard;

namespace Codalus.Plugins.Testing.Tests;

/// <summary>The samples tested as a plugin author would test theirs: in memory, no IDE, no plugin host (M22b).</summary>
public sealed class PluginTestingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Commands_notifications_output_and_the_status_bar()
    {
        var context = new TestPluginContext("codalus-samples.hello");
        context.Window.NotificationResponder = n => n.Actions.Contains("Show Output") ? "Show Output" : null;
        await using var host = await PluginTestHost.ActivateAsync<HelloPlugin>(context, Ct);

        Assert.Contains("hello.sayHello", context.Commands.Registered);
        Assert.Equal(1, await context.Commands.ExecuteAsync<int>("hello.sayHello"));
        context.Settings.Set("hello.greeting", "Ahoy");
        Assert.Equal(2, await context.Commands.ExecuteAsync<int>("hello.sayHello"));

        Assert.Equal(["Hello, Codalus!", "Ahoy, Codalus!"], context.Window.Notifications.Select(n => n.Message));
        var output = context.Window.OutputChannels["Greetings"];
        Assert.Equal("Hello, Codalus! (#1)\nAhoy, Codalus! (#2)\n", output.Text);
        Assert.True(output.Shown); // the responder chose "Show Output"
        var status = context.Window.StatusBarItems["count"];
        Assert.True(status.Visible);
        Assert.Equal("Hello: 2", status.Text);
        Assert.Contains(context.Log.Entries, e => e.Message == "Hello World activated.");

        // Arguments and results go through JSON, as over the protocol.
        var echo = await context.Commands.ExecuteAsync("hello.echo", Ct, 1, "two", new { three = 3 });
        Assert.Equal(3, echo!.Value.GetProperty("count").GetInt32());
        Assert.Equal(3, echo.Value.GetProperty("args")[2].GetProperty("three").GetInt32());

        // A call into the IDE's commands, answered by the test.
        context.Commands.OnShellCommand("test.add", args => args[0].GetInt32() + args[1].GetInt32());
        Assert.Equal(42, await context.Commands.ExecuteAsync<int>("hello.callShell", "test.add", 2, 40));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Commands.ExecuteAsync("nobody.has.this", Ct));
    }

    [Fact]
    public async Task Trees_expand_refresh_on_settings_and_run_their_commands()
    {
        var context = new TestPluginContext();
        await using var host = await PluginTestHost.ActivateAsync<HelloPlugin>(context, Ct);

        var roots = await context.Window.GetTreeChildrenAsync("hello.view", cancellationToken: Ct);
        var root = Assert.Single(roots);
        Assert.Equal("Hello #1", root.Item.Label);
        Assert.Equal(TreeItemCollapsibleState.Collapsed, root.Item.Collapsible);
        var letters = await context.Window.GetTreeChildrenAsync("hello.view", root, Ct);
        Assert.Equal(["H", "e", "l", "l", "o"], letters.Select(l => l.Item.Label));

        context.Settings.Set("hello.times", 3);
        Assert.Equal(1, context.Window.TreeRefreshes("hello.view"));
        Assert.Equal(3, (await context.Window.GetTreeChildrenAsync("hello.view", cancellationToken: Ct)).Count);

        await context.Window.ClickTreeItemAsync(root, Ct);
        Assert.Single(context.Window.Notifications);
    }

    [Fact]
    public async Task A_task_with_progress_reports_and_can_be_cancelled()
    {
        var context = new TestPluginContext();
        await using var host = await PluginTestHost.ActivateAsync<HelloPlugin>(context, Ct);

        var run = context.Commands.ExecuteAsync<string>("hello.count");
        await WaitUntil(() => context.Window.Progress.Count == 1 && context.Window.Progress[0].Reports.Count >= 2);
        var task = context.Window.Progress[0];
        Assert.Equal("Counting", task.Title);
        Assert.Equal(new ProgressReport("1 of 10", 10), task.Reports[0]);
        task.Cancel();
        Assert.StartsWith("cancelled at ", await run, StringComparison.Ordinal);
        Assert.True(task.IsDone);
    }

    [Fact]
    public async Task Providers_files_accounts_and_source_control()
    {
        var context = new TestPluginContext();
        await using var host = await PluginTestHost.ActivateAsync<HelloPlugin>(context, Ct);

        // File system: mounted on demand, then read and written through the provider.
        Assert.Empty(context.FileSystems.Providers);
        await context.Commands.ExecuteAsync("hello.mountFiles", Ct);
        var files = context.FileSystems["hello"];
        Assert.Equal(["notes", "readme.md"], (await files.ReadDirectoryAsync("/", Ct)).Select(e => e.Name).Order(StringComparer.Ordinal));
        await files.WriteFileAsync("/new.txt", "hi"u8.ToArray(), Ct);
        Assert.Equal("hi", await context.Commands.ExecuteAsync<string>("hello.readFile", "/new.txt"));
        Assert.False((await files.StatAsync("/missing", Ct)).Exists);

        // Authentication: its own provider signs in once; later requests get the same session.
        Assert.Equal("user 1", await context.Commands.ExecuteAsync<string>("hello.signIn"));
        Assert.Equal("user 1", await context.Commands.ExecuteAsync<string>("hello.signIn"));
        var (label, accounts) = context.Authentication.Providers["hello-accounts"];
        Assert.Equal("Hello Accounts", label);
        Assert.Single(await accounts.GetSessionsAsync(null, Ct));

        // Source control: the provider's state, a commit, an original.
        var folder = Directory.CreateTempSubdirectory("codalus-testing-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
            await context.Commands.ExecuteAsync("hello.startScm", Ct, folder);
            var scm = context.SourceControl.Providers["hello-scm"];
            Assert.Equal("a.txt", Path.GetFileName(Assert.Single(Assert.Single(scm.State!.Groups).Resources).Path));
            await scm.Provider.AcceptAsync("first", Ct);
            Assert.Equal("1 commit(s), last: first", scm.State!.Status);
            Assert.Equal("the original of a.txt\n", await scm.Provider.GetOriginalAsync(Path.Combine(folder, "a.txt"), Ct));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_custom_editor_renders_its_view_and_edits_its_file()
    {
        var context = new TestPluginContext("codalus-samples.todoboard");
        await using var host = await PluginTestHost.ActivateAsync<TodoBoardPlugin>(context, Ct);
        Assert.Equal("Open a *.todos.json file to see it here.", context.Window.GetView("todoboard.summary")!.Children![1].Text);

        var viewId = await context.Window.OpenCustomEditorAsync("todoboard.board", "/work/chores.todos.json",
            """[{ "title": "Feed the cat", "done": false }]""", Ct);
        Assert.Equal("0 of 1 done", context.Window.GetView(viewId)!.Children![1].Text);
        Assert.Null(context.Window.GetCustomEditorText(viewId)); // nothing changed yet

        await context.Window.ChangeAsync(viewId, "new", "Call the vet");
        await context.Window.RaiseViewEventAsync(viewId, new ViewEvent("items", "select", Row: 0), Ct);
        await context.Window.ChangeAsync(viewId, "done", "true");

        Assert.Equal("1 of 2 done", context.Window.GetView(viewId)!.Children![1].Text);
        Assert.Equal(2, context.Window.FindElement(viewId, "items")!.Rows!.Count);
        Assert.Contains("\"Call the vet\"", context.Window.GetCustomEditorText(viewId), StringComparison.Ordinal);
        Assert.Equal(["chores.todos.json", "1/2"], context.Window.FindElement("todoboard.summary", "boards")!.Rows![0]);

        await context.Window.CloseCustomEditorAsync("todoboard.board", viewId, Ct);
        Assert.Null(context.Window.FindElement("todoboard.summary", "boards"));
    }

    [Fact]
    public async Task Deactivation_disposes_what_the_plugin_registered()
    {
        var context = new TestPluginContext();
        var host = await PluginTestHost.ActivateAsync<HelloPlugin>(context, Ct);
        Assert.NotEmpty(context.Commands.Registered);
        await host.DisposeAsync();
        Assert.Empty(context.Commands.Registered);
        Assert.Empty(context.Authentication.Providers);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Window.GetTreeChildrenAsync("hello.view", cancellationToken: Ct));
    }

    [Fact]
    public void Services_the_test_doesnt_supply_say_so()
    {
        IPluginContext context = new TestPluginContext();
        Assert.Contains("set Workspace", Assert.Throws<NotSupportedException>(() => context.Workspace).Message, StringComparison.Ordinal);
        Assert.NotNull(context.Secrets);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, Ct);
        }

        Assert.True(condition(), "Timed out waiting for the condition.");
    }
}
