using System.Collections.Concurrent;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Rpc;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// While the solution loads (M34b), a document whose project hasn't arrived gets syntax diagnostics only: its semantic
/// errors would be false (the types of projects still loading), and the workspace isn't ready.
/// </summary>
public sealed class LoadingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-loading-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<string, Diagnostic[]> _diagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<WorkspaceStatusParams> _status = new();
    private LanguageHostClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _client = new LanguageHostClient("C# language service", ProcessLaunchInfo.FromPath(RepoPaths.HostAssembly), policy: new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) });
        _client.Diagnostics += (_, p) => _diagnostics[p.Uri] = p.Diagnostics;
        _client.WorkspaceStatus += (_, s) => _status.Enqueue(s);
        await _client.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Documents_outside_loaded_projects_get_syntax_diagnostics_until_loading_ends()
    {
        await _client.UpdateProjectsAsync([], Ct, loading: true);
        var path = Path.Combine(_root, "Orphan.cs");
        const string Text = "class Orphan { NotLoadedYet field; void M() { int x = } }";
        await File.WriteAllTextAsync(path, Text, Ct);
        var uri = DocumentUris.FromPath(path);
        await _client.DidOpenAsync(new OpenDocumentState(uri, "csharp", 1, Text), Ct);

        // The syntax error, and nothing about the type a loading project may well define.
        await LanguageFixture.WaitUntilAsync(() => _diagnostics.TryGetValue(uri, out var d) && d.Length > 0, TimeSpan.FromSeconds(60));
        await Task.Delay(2500, Ct); // the background pass ran too
        Assert.Contains(_diagnostics[uri], d => d.Code == "CS1525");
        Assert.DoesNotContain(_diagnostics[uri], d => d.Code == "CS0246");
        Assert.DoesNotContain(_status, s => s.State == WorkspaceStates.Ready);

        // Loading ended: the file really is on its own, and its semantic errors are real.
        await _client.UpdateProjectsAsync([], Ct, loading: false);
        await LanguageFixture.WaitUntilAsync(() => _diagnostics[uri].Any(d => d.Code == "CS0246"), TimeSpan.FromSeconds(60));
        await LanguageFixture.WaitUntilAsync(() => _status.Any(s => s.State == WorkspaceStates.Ready), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_Hot_Reload_session_starts_once_the_solution_has_loaded()
    {
        // M55: a baseline taken while projects were still arriving took their arrival for changed references, a
        // restart, and Continue waited on that question (arm64 CI, run 40896721482).
        await _client.UpdateProjectsAsync([], Ct, loading: true);
        var start = _client.HotReloadStartAsync("loading", [], ["Baseline"], Ct);
        await Task.Delay(1000, Ct);
        Assert.False(start.IsCompleted, "The session started while the solution was loading.");

        await _client.UpdateProjectsAsync([], Ct, loading: false);
        Assert.False(await start.WaitAsync(TimeSpan.FromSeconds(60), Ct)); // no projects to follow
    }

    [Fact]
    public async Task A_Hot_Reload_start_waiting_for_the_solution_ends_with_the_debug_session()
    {
        await _client.UpdateProjectsAsync([], Ct, loading: true);
        using var cancel = new CancellationTokenSource();
        var start = _client.HotReloadStartAsync("ended", [], ["Baseline"], cancel.Token);
        await Task.Delay(500, Ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(60), Ct));

        // The host dropped the wait: a later start is answered.
        await _client.UpdateProjectsAsync([], Ct, loading: false);
        Assert.False(await _client.HotReloadStartAsync("after", [], ["Baseline"], Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct));
    }
}
