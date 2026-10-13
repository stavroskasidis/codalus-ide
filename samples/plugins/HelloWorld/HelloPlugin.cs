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

        // A file system (API 1.0): files in memory under the scheme "hello", which the editor opens and saves.
        var files = new MemoryFiles();
        context.Subscriptions.Add(context.Commands.Register("hello.mountFiles", (_, _) =>
        {
            context.Subscriptions.Add(context.FileSystems.RegisterProvider("hello", files));
            return Task.FromResult<object?>(context.FileSystems.RootOf("hello"));
        }));
        context.Subscriptions.Add(context.Commands.Register("hello.readFile", async (invocation, ct) =>
            System.Text.Encoding.UTF8.GetString(await files.ReadFileAsync(invocation.Arg<string>(0)!, ct))));

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

/// <summary>A small writable file system in memory: <c>/readme.md</c> and <c>/notes/todo.txt</c> to start with.</summary>
internal sealed class MemoryFiles : IFileSystemProvider
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal)
    {
        ["/readme.md"] = "# Hello\n\nServed by the Hello World plugin.\n"u8.ToArray(),
        ["/notes/todo.txt"] = "water the plants\n"u8.ToArray(),
    };

    private static string Parent(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : "/";

    private bool IsDirectory(string path) => path == "/" || _files.Keys.Any(f => f.StartsWith(path + "/", StringComparison.Ordinal));

    public Task<FileStat> StatAsync(string path, CancellationToken cancellationToken)
    {
        lock (_files)
        {
            return Task.FromResult(_files.TryGetValue(path, out var content) ? new FileStat(true, false, content.Length, DateTimeOffset.UnixEpoch)
                : IsDirectory(path) ? new FileStat(true, true, 0, DateTimeOffset.UnixEpoch) : FileStat.Missing);
        }
    }

    public Task<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var prefix = path == "/" ? "/" : path + "/";
        lock (_files)
        {
            IReadOnlyList<DirectoryEntry> entries = [.. _files.Keys.Where(f => f.StartsWith(prefix, StringComparison.Ordinal))
                .Select(f => f[prefix.Length..].Split('/')[0]).Distinct()
                .Select(name => _files.TryGetValue(prefix + name, out var content)
                    ? new DirectoryEntry(name, false, content.Length, DateTimeOffset.UnixEpoch)
                    : new DirectoryEntry(name, true, 0, DateTimeOffset.UnixEpoch))];
            return Task.FromResult(entries);
        }
    }

    public Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        lock (_files)
        {
            return _files.TryGetValue(path, out var content) ? Task.FromResult(content) : throw new FileNotFoundException("No such file.", path);
        }
    }

    public Task WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        lock (_files)
        {
            _files[path] = content;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        lock (_files)
        {
            _files.Remove(path);
            foreach (var key in _files.Keys.Where(f => recursive && f.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
            {
                _files.Remove(key);
            }
        }

        return Task.CompletedTask;
    }

    public Task RenameAsync(string source, string target, bool overwrite, CancellationToken cancellationToken)
    {
        lock (_files)
        {
            if (!_files.Remove(source, out var content))
            {
                throw new FileNotFoundException("No such file.", source);
            }

            _files[target] = content;
        }

        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
}
