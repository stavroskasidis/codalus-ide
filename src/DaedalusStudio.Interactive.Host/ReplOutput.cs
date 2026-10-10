using System.Text;
using DaedalusStudio.Interactive.Protocol;
using DaedalusStudio.Rpc;

namespace DaedalusStudio.Interactive.Host;

/// <summary>
/// <c>Console.Out</c> (or <c>Console.Error</c>) of scripts: text goes to the shell as <c>repl/output</c>, tagged with the
/// running submission. Complete lines are sent at once (the writing thread waits, as it would on a console); a partial
/// line goes after a short delay or when the submission ends.
/// </summary>
internal sealed class ReplOutput : TextWriter
{
    private static readonly TimeSpan PartialLineDelay = TimeSpan.FromMilliseconds(100);

    private readonly JsonRpcConnection _connection;
    private readonly SemaphoreSlim _send;
    private readonly Func<int> _submission;
    private readonly bool _isError;
    private readonly StringBuilder _buffer = new();
    private readonly Lock _gate = new();
    private readonly Timer _timer;

    public ReplOutput(JsonRpcConnection connection, SemaphoreSlim send, Func<int> submission, bool isError)
    {
        _connection = connection;
        _send = send;
        _submission = submission;
        _isError = isError;
        _timer = new Timer(_ => _ = FlushAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        bool lineEnded;
        lock (_gate)
        {
            _buffer.Append(value);
            lineEnded = value == '\n';
        }

        Schedule(lineEnded);
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        lock (_gate)
        {
            _buffer.Append(value);
        }

        Schedule(value.Contains('\n', StringComparison.Ordinal));
    }

    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    public override void WriteLine(string? value) => Write(value + "\n");

    public override void Flush() => FlushAsync().GetAwaiter().GetResult();

    /// <summary>Sends whatever is buffered.</summary>
    public override async Task FlushAsync()
    {
        // The buffer is taken under the send lock: a flush at the end of a submission then waits for a timer's flush
        // that is still sending, so output always reaches the shell before the submission's result.
        await _send.WaitAsync().ConfigureAwait(false);
        try
        {
            string text;
            lock (_gate)
            {
                if (_buffer.Length == 0)
                {
                    return;
                }

                text = _buffer.ToString();
                _buffer.Clear();
            }

            await _connection.NotifyAsync(
                InteractiveMethods.Output,
                new OutputParams(_submission(), text, _isError),
                InteractiveJsonContext.Default.OutputParams,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (JsonRpcConnectionLostException)
        {
            // The shell went away; nothing to show the output in.
        }
        finally
        {
            _send.Release();
        }
    }

    private void Schedule(bool lineEnded)
    {
        if (lineEnded)
        {
            Flush();
        }
        else
        {
            _timer.Change(PartialLineDelay, Timeout.InfiniteTimeSpan);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}
