using Codalus.Plugins;

namespace HelloWorld;

/// <summary>
/// The sample plugin. Everything visible before activation (menus, the key binding, the tool window, the
/// settings) comes from codalus-plugin.json; this code only provides behavior.
/// </summary>
public sealed class HelloPlugin : ICodalusPlugin
{
    private int _greetings;

    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var output = context.Window.CreateOutputChannel("Greetings");
        var status = context.Window.CreateStatusBarItem("count", StatusBarAlignment.Right, priority: 10);
        status.Command = "hello.sayHello";
        status.Tooltip = "Greetings so far (click to greet again)";
        status.Text = "Hello: 0";
        status.Show();

        var tree = new GreetingTree(context.Settings);

        context.Subscriptions.Add(context.Commands.Register("hello.sayHello", async (_, ct) =>
        {
            var greeting = context.Settings.GetValue("hello.greeting", "Hello");
            _greetings++;
            status.Text = $"Hello: {_greetings}";
            output.AppendLine($"{greeting}, Codalus! (#{_greetings})");
            var choice = await context.Window.ShowNotificationAsync(NotificationSeverity.Info, $"{greeting}, Codalus!", ["Show Output"], ct);
            if (choice == "Show Output")
            {
                output.Show();
            }

            return _greetings;
        }));

        // Round-trips JSON arguments; used by the integration tests.
        context.Subscriptions.Add(context.Commands.Register("hello.echo", (invocation, _) =>
            Task.FromResult<object?>(new { count = invocation.Args.Count, args = invocation.Args })));

        // Calls back into the shell: executes the command named in the first argument with the remaining ones.
        context.Subscriptions.Add(context.Commands.Register("hello.callShell", async (invocation, ct) =>
            await context.Commands.ExecuteAsync(invocation.Arg<string>(0)!, ct, [.. invocation.Args.Skip(1).Cast<object?>()])));

        context.Subscriptions.Add(context.Commands.Register("hello.refresh", tree.Refresh));

        // A task in the status bar (API 1.0): counts to ten, reporting each step, until done or cancelled.
        context.Subscriptions.Add(context.Commands.Register("hello.count", async (_, ct) =>
            await context.Window.WithProgressAsync<object?>("Counting", async (progress, token) =>
            {
                for (var i = 1; i <= 10; i++)
                {
                    progress.Report(new ProgressReport($"{i} of 10", i * 10));
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(400), token);
                    }
                    catch (OperationCanceledException)
                    {
                        return $"cancelled at {i}";
                    }
                }

                return "done";
            }, cancellable: true, ct)));

        // Simulates a fatal plugin bug, so the shell's host supervision can be exercised.
        context.Subscriptions.Add(context.Commands.Register("hello.crashHost", () => Environment.Exit(42)));

        context.Subscriptions.Add(context.Window.RegisterTreeDataProvider("hello.view", tree));

        context.Settings.Changed += async (_, e) =>
        {
            if (e.Affects("hello.greeting") || e.Affects("hello.times"))
            {
                tree.Refresh();
            }

            if (e.Affects("hello.announce") && context.Settings.GetValue("hello.announce", false))
            {
                await context.Window.ShowNotificationAsync(NotificationSeverity.Info, "Announcements are on.");
            }
        };

        context.Log.Info("Hello World activated.");
        return Task.CompletedTask;
    }

    /// <summary>Two levels: one node per greeting, each with the letters of the greeting.</summary>
    private sealed class GreetingTree(ISettings settings) : ITreeDataProvider<string>
    {
        public event EventHandler<TreeDataChangedEventArgs<string>>? Changed;

        public void Refresh() => Changed?.Invoke(this, new TreeDataChangedEventArgs<string>(null));

        public Task<IReadOnlyList<string>> GetChildrenAsync(string? element, CancellationToken cancellationToken)
        {
            var greeting = settings.GetValue("hello.greeting", "Hello");
            if (element is null)
            {
                var times = settings.GetValue("hello.times", 1);
                return Task.FromResult<IReadOnlyList<string>>([.. Enumerable.Range(1, times).Select(i => $"{greeting} #{i}")]);
            }

            return Task.FromResult<IReadOnlyList<string>>(element.Contains('#') ? [.. greeting.Select((c, i) => $"{i}:{c}")] : []);
        }

        public TreeItem GetTreeItem(string element) => element.Contains('#')
            ? new TreeItem(element) { Icon = "info", Collapsible = TreeItemCollapsibleState.Collapsed, Command = new TreeItemCommand("hello.sayHello") }
            : new TreeItem(element[(element.IndexOf(':') + 1)..]) { Description = "letter " + element[..element.IndexOf(':')] };
    }
}
