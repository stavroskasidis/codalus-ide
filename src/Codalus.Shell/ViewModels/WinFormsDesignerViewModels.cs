using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Docking;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Previewer;
using Codalus.Workbench.WinForms;

namespace Codalus.Shell.ViewModels;

/// <summary>A rectangle of the designer's frame (pixels of the form at design time).</summary>
public readonly record struct DesignerRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && y >= Y && x < Right && y < Bottom;

    public bool Intersects(DesignerRect other) => other.X < Right && X < other.Right && other.Y < Bottom && Y < other.Bottom;

    public static DesignerRect Between(double x1, double y1, double x2, double y2) => new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
}

/// <summary>
/// A component of the tray under the surface (M41b): a non-visual component (a timer, a tool tip, a binding source), a
/// context menu, a menu or tool strip; selected with the surface's selection, renamed, deleted, arranged.
/// </summary>
public sealed class DesignerTrayItem(string name, string type, string icon, bool isSelected, ICommand select) : ObservableObject
{
    public string Name { get; } = name;

    public string Type { get; } = type;

    public string ShortType => Type[(Type.LastIndexOf('.') + 1)..];

    public string Icon { get; } = icon;

    public bool IsSelected { get; } = isSelected;

    public ICommand Select { get; } = select;
}

/// <summary>Components copied in the designer (their statements from the code), for Paste: their properties, and their extender properties, data bindings, collections and event hookups.</summary>
internal sealed record DesignerClipboardEntry(string Name, string Type, DesignerValue[] CreationArguments, string? Parent, string? ParentPath, int[]? Cell, DesignerPropertyValue[] Properties, bool SupportsInitialize)
{
    public DesignerStatement[] Calls { get; init; } = [];

    public DesignerStatement[] Events { get; init; } = [];

    /// <summary>A local the copied collections hold (a list view's item): its creation.</summary>
    public DesignerValue? LocalValue { get; init; }

    /// <summary>The designer file it was copied from: hookups are kept only when pasting into the same form (ADR 0073).</summary>
    public string Source { get; init; } = string.Empty;
}

/// <summary>
/// A form or user control in the Windows Forms designer (M41a, ADR 0073): the document over its code file. The C#
/// host reads InitializeComponent into a model; a designer host (Windows only, D31) draws it and applies the gestures;
/// each change comes back as code, which the C# host writes into the designer file as minimal text edits, so the
/// designer file's own undo stack is the designer's (undo and redo reload the form from the code). The code is the
/// only state: when it changes otherwise (typed in the code view, undone), the form is loaded again from it.
/// </summary>
public sealed partial class WinFormsDesignerDocument : ShellDocument, Views.ISavableDocument, IDesignerPropertySource, IDisposable
{
    public const string DocumentKind = "winformsDesigner";

    private static List<DesignerClipboardEntry> s_clipboard = [];

    private readonly ShellHost _shell;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<CodeDocument> _resourceModels = [];
    private readonly Dictionary<CodeDocument, int> _expectedResources = [];
    private readonly List<(CodeDocument Model, int Version)[]> _undo = [];
    private readonly List<(CodeDocument Model, int Version)[]> _redo = [];
    private CodeDocument? _designerCode;
    private IWinFormsDesignerHost? _host;
    private WinFormsDesignerTarget? _target;
    private WinFormsReadResult? _read;
    private DesignerModel? _model;
    private int _expectedVersion = -1;
    private CancellationTokenSource? _reload;
    private Bitmap? _frame;
    private double _frameWidth;
    private double _frameHeight;
    private DesignerComponentView[] _components = [];
    private List<string> _selection = [];
    private string _status;
    private string? _problems;
    private bool _isBusy;
    private bool _disposed;

    public WinFormsDesignerDocument(ShellHost shell, string codePath)
    {
        _shell = shell;
        CodePath = codePath;
        DesignerPath = codePath[..^".cs".Length] + ".Designer.cs";
        Id = IdFor(codePath);
        Title = Localizer.Format("msg.winFormsDesigner.title", "{0} [Design]", System.IO.Path.GetFileName(codePath));
        _status = Localizer.Format("msg.winFormsDesigner.opening", "Opening {0} in the designer…", System.IO.Path.GetFileName(codePath));
        ViewCodeCommand = new RelayCommand(() => _ = _shell.ViewWinFormsCodeAsync(this));
        RefreshCommand = new RelayCommand(() => _ = ReloadAsync(restartHost: true));
    }

    public override string Kind => DocumentKind;

    public override string? FilePath => CodePath;

    public override string? TabIcon => Controls.FileIcons.Form;

    public static string IdFor(string codePath) => "winforms:" + codePath;

    public string CodePath { get; }

    public string DesignerPath { get; }

    public ICommand ViewCodeCommand { get; }

    public ICommand RefreshCommand { get; }

    /// <summary>The shell's Toolbox (its armed item is what a click or a drag on the surface creates).</summary>
    public ToolboxViewModel Toolbox => _shell.Toolbox;

    /// <summary>The model read from the code (null until it's read).</summary>
    public DesignerModel? Model => _model;

    /// <summary>The designer host (tests).</summary>
    public IWinFormsDesignerHost? Host => _host;

    /// <summary>The designer file's text model, held while the designer is open.</summary>
    public CodeDocument? DesignerCode => _designerCode;

    public Bitmap? Frame
    {
        get => _frame;
        private set => SetProperty(ref _frame, value);
    }

    public double FrameWidth
    {
        get => _frameWidth;
        private set => SetProperty(ref _frameWidth, value);
    }

    public double FrameHeight
    {
        get => _frameHeight;
        private set => SetProperty(ref _frameHeight, value);
    }

    /// <summary>How many frames have arrived (tests).</summary>
    public int FrameCount { get; private set; }

    /// <summary>The surface's components: the root first, then controls (front first, parents before children), then non-visual components.</summary>
    public IReadOnlyList<DesignerComponentView> Components => _components;

    public ObservableCollection<DesignerTrayItem> Tray { get; } = [];

    public bool HasTray => Tray.Count > 0;

    /// <summary>The selected components' names (the first is the primary selection, which gets the grab handles).</summary>
    public IReadOnlyList<string> Selection => _selection;

    public string? PrimarySelection => _selection.Count > 0 ? _selection[0] : null;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>What the designer host couldn't apply (statements, the drawing), or null.</summary>
    public string? Problems
    {
        get => _problems;
        private set
        {
            if (SetProperty(ref _problems, value))
            {
                OnPropertyChanged(nameof(HasProblems));
            }
        }
    }

    public bool HasProblems => _problems is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>The surface changed (a frame, the components, the selection): the view draws again.</summary>
    public event EventHandler? SurfaceChanged;

    public event EventHandler? PropertiesChanged;

    protected override bool NeedsSaveOnClose => IsModified && !_shell.HasEditorFor(DesignerPath);

    // ------------------------------------------------------------------ loading

    /// <summary>Opens the designer file's text, reads the model through the C# host, starts the designer host and loads the form.</summary>
    public async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            _designerCode = await _shell.AcquireModelAsync(DesignerPath).ConfigureAwait(true);
            _designerCode.ContentChanged += OnDesignerCodeChanged;
            _designerCode.PropertyChanged += OnDesignerCodePropertyChanged;
            await HoldResourcesAsync().ConfigureAwait(true);
            IsModified = _designerCode.IsModified || _resourceModels.Any(m => m.IsModified);
            if (!await ReadAsync(closeOnProblem: true).ConfigureAwait(true))
            {
                return;
            }

            await StartHostAsync().ConfigureAwait(true);
        }
        catch (PreviewerException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = Localizer.Format("msg.winFormsDesigner.failed", "The designer failed: {0}", ex.Message);
            _shell.WinFormsDesignerLog.AppendLine(ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reads the model from the code. Code it can't read: the first time, the file opens as code with the reason (as Visual Studio does); later, the designer says so.</summary>
    private async Task<bool> ReadAsync(bool closeOnProblem)
    {
        Status = Localizer.Format("msg.winFormsDesigner.reading", "Reading {0}…", System.IO.Path.GetFileName(DesignerPath));
        var client = await _shell.Languages.ClientAsync(ShellHost.CSharpLanguageId).ConfigureAwait(true)
            ?? throw new PreviewerException(Localizer.Get("msg.winFormsDesigner.noCSharp", "The C# language service isn't available: the designer reads the form through it."));
        var version = _designerCode!.Version;
        var read = await client.WinFormsReadAsync(new WinFormsReadParams(new TextDocumentIdentifier(Codalus.LanguageServices.Protocol.DocumentUris.FromPath(CodePath))) { ResourceFiles = HeldResourceFiles() }, CancellationToken.None).ConfigureAwait(true)
            ?? throw new PreviewerException(Localizer.Get("msg.winFormsDesigner.noAnswer", "The C# language service didn't read the form."));
        _read = read;
        if (read.Model is not { } model)
        {
            var problem = read.Problem ?? Localizer.Get("msg.winFormsDesigner.cantRead", "The designer can't read this form.");
            if (closeOnProblem)
            {
                await _shell.OpenWinFormsAsCodeAsync(this, problem, read.ProblemLine is { } line ? (DesignerPath, line) : null).ConfigureAwait(true);
                return false;
            }

            Status = problem;
            return false;
        }

        _model = model;
        _expectedVersion = version;
        if (!read.IsLocalizable)
        {
            // Only a localizable form has languages.
            _culture = string.Empty;
        }

        UpdateTray();
        return true;
    }

    private async Task StartHostAsync()
    {
        _target = await _shell.WinFormsDesigner.ResolveAsync(CodePath, CancellationToken.None).ConfigureAwait(true);
        var host = await _shell.WinFormsDesigner.StartAsync(_target, message => Ui.Post(() => Status = message), CancellationToken.None).ConfigureAwait(true);
        if (_disposed)
        {
            await host.DisposeAsync().ConfigureAwait(true);
            return;
        }

        _host = host;
        host.Log += (_, line) => _shell.WinFormsDesignerLog.AppendLine(line);
        host.Restarted += (_, _) => Ui.Post(() => _ = ReloadAsync(restartHost: false));
        await LoadFormAsync().ConfigureAwait(true);
        _ = LoadToolboxAsync();
    }

    private async Task LoadFormAsync()
    {
        if (_host is null || _target is null || _model is null)
        {
            return;
        }

        Status = Localizer.Format("msg.winFormsDesigner.loading", "Loading {0}…", _model.ClassName);
        await HoldResourcesAsync().ConfigureAwait(true);
        var render = await _host.LoadAsync(WinFormsDesignerService.LoadParams(_target, _model, _read?.ClassFullName) with
        {
            Resources = [.. _resourceModels.Select(m => new DesignerResourceText(CultureOf(m.Path), m.Text.Text))],
            Culture = _culture.Length == 0 ? null : _culture,
            Localizable = _read?.IsLocalizable ?? false,
        }, CancellationToken.None).ConfigureAwait(true);
        Show(render);
        SetSelection([.. _selection.Where(s => _components.Any(c => c.Name == s))], notify: true);
        Status = Localizer.Format("msg.winFormsDesigner.ready", "{0}: drag from the Toolbox, select, move and resize; F7 shows the code.", _model.ClassName);
    }

    private Task _toolboxLoad = Task.CompletedTask;

    /// <summary>The designer's newest request for the Toolbox's project items (tests).</summary>
    internal Task ToolboxLoad => _toolboxLoad;

    /// <summary>
    /// Asks the C# host for the project's own controls and components, for the Toolbox: when the designer has started,
    /// when it becomes the active document, and when the C# service has loaded the projects again (the shell asks the
    /// active designer). The host finds them in the project's compilation, which before the restore can lack the
    /// framework they derive from (off Windows, Windows Forms' reference pack comes with the restore), so the first
    /// answer may miss them. Only the Toolbox's newest request is shown: a slower earlier answer doesn't replace it.
    /// </summary>
    internal Task LoadToolboxAsync() => _toolboxLoad = LoadToolboxCoreAsync();

    private async Task LoadToolboxCoreAsync()
    {
        if (_disposed || _read?.Project is not { } project || _shell.Languages.RunningClient(ShellHost.CSharpLanguageId) is not { } client)
        {
            return;
        }

        var request = _shell.Toolbox.BeginProjectItemsRequest();
        try
        {
            if (await client.WinFormsToolboxAsync(project, CancellationToken.None).ConfigureAwait(true) is { } toolbox && !_disposed)
            {
                _shell.Toolbox.SetProjectItems(request, toolbox.Items);
            }
        }
        catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException)
        {
            _shell.WinFormsDesignerLog.AppendLine(ex.Message);
        }
    }

    /// <summary>The code changed (typed, undone, another tool): read it again and load the form from it.</summary>
    public async Task ReloadAsync(bool restartHost)
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_disposed || _designerCode is null)
            {
                return;
            }

            if (restartHost && _host is not null)
            {
                await _host.DisposeAsync().ConfigureAwait(true);
                _host = null;
            }

            if (!await ReadAsync(closeOnProblem: false).ConfigureAwait(true))
            {
                return;
            }

            if (_host is null)
            {
                await StartHostAsync().ConfigureAwait(true);
            }
            else
            {
                await LoadFormAsync().ConfigureAwait(true);
            }
        }
        catch (PreviewerException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or IOException or InvalidOperationException)
        {
            Status = Localizer.Format("msg.winFormsDesigner.failed", "The designer failed: {0}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnDesignerCodeChanged(object? sender, CodeDocumentChangedEventArgs e)
    {
        if (e.Version == _expectedVersion)
        {
            return;
        }

        ScheduleReload();
    }

    /// <summary>The code changed otherwise: the form loads again from it (a moment later, after more typing).</summary>
    private void ScheduleReload()
    {
        _reload?.Cancel();
        var reload = _reload = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, reload.Token).ConfigureAwait(false);
                await Ui.RunAsync(() => ReloadAsync(restartHost: false)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void OnDesignerCodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeDocument.IsModified))
        {
            IsModified = (_designerCode?.IsModified ?? false) || _resourceModels.Any(m => m.IsModified);
        }
    }

    private void Show(DesignerRenderResult render)
    {
        if (render.Frame is { } frame)
        {
            using var stream = new MemoryStream(Convert.FromBase64String(frame.Png));
            var old = Frame;
            Frame = new Bitmap(stream);
            old?.Dispose();
            FrameWidth = frame.Width;
            FrameHeight = frame.Height;
            FrameCount++;
        }

        _components = render.Components;
        Problems = render.Errors.Length == 0 ? null : string.Join("\n", render.Errors);
        UpdateTray();
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateTray()
    {
        Tray.Clear();
        var order = _read?.TrayOrder ?? [];
        var tray = _components.Where(c => c.InTray || (!c.IsControl && !c.IsItem))
            .OrderBy(c => Array.IndexOf(order, c.Name) is var at and >= 0 ? at : int.MaxValue).ToList();
        foreach (var component in tray)
        {
            var name = component.Name;
            Tray.Add(new DesignerTrayItem(name, component.Type, TrayIcon(component.Type), _selection.Contains(name), new RelayCommand(() => SetSelection([name], notify: true))));
        }

        OnPropertyChanged(nameof(HasTray));
    }

    // ------------------------------------------------------------------ the surface

    public DesignerComponentView? Find(string name) => _components.FirstOrDefault(c => c.Name == name);

    public DesignerComponentView? Root => _components.Length > 0 ? _components[0] : null;

    /// <summary>The children of a control on the surface (front first).</summary>
    public IEnumerable<DesignerComponentView> ChildrenOf(string name) => _components.Where(c => c.IsControl && c.Parent == name);

    /// <summary>The deepest control at a point of the frame (the root when nothing else), skipping <paramref name="except"/> and what's inside them.</summary>
    public DesignerComponentView? HitTest(double x, double y, IReadOnlySet<string>? except = null, bool containersOnly = false)
    {
        if (Root is not { } current || !Bounds(current).Contains(x, y))
        {
            return null;
        }

        while (true)
        {
            var next = ChildrenOf(current.Name).FirstOrDefault(c => c.Visible && Bounds(c).Contains(x, y) && (except is null || !except.Contains(c.Name)) && (!containersOnly || c.IsContainer || HasContainerChildren(c)));
            if (next is null)
            {
                return containersOnly && !current.IsContainer ? null : current;
            }

            current = next;
        }
    }

    private bool HasContainerChildren(DesignerComponentView view) => ChildrenOf(view.Name).Any(c => c.IsContainer);

    public static DesignerRect Bounds(DesignerComponentView view) => new(view.X, view.Y, view.Width, view.Height);

    /// <summary>The component a click selects: a SplitContainer's panel selects itself; a part that can't be selected selects its owner.</summary>
    public void SetSelection(IReadOnlyList<string> names, bool notify)
    {
        var distinct = names.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.SequenceEqual(_selection))
        {
            return;
        }

        _selection = distinct;
        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(PrimarySelection));
        UpdateTray();
        UpdateMenus();
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
        if (notify)
        {
            _shell.PropertySource = this;
            PropertiesChanged?.Invoke(this, EventArgs.Empty);
            if (_host is { } host)
            {
                _ = SelectInHostAsync(host, distinct);
            }
        }
    }

    private static async Task SelectInHostAsync(IWinFormsDesignerHost host, IReadOnlyList<string> names)
    {
        try
        {
            await host.SelectAsync(new DesignerComponentsParams([.. names]), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PreviewerException or Rpc.JsonRpcConnectionLostException or Rpc.JsonRpcRemoteException)
        {
        }
    }

    /// <summary>A click: the component under it alone, or with Ctrl or Shift added to (or taken from) the selection.</summary>
    public void Click(double x, double y, bool toggle)
    {
        var hit = HitTest(x, y);
        var name = hit?.Name ?? Root?.Name;
        if (name is null)
        {
            return;
        }

        if (toggle && hit is not null && hit != Root)
        {
            SetSelection(_selection.Contains(name) ? [.. _selection.Where(s => s != name)] : [.. _selection.Where(s => s != Root?.Name), name], notify: true);
        }
        else if (!_selection.Contains(name))
        {
            SetSelection([name], notify: true);
        }
        else if (_selection[0] != name)
        {
            SetSelection([name, .. _selection.Where(s => s != name)], notify: true);
        }
    }

    /// <summary>A rubber band: the controls inside the container it started in that it touches.</summary>
    public void SelectInRectangle(DesignerRect band, string container, bool add)
    {
        var picked = ChildrenOf(container).Where(c => Bounds(c).Intersects(band)).Select(c => c.Name).ToList();
        SetSelection(add ? [.. _selection.Where(s => s != Root?.Name), .. picked] : picked.Count == 0 ? [container] : picked, notify: true);
    }

    /// <summary>The selected controls that can move (not the root, not parts).</summary>
    private List<DesignerComponentView> Movable() =>
        [.. _selection.Select(Find).OfType<DesignerComponentView>().Where(c => c.IsControl && c != Root && !c.IsPart)];

    private static string Invariant(double value) => Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private string ComponentOf(string name) => name == _model?.ClassName ? string.Empty : name;

    /// <summary>Moves the selected controls by a delta, into another container when they're dropped in one.</summary>
    public Task MoveSelectionAsync(double dx, double dy, double dropX, double dropY)
    {
        var moving = Movable();
        if (moving.Count == 0 || (Math.Abs(dx) < 1 && Math.Abs(dy) < 1))
        {
            return Task.CompletedTask;
        }

        var except = new HashSet<string>(moving.Select(m => m.Name), StringComparer.Ordinal);
        var target = HitTest(dropX, dropY, except, containersOnly: true);
        if (target is not null && moving.Count == 1 && target.Name != moving[0].Parent && target.IsContainer)
        {
            var control = moving[0];
            return RunAsync(host => host.SetParentAsync(new DesignerSetParentParams(control.Name, target == Root ? null : target.Name, control.X + dx, control.Y + dy), CancellationToken.None));
        }

        return RunAsync(host => host.SetPropertiesAsync(new DesignerSetPropertiesParams([.. moving.Select(m =>
        {
            var parent = m.Parent is { } p ? Find(p) : null;
            var x = m.X + dx - (parent?.ClientX ?? 0);
            var y = m.Y + dy - (parent?.ClientY ?? 0);
            return new DesignerPropertyEdit(m.Name, "Location") { Text = $"{Invariant(x)}, {Invariant(y)}" };
        })]), CancellationToken.None));
    }

    /// <summary>Resizes the primary selection to <paramref name="bounds"/> (frame coordinates), from a grab handle.</summary>
    public Task ResizeAsync(string name, DesignerRect bounds)
    {
        if (Find(name) is not { } view)
        {
            return Task.CompletedTask;
        }

        var edits = new List<DesignerPropertyEdit>();
        var width = Math.Max(1, bounds.Width);
        var height = Math.Max(1, bounds.Height);
        if (view == Root)
        {
            // The root's size (a form writes its client size from it).
            edits.Add(new DesignerPropertyEdit(ComponentOf(name), "Size") { Text = $"{Invariant(width)}, {Invariant(height)}" });
        }
        else
        {
            var parent = view.Parent is { } p ? Find(p) : null;
            if (Math.Abs(bounds.X - view.X) >= 1 || Math.Abs(bounds.Y - view.Y) >= 1)
            {
                edits.Add(new DesignerPropertyEdit(name, "Location") { Text = $"{Invariant(bounds.X - (parent?.ClientX ?? 0))}, {Invariant(bounds.Y - (parent?.ClientY ?? 0))}" });
            }

            edits.Add(new DesignerPropertyEdit(name, "Size") { Text = $"{Invariant(width)}, {Invariant(height)}" });
        }

        return RunAsync(host => host.SetPropertiesAsync(new DesignerSetPropertiesParams([.. edits]), CancellationToken.None));
    }

    /// <summary>The arrow keys: moves the selection a pixel, or with Shift resizes it.</summary>
    public Task NudgeAsync(int dx, int dy, bool resize)
    {
        var moving = Movable();
        if (moving.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunAsync(host => host.SetPropertiesAsync(new DesignerSetPropertiesParams([.. moving.Select(m =>
        {
            if (resize)
            {
                return new DesignerPropertyEdit(m.Name, "Size") { Text = $"{Invariant(Math.Max(1, m.Width + dx))}, {Invariant(Math.Max(1, m.Height + dy))}" };
            }

            var parent = m.Parent is { } p ? Find(p) : null;
            return new DesignerPropertyEdit(m.Name, "Location") { Text = $"{Invariant(m.X + dx - (parent?.ClientX ?? 0))}, {Invariant(m.Y + dy - (parent?.ClientY ?? 0))}" };
        })]), CancellationToken.None));
    }

    /// <summary>Delete: the selected components (and the controls they contain).</summary>
    public Task DeleteSelectionAsync()
    {
        var names = _selection.Where(s => s != Root?.Name && Find(s) is { IsPart: false }).ToList();
        if (names.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunAsync(host => host.DeleteAsync(new DesignerComponentsParams([.. names]), CancellationToken.None), after: () => SetSelection(Root is { } root ? [root.Name] : [], notify: true));
    }

    /// <summary>Creates a Toolbox item at a point of the frame (drawn with a size, or at its default size) and selects it.</summary>
    public async Task CreateAsync(string type, double x, double y, double? width = null, double? height = null)
    {
        if (_model is null)
        {
            return;
        }

        var name = NewName(type);
        var container = HitTest(x, y, containersOnly: true);

        await RunAsync(host => host.CreateAsync(new DesignerCreateParams(type, name, container is null || container == Root ? null : container.Name, x, y) { Width = width, Height = height }, CancellationToken.None),
            after: () => SetSelection([name], notify: true)).ConfigureAwait(true);
        _shell.Toolbox.Disarm();
    }

    /// <summary>Visual Studio's name for a new component: its type's name, first letter lowercased, and the first free number (<c>button1</c>).</summary>
    public string NewName(string type)
    {
        var simple = type[(type.LastIndexOf('.') + 1)..];
        var stem = char.ToLowerInvariant(simple[0]) + simple[1..];
        var taken = new HashSet<string>(_model?.Components.Select(c => c.Name) ?? [], StringComparer.Ordinal);
        foreach (var component in _components)
        {
            taken.Add(component.Name);
        }

        for (var i = 1; ; i++)
        {
            if (!taken.Contains(stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                return stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    // ------------------------------------------------------------------ changes → code

    /// <summary>
    /// A change through the designer host, then its code through the C# host, applied to the designer file as one
    /// undoable edit. One at a time; when the code can't take it, the form is loaded again from the code.
    /// </summary>
    private async Task RunAsync(Func<IWinFormsDesignerHost, Task<DesignerChangeResult>> change, Action? after = null)
    {
        if (_host is not { } host || _designerCode is null || _model is null)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            IsBusy = true;
            var result = await change(host).ConfigureAwait(true);
            if (result.Error is { } error)
            {
                Status = error;
                Show(result.Render);
                return;
            }

            var edits = EditsFor(result);
            if (edits.Count > 0 && !await WriteAsync(edits).ConfigureAwait(true))
            {
                await LoadFormAsync().ConfigureAwait(true);
                return;
            }

            Show(result.Render);
            after?.Invoke();
            PropertiesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (PreviewerException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or InvalidOperationException)
        {
            Status = Localizer.Format("msg.winFormsDesigner.failed", "The designer failed: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// The C# host's edits for what the designer host did, in the order the code needs them: the new components, the
    /// controls that moved, the collections (and the locals they hold), the components that went, the controls' new
    /// order, the properties (extender properties and data bindings among them), the components that now get their
    /// properties from the resources, and the resources' keys.
    /// </summary>
    internal List<DesignerEdit> EditsFor(DesignerChangeResult result)
    {
        var edits = new List<DesignerEdit>();
        var model = _model;
        var created = new HashSet<string>(StringComparer.Ordinal);
        bool Known(string name) => name.Length == 0 || created.Contains(name) || (model?.Components.Any(c => c.Name == name) ?? false);
        foreach (var component in (result.Created is { } main ? [main] : Array.Empty<DesignerCreated>()).Concat(result.Added))
        {
            if (Known(component.Name))
            {
                continue;
            }

            created.Add(component.Name);
            edits.Add(new DesignerEdit
            {
                Kind = DesignerEditKinds.AddComponent,
                Component = component.Name,
                Type = component.Type,
                CreationArguments = component.CreationArguments,
                Parent = component.IsControl ? ComponentOf(component.Parent ?? string.Empty) : null,
                ParentPath = component.ParentPath,
                Cell = component.Cell,
                Index = component.IsControl ? component.Index : 0,
                Properties = component.Properties,
                SupportsInitialize = component.SupportsInitialize,
            });
        }

        foreach (var moved in (result.Reparented is { } reparented ? [reparented] : Array.Empty<DesignerReparented>()).Concat(result.Moved))
        {
            edits.Add(new DesignerEdit { Kind = DesignerEditKinds.SetParent, Component = moved.Component, Parent = ComponentOf(moved.Parent ?? string.Empty), ParentPath = moved.ParentPath, Cell = moved.Cell, Index = 0 });
        }

        foreach (var collection in result.Collections)
        {
            if (!Known(ComponentOf(collection.Component)))
            {
                continue;
            }

            foreach (var local in collection.Locals)
            {
                if (Known(local.Name))
                {
                    edits.Add(new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = local.Name });
                }

                edits.Add(new DesignerEdit { Kind = DesignerEditKinds.AddLocal, Component = local.Name, Value = local.Value });
                created.Add(local.Name);
            }

            edits.Add(new DesignerEdit
            {
                Kind = DesignerEditKinds.SetCollection,
                Component = ComponentOf(collection.Component),
                Property = collection.Property,
                Method = collection.Method,
                Type = collection.ElementType,
                Items = collection.Items,
            });
        }

        foreach (var removed in result.Removed)
        {
            if (Known(removed) && removed.Length > 0 && !created.Contains(removed))
            {
                edits.Add(new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = removed });
            }
        }

        foreach (var order in result.Reordered)
        {
            for (var i = 0; i < order.Children.Length; i++)
            {
                if (Known(order.Children[i]))
                {
                    edits.Add(new DesignerEdit { Kind = DesignerEditKinds.SetChildIndex, Component = order.Children[i], Index = i });
                }
            }
        }

        foreach (var change in result.Changes)
        {
            var component = ComponentOf(change.Component);
            if (!Known(component) || (result.Created?.Name == component && change.Provider is null && !change.DataBinding))
            {
                continue;
            }

            edits.Add(change switch
            {
                { Provider: { } provider } => new DesignerEdit { Kind = DesignerEditKinds.SetExtender, Component = component, Provider = provider, Property = change.Property, Value = change.Value },
                { DataBinding: true } => new DesignerEdit { Kind = DesignerEditKinds.SetDataBinding, Component = component, Property = change.Property, Value = change.Value },
                _ => new DesignerEdit { Kind = DesignerEditKinds.SetProperty, Component = component, Property = change.Property, Value = change.Value },
            });
        }

        foreach (var localized in result.Localized)
        {
            if (Known(ComponentOf(localized.Component)))
            {
                edits.Add(new DesignerEdit { Kind = DesignerEditKinds.SetLocalized, Component = ComponentOf(localized.Component), Localized = localized.Localized });
            }
        }

        foreach (var resource in result.Resources)
        {
            edits.Add(new DesignerEdit { Kind = DesignerEditKinds.SetResource, Key = resource.Key, Culture = resource.Culture, Resource = resource.Resource, Metadata = resource.Metadata });
        }

        return edits;
    }

    /// <summary>Writes edits through the C# host into the designer file (and the code file for handlers); false when the code didn't take them.</summary>
    private async Task<bool> WriteAsync(IReadOnlyList<DesignerEdit> edits, bool reloadAfter = false, bool showHandler = true)
    {
        var client = _shell.Languages.RunningClient(ShellHost.CSharpLanguageId);
        if (client is null || _designerCode is null)
        {
            Status = Localizer.Get("msg.winFormsDesigner.noCSharp", "The C# language service isn't available: the designer reads the form through it.");
            return false;
        }

        var result = await client.WinFormsEditAsync(new WinFormsEditParams(new TextDocumentIdentifier(Codalus.LanguageServices.Protocol.DocumentUris.FromPath(DesignerPath)), [.. edits]) { Version = _designerCode.Version, ResourceFiles = HeldResourceFiles() }, CancellationToken.None).ConfigureAwait(true);
        if (result is null || result.Error is { } error)
        {
            Status = result?.Error ?? Localizer.Get("msg.winFormsDesigner.noAnswer", "The C# language service didn't read the form.");
            return false;
        }

        var touched = TouchedModels(result.Edit);
        if (!reloadAfter)
        {
            // The designer host already shows this: the edit's own change of the text doesn't load the form again.
            _expectedVersion = result.Edit.DocumentChanges.Any(d => SolutionPaths.Comparer.Equals(Codalus.LanguageServices.Protocol.DocumentUris.ToPath(d.TextDocument.Uri), DesignerPath))
                ? _designerCode.Version + 1 : _designerCode.Version;
            foreach (var model in touched.Where(m => m != _designerCode))
            {
                _expectedResources[model] = model.Version + 1;
            }
        }

        if (!await _shell.ApplyWorkspaceEditAsync(result.Edit, quiet: true).ConfigureAwait(true))
        {
            return false;
        }

        if (!reloadAfter)
        {
            _expectedVersion = _designerCode.Version;
            foreach (var model in touched.Where(m => m != _designerCode))
            {
                _expectedResources[model] = model.Version;
            }

            _model = result.Model ?? _model;
        }

        RecordUndo(touched);
        if (result.Edit.DocumentChanges.Any(d => d.TextDocument.Version is null && Codalus.LanguageServices.Protocol.DocumentUris.ToPath(d.TextDocument.Uri) is { } path && path.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)))
        {
            // A new culture's resources: held from now on.
            await HoldResourcesAsync().ConfigureAwait(true);
        }

        if (showHandler && result.Handler is { } handler && Codalus.LanguageServices.Protocol.DocumentUris.ToPath(handler.Uri) is { } file)
        {
            await _shell.OpenFileAsync(file, preview: false, at: handler.Range.Start, textEditor: true).ConfigureAwait(true);
        }

        return true;
    }

    /// <summary>A property set in the Properties window (as text, converted by the property's type converter), or reset to its default (null).</summary>
    public Task SetPropertyAsync(IReadOnlyList<string> components, string property, string? text)
    {
        return RunAsync(host => host.SetPropertiesAsync(new DesignerSetPropertiesParams([.. components.Select(c => text is null
            ? new DesignerPropertyEdit(ComponentOf(c), property) { Reset = true }
            : new DesignerPropertyEdit(ComponentOf(c), property) { Text = text })]), CancellationToken.None));
    }

    /// <summary>The Properties window's (Name): renames the component in the code (its uses elsewhere too), then loads the form again.</summary>
    public async Task RenameAsync(string component, string newName)
    {
        if (newName == component || newName.Length == 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (await WriteAsync([new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = component, NewName = newName }], reloadAfter: true).ConfigureAwait(true))
            {
                _selection = [.. _selection.Select(s => s == component ? newName : s)];
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Hooks an event to a handler (a name typed in the Events view, or the default <c>button1_Click</c> when null),
    /// writing the handler in the code file when it doesn't exist, and shows it. An empty name unhooks the event.
    /// </summary>
    public async Task HookEventAsync(string component, string eventName, string? handler)
    {
        var target = ComponentOf(component);
        if (handler is { Length: 0 })
        {
            await _gate.WaitAsync().ConfigureAwait(true);
            try
            {
                await WriteAsync([new DesignerEdit { Kind = DesignerEditKinds.RemoveEvent, Component = target, Event = eventName }]).ConfigureAwait(true);
            }
            finally
            {
                _gate.Release();
            }

            PropertiesChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // An event already hooked keeps its handler (double-click shows it).
        handler ??= _model?.Statements.FirstOrDefault(s => s.Kind == DesignerStatementKinds.Event && s.Target == target && s.Member == eventName)?.Handler;
        DesignerEventInfo? info = null;
        if (_host is { } host)
        {
            try
            {
                info = (await host.EventsAsync(new DesignerComponentsParams([target]), CancellationToken.None).ConfigureAwait(true)).Events.FirstOrDefault(e => e.Name == eventName);
            }
            catch (PreviewerException)
            {
            }
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            await WriteAsync([new DesignerEdit
            {
                Kind = DesignerEditKinds.AddEvent,
                Component = target,
                Event = eventName,
                Handler = handler,
                DelegateType = info?.DelegateType,
                HandlerParameters = info?.Parameters,
            }]).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }

        PropertiesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A double-click on a component: its default event's handler (<c>[DefaultEvent]</c>: a button's Click, a form's Load), as Visual Studio does.</summary>
    public Task DefaultEventAsync(double x, double y)
    {
        if (HitTest(x, y) is not { } hit || hit.IsPart)
        {
            return Task.CompletedTask;
        }

        var eventName = hit.DefaultEvent ?? (hit == Root ? "Load" : "Click");
        return HookEventAsync(hit.Name, eventName, handler: null);
    }

    // ------------------------------------------------------------------ undo, clipboard

    public void Undo()
    {
        if (Step(_undo, _redo, undo: true))
        {
            return;
        }

        if (_designerCode?.Text.UndoStack is { CanUndo: true } stack)
        {
            stack.Undo();
        }
    }

    public void Redo()
    {
        if (Step(_redo, _undo, undo: false))
        {
            return;
        }

        if (_designerCode?.Text.UndoStack is { CanRedo: true } stack)
        {
            stack.Redo();
        }
    }

    /// <summary>
    /// Copy: the selected components, the controls inside them and the items of their collections (a menu's items, a list
    /// view's columns and items), as their code says them: their properties, extender properties, data bindings,
    /// collections and event hookups.
    /// </summary>
    public void Copy()
    {
        if (_model is not { } model)
        {
            return;
        }

        var names = new List<string>();
        var locals = model.Components.Where(c => c.IsLocal).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        void Add(string name)
        {
            if (names.Contains(name) || name == "resources")
            {
                return;
            }

            names.Add(name);
            foreach (var call in model.Statements.Where(s => s.Kind == DesignerStatementKinds.Call && s.Target == name && s.Member is { } member && IsCollectionCall(member)))
            {
                foreach (var child in References(call.Arguments ?? []))
                {
                    if (model.Components.Any(c => c.Name == child && !c.IsRoot))
                    {
                        Add(child);
                    }
                }
            }
        }

        foreach (var selected in _selection.Where(s => s != model.ClassName && Find(s) is { IsPart: false }))
        {
            Add(selected);
        }

        s_clipboard = [.. names.Select(n => Describe(model, n, DesignerPath, locals)).OfType<DesignerClipboardEntry>()];
        _shell.StatusBar.ShowMessage(Localizer.Format("msg.winFormsDesigner.copied", "Copied {0} component(s).", s_clipboard.Count), TimeSpan.FromSeconds(3));
    }

    /// <summary><c>Controls.Add</c> and the collections' calls (<c>Items.AddRange</c>, <c>ColumnStyles.Add</c>, <c>Images.SetKeyName</c>).</summary>
    private static bool IsCollectionCall(string member) =>
        member.EndsWith(".Add", StringComparison.Ordinal) || member.EndsWith(".AddRange", StringComparison.Ordinal) || member.EndsWith(".SetKeyName", StringComparison.Ordinal);

    private static IEnumerable<string> References(IEnumerable<DesignerValue> values)
    {
        foreach (var value in values)
        {
            if (value is { Kind: DesignerValueKinds.Reference, Value: { Length: > 0 } name })
            {
                yield return name;
            }

            foreach (var inner in References(value.Arguments ?? []))
            {
                yield return inner;
            }
        }
    }

    private static DesignerClipboardEntry? Describe(DesignerModel model, string name, string source, HashSet<string> locals)
    {
        if (model.Components.FirstOrDefault(c => c.Name == name) is not { } component
            || model.Statements.FirstOrDefault(s => s.Kind == DesignerStatementKinds.Create && s.Target == name) is not { Value: { } creation })
        {
            return null;
        }

        if (locals.Contains(name))
        {
            return new DesignerClipboardEntry(name, component.Type, [], null, null, null, [], false) { LocalValue = creation, Source = source };
        }

        var add = model.Statements.FirstOrDefault(s => s.Kind == DesignerStatementKinds.Call && s.Member?.EndsWith("Controls.Add", StringComparison.Ordinal) == true && s.Arguments?.FirstOrDefault()?.Value == name);
        var path = add?.Member is { } member && member.Length > "Controls.Add".Length ? member[..^".Controls.Add".Length] : null;
        int[]? cell = add?.Arguments is { Length: 3 } arguments ? [.. arguments.Skip(1).Select(a => int.Parse(a.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture))] : null;
        var properties = model.Statements
            .Where(s => s.Kind == DesignerStatementKinds.Assign && s.Target == name && s.Value is { Kind: not DesignerValueKinds.Resource })
            .Select(s => new DesignerPropertyValue(s.Member!, s.Value!)).ToArray();

        // Its collections, its data bindings, and the extender properties others give it.
        var calls = model.Statements.Where(s => s.Kind == DesignerStatementKinds.Call && (
            (s.Target == name && s.Member is { } m && !m.EndsWith("Controls.Add", StringComparison.Ordinal) && (IsCollectionCall(m) || m == "DataBindings.Add"))
            || (s.Arguments is [{ Kind: DesignerValueKinds.Reference } extended, _] && extended.Value == name && s.Member is { } setter && setter.StartsWith("Set", StringComparison.Ordinal) && !setter.Contains('.', StringComparison.Ordinal) && s.Target != name)))
            .ToArray();
        var events = model.Statements.Where(s => s.Kind == DesignerStatementKinds.Event && s.Target == name).ToArray();
        return new DesignerClipboardEntry(name, component.Type, creation.Arguments ?? [], add?.Target, path, cell, properties,
            model.Statements.Any(s => s.Kind == DesignerStatementKinds.Layout && s.Target == name && s.Member == "BeginInit"))
        {
            Calls = calls,
            Events = events,
            Source = source,
        };
    }

    /// <summary>
    /// Paste: the copied components under new names, in the selected container (or the copied ones' parent), a little
    /// lower and to the right, with their collections, extender properties and data bindings where what they name
    /// exists. Their event hookups come too when pasting into the form they were copied from (the handlers are its);
    /// into another form they don't, as Visual Studio does (ADR 0073).
    /// </summary>
    public async Task PasteAsync()
    {
        if (_model is not { } model || s_clipboard.Count == 0)
        {
            return;
        }

        var target = _selection.Select(Find).OfType<DesignerComponentView>().FirstOrDefault(c => c.IsContainer && !c.IsPart);
        var targetName = target is null || target == Root ? string.Empty : target.Name;
        var copied = s_clipboard.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = model.Components.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in s_clipboard)
        {
            var stem = new string(entry.Name.TrimEnd("0123456789".ToCharArray()));
            var i = 1;
            while (taken.Contains(stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                i++;
            }

            names[entry.Name] = stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            taken.Add(names[entry.Name]);
        }

        // A name in a copied statement: the pasted component's, or one the form has; else the statement can't come.
        var sameForm = s_clipboard.All(e => SolutionPaths.Comparer.Equals(e.Source, DesignerPath));
        string? Rename(string name) => names.TryGetValue(name, out var renamed) ? renamed : name.Length == 0 || model.Components.Any(c => c.Name == name) || name == "components" ? name : null;
        DesignerValue? Remap(DesignerValue value)
        {
            if (value.Kind == DesignerValueKinds.Reference)
            {
                return Rename(value.Value ?? string.Empty) is { } renamed ? value with { Value = renamed } : null;
            }

            if (value.Arguments is not { } arguments)
            {
                return value;
            }

            var mapped = arguments.Select(Remap).ToArray();
            return mapped.Any(a => a is null) ? null : value with { Arguments = [.. mapped.OfType<DesignerValue>()] };
        }

        var edits = new List<DesignerEdit>();
        foreach (var entry in s_clipboard.Where(e => e.LocalValue is not null))
        {
            edits.Add(new DesignerEdit { Kind = DesignerEditKinds.AddLocal, Component = names[entry.Name], Value = entry.LocalValue });
        }

        foreach (var entry in s_clipboard.Where(e => e.LocalValue is null))
        {
            var topLevel = entry.Parent is null || !copied.Contains(entry.Parent);
            var parent = topLevel ? (entry.Parent is null ? null : targetName) : names[entry.Parent!];
            var properties = entry.Properties.Select(p => p.Name switch
            {
                "Name" => p with { Value = DesignerValue.String(names[entry.Name]) },
                "Location" when topLevel && p.Value is { Kind: DesignerValueKinds.New, Arguments: [{ Value: { } x }, { Value: { } y }] } => p with
                {
                    Value = DesignerValue.NewOf(p.Value.Type!, DesignerValue.Int(int.Parse(x, System.Globalization.CultureInfo.InvariantCulture) + 16), DesignerValue.Int(int.Parse(y, System.Globalization.CultureInfo.InvariantCulture) + 16)),
                },
                _ => Remap(p.Value) is { } value ? p with { Value = value } : null,
            }).OfType<DesignerPropertyValue>().ToArray();
            edits.Add(new DesignerEdit
            {
                Kind = DesignerEditKinds.AddComponent,
                Component = names[entry.Name],
                Type = entry.Type,
                CreationArguments = entry.CreationArguments,
                Parent = parent,
                ParentPath = topLevel ? null : entry.ParentPath,
                Cell = topLevel ? null : entry.Cell,
                Index = topLevel ? 0 : int.MaxValue,
                Properties = properties,
                SupportsInitialize = entry.SupportsInitialize,
            });
        }

        foreach (var entry in s_clipboard)
        {
            foreach (var group in entry.Calls.GroupBy(c => (c.Target, Member: c.Member == "DataBindings.Add" || !c.Member!.Contains('.', StringComparison.Ordinal) ? c.Member + "#" + c.Line : c.Member![..c.Member!.LastIndexOf('.')])))
            {
                var first = group.First();
                var arguments = group.SelectMany(c => c.Arguments ?? []).Select(Remap).ToList();
                if (arguments.Any(a => a is null) || Rename(first.Target) is not { } owner)
                {
                    continue;
                }

                if (first.Member == "DataBindings.Add" && arguments[0] is { Arguments: [{ Value: { } bound }, ..] } binding)
                {
                    edits.Add(new DesignerEdit { Kind = DesignerEditKinds.SetDataBinding, Component = owner, Property = bound, Value = binding });
                }
                else if (!first.Member!.Contains('.', StringComparison.Ordinal) && arguments is [{ } extended, { } value])
                {
                    edits.Add(new DesignerEdit { Kind = DesignerEditKinds.SetExtender, Component = extended.Value ?? string.Empty, Provider = owner, Property = first.Member["Set".Length..], Value = value });
                }
                else
                {
                    var method = first.Member[(first.Member.LastIndexOf('.') + 1)..];
                    DesignerValue[] items = method switch
                    {
                        "AddRange" => arguments[0]!.Arguments ?? [],
                        "SetKeyName" => [.. arguments.Where((_, i) => i % 2 == 1).OfType<DesignerValue>()],
                        _ => [.. arguments.OfType<DesignerValue>()],
                    };
                    edits.Add(new DesignerEdit { Kind = DesignerEditKinds.SetCollection, Component = owner, Property = group.Key.Member, Method = method, Type = method == "AddRange" ? arguments[0]!.Type : null, Items = items });
                }
            }

            if (sameForm)
            {
                foreach (var hookup in entry.Events)
                {
                    edits.Add(new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = names[entry.Name], Event = hookup.Member, Handler = hookup.Handler, DelegateType = hookup.Value?.Type });
                }
            }
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (await WriteAsync(edits, reloadAfter: true, showHandler: false).ConfigureAwait(true))
            {
                _selection = [.. s_clipboard.Where(e => e.LocalValue is null && (e.Parent is null || !copied.Contains(e.Parent))).Select(e => names[e.Name])];
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DuplicateAsync()
    {
        Copy();
        await PasteAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ the Properties window

    public string PropertiesTitle => _selection.Count switch
    {
        0 => Title,
        1 when Find(_selection[0]) is { } view => $"{_selection[0]} {view.Type}",
        _ => Localizer.Format("msg.winFormsDesigner.selectedCount", "{0} components", _selection.Count),
    };

    public async Task<IReadOnlyList<DesignerPropertyRow>> PropertyRowsAsync(CancellationToken cancellationToken)
    {
        if (_host is not { } host || _selection.Count == 0)
        {
            return [];
        }

        var selected = _selection.ToList();
        var result = await host.PropertiesAsync(new DesignerComponentsParams([.. selected.Select(ComponentOf)]), cancellationToken).ConfigureAwait(true);
        List<DesignerPropertyRow> rows = [.. result.Properties.Select(p => new DesignerPropertyRow(p.Category, RowFor(selected, p)))];
        if (IsLocalizable && selected.Count == 1 && ComponentOf(selected[0]).Length == 0)
        {
            rows.Add(LanguageRow());
        }

        return rows;
    }

    private PropertyRowViewModel RowFor(List<string> selected, DesignerPropertyInfo info)
    {
        var value = info.Mixed ? string.Empty : info.Value ?? string.Empty;
        if (info.Name == "Name" && selected.Count == 1)
        {
            return new PropertyRowViewModel("(Name)", value, PropertyRowKind.Text, info.Description ?? string.Empty,
                info.IsReadOnly ? null : name => RenameAsync(selected[0], name?.Trim() ?? string.Empty), isSet: true);
        }

        return RowFor(selected, info, value);
    }

    public bool HasEvents => _selection.Count == 1;

    public async Task<IReadOnlyList<DesignerPropertyRow>> EventRowsAsync(CancellationToken cancellationToken)
    {
        if (_host is not { } host || _selection.Count != 1)
        {
            return [];
        }

        var component = _selection[0];
        var target = ComponentOf(component);
        var events = await host.EventsAsync(new DesignerComponentsParams([target]), cancellationToken).ConfigureAwait(true);
        var hooked = _model?.Statements.Where(s => s.Kind == DesignerStatementKinds.Event && s.Target == target).ToDictionary(s => s.Member!, s => s.Handler!, StringComparer.Ordinal) ?? [];
        return [.. events.Events.Select(e => new DesignerPropertyRow(e.Category, new PropertyRowViewModel(e.Name, hooked.GetValueOrDefault(e.Name) ?? string.Empty, PropertyRowKind.Text, e.Description ?? string.Empty,
            handler => HookEventAsync(component, e.Name, handler?.Trim() ?? string.Empty), isSet: hooked.ContainsKey(e.Name))
        {
            Action = new RelayCommand(() => _ = HookEventAsync(component, e.Name, handler: null)),
            ActionTitle = Localizer.Format("msg.winFormsDesigner.generateHandler", "Go to or create the {0} handler", e.Name),
        }))];
    }

    // ------------------------------------------------------------------ saving and closing

    public async Task SaveAsync()
    {
        if (_designerCode is { } model)
        {
            await _shell.SaveModelAsync(model).ConfigureAwait(true);
        }

        foreach (var resources in _resourceModels.Where(m => m.IsModified).ToList())
        {
            await _shell.SaveModelAsync(resources).ConfigureAwait(true);
        }
    }

    /// <summary>The designer closed: its designer host stops and the designer file's text is let go.</summary>
    public async Task CloseAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reload?.Cancel();
        if (_designerCode is { } model)
        {
            model.ContentChanged -= OnDesignerCodeChanged;
            model.PropertyChanged -= OnDesignerCodePropertyChanged;
            _shell.ReleaseModel(model);
        }

        ReleaseResources();
        if (_shell.PropertySource == this)
        {
            _shell.PropertySource = null;
        }

        if (_host is { } host)
        {
            _host = null;
            await host.DisposeAsync().ConfigureAwait(true);
        }

        Frame?.Dispose();
        Dispose();
    }

    public void Dispose()
    {
        _reload?.Dispose();
        _gate.Dispose();
    }
}

/// <summary>An item of the Toolbox: a control or component type (or the Pointer, which disarms).</summary>
public sealed record ToolboxItemViewModel(string Name, string Type, string Category, bool IsControl, string Icon)
{
    public bool IsPointer => Type.Length == 0;

    public string? Description { get; init; }

    public string ToolTip => IsPointer ? Name : Description is { } description ? $"{Type}\n{description}" : Type;
}

public sealed class ToolboxCategoryViewModel(string title, IReadOnlyList<ToolboxItemViewModel> items) : ObservableObject
{
    private bool _isExpanded = true;

    public string Title { get; } = title;

    public IReadOnlyList<ToolboxItemViewModel> Items { get; } = items;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }
}

/// <summary>
/// The Toolbox (Ctrl+Alt+X, M41a): Windows Forms' common controls by Visual Studio's categories, and the project's own
/// controls and components (from the C# host). Selecting an item arms it: the next click or drag on the designer's
/// surface creates it there (the Pointer, or creating it, disarms). Items can also be dragged onto the surface.
/// </summary>
public sealed class ToolboxViewModel : ObservableObject
{
    public const string ToolWindowId = "toolbox";

    /// <summary>The framework's items by category, as Visual Studio's Toolbox has them for Windows Forms on .NET.</summary>
    public static IReadOnlyList<(string Category, string[] Types)> Framework { get; } =
    [
        ("Common Controls", ["Button", "CheckBox", "CheckedListBox", "ComboBox", "DateTimePicker", "Label", "LinkLabel", "ListBox", "ListView", "MaskedTextBox", "MonthCalendar", "NotifyIcon", "NumericUpDown", "PictureBox", "ProgressBar", "RadioButton", "RichTextBox", "TextBox", "ToolTip", "TrackBar", "TreeView", "WebBrowser"]),
        ("Containers", ["FlowLayoutPanel", "GroupBox", "Panel", "SplitContainer", "TabControl", "TableLayoutPanel"]),
        ("Menus & Toolbars", ["ContextMenuStrip", "MenuStrip", "StatusStrip", "ToolStrip", "ToolStripContainer"]),
        ("Data", ["BindingNavigator", "BindingSource", "DataGridView"]),
        ("Components", ["System.ComponentModel.BackgroundWorker", "ErrorProvider", "System.IO.FileSystemWatcher", "HelpProvider", "ImageList", "System.Diagnostics.Process", "Timer"]),
        ("Printing", ["PageSetupDialog", "PrintDialog", "System.Drawing.Printing.PrintDocument", "PrintPreviewControl", "PrintPreviewDialog"]),
        ("Dialogs", ["ColorDialog", "FolderBrowserDialog", "FontDialog", "OpenFileDialog", "SaveFileDialog"]),
    ];

    /// <summary>
    /// Each framework item's icon (M34h: they shared one per category), by its simple name; a palette color for each is in
    /// <c>CommandIcons</c>. A type that isn't here takes the icon of the known control its name ends with (a project's
    /// <c>OrderTextBox</c>, a vendor's <c>RadButton</c>), else <see cref="GenericControlIcon"/> or
    /// <see cref="GenericComponentIcon"/>.
    /// </summary>
    private static readonly Dictionary<string, string> s_icons = new(StringComparer.Ordinal)
    {
        ["Button"] = "control-button",
        ["CheckBox"] = "control-check-box",
        ["CheckedListBox"] = "control-checked-list-box",
        ["ComboBox"] = "control-combo-box",
        ["DateTimePicker"] = "control-date-time-picker",
        ["Label"] = "control-label",
        ["LinkLabel"] = "control-link-label",
        ["ListBox"] = "control-list-box",
        ["ListView"] = "control-list-view",
        ["MaskedTextBox"] = "control-masked-text-box",
        ["MonthCalendar"] = "control-month-calendar",
        ["NotifyIcon"] = "control-notify-icon",
        ["NumericUpDown"] = "control-numeric-up-down",
        ["PictureBox"] = "control-picture-box",
        ["ProgressBar"] = "control-progress-bar",
        ["RadioButton"] = "control-radio-button",
        ["RichTextBox"] = "control-rich-text-box",
        ["TextBox"] = "control-text-box",
        ["ToolTip"] = "control-tool-tip",
        ["TrackBar"] = "control-track-bar",
        ["TreeView"] = "control-tree-view",
        ["WebBrowser"] = "control-web-browser",
        ["WebView2"] = "control-web-browser",
        ["FlowLayoutPanel"] = "control-flow-layout-panel",
        ["GroupBox"] = "control-group-box",
        ["Panel"] = "control-panel",
        ["SplitContainer"] = "control-split-container",
        ["TabControl"] = "control-tab-control",
        ["TableLayoutPanel"] = "control-table-layout-panel",
        ["ContextMenuStrip"] = "control-context-menu-strip",
        ["MenuStrip"] = "control-menu-strip",
        ["StatusStrip"] = "control-status-strip",
        ["ToolStrip"] = "control-tool-strip",
        ["ToolStripContainer"] = "control-tool-strip-container",
        ["BindingNavigator"] = "control-binding-navigator",
        ["BindingSource"] = "control-binding-source",
        ["DataGridView"] = "control-data-grid-view",
        ["BackgroundWorker"] = "control-background-worker",
        ["ErrorProvider"] = "control-error-provider",
        ["FileSystemWatcher"] = "control-file-system-watcher",
        ["HelpProvider"] = "control-help-provider",
        ["ImageList"] = "control-image-list",
        ["Process"] = "control-process",
        ["Timer"] = "control-timer",
        ["PageSetupDialog"] = "control-page-setup-dialog",
        ["PrintDialog"] = "control-print-dialog",
        ["PrintDocument"] = "control-print-document",
        ["PrintPreviewControl"] = "control-print-preview-control",
        ["PrintPreviewDialog"] = "control-print-preview-dialog",
        ["ColorDialog"] = "control-color-dialog",
        ["FolderBrowserDialog"] = "control-folder-browser-dialog",
        ["FontDialog"] = "control-font-dialog",
        ["OpenFileDialog"] = "control-open-file-dialog",
        ["SaveFileDialog"] = "control-save-file-dialog",
    };

    /// <summary>The Pointer's icon (the arrow), and the icons of a control and a component the Toolbox has no icon for.</summary>
    public const string PointerIcon = "control-pointer";

    public const string GenericControlIcon = "control-generic";

    public const string GenericComponentIcon = "control-component";

    /// <summary>The icon of a Toolbox item by its type (full or simple name).</summary>
    public static string IconFor(string type, bool isControl)
    {
        var name = type[(type.LastIndexOf('.') + 1)..];
        if (s_icons.TryGetValue(name, out var icon))
        {
            return icon;
        }

        // A derived or third-party control usually ends with what it derives from: the longest such name wins
        // (OrderRichTextBox is a rich text box, not a text box).
        var best = string.Empty;
        foreach (var (known, knownIcon) in s_icons)
        {
            if (known.Length > best.Length && name.EndsWith(known, StringComparison.Ordinal))
            {
                best = known;
                icon = knownIcon;
            }
        }

        return icon ?? (isControl ? GenericControlIcon : GenericComponentIcon);
    }

    /// <summary>The framework's types that aren't controls (they go to the tray).</summary>
    private static readonly HashSet<string> s_components = new(StringComparer.Ordinal)
    {
        "NotifyIcon", "ToolTip", "BindingSource", "System.ComponentModel.BackgroundWorker", "ErrorProvider", "System.IO.FileSystemWatcher", "HelpProvider", "ImageList",
        "System.Diagnostics.Process", "Timer", "PageSetupDialog", "PrintDialog", "System.Drawing.Printing.PrintDocument", "PrintPreviewDialog", "ColorDialog",
        "FolderBrowserDialog", "FontDialog", "OpenFileDialog", "SaveFileDialog", "ContextMenuStrip",
    };

    private IReadOnlyList<WinFormsToolboxItem> _projectItems = [];
    private int _projectItemsRequest;
    private string _filter = string.Empty;
    private ToolboxItemViewModel? _armed;
    private bool _isAvailable;

    public ToolboxViewModel()
    {
        Rebuild();
    }

    public ObservableCollection<ToolboxCategoryViewModel> Categories { get; } = [];

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                Rebuild();
            }
        }
    }

    /// <summary>The item the next click or drag on the surface creates (null: the Pointer).</summary>
    public ToolboxItemViewModel? Armed
    {
        get => _armed;
        set
        {
            if (SetField(ref _armed, value is { IsPointer: true } ? null : value))
            {
                OnPropertyChanged(nameof(ArmedText));
                OnPropertyChanged(nameof(HasArmed));
            }
        }
    }

    /// <summary>What a click or a drag on the surface creates now.</summary>
    public string? ArmedText => _armed is { } item ? Localizer.Format("msg.toolbox.armed", "Click or drag on the form to add a {0} (Esc cancels).", item.Name) : null;

    public bool HasArmed => _armed is not null;

    public void Disarm() => Armed = null;

    /// <summary>A Windows Forms designer is the active document.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (SetField(ref _isAvailable, value))
            {
                OnPropertyChanged(nameof(Message));
                OnPropertyChanged(nameof(HasMessage));
                OnPropertyChanged(nameof(HasNoMatches));
                if (!value)
                {
                    Disarm();
                }
            }
        }
    }

    public string? Message => _isAvailable ? null : Localizer.Get("msg.toolbox.noDesigner", "Open a form or user control in the Windows Forms designer to use the Toolbox.");

    public bool HasMessage => !_isAvailable;

    /// <summary>A filter that matches nothing says so (M34g: the Toolbox went blank), with the way back.</summary>
    public bool HasNoMatches => _isAvailable && Categories.Count == 0 && _filter.Length > 0;

    public string NoMatchesText => Localizer.Format("msg.toolbox.noMatches", "No Toolbox items match '{0}'. Clear the search box to see them all.", _filter);

    /// <summary>
    /// The project's own controls and components (from the C# host). The same items again change nothing: the
    /// categories stay as they are (expanded or collapsed) and the armed item stays one of the Toolbox's.
    /// </summary>
    public void SetProjectItems(IReadOnlyList<WinFormsToolboxItem> items)
    {
        if (items.SequenceEqual(_projectItems))
        {
            return;
        }

        _projectItems = items;
        Rebuild();
    }

    /// <summary>
    /// Starts a request for the project's items (the C# host answers it later): only the newest request's answer is
    /// shown, so a slower earlier answer, such as one from before the restore, doesn't replace a newer one.
    /// </summary>
    public int BeginProjectItemsRequest() => ++_projectItemsRequest;

    /// <summary>The answer to <paramref name="request"/>: shown unless a newer request has started since.</summary>
    public void SetProjectItems(int request, IReadOnlyList<WinFormsToolboxItem> items)
    {
        if (request == _projectItemsRequest)
        {
            SetProjectItems(items);
        }
    }

    /// <summary>A framework item's full type name (<c>Button</c> is <c>System.Windows.Forms.Button</c>).</summary>
    public static string FullType(string type) => type.Contains('.', StringComparison.Ordinal) ? type : "System.Windows.Forms." + type;

    private void Rebuild()
    {
        bool Matches(string name) => _filter.Length == 0 || name.Contains(_filter, StringComparison.OrdinalIgnoreCase);
        Categories.Clear();
        var pointer = Localizer.Get("msg.toolbox.pointer", "Pointer");
        foreach (var group in _projectItems.GroupBy(i => i.Project))
        {
            var category = Localizer.Format("msg.toolbox.projectComponents", "{0} Components", group.Key);
            var items = group.Where(i => Matches(i.Name)).Select(i => new ToolboxItemViewModel(i.Name, i.Type, category, i.IsControl, IconFor(i.Type, i.IsControl)) { Description = i.Description }).ToList();
            if (items.Count > 0)
            {
                Categories.Add(new ToolboxCategoryViewModel(category, [new ToolboxItemViewModel(pointer, string.Empty, category, false, PointerIcon), .. items]));
            }
        }

        foreach (var (title, types) in Framework)
        {
            var category = CategoryTitle(title);
            var items = types.Select(t => (Type: t, Name: t[(t.LastIndexOf('.') + 1)..])).Where(t => Matches(t.Name))
                .Select(t => new ToolboxItemViewModel(t.Name, FullType(t.Type), category, !s_components.Contains(t.Type), IconFor(t.Type, !s_components.Contains(t.Type)))).ToList();
            if (items.Count > 0)
            {
                Categories.Add(new ToolboxCategoryViewModel(category, [new ToolboxItemViewModel(pointer, string.Empty, category, false, PointerIcon), .. items]));
            }
        }

        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(NoMatchesText));
    }

    private static string CategoryTitle(string title) => title switch
    {
        "Common Controls" => Localizer.Get("msg.toolbox.commonControls", "Common Controls"),
        "Containers" => Localizer.Get("msg.toolbox.containers", "Containers"),
        "Menus & Toolbars" => Localizer.Get("msg.toolbox.menusAndToolbars", "Menus & Toolbars"),
        "Data" => Localizer.Get("msg.toolbox.data", "Data"),
        "Components" => Localizer.Get("msg.toolbox.components", "Components"),
        "Printing" => Localizer.Get("msg.toolbox.printing", "Printing"),
        "Dialogs" => Localizer.Get("msg.toolbox.dialogs", "Dialogs"),
        _ => title,
    };

    /// <summary>Every item (tests).</summary>
    public IEnumerable<ToolboxItemViewModel> Items => Categories.SelectMany(c => c.Items).Where(i => !i.IsPointer);
}
