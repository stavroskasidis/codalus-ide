using System.Runtime.Loader;
using System.Text.Json;
using Codalus.Rpc;
using Codalus.Workbench.WinForms;

namespace Codalus.WinForms.Designer;

/// <summary>
/// The Windows Forms designer's host (M41a, ADR 0073). The IDE starts it as a supervised process on the designed
/// project's app (<c>dotnet exec --runtimeconfig App.runtimeconfig.json --depsfile App.deps.json
/// codalus-winformsdesigner.dll</c>), so it runs on the app's own .NET and Windows Forms, and speaks JSON-RPC on stdio
/// (docs/winforms-designer-protocol.md): a model in, frames and the code's changes out.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The Windows Forms designer runs on Windows only.");
            return 3;
        }

        _ = args;
        HostWindow.MainEntered = System.Diagnostics.Stopwatch.GetTimestamp();
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        // stdout carries the protocol; the user's code writing to Console goes to stderr (the IDE's log).
        Console.SetOut(Console.Error);

        // Design-time pixels are the code's units: no DPI scaling of the designed controls.
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var window = new HostWindow();
        using var session = new DesignerSession(window);
        window.Phase = () => session.Phase;
        AssemblyLoadContext.Default.Resolving += (context, name) => session.ResolveAssembly(context, name);

        var connection = new JsonRpcConnection(stdin, stdout);
        connection.Slow += (_, message) => Console.Error.WriteLine($"[designer] {message} {window.Describe()}");
        Register(connection, window, session);
        connection.Start();
        connection.Disconnected += (_, _) => window.BeginInvoke(Application.ExitThread);
        Application.ThreadException += (_, e) => Console.Error.WriteLine($"The designed code failed: {e.Exception}");
        Application.Run(window.Context);
        connection.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        return 0;
    }

    private static void Register(JsonRpcConnection connection, HostWindow window, DesignerSession session)
    {
        var json = WinFormsDesignerJsonContext.Default;
        connection.OnRequest(WinFormsDesignerMethods.Load, json.DesignerLoadParams, json.DesignerRenderResult, (p, _) => window.Run(WinFormsDesignerMethods.Load, () => session.Load(p)));
        connection.OnRequest(WinFormsDesignerMethods.Render, json.JsonElement, json.DesignerRenderResult, (_, _) => window.Run(WinFormsDesignerMethods.Render, session.Render));
        connection.OnRequest(WinFormsDesignerMethods.Create, json.DesignerCreateParams, json.DesignerChangeResult, (p, _) => window.Run(WinFormsDesignerMethods.Create, () => session.Create(p)));
        connection.OnRequest(WinFormsDesignerMethods.SetProperties, json.DesignerSetPropertiesParams, json.DesignerChangeResult, (p, _) => window.Run(WinFormsDesignerMethods.SetProperties, () => session.SetProperties(p)));
        connection.OnRequest(WinFormsDesignerMethods.SetParent, json.DesignerSetParentParams, json.DesignerChangeResult, (p, _) => window.Run(WinFormsDesignerMethods.SetParent, () => session.SetParent(p)));
        connection.OnRequest(WinFormsDesignerMethods.Delete, json.DesignerComponentsParams, json.DesignerChangeResult, (p, _) => window.Run(WinFormsDesignerMethods.Delete, () => session.Delete(p)));
        connection.OnRequest(WinFormsDesignerMethods.Properties, json.DesignerComponentsParams, json.DesignerPropertiesResult, (p, _) => window.Run(WinFormsDesignerMethods.Properties, () => session.Properties(p)));
        connection.OnRequest(WinFormsDesignerMethods.Events, json.DesignerComponentsParams, json.DesignerEventsResult, (p, _) => window.Run(WinFormsDesignerMethods.Events, () => session.Events(p)));
        connection.OnRequest(WinFormsDesignerMethods.SetChildIndex, json.DesignerChildIndexParams, json.DesignerChangeResult, (p, _) => window.Run(WinFormsDesignerMethods.SetChildIndex, () => session.SetChildIndex(p)));
        connection.OnRequest(WinFormsDesignerMethods.Collection, json.DesignerCollectionParams, json.DesignerCollectionResult, (p, _) => window.Run(WinFormsDesignerMethods.Collection, () => session.Collection(p)));
        connection.OnRequest(WinFormsDesignerMethods.SetCollection, json.DesignerSetCollectionParams, json.DesignerChangeResult, (p, _) => window.Run(WinFormsDesignerMethods.SetCollection, () => session.SetCollection(p)));
        connection.OnRequest(WinFormsDesignerMethods.Actions, json.DesignerComponentsParams, json.DesignerActionsResult, (p, _) => window.Run(WinFormsDesignerMethods.Actions, () => session.Actions(p)));
        connection.OnRequest(WinFormsDesignerMethods.InvokeAction, json.DesignerInvokeActionParams, json.DesignerChangeResult, (p, _) => window.Run(WinFormsDesignerMethods.InvokeAction, () => session.InvokeAction(p)));
        connection.OnRequest(WinFormsDesignerMethods.Select, async (p, _) =>
        {
            var selection = p.Deserialize(json.DesignerComponentsParams) ?? new DesignerComponentsParams([]);
            await window.Run(WinFormsDesignerMethods.Select, () =>
            {
                session.Select(selection);
                return true;
            }).ConfigureAwait(false);
            return null;
        });
        connection.OnRequest(WinFormsDesignerMethods.Ready, json.JsonElement, json.DesignerStatus, (_, _) => window.Run(WinFormsDesignerMethods.Ready, () =>
        {
            var status = new DesignerStatus(window.Describe());
            Console.Error.WriteLine($"[designer] Ready: {status.State}");
            return status;
        }));

        // Off the UI thread: what it's doing when a request takes too long.
        connection.OnRequest(WinFormsDesignerMethods.Status, json.JsonElement, json.DesignerStatus, (_, _) => Task.FromResult(new DesignerStatus(window.Describe())));
        connection.OnRequest("shutdown", (_, _) => Task.FromResult<JsonElement?>(null));
        connection.OnNotification("exit", json.JsonElement, _ => window.BeginInvoke(Application.ExitThread));
    }
}

/// <summary>
/// The host's hidden window: the design surface's view lives in it, off screen (shown, so its controls have handles and
/// lay out as in an app), and it marshals the protocol's requests to the UI thread.
/// </summary>
internal sealed class HostWindow : Form
{
    private readonly object _gate = new();
    private readonly Dictionary<long, (string What, long Queued)> _queued = new();
    private long _nextWork;
    private (string What, long Started)? _current;
    private (string What, TimeSpan Took)? _last;
    private long _shown;

    /// <summary>When Main started (the time before it is the runtime's start).</summary>
    public static long MainEntered { get; set; }

    /// <summary>Where the UI thread's work is (the session's phase), for <see cref="Describe"/>.</summary>
    public Func<string?> Phase { get; set; } = () => null;

    public HostWindow()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(2000, 1500);
        Text = "Codalus Windows Forms designer";
        Context = new ApplicationContext(this);
        CreateHandle();
    }

    public ApplicationContext Context { get; }

    protected override bool ShowWithoutActivation => true;

    protected override void OnLoad(EventArgs e)
    {
        Interlocked.CompareExchange(ref _shown, System.Diagnostics.Stopwatch.GetTimestamp(), 0);
        base.OnLoad(e);
    }

    /// <summary>Runs <paramref name="work"/> (a request, by its method) on the UI thread; <see cref="Describe"/> says what waits and what runs.</summary>
    public Task<T> Run<T>(string what, Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        long id;
        lock (_gate)
        {
            id = ++_nextWork;
            _queued[id] = (what, System.Diagnostics.Stopwatch.GetTimestamp());
        }

        try
        {
            BeginInvoke(() =>
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_gate)
                {
                    _queued.Remove(id);
                    _current = (what, started);
                }

                try
                {
                    done.SetResult(work());
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
                finally
                {
                    var took = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                    lock (_gate)
                    {
                        _current = null;
                        _last = (what, took);
                    }

                    if (took > TimeSpan.FromSeconds(10))
                    {
                        Console.Error.WriteLine($"[designer] {what} took {took.TotalSeconds:0.0} s on the UI thread.");
                    }
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window is gone (the host is exiting): the request fails rather than waiting forever.
            lock (_gate)
            {
                _queued.Remove(id);
            }

            done.TrySetException(ex);
        }

        return done.Task;
    }

    /// <summary>
    /// The host's state in words, safe off the UI thread: its uptime and when the runtime reached Main, whether the
    /// window's message loop runs, the UI thread's work (and the session's phase in it), the requests waiting for it, and
    /// the last one done.
    /// </summary>
    public string Describe()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        string Seconds(long since) => $"{System.Diagnostics.Stopwatch.GetElapsedTime(since, now).TotalSeconds:0.0} s";
        var parts = new List<string>();
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var uptime = DateTime.Now - self.StartTime;
            var main = uptime - System.Diagnostics.Stopwatch.GetElapsedTime(MainEntered, now);
            parts.Add($"up {uptime.TotalSeconds:0.0} s (Main after {main.TotalSeconds:0.0} s)");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            parts.Add($"in Main for {Seconds(MainEntered)}");
        }

        var shown = Interlocked.Read(ref _shown);
        parts.Add(shown == 0 ? "its window's message loop hasn't started" : $"message loop running for {Seconds(shown)}");
        lock (_gate)
        {
            parts.Add(_current is var (what, started)
                ? $"UI thread: {what} for {Seconds(started)}" + (Phase() is { Length: > 0 } phase ? $" ({phase})" : string.Empty)
                : "UI thread: idle");
            if (_queued.Count > 0)
            {
                var oldest = _queued.MinBy(q => q.Key).Value;
                parts.Add($"{_queued.Count} request(s) wait for the UI thread, the oldest ({oldest.What}) for {Seconds(oldest.Queued)}");
            }

            if (_last is var (lastWhat, took))
            {
                parts.Add($"last done: {lastWhat} in {took.TotalMilliseconds:0} ms");
            }
        }

        return string.Join("; ", parts) + ".";
    }
}
