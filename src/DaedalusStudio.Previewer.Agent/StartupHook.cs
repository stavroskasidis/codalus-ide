using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DaedalusStudio.Previewer.Agent;

/// <summary>
/// A .NET startup hook (<c>DOTNET_STARTUP_HOOKS</c>, M19a, ADR 0048) that Daedalus Studio loads into Avalonia's
/// designer host when it previews XAML. Avalonia's remote protocol carries XAML in and frames out, but nothing about the
/// elements, so this agent answers what the protocol can't: which element is at a point of the preview (click to
/// select in the XAML), where an element is (the selection's adorner) and the theme variant. It connects back to the
/// IDE at <c>127.0.0.1:$DAEDALUS_PREVIEW_AGENT_PORT</c>, says <c>hello $DAEDALUS_PREVIEW_AGENT_TOKEN</c>, and answers
/// tab-separated request lines. Without the variables it does nothing. Avalonia is reached by reflection, so one agent
/// serves every Avalonia version the user's app may use.
/// With <c>DAEDALUS_LIVE_AGENT_PORT</c> and <c>_TOKEN</c> instead, the same agent serves a running app's Live Visual
/// Tree and Live Property Explorer (M19b, ADR 0052): <see cref="LiveAgent"/>, which also sends lines of its own
/// (<c>event …</c>).
/// </summary>
internal static class StartupHook
{
    public const string PortVariable = "DAEDALUS_PREVIEW_AGENT_PORT";
    public const string TokenVariable = "DAEDALUS_PREVIEW_AGENT_TOKEN";
    public const string LivePortVariable = "DAEDALUS_LIVE_AGENT_PORT";
    public const string LiveTokenVariable = "DAEDALUS_LIVE_AGENT_TOKEN";

    public static void Initialize()
    {
        var live = int.TryParse(Environment.GetEnvironmentVariable(LivePortVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var livePort) && livePort > 0;
        var portText = Environment.GetEnvironmentVariable(live ? LivePortVariable : PortVariable);
        var token = Environment.GetEnvironmentVariable(live ? LiveTokenVariable : TokenVariable) ?? string.Empty;
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port <= 0)
        {
            return;
        }

        // The user's app's own child processes don't get an agent.
        foreach (var variable in new[] { PortVariable, TokenVariable, LivePortVariable, LiveTokenVariable })
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        var thread = new Thread(() => Run(port, token, live)) { IsBackground = true, Name = live ? "Daedalus live agent" : "Daedalus previewer agent" };
        thread.Start();
    }

    private static void Run(int port, string token, bool live)
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
            Func<string[], string> handle = live ? new LiveAgent(Send).Handle : new PreviewAgent().Handle;
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
                    reply = "error\t" + (ex.InnerException ?? ex).Message.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
                }

                Send(parts[0] + "\t" + reply);
            }
        }
        catch (Exception ex)
        {
            // The IDE went away or never listened: the preview works without the agent, only without selection.
            Console.Error.WriteLine("Daedalus previewer agent: " + ex.Message);
        }
    }
}
