using System.Diagnostics;
using System.Globalization;
using Codalus.Platform;
using Codalus.Workbench.Ai.Acp;

namespace Codalus.Workbench.Previewer;

/// <summary>
/// An X server without a screen (Xvfb) for desktop apps an agent runs off-screen on Linux (M60b, ADR 0099): their windows
/// open there, out of the user's way, and the desktop-app tools still read and drive them through the live agent, which
/// renders each window itself. One server per IDE, started on the first off-screen run and ended with the IDE. Xvfb
/// picks a free display and says which (<c>-displayfd 1</c>); it listens on no TCP port. Elsewhere there is none:
/// Windows and macOS have no window server an app can be sent to without a session of its own, so an off-screen run
/// opens its windows as usual (the desktop-app tools still never move the user's pointer or take their focus).
/// TODO(issue): an off-screen run on Windows (a desktop of its own) and macOS.
/// </summary>
public sealed class OffScreenDisplay : IDisposable
{
    /// <summary>How long Xvfb has to say which display it took.</summary>
    private static readonly TimeSpan s_startLimit = TimeSpan.FromSeconds(15);

    private readonly Process _server;

    private OffScreenDisplay(Process server, string display)
    {
        _server = server;
        Display = display;
    }

    /// <summary>The <c>DISPLAY</c> an app gets: <c>:99</c>.</summary>
    public string Display { get; }

    /// <summary>Whether the server still runs.</summary>
    public bool IsRunning
    {
        get
        {
            try
            {
                return !_server.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Xvfb on <paramref name="path"/> (the IDE's PATH), or null: not Linux, or not installed.</summary>
    public static string? Locate(string? path) =>
        OperatingSystem.IsLinux() ? AcpAgentConnector.FindOnPath("Xvfb", path) : null;

    /// <summary>
    /// Starts Xvfb (<paramref name="xvfb"/>) and waits until it says its display; throws <see cref="InvalidOperationException"/>
    /// with why it couldn't.
    /// </summary>
    public static async Task<OffScreenDisplay> StartAsync(string xvfb, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(xvfb)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in new[] { "-displayfd", "1", "-nolisten", "tcp", "-screen", "0", "1920x1080x24" })
        {
            start.ArgumentList.Add(argument);
        }

        var server = Process.Start(start) ?? throw new InvalidOperationException("Xvfb didn't start.");
        var errors = new System.Text.StringBuilder();
        server.ErrorDataReceived += (_, e) =>
        {
            lock (errors)
            {
                if (e.Data is { } line && errors.Length < 2000)
                {
                    errors.AppendLine(line);
                }
            }
        };
        server.BeginErrorReadLine();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(s_startLimit);
            var line = await server.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(false);
            if (line is null || !int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                string said;
                lock (errors)
                {
                    said = errors.ToString().Trim();
                }

                throw new InvalidOperationException("Xvfb didn't start a display" + (said.Length > 0 ? ": " + said : "."));
            }

            return new OffScreenDisplay(server, ":" + number.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            End(server);
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Xvfb didn't start a display within {s_startLimit.TotalSeconds:0} s.", ex);
            }

            throw;
        }
    }

    private static void End(Process server)
    {
        try
        {
            if (!server.HasExited)
            {
                ProcessTree.Kill(server);
            }
        }
        catch (InvalidOperationException)
        {
        }

        server.Dispose();
    }

    public void Dispose() => End(_server);
}
