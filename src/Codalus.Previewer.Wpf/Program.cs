namespace Codalus.Previewer.Wpf;

/// <summary>
/// The WPF previewer's designer host (M40b, ADR 0072). The IDE starts it as it starts Avalonia's designer host
/// (ADR 0048), on the previewed app's own .NET and WPF:
/// <c>dotnet exec --runtimeconfig App.runtimeconfig.json --depsfile App.deps.json codalus-wpfpreviewer.dll
/// --transport tcp-bson://127.0.0.1:port/ --session-id id --method avalonia-remote App.dll</c>. It connects back,
/// speaks Avalonia's remote protocol (XAML in, frames out) and serves the previewer agent's commands on the agent
/// port it finds in <c>CODALUS_PREVIEW_AGENT_PORT</c>.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? transport = null;
        string? sessionId = null;
        string? app = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--transport" when i + 1 < args.Length:
                    transport = args[++i];
                    break;
                case "--session-id" when i + 1 < args.Length:
                    sessionId = args[++i];
                    break;
                case "--method" when i + 1 < args.Length:
                    i++;
                    break;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        app = args[i];
                    }

                    break;
            }
        }

        if (transport is null || app is null || !Uri.TryCreate(transport, UriKind.Absolute, out var uri) || uri.Port <= 0)
        {
            Console.Error.WriteLine("usage: codalus-wpfpreviewer --transport tcp-bson://127.0.0.1:<port>/ --session-id <id> <app.dll>");
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The WPF previewer runs on Windows only.");
            return 3;
        }

        return new WpfDesigner(Path.GetFullPath(app)).Run(uri.Host, uri.Port, sessionId);
    }
}
