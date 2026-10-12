using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DaedalusStudio.Previewer.Agent;

/// <summary>
/// A .NET startup hook (<c>DOTNET_STARTUP_HOOKS</c>, M19a, ADR 0047) that Daedalus Studio loads into Avalonia's
/// designer host when it previews XAML. Avalonia's remote protocol carries XAML in and frames out, but nothing about the
/// elements, so this agent answers what the protocol can't: which element is at a point of the preview (click to
/// select in the XAML), where an element is (the selection's adorner) and the theme variant. It connects back to the
/// IDE at <c>127.0.0.1:$DAEDALUS_PREVIEW_AGENT_PORT</c>, says <c>hello $DAEDALUS_PREVIEW_AGENT_TOKEN</c>, and answers
/// tab-separated request lines. Without the variables it does nothing. Avalonia is reached by reflection, so one agent
/// serves every Avalonia version the user's app may use.
/// </summary>
internal static class StartupHook
{
    public const string PortVariable = "DAEDALUS_PREVIEW_AGENT_PORT";
    public const string TokenVariable = "DAEDALUS_PREVIEW_AGENT_TOKEN";

    public static void Initialize()
    {
        var portText = Environment.GetEnvironmentVariable(PortVariable);
        var token = Environment.GetEnvironmentVariable(TokenVariable) ?? string.Empty;
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port <= 0)
        {
            return;
        }

        // The user's app's own child processes don't get an agent.
        Environment.SetEnvironmentVariable(PortVariable, null);
        Environment.SetEnvironmentVariable(TokenVariable, null);
        var thread = new Thread(() => Run(port, token)) { IsBackground = true, Name = "Daedalus previewer agent" };
        thread.Start();
    }

    private static void Run(int port, string token)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            client.NoDelay = true;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            writer.WriteLine("hello\t" + token);
            var agent = new PreviewAgent();
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
                    reply = "ok\t" + agent.Handle(parts);
                }
                catch (Exception ex)
                {
                    reply = "error\t" + (ex.InnerException ?? ex).Message.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
                }

                writer.WriteLine(parts[0] + "\t" + reply);
            }
        }
        catch (Exception ex)
        {
            // The IDE went away or never listened: the preview works without the agent, only without selection.
            Console.Error.WriteLine("Daedalus previewer agent: " + ex.Message);
        }
    }
}
