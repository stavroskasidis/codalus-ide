using System.Globalization;
using Codalus.Previewer.Wpf;

/// <summary>
/// The live agent's entry (M40b, ADR 0072): a .NET startup hook (<c>DOTNET_STARTUP_HOOKS</c>) that the IDE adds to a WPF
/// app it runs or debugs, as it adds Avalonia's agent to an Avalonia app (ADR 0052). With
/// <c>CODALUS_LIVE_AGENT_PORT</c> and <c>_TOKEN</c> it connects back on a thread of its own and serves the Live Visual
/// Tree and Live Property Explorer (<see cref="LiveAgent"/>); without them it does nothing. The app's own child
/// processes don't inherit the variables.
/// </summary>
internal static class StartupHook
{
    public const string PortVariable = "CODALUS_LIVE_AGENT_PORT";
    public const string TokenVariable = "CODALUS_LIVE_AGENT_TOKEN";

    public static void Initialize()
    {
        // The Codalus debugger's launcher runs with the program's environment: the variables are the app's (M64b).
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "codalus-debughost")
        {
            return;
        }

        if (!int.TryParse(Environment.GetEnvironmentVariable(PortVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port <= 0)
        {
            return;
        }

        var token = Environment.GetEnvironmentVariable(TokenVariable) ?? string.Empty;
        Environment.SetEnvironmentVariable(PortVariable, null);
        Environment.SetEnvironmentVariable(TokenVariable, null);

        // Failed bindings (M64) from the app's first one on: WPF's trace source needs no UI thread.
        WpfBindings.Install(null);

        // XAML breakpoints (M64b): the launch's list.
        WpfBreak.ConfigureFromEnvironment();

        // A background thread that never touches WPF before the app has made its Application on its own UI thread.
        var thread = new Thread(() => AgentLink.Serve(port, token, send =>
        {
            WpfBindings.Install(send);
            WpfBreak.Connect(send);
            return new LiveAgent(send).Handle;
        }, "WPF live agent"))
        {
            IsBackground = true,
            Name = "Codalus WPF live agent",
        };
        thread.Start();
    }
}
