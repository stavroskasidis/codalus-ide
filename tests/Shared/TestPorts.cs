using System.Net;
using System.Net.Sockets;

namespace Codalus.Tests;

/// <summary>
/// A port for a server a test starts later (a launch profile's URL, a process told its port). The OS's own pick, a
/// listener on port 0, comes from the ephemeral range, which outgoing connections draw their local ports from too: the
/// port is free when picked, but the server binds it seconds later (a restore and a build first), and an outgoing
/// connection of any process can hold it by then. A listener can't bind a port that a connection uses as its local
/// port, SO_REUSEADDR or not: Kestrel failed "address already in use" at its first start (Linux CI, PR #373's run
/// 43636497934; docs/ci-flakes.md). So the port comes from below every OS's ephemeral range (Linux 32768–60999, Windows
/// and macOS 49152–65535), and is checked free when picked.
/// </summary>
internal static class TestPorts
{
    private const int First = 20000;
    private const int Last = 32000;

    /// <summary>A loopback port outside the ephemeral range, free when it's picked.</summary>
    public static int Free()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var port = Random.Shared.Next(First, Last);
            try
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException)
            {
                // In use: another one.
            }
        }

        throw new InvalidOperationException($"No free loopback port between {First} and {Last} after 200 tries.");
    }
}
