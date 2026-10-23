using Codalus.LanguageServices.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.WinForms;

namespace Codalus.Shell.Tests;

/// <summary>
/// The headless shell with a fake designer host (any OS) on a copy of samples/WindowsDesktop's Windows Forms app, for
/// the designer's test classes (M41b, #67): the solution opened and trusted, the C# service ready, a form open in the
/// designer with its first frame, the Properties window's rows and the designer file's text.
/// </summary>
internal static class WinFormsDesignerTestHost
{
    internal static async Task<(ShellFixture Fixture, string App, List<FakeWinFormsDesignerHost> Hosts)> OpenAsync()
    {
        var f = ShellFixture.Create();
        var folder = f.CopySample("WindowsDesktop");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "WinFormsApp.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);
        var hosts = new List<FakeWinFormsDesignerHost>();
        f.Shell.WinFormsDesigner = new WinFormsDesignerService(f.Shell.Solutions, _ => true, (_, _) => Task.FromResult(true), async (project, names, ct) =>
        {
            var properties = await f.Shell.ProjectManagement.GetPropertiesAsync(project, names, null, ct);
            return properties.Properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        })
        {
            IsWindows = () => true,
            DesignerHost = () => typeof(WinFormsDesignerTestHost).Assembly.Location,
            HostFactory = (_, _) =>
            {
                var host = new FakeWinFormsDesignerHost();
                hosts.Add(host);
                return Task.FromResult<IWinFormsDesignerHost>(host);
            },
        };
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 300);
        return (f, Path.Combine(folder, "WinFormsApp"), hosts);
    }

    internal static async Task<WinFormsDesignerDocument> DesignAsync(ShellFixture f, string path)
    {
        await f.Shell.OpenFileAsync(path);
        var designer = f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>().Single(d => d.CodePath == path);
        try
        {
            await ShellFixture.PumpUntil(() => designer.FrameCount > 0, seconds: 120);
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail($"No frame: {designer.Status}\n{f.Shell.WinFormsDesignerLog.GetText()}");
        }

        return designer;
    }

    internal static async Task<List<PropertyRowViewModel>> RowsAsync(ShellFixture f)
    {
        f.Shell.Layout.ShowToolWindow(PropertiesWindowViewModel.ToolWindowId, focus: false);
        var properties = f.Shell.PropertiesWindow;
        properties.ShowEvents = false;
        await properties.RefreshAsync();
        return [.. properties.Categories.SelectMany(c => c.Rows)];
    }

    internal static string Code(WinFormsDesignerDocument designer) => designer.DesignerCode!.Text.Text;
}
