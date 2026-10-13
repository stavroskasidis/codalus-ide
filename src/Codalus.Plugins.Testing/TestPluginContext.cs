using System.Text.Json;

namespace Codalus.Plugins.Testing;

/// <summary>
/// An <see cref="IPluginContext"/> for unit tests: no IDE and no plugin host, everything in memory. Activate a plugin
/// with it (or with <see cref="PluginTestHost"/>), then drive it the way the IDE would (run its commands, expand its
/// trees, act in its views, ask its providers) and check what it registered and showed.
/// </summary>
public sealed class TestPluginContext : IPluginContext
{
    /// <summary>The values commands and settings pass through, as the plugin host serializes them.</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>A context for the plugin <paramref name="id"/>, installed in <paramref name="installPath"/> (default: the test's folder).</summary>
    public TestPluginContext(string id = "test.plugin", string version = "1.0.0", string? installPath = null)
    {
        Plugin = new PluginInfo(id, version, installPath ?? AppContext.BaseDirectory);
        Commands = new TestCommands();
        Window = new TestWindow(Commands);
    }

    /// <inheritdoc/>
    public PluginInfo Plugin { get; }

    /// <summary>The commands the plugin registered, and the IDE's commands it can call (<see cref="TestCommands.OnShellCommand"/>).</summary>
    public TestCommands Commands { get; }

    /// <summary>What the plugin showed: notifications, output, status bar items, trees, views, progress, custom editors.</summary>
    public TestWindow Window { get; }

    /// <summary>The settings the plugin reads; <see cref="TestSettings.Set"/> changes one and raises <see cref="ISettings.Changed"/>.</summary>
    public TestSettings Settings { get; } = new();

    /// <summary>What the plugin logged.</summary>
    public TestLog Log { get; } = new();

    /// <inheritdoc/>
    public IList<IDisposable> Subscriptions { get; } = [];

    /// <summary>The plugin's secrets, in memory.</summary>
    public TestSecrets Secrets { get; } = new();

    /// <summary>File system providers the plugin registered.</summary>
    public TestFileSystems FileSystems { get; } = new();

    /// <summary>Authentication providers the plugin registered, and sessions it asks of others (<see cref="TestAuthentication.Sessions"/>).</summary>
    public TestAuthentication Authentication { get; } = new();

    /// <summary>Source control providers the plugin registered, and the state each one shows.</summary>
    public TestSourceControl SourceControl { get; } = new();

    /// <summary>Test controllers, when the test supplies them; otherwise the plugin gets <see cref="NotSupportedException"/>.</summary>
    public ITests? Tests { get; init; }

    /// <summary>The workspace, when the test supplies one.</summary>
    public IWorkspace? Workspace { get; init; }

    /// <summary>The build, when the test supplies one.</summary>
    public IBuild? Build { get; init; }

    /// <summary>The debugger, when the test supplies one.</summary>
    public IDebug? Debug { get; init; }

    /// <summary>Terminals, when the test supplies them.</summary>
    public ITerminals? Terminals { get; init; }

    /// <summary>Git hosting, when the test supplies it.</summary>
    public IHosting? Hosting { get; init; }

    ICommands IPluginContext.Commands => Commands;

    IWindow IPluginContext.Window => Window;

    ISettings IPluginContext.Settings => Settings;

    IPluginLog IPluginContext.Log => Log;

    ISecrets IPluginContext.Secrets => Secrets;

    IFileSystems IPluginContext.FileSystems => FileSystems;

    IAuthentication IPluginContext.Authentication => Authentication;

    ISourceControl IPluginContext.SourceControl => SourceControl;

    ITests IPluginContext.Tests => Tests ?? throw new NotSupportedException("This test context has no test controllers; set Tests.");

    IWorkspace IPluginContext.Workspace => Workspace ?? throw new NotSupportedException("This test context has no workspace; set Workspace.");

    IBuild IPluginContext.Build => Build ?? throw new NotSupportedException("This test context has no build; set Build.");

    IDebug IPluginContext.Debug => Debug ?? throw new NotSupportedException("This test context has no debugger; set Debug.");

    ITerminals IPluginContext.Terminals => Terminals ?? throw new NotSupportedException("This test context has no terminals; set Terminals.");

    IHosting IPluginContext.Hosting => Hosting ?? throw new NotSupportedException("This test context has no hosting; set Hosting.");

    /// <summary>Disposes the subscriptions, last first, as the host does after deactivation.</summary>
    public void DisposeSubscriptions()
    {
        for (var i = Subscriptions.Count - 1; i >= 0; i--)
        {
            Subscriptions[i].Dispose();
        }

        Subscriptions.Clear();
    }
}

/// <summary>Activates a plugin on a <see cref="TestPluginContext"/> and deactivates it when disposed.</summary>
/// <typeparam name="TPlugin">The plugin's class.</typeparam>
public sealed class PluginTestHost<TPlugin> : IAsyncDisposable
    where TPlugin : ICodalusPlugin
{
    internal PluginTestHost(TPlugin plugin, TestPluginContext context)
    {
        Plugin = plugin;
        Context = context;
    }

    /// <summary>The activated plugin.</summary>
    public TPlugin Plugin { get; }

    /// <summary>Its context.</summary>
    public TestPluginContext Context { get; }

    /// <summary>Deactivates the plugin and disposes its subscriptions.</summary>
    public async ValueTask DisposeAsync()
    {
        await Plugin.DeactivateAsync(CancellationToken.None).ConfigureAwait(false);
        Context.DisposeSubscriptions();
    }
}

/// <summary>Starting plugins under test.</summary>
public static class PluginTestHost
{
    /// <summary>
    /// Creates <typeparamref name="TPlugin"/> and activates it on <paramref name="context"/> (a new one by default).
    /// Dispose the result to deactivate it.
    /// </summary>
    public static async Task<PluginTestHost<TPlugin>> ActivateAsync<TPlugin>(TestPluginContext? context = null, CancellationToken cancellationToken = default)
        where TPlugin : ICodalusPlugin, new()
    {
        context ??= new TestPluginContext();
        var plugin = new TPlugin();
        await plugin.ActivateAsync(context, cancellationToken).ConfigureAwait(false);
        return new PluginTestHost<TPlugin>(plugin, context);
    }
}

/// <summary>Removes an entry when disposed.</summary>
internal sealed class Registration(Action remove) : IDisposable
{
    private Action? _remove = remove;

    public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
}
