using System.Runtime.Loader;
using System.Text.Json;
using Codalus.Rpc;
using Codalus.Workbench.WinForms;

namespace Codalus.WinForms.Designer;

/// <summary>
/// The Windows Forms designer's host (M41a, ADR 0073). The IDE starts it as a supervised process on the designed
/// project's app (<c>dotnet exec --runtimeconfig App.runtimeconfig.json --depsfile App.deps.json
/// codalus-winformsdesigner.dll</c>), so it runs on the app's own .NET and Windows Forms, and speaks JSON-RPC on stdio
/// (docs/winforms-designer-protocol.md): a model in, frames and the code's changes out.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The Windows Forms designer runs on Windows only.");
            return 3;
        }

        _ = args;
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        // stdout carries the protocol; the user's code writing to Console goes to stderr (the IDE's log).
        Console.SetOut(Console.Error);

        // Design-time pixels are the code's units: no DPI scaling of the designed controls.
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var window = new HostWindow();
        using var session = new DesignerSession(window);
        AssemblyLoadContext.Default.Resolving += (context, name) => session.ResolveAssembly(context, name);

        var connection = new JsonRpcConnection(stdin, stdout);
        Register(connection, window, session);
        connection.Start();
        connection.Disconnected += (_, _) => window.BeginInvoke(Application.ExitThread);
        Application.ThreadException += (_, e) => Console.Error.WriteLine($"The designed code failed: {e.Exception}");
        Application.Run(window.Context);
        connection.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        return 0;
    }

    private static void Register(JsonRpcConnection connection, HostWindow window, DesignerSession session)
    {
        var json = WinFormsDesignerJsonContext.Default;
        connection.OnRequest(WinFormsDesignerMethods.Load, json.DesignerLoadParams, json.DesignerRenderResult, (p, _) => window.Run(() => session.Load(p)));
        connection.OnRequest(WinFormsDesignerMethods.Render, json.JsonElement, json.DesignerRenderResult, (_, _) => window.Run(session.Render));
        connection.OnRequest(WinFormsDesignerMethods.Create, json.DesignerCreateParams, json.DesignerChangeResult, (p, _) => window.Run(() => session.Create(p)));
        connection.OnRequest(WinFormsDesignerMethods.SetProperties, json.DesignerSetPropertiesParams, json.DesignerChangeResult, (p, _) => window.Run(() => session.SetProperties(p)));
        connection.OnRequest(WinFormsDesignerMethods.SetParent, json.DesignerSetParentParams, json.DesignerChangeResult, (p, _) => window.Run(() => session.SetParent(p)));
        connection.OnRequest(WinFormsDesignerMethods.Delete, json.DesignerComponentsParams, json.DesignerChangeResult, (p, _) => window.Run(() => session.Delete(p)));
        connection.OnRequest(WinFormsDesignerMethods.Properties, json.DesignerComponentsParams, json.DesignerPropertiesResult, (p, _) => window.Run(() => session.Properties(p)));
        connection.OnRequest(WinFormsDesignerMethods.Events, json.DesignerComponentsParams, json.DesignerEventsResult, (p, _) => window.Run(() => session.Events(p)));
        connection.OnRequest(WinFormsDesignerMethods.Select, async (p, _) =>
        {
            var selection = p.Deserialize(json.DesignerComponentsParams) ?? new DesignerComponentsParams([]);
            await window.Run(() =>
            {
                session.Select(selection);
                return true;
            }).ConfigureAwait(false);
            return null;
        });
        connection.OnRequest("shutdown", (_, _) => Task.FromResult<JsonElement?>(null));
        connection.OnNotification("exit", json.JsonElement, _ => window.BeginInvoke(Application.ExitThread));
    }
}

/// <summary>
/// The host's hidden window: the design surface's view lives in it, off screen (shown, so its controls have handles and
/// lay out as in an app), and it marshals the protocol's requests to the UI thread.
/// </summary>
internal sealed class HostWindow : Form
{
    public HostWindow()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(2000, 1500);
        Text = "Codalus Windows Forms designer";
        Context = new ApplicationContext(this);
        CreateHandle();
    }

    public ApplicationContext Context { get; }

    protected override bool ShowWithoutActivation => true;

    /// <summary>Runs <paramref name="work"/> on the UI thread.</summary>
    public Task<T> Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }
}
