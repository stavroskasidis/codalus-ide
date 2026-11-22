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

        // #461: what a folder of control libraries' design assemblies needs of the replacements (eng and the tests run it).
        if (args is ["--check-vendor-designers", .. var check])
        {
            return VendorDesigners.Check(check);
        }

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
        AssemblyLoadContext.Default.Resolving += (context, name) => VendorDesigners.Resolve(context, name) ?? session.ResolveAssembly(context, name);

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
        connection.OnRequest(WinFormsDesignerMethods.Click, json.DesignerClickParams, json.DesignerChangeResult, (p, _) => window.Run(WinFormsDesignerMethods.Click, () => session.Click(p)));
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
        connection.OnRequest(WinFormsDesignerMethods.Status, json.JsonElement, json.DesignerStatus, (_, _) => Task.FromResult(new DesignerStatus(window.Describe()) { DialogOpen = window.DialogOpen, SinceDialog = window.SinceDialog?.TotalSeconds }));
        connection.OnRequest("shutdown", (_, _) => Task.FromResult<JsonElement?>(null));
        connection.OnNotification("exit", json.JsonElement, _ => window.BeginInvoke(Application.ExitThread));
    }
}

/// <summary>Win32 calls the host's window makes for a control library's dialog (#461).</summary>
internal static class NativeMethods
{
    public const int GwlpHwndParent = -8;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    /// <summary><paramref name="window"/>'s rectangle in physical pixels, as a per-monitor-aware thread sees it (this host is unaware of DPI).</summary>
    public static bool GetPhysicalWindowRect(IntPtr window, out WindowRect rect)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        try
        {
            return GetWindowRect(window, out rect);
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                _ = SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    /// <summary>A mouse button is down, anywhere on the screen.</summary>
    public static bool MouseButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;

    /// <summary>The process's visible top-level windows.</summary>
    public static HashSet<IntPtr> VisibleWindows()
    {
        var self = (uint)Environment.ProcessId;
        var windows = new HashSet<IntPtr>();
        EnumWindows((window, parameter) =>
        {
            if (GetWindowThreadProcessId(window, out var process) != 0 && process == self && IsWindowVisible(window))
            {
                windows.Add(window);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }


    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out WindowRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, [System.Runtime.InteropServices.Out] char[] text, int length);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, [System.Runtime.InteropServices.Out] char[] text, int length);

    /// <summary>The process's visible top-level windows, for the log (a dialog that doesn't show where expected).</summary>
    public static void LogThreadWindows()
    {
        var self = (uint)Environment.ProcessId;
        var lines = new List<string>();
        EnumWindows((window, parameter) =>
        {
            if (GetWindowThreadProcessId(window, out var process) != 0 && process == self && IsWindowVisible(window))
            {
                var text = new char[256];
                var type = new char[256];
                var textLength = GetWindowText(window, text, text.Length);
                var typeLength = GetClassName(window, type, type.Length);
                _ = GetWindowRect(window, out var rect);
                lines.Add($"'{new string(text, 0, Math.Max(0, textLength))}' {new string(type, 0, Math.Max(0, typeLength))} at {rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
            }

            return true;
        }, IntPtr.Zero);
        Console.Error.WriteLine("[designer] The host's visible windows: " + string.Join("; ", lines));
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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

    private readonly List<Action> _deferred = [];
    private int _modal;
    private long _dialogClosed;

    /// <summary>
    /// A dialog is open on the UI thread (#461: a control library's designer showed one). Its message loop would run the
    /// requests posted meanwhile inside it, on a surface in the middle of the dialog's change: they wait until it closes.
    /// </summary>
    public bool DialogOpen => Volatile.Read(ref _modal) > 0;

    /// <summary>How long ago the last dialog closed (null: none has), for the shell's wait on the request that opened it.</summary>
    public TimeSpan? SinceDialog => Interlocked.Read(ref _dialogClosed) is var closed and not 0 ? System.Diagnostics.Stopwatch.GetElapsedTime(closed) : null;

    public HostWindow()
    {
        Application.EnterThreadModal += (_, _) =>
        {
            Interlocked.Increment(ref _modal);
            Console.Error.WriteLine("[designer] A modal loop started on the UI thread: requests wait until it ends.");

            // The dialog's window exists once its loop runs: over the IDE, not where the hidden window would put it.
            BeginInvoke(PlaceDialogs);
        };
        Application.LeaveThreadModal += (_, _) => LeaveModal();

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

    private int _vendorModal;
    private readonly HashSet<Form> _placed = [];

    /// <summary>The IDE's main window (an HWND; 0: none), over which a control library's dialogs show (#461).</summary>
    public long DialogOwner { get; set; }

    /// <summary>The top-level dialogs open on this thread (not the form being designed, which is inside this window).</summary>
    private List<Form> Dialogs() =>
        [.. Application.OpenForms.Cast<Form>().Where(f => !ReferenceEquals(f, this) && f.TopLevel && f.Site is null && f.Visible && !f.IsDisposed)];

    private void PlaceDialogs()
    {
        _placed.RemoveWhere(f => f.IsDisposed);
        foreach (var dialog in Dialogs().Where(_placed.Add))
        {
            Place(dialog, DialogOwner);
        }
    }

    /// <summary>A control library's designer started its dialog's modal loop (#461, <c>VendorModal</c>): a dialog is open from now on.</summary>
    public void BeginVendorModal()
    {
        Interlocked.Increment(ref _vendorModal);
        Interlocked.Increment(ref _modal);
    }

    /// <summary>The library's dialog closed (its designer stopped the loop, or no window of it is left).</summary>
    public void EndVendorModal()
    {
        if (Interlocked.Decrement(ref _vendorModal) < 0)
        {
            Interlocked.Increment(ref _vendorModal);
            return;
        }

        LeaveModal();
    }

    private void LeaveModal()
    {
        if (Interlocked.Decrement(ref _modal) == 0)
        {
            Interlocked.Exchange(ref _dialogClosed, System.Diagnostics.Stopwatch.GetTimestamp());
            List<Action> waiting;
            lock (_gate)
            {
                waiting = [.. _deferred];
                _deferred.Clear();
            }

            foreach (var work in waiting)
            {
                BeginInvoke(work);
            }
        }
    }

    /// <summary>
    /// The request that opened a control library's dialog waits here until it closes (#461): the vendor's designer shows
    /// it as an ordinary window and returns, so the host runs the modal loop the classic IDE would, with the dialog over
    /// the IDE (<paramref name="owner"/>, its main window), and the change the request answers with is what the dialog did.
    /// A dialog that closes without its designer saying so ends the wait too.
    /// </summary>
    public void WaitForVendorDialog()
    {
        if (Volatile.Read(ref _vendorModal) == 0)
        {
            return;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var lastSeen = started;
        var seen = false;
        while (Volatile.Read(ref _vendorModal) > 0 && !IsDisposed)
        {
            var dialogs = Dialogs();
            PlaceDialogs();
            seen |= dialogs.Count > 0;

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (dialogs.Count > 0)
            {
                lastSeen = now;
            }
            else if (System.Diagnostics.Stopwatch.GetElapsedTime(lastSeen, now) > TimeSpan.FromSeconds(seen ? 1 : 10))
            {
                if (!seen)
                {
                    NativeMethods.LogThreadWindows();
                }

                Console.Error.WriteLine(seen ? "[designer] The control library's dialog closed without its designer saying so." : "[designer] The control library's designer said it shows a dialog, but none showed.");
                EndVendorModal();
                break;
            }

            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    /// <summary>
    /// The windows a click opened (#461: DevExpress's Change view menu, a <c>DXPopupMenu</c>, which shows at the hidden
    /// grid's place on the screen, pushed onto a monitor's edge by Windows, and returns): moved to where the click was in
    /// the IDE (<paramref name="at"/>: the first one's top-left there, the others with it), and waited for, as a dialog
    /// is (the requests that come meanwhile wait, the shell's request keeps waiting), so the change the click answers
    /// with is what its menu's item did. A mouse press outside them closes a menu, as a click elsewhere would in the app.
    /// </summary>
    public void WaitForPopups(HashSet<IntPtr> before, Point? at)
    {
        List<IntPtr> New() => [.. NativeMethods.VisibleWindows().Where(w => !before.Contains(w) && w != Handle)];
        var opened = New();
        if (opened.Count == 0)
        {
            return;
        }

        Size? by = null;
        foreach (var window in opened)
        {
            _ = NativeMethods.GetWindowRect(window, out var rect);
            if (at is { } target)
            {
                by ??= new Size(target.X - rect.Left, target.Y - rect.Top);
                _ = NativeMethods.SetWindowPos(window, IntPtr.Zero, rect.Left + by.Value.Width, rect.Top + by.Value.Height, 0, 0, NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
            }

            Console.Error.WriteLine($"[designer] A window the click opened is open{(by is { } moved ? $", moved to {rect.Left + moved.Width},{rect.Top + moved.Height}" : string.Empty)}: requests wait until it closes.");
        }

        Interlocked.Increment(ref _modal);
        try
        {
            var pressed = NativeMethods.MouseButtonDown();
            while (!IsDisposed && New() is { Count: > 0 } open)
            {
                // A press outside them (in the IDE): a menu closes, as a click elsewhere closes it in an app. The host
                // doesn't take the foreground for it: a menu closes when the focus moves.
                var down = NativeMethods.MouseButtonDown();
                if (down && !pressed && NativeMethods.GetCursorPos(out var cursor) && !open.Any(w => NativeMethods.GetWindowRect(w, out var r) && Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).Contains(cursor)))
                {
                    foreach (var window in open)
                    {
                        if (FromHandle(window) is ToolStripDropDown menu)
                        {
                            menu.Close(ToolStripDropDownCloseReason.AppClicked);
                        }
                    }
                }

                pressed = down;
                Application.DoEvents();
                Thread.Sleep(15);
            }
        }
        finally
        {
            LeaveModal();
        }
    }

    /// <summary>A library's dialog over the IDE: owned by its window, centered on it, in front.</summary>
    private void Place(Form dialog, long owner)
    {
        Console.Error.WriteLine($"[designer] A dialog is open: {dialog.Text} ({dialog.GetType().FullName}).");
        if (owner == 0)
        {
            dialog.Activate();
            return;
        }

        var ide = new IntPtr(owner);
        _ = NativeMethods.SetWindowLongPtr(dialog.Handle, NativeMethods.GwlpHwndParent, ide);
        if (NativeMethods.GetWindowRect(ide, out var frame))
        {
            var bounds = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
            dialog.Location = new Point(bounds.Left + Math.Max(0, (bounds.Width - dialog.Width) / 2), bounds.Top + Math.Max(0, (bounds.Height - dialog.Height) / 2));
        }

        dialog.Activate();
        _ = NativeMethods.SetForegroundWindow(dialog.Handle);
    }

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
            Action run = null!;
            run = () =>
            {
                if (DialogOpen)
                {
                    lock (_gate)
                    {
                        _deferred.Add(run);
                    }

                    return;
                }

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
            };
            BeginInvoke(run);
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
        if (DialogOpen)
        {
            parts.Add("a dialog is open (a control library's): the requests wait until it closes");
        }
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
