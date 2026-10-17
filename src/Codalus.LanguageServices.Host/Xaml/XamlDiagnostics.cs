using System.Collections.Concurrent;
using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Xaml;

/// <summary>
/// Publishes open XAML files' live diagnostics (M19a, ADR 0047): 250 ms after an edit of the file, and 600 ms after an
/// edit of anything else (a view model's property renamed in C# changes what a binding path may say).
/// </summary>
internal sealed class XamlDiagnostics(WorkspaceManager workspace, Func<PublishDiagnosticsParams, Task> publish, Action<string> log) : IDisposable
{
    private static readonly TimeSpan s_ownEdit = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan s_otherEdit = TimeSpan.FromMilliseconds(600);

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();

    public void DocumentChanged(string path) => Schedule(Path.GetFullPath(path), s_ownEdit);

    /// <summary>Something else changed (C#, projects): the open XAML files again.</summary>
    public void OthersChanged()
    {
        foreach (var path in workspace.OpenPaths.Where(XamlContext.IsXaml))
        {
            Schedule(path, s_otherEdit);
        }
    }

    public void DocumentClosed(string path)
    {
        path = Path.GetFullPath(path);
        if (_pending.TryRemove(path, out var previous))
        {
            previous.Cancel();
        }

        _ = publish(new PublishDiagnosticsParams(DocumentUris.FromPath(path), null, []));
    }

    private void Schedule(string path, TimeSpan delay)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (_pending.TryGetValue(path, out var previous))
        {
            previous.Cancel();
        }

        _pending[path] = cancellation;
        var token = cancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                var version = workspace.Version(path);
                if (await XamlContext.CreateAsync(workspace.CurrentSolution, path, workspace.OpenText(path), token).ConfigureAwait(false) is not { } context)
                {
                    return;
                }

                var resources = context.IsWpfStyle ? await XamlResources.ScopeAsync(context, token).ConfigureAwait(false) : null;
                var diagnostics = XamlAnalysis.Diagnose(context, resources);
                token.ThrowIfCancellationRequested();
                await publish(new PublishDiagnosticsParams(DocumentUris.FromPath(path), version, diagnostics)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log($"XAML diagnostics failed for {Path.GetFileName(path)}: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
