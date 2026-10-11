using System.Globalization;
using DaedalusStudio.Workbench.Build;

namespace DaedalusStudio.Workbench.Debugging.Remote;

/// <summary>
/// A machine to debug on (M14c, ADR 0039): over SSH, in a container, or in a WSL distribution. Everything runs there
/// through <c>sh -c</c> with stdin and stdout piped: listing processes, installing netcoredbg, and netcoredbg itself as
/// a pipe transport.
/// </summary>
public abstract record RemoteTarget
{
    /// <summary>A name for messages and the Processes window: <c>ssh pi@host</c>, <c>container web</c>, <c>WSL: Ubuntu</c>.</summary>
    public abstract string Display { get; }

    /// <summary>The program and arguments that run <paramref name="script"/> with <c>sh</c> on the target.</summary>
    public abstract (string Program, IReadOnlyList<string> Arguments) Shell(string script);

    /// <summary>Where netcoredbg is installed on the target (a shell expression): <see cref="Home"/>, else the target's default.</summary>
    public string DebuggerHome => Home ?? DefaultHome;

    /// <summary>Another folder for netcoredbg on the target (tests use <c>/tmp</c>).</summary>
    public string? Home { get; init; }

    protected virtual string DefaultHome => "${HOME:-/tmp}/.daedalus/netcoredbg";
}

/// <summary>
/// A machine over SSH, through the user's <c>ssh</c> (keys, agent and <c>~/.ssh/config</c> apply). Never a password
/// prompt on the console: one goes through <c>SSH_ASKPASS</c> when the shell provides it.
/// </summary>
public sealed record SshTarget(string Host, string? User = null, int? Port = null, string Program = "ssh") : RemoteTarget
{
    public override string Display => "ssh " + Destination + (Port is { } port ? ":" + port.ToString(CultureInfo.InvariantCulture) : string.Empty);

    private string Destination => User is null ? Host : User + "@" + Host;

    public override (string Program, IReadOnlyList<string> Arguments) Shell(string script)
    {
        List<string> arguments = ["-T", "-o", "ServerAliveInterval=15"];
        if (Port is { } port)
        {
            arguments.AddRange(["-p", port.ToString(CultureInfo.InvariantCulture)]);
        }

        // The remote side gets one command line, run by the user's login shell.
        arguments.AddRange([Destination, "sh -c " + WslLaunch.ShellQuote(script)]);
        return (Program, arguments);
    }

    /// <summary>Reads <c>user@host:port</c>, <c>host</c> or <c>ssh://user@host:port</c>; null when it isn't one.</summary>
    public static SshTarget? Parse(string? text, string program = "ssh")
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            value = value["ssh://".Length..].TrimEnd('/');
        }

        if (value.Length == 0 || value.Any(char.IsWhiteSpace) || value.StartsWith('-'))
        {
            return null;
        }

        string? user = null;
        var at = value.LastIndexOf('@');
        if (at >= 0)
        {
            user = value[..at];
            value = value[(at + 1)..];
        }

        string host;
        string? portText = null;
        if (value.StartsWith('['))
        {
            // [::1]:2222
            var close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return null;
            }

            host = value[1..close];
            var rest = value[(close + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':'))
                {
                    return null;
                }

                portText = rest[1..];
            }
        }
        else if (value.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0 && colon == value.LastIndexOf(':'))
        {
            host = value[..colon];
            portText = value[(colon + 1)..];
        }
        else
        {
            host = value; // a name, or an IPv6 address without a port
        }

        int? port = null;
        if (portText is not null)
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 65535)
            {
                return null;
            }

            port = parsed;
        }

        return host.Length == 0 || user is { Length: 0 } ? null : new SshTarget(host, user, port, program);
    }
}

/// <summary>
/// A running container, through <c>docker exec -i</c> or Podman's. <see cref="Tool"/> is the container CLI's command
/// line (<c>docker</c>, <c>podman</c>, or <c>wsl -d Ubuntu -- podman</c> for one inside WSL).
/// </summary>
public sealed record ContainerTarget(string Container, IReadOnlyList<string> Tool) : RemoteTarget
{
    public override string Display => $"container {Container} ({Path.GetFileNameWithoutExtension(Tool[^1])})";

    public override (string Program, IReadOnlyList<string> Arguments) Shell(string script) =>
        (Tool[0], [.. Tool.Skip(1), "exec", "-i", Container, "sh", "-c", script]);

    /// <summary>In a container, <c>HOME</c> may be unset or read-only: netcoredbg goes to <c>/tmp</c>.</summary>
    protected override string DefaultHome => "/tmp/.daedalus/netcoredbg";
}

/// <summary>A WSL distribution, from Windows (empty <see cref="Distribution"/>: the default one).</summary>
public sealed record WslTarget(string Distribution, string Program = "wsl.exe") : RemoteTarget
{
    public override string Display => "WSL: " + (Distribution.Length == 0 ? "default" : Distribution);

    public override (string Program, IReadOnlyList<string> Arguments) Shell(string script)
    {
        List<string> arguments = Distribution.Length == 0 ? [] : ["-d", Distribution];
        arguments.AddRange(["--exec", "sh", "-c", script]);
        return (Program, arguments);
    }
}
