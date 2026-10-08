using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Daedalus.Diagnostics.BenchEvents;
using Daedalus.LanguageServices.Client;
using Daedalus.LanguageServices.Protocol;
using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace Daedalus.Spike.Avalonia;

/// <summary>Owns the language host connection, the dock layout and the open documents.</summary>
public sealed class ShellViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly SpikeOptions _options;
    private readonly IDocumentDock _documents;
    private LanguageHostClient? _host;
    private string _status = "Starting language service…";

    internal ShellViewModel(SpikeOptions options)
    {
        _options = options;
        Factory = new Factory();
        Explorer = new SolutionExplorerTool(this) { Id = "SolutionExplorer", Title = "Solution Explorer", CanClose = false };
        Output = new OutputTool { Id = "Output", Title = "Output", CanClose = false };

        Factory.DocumentDock(out _documents, d => d.WithId("Documents").WithIsCollapsable(false))
            .ToolDock(out var left, Alignment.Left, t => Fill(t.WithId("LeftPane"), Explorer))
            .ToolDock(out var bottom, Alignment.Bottom, t => Fill(t.WithId("BottomPane"), Output))
            .ProportionalDockSplitter(out var leftSplit)
            .ProportionalDockSplitter(out var bottomSplit)
            .ProportionalDock(out var center, Orientation.Vertical, d => d.Add(_documents, bottomSplit, bottom))
            .ProportionalDock(out var main, Orientation.Horizontal, d => d.Add(left, leftSplit, center))
            .RootDock(out var root, r => r.Add(main).WithDefaultDockable(main).WithActiveDockable(main));
        left.WithProportion(0.22);
        bottom.WithProportion(0.22);
        Factory.InitLayout(root);
        Layout = root;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // Dock's AppendTool goes through dock.Factory, which is only assigned by InitLayout; populate directly instead.
    private void Fill(IDock dock, IDockable tool)
    {
        dock.VisibleDockables = Factory.CreateList(tool);
        dock.ActiveDockable = tool;
    }

    public Factory Factory { get; }

    public IRootDock Layout { get; }

    public SolutionExplorerTool Explorer { get; }

    public OutputTool Output { get; }

    public LanguageHostClient? Host => _host;

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    internal SpikeOptions Options => _options;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var launch = HostLaunchInfo.Resolve();
        if (launch is null)
        {
            Status = "Language service host not found (set DAEDALUS_LSHOST).";
            Output.Append(Status);
            return;
        }

        var host = new LanguageHostClient(launch) { DocumentTextProvider = ProvideDocumentText };
        host.HostLog += (_, line) => Dispatcher.UIThread.Post(() => Output.Append(line));
        host.HostCrashed += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            Status = e.WillRestart ? "Language service crashed — restarting…" : "Language service crashed repeatedly and was stopped.";
            Output.Append($"[notification] {Status} (exit code {e.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"})");
        });
        host.HostRestarted += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            Status = "Language service restarted";
            Output.Append("[notification] Language service restarted; state restored.");
            foreach (var document in _documents.VisibleDockables?.OfType<EditorDocument>() ?? [])
            {
                document.Controller.ScheduleHighlight(TimeSpan.Zero);
            }
        });

        _host = host;
        await host.StartAsync(cancellationToken).ConfigureAwait(true);
        Status = "Language service ready";

        if (_options.Solution is { } solution)
        {
            Status = $"Loading {Path.GetFileName(solution)}…";
            var result = await host.OpenSolutionAsync(Path.GetFullPath(solution), cancellationToken).ConfigureAwait(true);
            Explorer.SetRoot(result.Root);
            foreach (var diagnostic in result.Diagnostics)
            {
                Output.Append($"[workspace] {diagnostic}");
            }

            BenchEvents.Emit(BenchEventNames.SolutionLoaded);
            Status = "Ready";
        }

        if (_options.OpenFile is { } file)
        {
            await OpenDocumentAsync(Path.GetFullPath(file), cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task OpenDocumentAsync(string path, CancellationToken cancellationToken)
    {
        var existing = _documents.VisibleDockables?.OfType<EditorDocument>().FirstOrDefault(d => d.Path == path);
        if (existing is not null)
        {
            Factory.SetActiveDockable(existing);
            return;
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true);
        var document = new EditorDocument(this, path, text);
        Factory.AddDockable(_documents, document);
        Factory.SetActiveDockable(document);
        Factory.SetFocusedDockable(_documents, document);
        if (_host is { } host)
        {
            await host.DidOpenAsync(path, text, document.Controller.Version, cancellationToken).ConfigureAwait(true);
        }
    }

    private (string Text, int Version)? ProvideDocumentText(string path) =>
        Dispatcher.UIThread.Invoke(() =>
            _documents.VisibleDockables?.OfType<EditorDocument>().FirstOrDefault(d => d.Path == path) is { } document
                ? (document.Text.Text, document.Controller.Version)
                : ((string, int)?)null);

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

public sealed class OutputTool : Tool
{
    private const int MaxLines = 2000;

    public ObservableCollection<string> Lines { get; } = [];

    public void Append(string line)
    {
        Lines.Add(line);
        if (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }
    }
}

public sealed class SolutionExplorerTool(ShellViewModel shell) : Tool
{
    public ObservableCollection<SolutionNodeViewModel> Roots { get; } = [];

    public void SetRoot(SolutionNode root)
    {
        Roots.Clear();
        Roots.Add(new SolutionNodeViewModel(root) { IsExpanded = true });
    }

    public void Open(SolutionNodeViewModel node)
    {
        if (node.Node is { Kind: SolutionNodeKind.Document, Path: { } path })
        {
            _ = shell.OpenDocumentAsync(path, CancellationToken.None);
        }
    }
}

public sealed class SolutionNodeViewModel(SolutionNode node)
{
    public SolutionNode Node { get; } = node;

    public string Name => Node.Name;

    public string Glyph => Node.Kind switch
    {
        SolutionNodeKind.Solution => "◆",
        SolutionNodeKind.Project => "▣",
        SolutionNodeKind.Folder => "▸",
        _ => "·",
    };

    public bool IsExpanded { get; set; } = node.Kind is SolutionNodeKind.Solution or SolutionNodeKind.Project;

    public IReadOnlyList<SolutionNodeViewModel> Children { get; } = [.. node.Children.Select(c => new SolutionNodeViewModel(c))];
}
