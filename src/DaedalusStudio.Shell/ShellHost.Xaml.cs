using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Previewer;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Shell;

/// <summary>
/// Avalonia XAML (M19a, ADR 0046 and 0047): the XAML Previewer tool window, which follows the active <c>.axaml</c>
/// editor while it is open (the editor's text, 150 ms after the last edit), rebuilds when the code changes, selects in
/// the XAML what is clicked in the preview and outlines in the preview the element at the caret; and the XAML
/// compiler's errors of the last build as squiggles in their files.
/// </summary>
public sealed partial class ShellHost
{
    private static readonly TimeSpan s_previewDebounce = TimeSpan.FromMilliseconds(150);

    private CodeDocument? _previewed;
    private PreviewTarget? _previewTarget;
    private CancellationTokenSource? _previewEdit;
    private CancellationTokenSource? _previewCaret;
    private (int Version, XamlLogicalNode[] Nodes)? _previewTree;
    private readonly HashSet<string> _buildXamlFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _previewHighlightScheduled;

    public PreviewService Previewer { get; private set; } = null!;

    public XamlPreviewViewModel XamlPreview { get; private set; } = null!;

    public OutputChannel PreviewerLog => Workbench.Output.GetOrCreate("daedalus.xamlPreview", "XAML Previewer");

    /// <summary>The XAML file the previewer shows, or null.</summary>
    public string? PreviewedPath => _previewed?.Path;

    private void InitializeXaml()
    {
        Previewer = new PreviewService(Solutions, path => Trust.IsTrusted(path), BuildForPreviewAsync, PreviewPropertiesAsync)
        {
            StartupProject = () => Runs.StartupProject,
        };
        Previewer.Log += (_, line) => PreviewerLog.AppendLine(line);
        Previewer.FrameReceived += (_, frame) => Ui.Post(() =>
        {
            XamlPreview.ShowFrame(frame);
            SchedulePreviewHighlight();
        });
        Previewer.SessionEnded += (_, _) => Ui.Post(() => XamlPreview.Status = "The previewer exited. Refresh to start it again (the XAML Previewer output has its log).");
        Previewer.StageChanged += (_, stage) => Ui.Post(() =>
        {
            var name = _previewed?.Name ?? "the file";
            XamlPreview.Status = stage switch
            {
                PreviewStage.Building => $"Building for the preview of {name}…",
                PreviewStage.Starting => "Starting the previewer…",
                PreviewStage.Rendering => $"Rendering {name}…",
                PreviewStage.Ready => $"{name}: click an element to select it in the XAML.",
                _ => XamlPreview.Status,
            };
        });

        XamlPreview = new XamlPreviewViewModel
        {
            RefreshRequested = () => RefreshPreviewAsync(rebuild: true),
            ScaleChanged = async scale =>
            {
                await Previewer.SetScaleAsync(scale, CancellationToken.None).ConfigureAwait(true);
                await RefreshPreviewAsync(rebuild: false).ConfigureAwait(true);
            },
            ThemeChanged = theme => Previewer.SetThemeAsync(theme, CancellationToken.None),
            Clicked = SelectFromPreviewAsync,
            PointerMoved = async (x, y) =>
            {
                if (Previewer.Session is { HasExited: false } session)
                {
                    try
                    {
                        await session.PointerMovedAsync(x, y, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (PreviewerException)
                    {
                    }
                }
            },
        };

        Builds.Finished += (_, _) => Ui.Post(ShowXamlBuildDiagnostics);
        Solutions.SolutionChanged += (_, _) => Ui.Post(() =>
        {
            // Another solution (or none): the designer host of the last one goes.
            _previewTarget = null;
            if (!Solutions.IsOpen && _previewed is not null)
            {
                _previewed.ContentChanged -= OnPreviewedChanged;
                _previewed = null;
                XamlPreview.Clear("Open an .axaml file to preview it.");
            }

            _ = Previewer.InvalidateAsync();
        });
    }

    private async Task<bool> BuildForPreviewAsync(string project, CancellationToken cancellationToken)
    {
        var result = await Builds.BuildAsync("Build", project, cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    private async Task<IReadOnlyDictionary<string, string>> PreviewPropertiesAsync(string project, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var result = await ProjectManagement.GetPropertiesAsync(project, names, null, cancellationToken).ConfigureAwait(false);
        return result.Properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ the tool window

    /// <summary>Opens the XAML Previewer next to the editor and previews the active <c>.axaml</c> file.</summary>
    public void ShowXamlPreview()
    {
        Layout.ShowToolWindow(XamlPreviewViewModel.ToolWindowId, focus: false);
        FollowXamlEditor(force: true);
    }

    /// <summary>While the previewer is open, it shows the active XAML editor's file.</summary>
    private void FollowXamlEditor(bool force = false)
    {
        if (Layout is null || !Layout.IsVisible(XamlPreviewViewModel.ToolWindowId))
        {
            return;
        }

        if (ActiveEditor is not { Model: { LanguageId: AxamlLanguageId } model } || (model == _previewed && !force))
        {
            return;
        }

        Preview(model);
    }

    private void Preview(CodeDocument model)
    {
        if (_previewed != model)
        {
            if (_previewed is { } old)
            {
                old.ContentChanged -= OnPreviewedChanged;
            }

            _previewed = model;
            _previewTarget = null;
            _previewTree = null;
            model.ContentChanged += OnPreviewedChanged;
            XamlPreview.Clear($"Preparing the preview of {model.Name}…");
            XamlPreview.FileName = model.Name;
        }

        _ = RefreshPreviewAsync(rebuild: false);
    }

    private void OnPreviewedChanged(object? sender, CodeDocumentChangedEventArgs e)
    {
        _previewTree = null;
        var cancellation = Restart(ref _previewEdit);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(s_previewDebounce, cancellation.Token).ConfigureAwait(false);
                await Ui.RunAsync(() => RefreshPreviewAsync(rebuild: false)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private static CancellationTokenSource Restart(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source = new CancellationTokenSource();
        return source;
    }

    /// <summary>Renders the previewed file's current text: resolves its project and app first, builds when stale.</summary>
    public async Task RefreshPreviewAsync(bool rebuild)
    {
        if (_previewed is not { } model)
        {
            FollowXamlEditor(force: true);
            return;
        }

        try
        {
            if (rebuild)
            {
                await Previewer.InvalidateAsync().ConfigureAwait(true);
                _previewTarget = null;
            }

            _previewTarget ??= await Previewer.ResolveAsync(model.Path, CancellationToken.None).ConfigureAwait(true);
            var text = model.Text.Text;
            var result = await Previewer.ShowAsync(_previewTarget, text, CancellationToken.None).ConfigureAwait(true);
            if (model != _previewed)
            {
                return;
            }

            XamlPreview.Error = result.Error is { } error
                ? (result.Line is { } line ? $"Line {line}{(result.Column is { } column ? $", column {column}" : string.Empty)}: " : string.Empty) + error
                : null;
        }
        catch (PreviewerException ex)
        {
            if (model == _previewed)
            {
                XamlPreview.Status = ex.Message;
                PreviewerLog.AppendLine(ex.Message);
            }
        }
        catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or InvalidOperationException or IOException)
        {
            XamlPreview.Status = "The preview failed: " + ex.Message;
            PreviewerLog.AppendLine(ex.ToString());
        }
    }

    /// <summary>A save in one of the preview's projects (code-behind, another XAML file): rebuild and render again.</summary>
    private void OnSavedForPreview(string path)
    {
        if (_previewTarget is not { } target || _previewed is null || SolutionService.PathComparer.Equals(path, target.XamlPath))
        {
            return;
        }

        var folders = target.Projects.Select(p => Path.GetDirectoryName(p)! + Path.DirectorySeparatorChar);
        if ((path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            && folders.Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
        {
            PreviewerLog.AppendLine($"{Path.GetFileName(path)} changed: rebuilding for the preview.");
            _ = RefreshPreviewAsync(rebuild: true);
        }
    }

    // ------------------------------------------------------------------ selection both ways

    private async Task<XamlLogicalNode[]> PreviewTreeAsync(CodeDocument model)
    {
        if (_previewTree is { } cached && cached.Version == model.Version)
        {
            return cached.Nodes;
        }

        var version = model.Version;
        if (Languages.RunningClient(AxamlLanguageId) is not { } client
            || await client.XamlLogicalTreeAsync(model.Uri, CancellationToken.None).ConfigureAwait(true) is not { } tree)
        {
            return [];
        }

        _previewTree = (version, tree.Nodes);
        return tree.Nodes;
    }

    /// <summary>A click in the preview: the element under it, selected (its start tag) in the XAML.</summary>
    public async Task SelectFromPreviewAsync(double x, double y)
    {
        if (_previewed is not { } model || Previewer.Session is not { HasExited: false } session)
        {
            return;
        }

        try
        {
            var hit = await session.HitTestAsync(x, y, CancellationToken.None).ConfigureAwait(true);
            if (hit is null)
            {
                return;
            }

            var node = PreviewSelection.NodeFor(await PreviewTreeAsync(model).ConfigureAwait(true), hit.Path);
            if (node is null || model != _previewed)
            {
                return;
            }

            var editor = Editors.FirstOrDefault(e => e.Model == model && e.Controller is not null);
            if (editor?.Controller is { } controller)
            {
                var start = model.OffsetAt(node.Tag.Start);
                var end = model.OffsetAt(node.Tag.End);
                controller.Editor.Select(start, Math.Max(0, end - start));
                controller.Editor.ScrollTo(node.Tag.Start.Line + 1, node.Tag.Start.Character + 1);
                Layout.OpenDocument(editor, preview: false, focus: true);
            }
        }
        catch (PreviewerException ex)
        {
            PreviewerLog.AppendLine(ex.Message);
        }
    }

    /// <summary>The caret moved in the previewed editor: outline its element in the preview (at most every 100 ms).</summary>
    private void OnPreviewCaretChanged(object? sender, EventArgs e)
    {
        if (_previewed is not null && ActiveEditor?.Model == _previewed)
        {
            SchedulePreviewHighlight();
        }
    }

    private void SchedulePreviewHighlight()
    {
        if (_previewHighlightScheduled)
        {
            return;
        }

        _previewHighlightScheduled = true;
        var cancellation = Restart(ref _previewCaret);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
                await Ui.RunAsync(HighlightCaretElementAsync).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _previewHighlightScheduled = false;
            }
        });
    }

    private async Task HighlightCaretElementAsync()
    {
        if (_previewed is not { } model || Previewer.Session is not { HasExited: false } session
            || Editors.FirstOrDefault(e => e.Model == model && e.Controller is not null)?.Controller is not { } controller)
        {
            return;
        }

        var at = PreviewSelection.At(await PreviewTreeAsync(model).ConfigureAwait(true), model.PositionAt(controller.Editor.CaretOffset));
        if (at is null)
        {
            XamlPreview.Selection = null;
            return;
        }

        try
        {
            XamlPreview.Selection = await session.BoundsAsync(at.Value.Path, CancellationToken.None).ConfigureAwait(true);
        }
        catch (PreviewerException ex)
        {
            PreviewerLog.AppendLine(ex.Message);
        }
    }

    // ------------------------------------------------------------------ the XAML compiler's errors

    /// <summary>The last build's AVLN errors as squiggles in their .axaml files, until the file changes or the next build.</summary>
    private void ShowXamlBuildDiagnostics()
    {
        var byFile = XamlBuildDiagnostics.ByFile(Builds.Diagnostics);
        foreach (var stale in _buildXamlFiles.Where(f => !byFile.ContainsKey(f)).ToList())
        {
            Languages.Diagnostics.Set(stale, XamlBuildDiagnostics.Source, null, []);
            _buildXamlFiles.Remove(stale);
        }

        foreach (var (file, diagnostics) in byFile)
        {
            Languages.Diagnostics.Set(file, XamlBuildDiagnostics.Source, null, diagnostics);
            _buildXamlFiles.Add(file);
        }
    }

    /// <summary>An edited .axaml file's build errors no longer point where they did.</summary>
    private void ClearXamlBuildDiagnostics(string path)
    {
        if (_buildXamlFiles.Remove(path))
        {
            Languages.Diagnostics.Set(path, XamlBuildDiagnostics.Source, null, []);
        }
    }

    private async ValueTask DisposeXamlAsync()
    {
        _previewEdit?.Cancel();
        _previewCaret?.Cancel();
        await Previewer.DisposeAsync().ConfigureAwait(false);
    }
}
