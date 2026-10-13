using System.Runtime.InteropServices;
using System.Text.Json;
using Codalus.Interactive.Protocol;
using Codalus.Rpc;

namespace Codalus.Interactive.Host;

/// <summary>
/// The REPL host's request handlers (docs/interactive-protocol.md). Submissions run one at a time; editor requests
/// (completion, tokens) run alongside them on the last completed state.
/// </summary>
internal sealed class ReplServer : IAsyncDisposable
{
    private readonly JsonRpcConnection _connection;
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _executing = new(1, 1);
    private readonly SemaphoreSlim _send = new(1, 1);
    private ReplSession? _session;
    private int _submission;

    public ReplServer(JsonRpcConnection connection)
    {
        _connection = connection;
        Out = new ReplOutput(connection, _send, () => Volatile.Read(ref _submission), isError: false);
        Error = new ReplOutput(connection, _send, () => Volatile.Read(ref _submission), isError: true);
    }

    public ReplOutput Out { get; }

    public ReplOutput Error { get; }

    public Task ExitRequested => _exit.Task;

    public void Register()
    {
        var json = InteractiveJsonContext.Default;
        _connection.OnRequest(InteractiveMethods.Reset, json.ResetParams, json.ResetResult, ResetAsync);
        _connection.OnRequest(InteractiveMethods.Execute, json.ExecuteParams, json.ExecuteResult, ExecuteAsync);
        _connection.OnRequest(InteractiveMethods.IsComplete, json.CodeParams, json.IsCompleteResult,
            (p, _) => Task.FromResult(new IsCompleteResult(ReplLanguage.IsComplete(p.Code))));
        _connection.OnRequest(InteractiveMethods.Completion, json.CodePositionParams, json.ReplCompletionResult,
            async (p, ct) => ReplLanguage.Completion(await SessionAsync(ct).ConfigureAwait(false), p.Code, p.Position, ct));
        _connection.OnRequest(InteractiveMethods.SignatureHelp, json.CodePositionParams, json.SignatureHelp,
            async (p, ct) => ReplLanguage.SignatureHelp(await SessionAsync(ct).ConfigureAwait(false), p.Code, p.Position, ct)!);
        _connection.OnRequest(InteractiveMethods.Tokens, json.CodeParams, json.TokensResult,
            async (p, ct) => new TokensResult(ReplLanguage.Tokens(await SessionAsync(ct).ConfigureAwait(false), p.Code, ct)));
        _connection.OnRequest(InteractiveMethods.Shutdown, (_, _) => Task.FromResult<JsonElement?>(null));
        _connection.OnNotification(InteractiveMethods.Exit, json.EmptyObject, _ => _exit.TrySetResult());
    }

    private async Task<ResetResult> ResetAsync(ResetParams parameters, CancellationToken cancellationToken)
    {
        await _executing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _submission, 0);
            var (session, errors) = await ReplSession.CreateAsync(parameters, Message, cancellationToken).ConfigureAwait(false);
            _session = session;
            await FlushAsync().ConfigureAwait(false);
            return new ResetResult(RuntimeInformation.FrameworkDescription, errors);
        }
        finally
        {
            _executing.Release();
        }
    }

    private async Task<ExecuteResult> ExecuteAsync(ExecuteParams parameters, CancellationToken cancellationToken)
    {
        await _executing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _session ??= (await ReplSession.CreateAsync(new ResetParams(Environment.CurrentDirectory, [], [], []), Message, cancellationToken).ConfigureAwait(false)).Session;
            Volatile.Write(ref _submission, parameters.Submission);

            // Scripts run on a thread-pool thread so a synchronous one doesn't hold the connection's dispatcher.
            ExecuteResult result;
            try
            {
                result = await Task.Run(() => _session.ExecuteAsync(parameters.Code, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
            {
                // A failure of the host itself (not of the script, which RunAsync catches): report it, keep the state.
                result = new ExecuteResult(false, null, e.ToString(), [], 0);
            }

            await FlushAsync().ConfigureAwait(false);
            return result;
        }
        finally
        {
            _executing.Release();
        }
    }

    private async Task<ReplSession> SessionAsync(CancellationToken cancellationToken)
    {
        if (_session is { } session)
        {
            return session;
        }

        await _executing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _session ??= (await ReplSession.CreateAsync(new ResetParams(Environment.CurrentDirectory, [], [], []), Message, cancellationToken).ConfigureAwait(false)).Session;
        }
        finally
        {
            _executing.Release();
        }
    }

    private void Message(string text) => Out.WriteLine(text);

    private async Task FlushAsync()
    {
        await Out.FlushAsync().ConfigureAwait(false);
        await Error.FlushAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        Out.Dispose();
        Error.Dispose();
        _executing.Dispose();
        _send.Dispose();
        return ValueTask.CompletedTask;
    }
}
