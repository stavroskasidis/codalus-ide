using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xaml;
using System.Xaml.Schema;
using System.Xml;
using Codalus.Workbench.Previewer;
using XamlReader = System.Windows.Markup.XamlReader;

namespace Codalus.Previewer.Wpf;

/// <summary>
/// One WPF designer host (M40b, ADR 0072): the previewed app's assemblies and its <c>Application</c> (so
/// <c>App.xaml</c>'s resources and merged dictionaries resolve), the edited XAML loaded by WPF's own loader without
/// what only the markup compiler wires up (<see cref="DesignTimeXamlReader"/>), drawn off screen with
/// <see cref="RenderTargetBitmap"/> and sent as frames over Avalonia's remote protocol, the workbench's codec. A
/// window or page can't be a child, so its content moves into a stand-in that keeps its background, resources, names
/// and data context. Design-time data comes from <c>d:DataContext</c> (<see cref="DesignXaml"/>), the size from
/// <c>d:DesignWidth</c> and <c>d:DesignHeight</c>, else the root's own size, else its content's.
/// </summary>
internal sealed class WpfDesigner(string appPath)
{
    public const string AgentPortVariable = "CODALUS_PREVIEW_AGENT_PORT";
    public const string AgentTokenVariable = "CODALUS_PREVIEW_AGENT_TOKEN";

    private const double MaxSize = 4096;
    private const int MaxPixels = 8192;

    private readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _probe = [];
    private readonly object _sendGate = new();
    private readonly Border _frame = new() { Background = SystemColors.WindowBrush, MaxWidth = MaxSize, MaxHeight = MaxSize };
    private Dispatcher _dispatcher = null!;
    private Stream? _stream;
    private Assembly _app = null!;
    private DependencyObject? _root;
    private string? _rootType;
    private double _scale = 1;
    private bool _bgra;
    private long _sequence;
    private byte[]? _lastPixels;
    private int _generation;

    public int Run(string host, int port, string? sessionId)
    {
        _probe.Add(Path.GetDirectoryName(appPath)!);
        AssemblyLoadContext.Default.Resolving += (_, name) => ResolveAssembly(name);
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) => ResolveNative(name);
        _app = Load(appPath);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _dispatcher.UnhandledException += (_, e) =>
        {
            // The user's code failed after the load (a binding, a timer, an animation): the preview goes on.
            Log("The previewed XAML's code failed: " + e.Exception);
            e.Handled = true;
        };

        try
        {
            // Relative pack URIs (pack://application:,,,/Images/x.png) are the app's, not this host's.
            Application.ResourceAssembly = _app;
        }
        catch (InvalidOperationException ex)
        {
            Log(ex.Message);
        }

        CreateApplication();
        using var source = CreateSource();

        using var client = new TcpClient();
        client.Connect(host, port);
        client.NoDelay = true;
        _stream = client.GetStream();
        Send(new StartDesignerSessionMessage(sessionId));
        new Thread(ReadLoop) { IsBackground = true, Name = "Codalus WPF previewer protocol" }.Start();
        StartAgent();
        Dispatcher.Run();
        return 0;
    }

    private static void Log(string line) => Console.Error.WriteLine("[wpf previewer] " + line);

    // ------------------------------------------------------------------ the app

    private Assembly? ResolveAssembly(AssemblyName name)
    {
        foreach (var directory in _probe.ToArray())
        {
            var candidate = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(candidate))
            {
                return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            }
        }

        return null;
    }

    private IntPtr ResolveNative(string name)
    {
        foreach (var directory in _probe.ToArray())
        {
            foreach (var candidate in new[] { Path.Combine(directory, name), Path.Combine(directory, name + ".dll") })
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                {
                    return handle;
                }
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// An assembly of the preview: by name first (the app's copy, which its deps.json and folder have), else from its
    /// path (a library's own output), whose folder is then searched for its references too.
    /// </summary>
    private Assembly Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (_loaded.TryGetValue(full, out var cached))
        {
            return cached;
        }

        Assembly assembly;
        try
        {
            assembly = AssemblyLoadContext.Default.LoadFromAssemblyName(AssemblyName.GetAssemblyName(full));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(full);
        }

        if (Path.GetDirectoryName(full) is { } directory && !_probe.Contains(directory, StringComparer.OrdinalIgnoreCase))
        {
            _probe.Add(directory);
        }

        _loaded[full] = assembly;
        return assembly;
    }

    /// <summary>
    /// The app's own <c>Application</c> (its <c>App</c> class and <c>InitializeComponent</c>, which loads App.xaml's
    /// compiled resources), never run: no <c>Startup</c>, no <c>StartupUri</c>. Without one, or when its constructor
    /// fails, a plain <c>Application</c>.
    /// </summary>
    private void CreateApplication()
    {
        Type[] types;
        try
        {
            types = _app.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.OfType<Type>()];
        }

        var appType = types.FirstOrDefault(t => typeof(Application).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null);
        if (appType is not null)
        {
            try
            {
                var application = (Application)Activator.CreateInstance(appType)!;
                appType.GetMethod("InitializeComponent", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)?.Invoke(application, null);
            }
            catch (Exception ex) when (ex is TargetInvocationException or MissingMethodException or InvalidOperationException)
            {
                Log($"{appType.FullName} failed to start ({(ex.InnerException ?? ex).Message}): previews may miss App.xaml's resources.");
            }
        }

        var current = Application.Current ?? new Application();
        current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    /// <summary>
    /// A hidden window to host the preview's elements, so they're loaded (Loaded events, templates) as in the app. It
    /// is never shown: frames come from <see cref="RenderTargetBitmap"/>.
    /// </summary>
    private HwndSource? CreateSource()
    {
        try
        {
            var parameters = new HwndSourceParameters("Codalus WPF previewer")
            {
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP, without WS_VISIBLE
                ExtendedWindowStyle = 0x08000080, // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                PositionX = -32000,
                PositionY = -32000,
                Width = 1,
                Height = 1,
            };
            return new HwndSource(parameters) { SizeToContent = SizeToContent.WidthAndHeight, RootVisual = _frame };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log("No hidden window (" + ex.Message + "): the preview's elements aren't loaded, only laid out.");
            return null;
        }
    }

    // ------------------------------------------------------------------ the protocol

    private void ReadLoop()
    {
        try
        {
            while (RemoteProtocol.ReadAsync(_stream!, CancellationToken.None).GetAwaiter().GetResult() is { } message)
            {
                _dispatcher.InvokeAsync(() => Handle(message), DispatcherPriority.Normal);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or SocketException)
        {
            Log(ex.Message);
        }

        // The IDE closed the session: so ends this process.
        _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
    }

    private void Send(PreviewMessage message)
    {
        var bytes = RemoteProtocol.Serialize(message);
        try
        {
            lock (_sendGate)
            {
                _stream!.Write(bytes);
                _stream.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            Log(ex.Message);
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }
    }

    private void Handle(PreviewMessage message)
    {
        switch (message)
        {
            case ClientSupportedPixelFormatsMessage formats:
                _bgra = formats.Formats.Contains(PreviewPixelFormat.Bgra8888);
                break;
            case ClientRenderInfoMessage render when render.DpiX > 0:
                _scale = render.DpiX / 96;
                if (_frame.Child is not null)
                {
                    RenderSoon();
                }

                break;
            case UpdateXamlMessage update:
                Update(update);
                break;
        }
    }

    // ------------------------------------------------------------------ loading

    private void Update(UpdateXamlMessage update)
    {
        var design = DesignXaml.Read(update.Xaml);
        var previous = (_frame.Child, _root, _rootType, _frame.Width, _frame.Height);
        try
        {
            var assembly = update.AssemblyPath is { Length: > 0 } path && File.Exists(path) ? Load(path) : _app;
            var (presented, root, width, height) = design.IsApplication
                ? (Message("App.xaml draws nothing: its resources show in the previews of the app's windows and controls."), null, null, null)
                : Present(LoadXaml(update.Xaml, assembly, update.XamlFileProjectPath), design);
            if (root is not null)
            {
                ApplyDesignData(root, design, assembly);
            }

            _frame.Child = null;
            _frame.Width = width ?? double.NaN;
            _frame.Height = height ?? double.NaN;
            _frame.Child = presented;
            _root = root;
            _rootType = design.RootType;
            _frame.Measure(new Size(width ?? double.PositiveInfinity, height ?? double.PositiveInfinity));
            _frame.Arrange(new Rect(_frame.DesiredSize));
            _frame.UpdateLayout();
        }
        catch (Exception ex)
        {
            // The last good preview stays; the error says why this one didn't load.
            _frame.Child = null;
            (_, _root, _rootType, _frame.Width, _frame.Height) = previous;
            _frame.Child = previous.Child;
            SendResult(ex);
            return;
        }

        SendResult(null);
        RenderSoon();
    }

    /// <summary>The XAML through WPF's loader, as the file's assembly sees it (its <c>clr-namespace:</c>s, its pack URIs).</summary>
    private static object LoadXaml(string xaml, Assembly assembly, string? projectPath)
    {
        var name = assembly.GetName().Name;
        var settings = new XamlXmlReaderSettings
        {
            LocalAssembly = assembly,
            ProvideLineInfo = true,
            BaseUri = new Uri("pack://application:,,,/" + name + ";component" + (projectPath is { Length: > 0 } p ? p : "/"), UriKind.Absolute),
        };
        using var xml = XmlReader.Create(new StringReader(xaml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        using var reader = new DesignTimeXamlReader(new XamlXmlReader(xml, XamlReader.GetWpfSchemaContext(), settings));
        return XamlReader.Load(reader);
    }

    /// <summary>What to draw for the loaded root, the element paths start from, and the size (design size first).</summary>
    private static (UIElement Presented, DependencyObject? Root, double? Width, double? Height) Present(object loaded, DesignXaml design)
    {
        switch (loaded)
        {
            case Window window:
            {
                var stand = StandIn(window, window.Content, window.Background);
                var size = (design.DesignWidth ?? Size(window.Width), design.DesignHeight ?? Size(window.Height));
                try
                {
                    window.Close(); // never shown; out of Application.Windows
                }
                catch (InvalidOperationException)
                {
                }

                return (stand, stand, size.Item1, size.Item2);
            }

            case Page page:
            {
                var stand = StandIn(page, page.Content, page.Background);
                return (stand, stand, design.DesignWidth ?? Size(page.Width), design.DesignHeight ?? Size(page.Height));
            }

            case UIElement element:
                return (element, element, design.DesignWidth, design.DesignHeight);
            case ResourceDictionary dictionary:
                var keys = dictionary.Keys.Cast<object>().Select(k => k is Type t ? "{x:Type " + t.Name + "}" : Convert.ToString(k, CultureInfo.InvariantCulture)).Take(40).ToList();
                return (Message($"A resource dictionary: {dictionary.Count} resources ({string.Join(", ", keys)}{(dictionary.Count > keys.Count ? ", …" : string.Empty)}), {dictionary.MergedDictionaries.Count} merged dictionaries. Its resources show in the previews that use them."), null, null, null);
            default:
                throw new InvalidOperationException($"A {loaded.GetType().Name} draws nothing: the previewer shows windows, pages, controls and resource dictionaries.");
        }
    }

    private static double? Size(double value) => double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? null : value;

    /// <summary>
    /// A window's or page's content in a border that keeps what the content inherits from it: the background, the
    /// resources, the name scope (ElementName bindings), the data context and the text properties set on it.
    /// </summary>
    private static Border StandIn(FrameworkElement source, object? content, Brush? background)
    {
        var resources = source.Resources;
        var names = NameScope.GetNameScope(source);
        var dataContext = source.ReadLocalValue(FrameworkElement.DataContextProperty);
        switch (source)
        {
            case Window window:
                window.Content = null;
                break;
            case Page page:
                page.Content = null;
                break;
        }

        var stand = new Border
        {
            Background = background ?? SystemColors.WindowBrush,
            Resources = resources,
            Child = content as UIElement ?? new ContentPresenter { Content = content },
        };
        if (names is not null)
        {
            NameScope.SetNameScope(stand, names);
        }

        if (dataContext != DependencyProperty.UnsetValue && dataContext is not Expression)
        {
            stand.DataContext = dataContext;
        }

        foreach (var property in new[] { TextElement.FontFamilyProperty, TextElement.FontSizeProperty, TextElement.FontWeightProperty, TextElement.FontStyleProperty, TextElement.ForegroundProperty })
        {
            if (source.ReadLocalValue(property) is var value && value != DependencyProperty.UnsetValue && value is not Expression)
            {
                stand.SetValue(property, value);
            }
        }

        return stand;
    }

    private static TextBlock Message(string text) => new()
    {
        Text = text,
        Margin = new Thickness(12),
        MaxWidth = 420,
        TextWrapping = TextWrapping.Wrap,
        Foreground = SystemColors.WindowTextBrush,
    };

    /// <summary><c>d:DataContext="{d:DesignInstance …, IsDesignTimeCreatable=True}"</c>: an instance of the type as the root's data context.</summary>
    private static void ApplyDesignData(DependencyObject root, DesignXaml design, Assembly local)
    {
        if (design.DataContextProblem is { } problem)
        {
            Log(problem);
        }

        if (design.DataContext is not { } instance)
        {
            return;
        }

        if (!instance.IsDesignTimeCreatable)
        {
            Log($"d:DesignInstance {instance.TypeName} isn't IsDesignTimeCreatable=True: the preview doesn't create it, so its bindings show no data.");
            return;
        }

        if (ResolveType(instance, local) is not { } type)
        {
            Log($"d:DesignInstance: the type {instance.TypeName} ({instance.Namespace}) wasn't found.");
            return;
        }

        try
        {
            var value = Activator.CreateInstance(type);
            if (instance.CreateList)
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
                list.Add(value);
                value = list;
            }

            root.SetValue(FrameworkElement.DataContextProperty, value);
        }
        catch (Exception ex) when (ex is TargetInvocationException or MissingMethodException or MemberAccessException or ArgumentException)
        {
            Log($"d:DesignInstance: {type.FullName} couldn't be created ({(ex.InnerException ?? ex).Message}).");
        }
    }

    private static Type? ResolveType(DesignInstance instance, Assembly local)
    {
        try
        {
            if (DesignXaml.ClrNamespace(instance.Namespace) is { } clr)
            {
                var assembly = clr.Assembly is null ? local : Assembly.Load(new AssemblyName(clr.Assembly));
                return assembly.GetType(clr.ClrNamespace.Length > 0 ? clr.ClrNamespace + "." + instance.TypeName : instance.TypeName);
            }

            return XamlReader.GetWpfSchemaContext().GetXamlType(new XamlTypeName(instance.Namespace, instance.TypeName))?.UnderlyingType;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            Log(ex.Message);
            return null;
        }
    }

    private void SendResult(Exception? error)
    {
        if (error is null)
        {
            Send(new UpdateXamlResultMessage(null, null, null));
            return;
        }

        var (line, column) = Position(error);
        var message = Describe(error);
        Send(new UpdateXamlResultMessage(message, null, new PreviewExceptionDetails(error.GetType().Name, message, line, column)));
    }

    /// <summary>The loader's message and what it wraps ("Set property … threw an exception." and why).</summary>
    internal static string Describe(Exception error)
    {
        var parts = new List<string>();
        for (var current = error; current is not null && parts.Count < 3; current = current.InnerException)
        {
            if (current is TargetInvocationException)
            {
                continue;
            }

            if (!parts.Any(p => p.Contains(current.Message, StringComparison.Ordinal)))
            {
                parts.Add(current.Message.Trim());
            }
        }

        return string.Join(" ", parts);
    }

    /// <summary>The 1-based line and column the loader gave (WPF's, System.Xaml's or the XML reader's), or none.</summary>
    internal static (int? Line, int? Column) Position(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case System.Windows.Markup.XamlParseException wpf when wpf.LineNumber > 0:
                    return (wpf.LineNumber, wpf.LinePosition);
                case XamlException xaml when xaml.LineNumber > 0:
                    return (xaml.LineNumber, xaml.LinePosition);
                case XmlException xml when xml.LineNumber > 0:
                    return (xml.LineNumber, xml.LinePosition);
            }
        }

        return (null, null);
    }

    // ------------------------------------------------------------------ frames

    /// <summary>
    /// A frame once the update's bindings and layout have run, and another half a second later if it changed
    /// (content that arrives late: images, data loaded asynchronously).
    /// </summary>
    private void RenderSoon()
    {
        var generation = ++_generation;
        _dispatcher.InvokeAsync(() => Render(force: true), DispatcherPriority.Background);
        _ = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            if (generation == _generation)
            {
                Render(force: false);
            }
        }, _dispatcher); // started by its constructor
    }

    private void Render(bool force)
    {
        try
        {
            _frame.UpdateLayout();
            var width = _frame.ActualWidth > 0 ? _frame.ActualWidth : _frame.DesiredSize.Width;
            var height = _frame.ActualHeight > 0 ? _frame.ActualHeight : _frame.DesiredSize.Height;
            var pixelWidth = Pixels(width);
            var pixelHeight = Pixels(height);
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
            bitmap.Render(_frame);
            var stride = pixelWidth * 4;
            var pixels = new byte[stride * pixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            if (!force && _lastPixels is { } last && last.AsSpan().SequenceEqual(pixels))
            {
                return;
            }

            _lastPixels = pixels;
            var data = pixels;
            if (!_bgra)
            {
                data = (byte[])pixels.Clone();
                for (var i = 0; i + 3 < data.Length; i += 4)
                {
                    (data[i], data[i + 2]) = (data[i + 2], data[i]);
                }
            }

            Send(new FrameMessage(++_sequence, _bgra ? PreviewPixelFormat.Bgra8888 : PreviewPixelFormat.Rgba8888, data, pixelWidth, pixelHeight, stride, 96 * _scale, 96 * _scale));
        }
        catch (Exception ex)
        {
            // A control of the user's that fails to draw: the preview keeps its last frame and the log says why.
            Log("The preview couldn't be drawn: " + ex);
        }
    }

    private int Pixels(double dips) => (int)Math.Clamp(Math.Ceiling(Math.Max(1, dips) * _scale), 1, MaxPixels);

    // ------------------------------------------------------------------ the agent (selection)

    private void StartAgent()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(AgentPortVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port <= 0)
        {
            Log("No agent port: the preview renders without selection.");
            return;
        }

        var token = Environment.GetEnvironmentVariable(AgentTokenVariable) ?? string.Empty;
        Environment.SetEnvironmentVariable(AgentPortVariable, null);
        Environment.SetEnvironmentVariable(AgentTokenVariable, null);
        new Thread(() => AgentLink.Serve(port, token, _ => HandleAgent, "WPF previewer agent")) { IsBackground = true, Name = "Codalus WPF previewer agent" }.Start();
    }

    /// <summary>
    /// The previewer agent's commands (ADR 0048): <c>hit x y</c> → the bounds of the element the XAML declares at the
    /// point and its logical path from the XAML's root; <c>bounds path</c> → an element's bounds, or <c>none</c>.
    /// WPF has no theme variants.
    /// </summary>
    private string HandleAgent(string[] request)
    {
        string Arg(int index) => request.Length > index ? request[index] : string.Empty;
        double Number(int index) => double.Parse(Arg(index), NumberStyles.Float, CultureInfo.InvariantCulture);
        return request[1] switch
        {
            "ping" => "pong",
            "hit" => AgentLink.OnUiThread(_dispatcher, () => Hit(Number(2), Number(3))),
            "bounds" => AgentLink.OnUiThread(_dispatcher, () => Bounds(Arg(2))),
            "theme" => throw new InvalidOperationException("WPF has no theme variants."),
            var other => throw new InvalidOperationException("Unknown command " + other),
        };
    }

    private string Hit(double x, double y)
    {
        var root = _root ?? throw new InvalidOperationException("Nothing is previewed.");
        _frame.UpdateLayout();
        var hit = VisualTreeHelper.HitTest(_frame, new Point(x, y))?.VisualHit;
        if (WpfTree.DeclaredElement(hit, root) is not { } element)
        {
            return string.Empty;
        }

        var bounds = WpfTree.BoundsIn(element, _frame) ?? new Rect(0, 0, 0, 0);
        return WpfTree.Format(bounds) + "\t" + WpfTree.PathOf(element, root, _rootType).Path;
    }

    private string Bounds(string path)
    {
        var root = _root ?? throw new InvalidOperationException("Nothing is previewed.");
        _frame.UpdateLayout();
        return WpfTree.Find(root, path) is { } element && WpfTree.BoundsIn(element, _frame) is { } bounds ? WpfTree.Format(bounds) : "none";
    }
}
