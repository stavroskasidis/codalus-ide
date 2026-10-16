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

/// <summary>A non-visual component (a timer, a tool tip) shown under the surface, to select it.</summary>
public sealed record DesignerTrayItem(string Name, string Type, ICommand Select);

/// <summary>Components copied in the designer (their statements from the code), for Paste.</summary>
internal sealed record DesignerClipboardEntry(string Name, string Type, DesignerValue[] CreationArguments, string? Parent, string? ParentPath, int[]? Cell, DesignerPropertyValue[] Properties, bool SupportsInitialize);

/// <summary>
/// A form or user control in the Windows Forms designer (M41a, ADR 0073): the document over its code file. The C#
/// host reads InitializeComponent into a model; a designer host (Windows only, D31) draws it and applies the gestures;
/// each change comes back as code, which the C# host writes into the designer file as minimal text edits, so the
/// designer file's own undo stack is the designer's (undo and redo reload the form from the code). The code is the
/// only state: when it changes otherwise (typed in the code view, undone), the form is loaded again from it.
/// </summary>
public sealed class WinFormsDesignerDocument : ShellDocument, Views.ISavableDocument, IDesignerPropertySource, IDisposable
{
    public const string DocumentKind = "winformsDesigner";

    private static List<DesignerClipboardEntry> s_clipboard = [];

    private readonly ShellHost _shell;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _warnedLocalized = new(StringComparer.Ordinal);
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
            IsModified = _designerCode.IsModified;
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
        var read = await client.WinFormsReadAsync(Codalus.LanguageServices.Protocol.DocumentUris.FromPath(CodePath), CancellationToken.None).ConfigureAwait(true)
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
        var render = await _host.LoadAsync(WinFormsDesignerService.LoadParams(_target, _model, _read?.ClassFullName), CancellationToken.None).ConfigureAwait(true);
        Show(render);
        SetSelection([.. _selection.Where(s => _components.Any(c => c.Name == s))], notify: true);
        Status = Localizer.Format("msg.winFormsDesigner.ready", "{0}: drag from the Toolbox, select, move and resize; F7 shows the code.", _model.ClassName);
    }

    private async Task LoadToolboxAsync()
    {
        try
        {
            if (_read?.Project is { } project && _shell.Languages.RunningClient(ShellHost.CSharpLanguageId) is { } client
                && await client.WinFormsToolboxAsync(project, CancellationToken.None).ConfigureAwait(true) is { } toolbox)
            {
                _shell.Toolbox.SetProjectItems(toolbox.Items);
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

        _reload?.Cancel();
        var reload = _reload = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, reload.Token).ConfigureAwait(false);
                await Ui.RunAsync(() => _designerCode?.Version == _expectedVersion ? Task.CompletedTask : ReloadAsync(restartHost: false)).ConfigureAwait(false);
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
            IsModified = _designerCode?.IsModified ?? false;
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
        foreach (var component in _components.Where(c => !c.IsControl))
        {
            var name = component.Name;
            Tray.Add(new DesignerTrayItem(name, component.Type, new RelayCommand(() => SetSelection([name], notify: true))));
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

    /// <summary>A component whose properties are in the resources (a localizable form): this version doesn't change them; says so.</summary>
    private bool IsLocalized(string component)
    {
        var name = component == _model?.ClassName ? string.Empty : component;
        return _model?.Statements.Any(s => s.Kind == DesignerStatementKinds.ApplyResources && s.Target == name) == true;
    }

    private bool RefuseLocalized(IEnumerable<string> components)
    {
        var localized = components.Where(IsLocalized).ToList();
        if (localized.Count == 0)
        {
            return false;
        }

        Status = Localizer.Format("msg.winFormsDesigner.localized", "{0} is localizable: its properties are in the form's .resx, which the designer doesn't edit yet. Edit them in the resource editor, or set Localizable off.", localized[0]);
        return true;
    }

    private static string Invariant(double value) => Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private string ComponentOf(string name) => name == _model?.ClassName ? string.Empty : name;

    /// <summary>Moves the selected controls by a delta, into another container when they're dropped in one.</summary>
    public Task MoveSelectionAsync(double dx, double dy, double dropX, double dropY)
    {
        var moving = Movable();
        if (moving.Count == 0 || (Math.Abs(dx) < 1 && Math.Abs(dy) < 1) || RefuseLocalized(moving.Select(m => m.Name)))
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
        if (Find(name) is not { } view || RefuseLocalized([name]))
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
        if (moving.Count == 0 || RefuseLocalized(moving.Select(m => m.Name)))
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
        if (container is not null && RefuseLocalized([container.Name]))
        {
            return;
        }

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

    /// <summary>The C# host's edits for what the designer host did. Changes to a localizable component's properties stay out (they belong in its .resx).</summary>
    private List<DesignerEdit> EditsFor(DesignerChangeResult result)
    {
        var edits = new List<DesignerEdit>();
        if (result.Created is { } created)
        {
            edits.Add(new DesignerEdit
            {
                Kind = DesignerEditKinds.AddComponent,
                Component = created.Name,
                Type = created.Type,
                CreationArguments = created.CreationArguments,
                Parent = created.IsControl ? created.Parent ?? string.Empty : null,
                ParentPath = created.ParentPath,
                Cell = created.Cell,
                Index = 0,
                Properties = created.Properties,
                SupportsInitialize = created.SupportsInitialize,
            });
        }

        if (result.Reparented is { } moved)
        {
            edits.Add(new DesignerEdit { Kind = DesignerEditKinds.SetParent, Component = moved.Component, Parent = moved.Parent ?? string.Empty, ParentPath = moved.ParentPath, Cell = moved.Cell, Index = 0 });
        }

        foreach (var removed in result.Removed)
        {
            edits.Add(new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = removed });
        }

        foreach (var change in result.Changes)
        {
            var component = ComponentOf(change.Component);
            if (IsLocalized(change.Component) || (component.Length > 0 && !(_model?.Components.Any(c => c.Name == component) ?? false) && result.Created?.Name != component))
            {
                continue;
            }

            edits.Add(new DesignerEdit { Kind = DesignerEditKinds.SetProperty, Component = component, Property = change.Property, Value = change.Value });
        }

        return edits;
    }

    /// <summary>Writes edits through the C# host into the designer file (and the code file for handlers); false when the code didn't take them.</summary>
    private async Task<bool> WriteAsync(IReadOnlyList<DesignerEdit> edits, bool reloadAfter = false)
    {
        var client = _shell.Languages.RunningClient(ShellHost.CSharpLanguageId);
        if (client is null || _designerCode is null)
        {
            Status = Localizer.Get("msg.winFormsDesigner.noCSharp", "The C# language service isn't available: the designer reads the form through it.");
            return false;
        }

        var result = await client.WinFormsEditAsync(new WinFormsEditParams(new TextDocumentIdentifier(Codalus.LanguageServices.Protocol.DocumentUris.FromPath(DesignerPath)), [.. edits]) { Version = _designerCode.Version }, CancellationToken.None).ConfigureAwait(true);
        if (result is null || result.Error is { } error)
        {
            Status = result?.Error ?? Localizer.Get("msg.winFormsDesigner.noAnswer", "The C# language service didn't read the form.");
            return false;
        }

        if (!reloadAfter)
        {
            // The designer host already shows this: the edit's own change of the text doesn't load the form again.
            _expectedVersion = _designerCode.Version + 1;
        }

        if (!await _shell.ApplyWorkspaceEditAsync(result.Edit, quiet: true).ConfigureAwait(true))
        {
            return false;
        }

        if (!reloadAfter)
        {
            _expectedVersion = _designerCode.Version;
            _model = result.Model ?? _model;
        }

        if (result.Handler is { } handler && Codalus.LanguageServices.Protocol.DocumentUris.ToPath(handler.Uri) is { } file)
        {
            await _shell.OpenFileAsync(file, preview: false, at: handler.Range.Start, textEditor: true).ConfigureAwait(true);
        }

        return true;
    }

    /// <summary>A property set in the Properties window (as text, converted by the property's type converter), or reset to its default (null).</summary>
    public Task SetPropertyAsync(IReadOnlyList<string> components, string property, string? text)
    {
        if (RefuseLocalized(components))
        {
            return Task.CompletedTask;
        }

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
        if (_designerCode?.Text.UndoStack is { CanUndo: true } stack)
        {
            stack.Undo();
        }
    }

    public void Redo()
    {
        if (_designerCode?.Text.UndoStack is { CanRedo: true } stack)
        {
            stack.Redo();
        }
    }

    /// <summary>Copy: the selected components and the controls inside them, as their code says them.</summary>
    public void Copy()
    {
        if (_model is not { } model)
        {
            return;
        }

        var names = new List<string>();
        void Add(string name)
        {
            if (names.Contains(name))
            {
                return;
            }

            names.Add(name);
            foreach (var child in model.Statements.Where(s => s.Kind == DesignerStatementKinds.Call && s.Target == name && s.Member?.EndsWith("Controls.Add", StringComparison.Ordinal) == true))
            {
                if (child.Arguments?.FirstOrDefault()?.Value is { } childName)
                {
                    Add(childName);
                }
            }
        }

        foreach (var selected in _selection.Where(s => s != model.ClassName && Find(s) is { IsPart: false }))
        {
            Add(selected);
        }

        s_clipboard = [.. names.Select(n => Describe(model, n)).OfType<DesignerClipboardEntry>()];
        _shell.StatusBar.ShowMessage(Localizer.Format("msg.winFormsDesigner.copied", "Copied {0} component(s).", s_clipboard.Count), TimeSpan.FromSeconds(3));
    }

    private static DesignerClipboardEntry? Describe(DesignerModel model, string name)
    {
        if (model.Components.FirstOrDefault(c => c.Name == name && !c.IsLocal) is not { } component
            || model.Statements.FirstOrDefault(s => s.Kind == DesignerStatementKinds.Create && s.Target == name) is not { Value: { } creation })
        {
            return null;
        }

        var add = model.Statements.FirstOrDefault(s => s.Kind == DesignerStatementKinds.Call && s.Member?.EndsWith("Controls.Add", StringComparison.Ordinal) == true && s.Arguments?.FirstOrDefault()?.Value == name);
        var path = add?.Member is { } member && member.Length > "Controls.Add".Length ? member[..^".Controls.Add".Length] : null;
        int[]? cell = add?.Arguments is { Length: 3 } arguments ? [.. arguments.Skip(1).Select(a => int.Parse(a.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture))] : null;
        var properties = model.Statements
            .Where(s => s.Kind == DesignerStatementKinds.Assign && s.Target == name && s.Value is { Kind: not DesignerValueKinds.Resource })
            .Select(s => new DesignerPropertyValue(s.Member!, s.Value!)).ToArray();
        return new DesignerClipboardEntry(name, component.Type, creation.Arguments ?? [], add?.Target, path, cell, properties,
            model.Statements.Any(s => s.Kind == DesignerStatementKinds.Layout && s.Target == name && s.Member == "BeginInit"));
    }

    /// <summary>Paste: the copied components under new names, in the selected container (or the copied ones' parent), a little lower and to the right.</summary>
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

        var edits = new List<DesignerEdit>();
        foreach (var entry in s_clipboard)
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
                _ => p,
            }).ToArray();
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

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (await WriteAsync(edits, reloadAfter: true).ConfigureAwait(true))
            {
                _selection = [.. s_clipboard.Where(e => e.Parent is null || !copied.Contains(e.Parent)).Select(e => names[e.Name])];
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
        return [.. result.Properties.Select(p => new DesignerPropertyRow(p.Category, RowFor(selected, p)))];
    }

    private PropertyRowViewModel RowFor(List<string> selected, DesignerPropertyInfo info)
    {
        var value = info.Mixed ? string.Empty : info.Value ?? string.Empty;
        if (info.Name == "Name" && selected.Count == 1)
        {
            return new PropertyRowViewModel("(Name)", value, PropertyRowKind.Text, info.Description ?? string.Empty,
                info.IsReadOnly ? null : name => RenameAsync(selected[0], name?.Trim() ?? string.Empty), isSet: true);
        }

        var localized = selected.Any(IsLocalized);
        Func<string?, Task>? commit = info.IsReadOnly || localized ? null : text => SetPropertyAsync(selected, info.Name, text ?? string.Empty);
        var description = localized ? Localizer.Get("msg.winFormsDesigner.localizedProperty", "Localizable: this value comes from the form's .resx.") + " " + info.Description : info.Description ?? string.Empty;
        return info.Editor switch
        {
            DesignerEditors.Boolean or DesignerEditors.Enum or DesignerEditors.Dock or DesignerEditors.Color when info.StandardValues is { Length: > 0 } values =>
                new PropertyRowViewModel(info.Name, value, info.Editor == DesignerEditors.Color ? PropertyRowKind.Color : PropertyRowKind.Choice, description, commit,
                    [.. values.Select(v => new PropertyOption(v, v))], info.IsSet),
            DesignerEditors.Anchor or DesignerEditors.Flags when info.StandardValues is { Length: > 0 } flags =>
                new PropertyRowViewModel(info.Name, value, PropertyRowKind.Flags, description, commit,
                    [.. flags.Where(f => f is not "None").Select(f => new PropertyOption(f, f))], info.IsSet),
            DesignerEditors.Color => new PropertyRowViewModel(info.Name, value, PropertyRowKind.Color, description, commit, null, info.IsSet),
            _ => new PropertyRowViewModel(info.Name, value, PropertyRowKind.Text, description, commit, null, info.IsSet),
        };
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
    public static IReadOnlyList<(string Category, string Icon, string[] Types)> Framework { get; } =
    [
        ("Common Controls", "window", ["Button", "CheckBox", "CheckedListBox", "ComboBox", "DateTimePicker", "Label", "LinkLabel", "ListBox", "ListView", "MaskedTextBox", "MonthCalendar", "NotifyIcon", "NumericUpDown", "PictureBox", "ProgressBar", "RadioButton", "RichTextBox", "TextBox", "ToolTip", "TrackBar", "TreeView", "WebBrowser"]),
        ("Containers", "layers", ["FlowLayoutPanel", "GroupBox", "Panel", "SplitContainer", "TabControl", "TableLayoutPanel"]),
        ("Menus & Toolbars", "list", ["ContextMenuStrip", "MenuStrip", "StatusStrip", "ToolStrip", "ToolStripContainer"]),
        ("Data", "table", ["BindingNavigator", "BindingSource", "DataGridView"]),
        ("Components", "gear", ["System.ComponentModel.BackgroundWorker", "ErrorProvider", "System.IO.FileSystemWatcher", "HelpProvider", "ImageList", "System.Diagnostics.Process", "Timer"]),
        ("Printing", "file", ["PageSetupDialog", "PrintDialog", "System.Drawing.Printing.PrintDocument", "PrintPreviewControl", "PrintPreviewDialog"]),
        ("Dialogs", "window", ["ColorDialog", "FolderBrowserDialog", "FontDialog", "OpenFileDialog", "SaveFileDialog"]),
    ];

    /// <summary>The framework's types that aren't controls (they go to the tray).</summary>
    private static readonly HashSet<string> s_components = new(StringComparer.Ordinal)
    {
        "NotifyIcon", "ToolTip", "BindingSource", "System.ComponentModel.BackgroundWorker", "ErrorProvider", "System.IO.FileSystemWatcher", "HelpProvider", "ImageList",
        "System.Diagnostics.Process", "Timer", "PageSetupDialog", "PrintDialog", "System.Drawing.Printing.PrintDocument", "PrintPreviewDialog", "ColorDialog",
        "FolderBrowserDialog", "FontDialog", "OpenFileDialog", "SaveFileDialog", "ContextMenuStrip",
    };

    private IReadOnlyList<WinFormsToolboxItem> _projectItems = [];
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
                if (!value)
                {
                    Disarm();
                }
            }
        }
    }

    public string? Message => _isAvailable ? null : Localizer.Get("msg.toolbox.noDesigner", "Open a form or user control in the Windows Forms designer to use the Toolbox.");

    public bool HasMessage => !_isAvailable;

    /// <summary>The project's own controls and components (from the C# host).</summary>
    public void SetProjectItems(IReadOnlyList<WinFormsToolboxItem> items)
    {
        _projectItems = items;
        Rebuild();
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
            var items = group.Where(i => Matches(i.Name)).Select(i => new ToolboxItemViewModel(i.Name, i.Type, category, i.IsControl, i.IsControl ? "window" : "gear") { Description = i.Description }).ToList();
            if (items.Count > 0)
            {
                Categories.Add(new ToolboxCategoryViewModel(category, [new ToolboxItemViewModel(pointer, string.Empty, category, false, "caret"), .. items]));
            }
        }

        foreach (var (title, icon, types) in Framework)
        {
            var category = CategoryTitle(title);
            var items = types.Select(t => (Type: t, Name: t[(t.LastIndexOf('.') + 1)..])).Where(t => Matches(t.Name))
                .Select(t => new ToolboxItemViewModel(t.Name, FullType(t.Type), category, !s_components.Contains(t.Type), icon)).ToList();
            if (items.Count > 0)
            {
                Categories.Add(new ToolboxCategoryViewModel(category, [new ToolboxItemViewModel(pointer, string.Empty, category, false, "caret"), .. items]));
            }
        }
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
