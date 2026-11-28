using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Codalus.Debugging.Host.CoreClr;
using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Device;

/// <summary>
/// The in-app debugger's server (ADR 0117): the Codalus debugger's CoreCLR adapter on a TCP port of the app
/// (<see cref="InAppDebugger.PortVariable"/>), which the IDE reaches over the device's tunnel. One IDE at a time; when one
/// goes without ending the app (a detach), the next may connect. The adapter's <c>launch</c> with
/// <see cref="NetFxDebugArguments.InProcess"/> attaches to this process (<see cref="IosDeviceRuntime"/>).
/// <para>
/// The app's start is held, in the profiler's <c>Initialize</c> before any managed code runs, until the IDE has set its
/// breakpoints (<c>configurationDone</c>) or <see cref="InAppDebugger.HoldVariable"/> seconds have passed: iOS ends an app
/// that takes 20 seconds to launch. Breakpoints set after that bind when their code loads, as in any attach.
/// </para>
/// </summary>
internal static class InAppServer
{
    private static int s_started;

    /// <summary>Since the debugger started, in the profiler's Initialize: the lines say when each step came.</summary>
    private static readonly System.Diagnostics.Stopwatch s_clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>A line in the app's console (the run's output): <c>[codalus-debugger +12 ms] …</c>.</summary>
    public static void Log(string text) =>
        Console.Error.WriteLine($"[codalus-debugger +{s_clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms] {text}");

    public static void Start()
    {
        if (Interlocked.Exchange(ref s_started, 1) == 1)
        {
            return;
        }

        if (!int.TryParse(Environment.GetEnvironmentVariable(InAppDebugger.PortVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is <= 0 or > 65535)
        {
            Log($"no port to serve on ({InAppDebugger.PortVariable}): the app runs without the debugger.");
            return;
        }

        var hold = int.TryParse(Environment.GetEnvironmentVariable(InAppDebugger.HoldVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : InAppDebugger.DefaultHoldSeconds;
        var configured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Start();
        Log($"waiting for the IDE on port {port.ToString(CultureInfo.InvariantCulture)}");
        new Thread(() => Serve(listener, configured)) { IsBackground = true, Name = "Codalus debugger" }.Start();

        if (hold > 0 && !configured.Task.Wait(TimeSpan.FromSeconds(hold)))
        {
            Log($"the IDE didn't set its breakpoints in {hold.ToString(CultureInfo.InvariantCulture)} s: the app starts, and breakpoints bind when the IDE has connected.");
        }
        else
        {
            Log(hold > 0 ? "the IDE has set its breakpoints: the app starts" : "the app starts (no hold)");
        }
    }

    private static void Serve(TcpListener listener, TaskCompletionSource configured)
    {
        while (true)
        {
            try
            {
                using var client = listener.AcceptTcpClient();
                client.NoDelay = true;
                Log($"the IDE connected from {client.Client.RemoteEndPoint}");
                using var stream = client.GetStream();
                var trace = Environment.GetEnvironmentVariable(InAppDebugger.TraceVariable) == "1" ? Console.Error : null;
                NetFxAdapter.RunAsync(stream, stream, Console.Error, new DbgShimDebugger(), trace,
                        configured: session => session.ContinueWith(_ => { Log("configurationDone"); configured.TrySetResult(); }, TaskScheduler.Default))
                    .GetAwaiter().GetResult();
                Log("the IDE disconnected");
            }
            catch (Exception ex)
            {
                Log("the session ended: " + ex.Message);
            }
        }
    }
}
