using System.Net.Sockets;
using System.Text;
using System.Windows.Threading;

namespace Codalus.Previewer.Wpf;

/// <summary>
/// The agent protocol of the previewer and the live tools (ADR 0048, 0052), the IDE's side being
/// <c>PreviewerSession</c> and <c>LiveSession</c>: connect back to the IDE's loopback port, say <c>hello token</c>,
/// then answer tab-separated request lines <c>id command args…</c> with <c>id ok payload</c> or <c>id error message</c>.
/// Lines of the agent's own (<c>event …</c>) go out through the send function.
/// </summary>
internal static class AgentLink
{
    private static readonly TimeSpan s_uiTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Serves requests until the IDE closes the connection (on the calling thread; never throws).</summary>
    public static void Serve(int port, string token, Func<Action<string>, Func<string[], string>> handler, string what)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            client.NoDelay = true;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            var gate = new object();
            void Send(string text)
            {
                lock (gate)
                {
                    writer.WriteLine(text);
                }
            }

            Send("hello\t" + token);
            var handle = handler(Send);
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split('\t');
                if (parts.Length < 2)
                {
                    continue;
                }

                string reply;
                try
                {
                    reply = "ok\t" + handle(parts);
                }
                catch (Exception ex)
                {
                    reply = "error\t" + WpfTree.Clean((ex.InnerException ?? ex).Message);
                }

                Send(parts[0] + "\t" + reply);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // The IDE went away or never listened: the app (or the preview) goes on without the agent.
            Console.Error.WriteLine("Codalus " + what + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on <paramref name="dispatcher"/>'s thread at <paramref name="priority"/> and waits
    /// for it (at most 10 s; a thread paused in the debugger doesn't answer). The work's exception comes back here,
    /// never to the app's dispatcher.
    /// </summary>
    public static string OnUiThread(Dispatcher dispatcher, Func<string> work, DispatcherPriority priority)
    {
        string? result = null;
        Exception? error = null;
        var operation = dispatcher.BeginInvoke(priority, new Action(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }));
        if (operation.Wait(s_uiTimeout) != DispatcherOperationStatus.Completed)
        {
            operation.Abort();
            throw new TimeoutException("The UI thread didn't answer within 10 s (is it paused in the debugger?).");
        }

        return error is null ? result ?? string.Empty : throw new InvalidOperationException(error.Message, error);
    }
}
