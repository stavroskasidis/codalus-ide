using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Build;

namespace Codalus.Shell.Tests;

/// <summary>
/// #66 in the headless shell (any OS, with the fake designer host: the real one is Windows only, D31). The designer host
/// runs on a copy of the build output, so a build can replace it while a form is open; after a build that replaced
/// what the host loaded, the active designer loads the form on a new host exactly once (another designer when it
/// becomes active), keeping its selection; a change made while the host starts again waits and applies on the new
/// host, and the surface takes no gesture meanwhile; a designer opened while a build runs waits for it.
/// </summary>
public sealed class WinFormsDesignerToolboxShellTests
{
    private static BuildCompleted Build(string project, bool canceled = false) =>
        new(new BuildRequest("Build", project), new BuildResultInfo(!canceled, canceled, 0, 0, 10));

    [AvaloniaFact]
    public async Task A_build_that_replaced_the_designed_output_loads_the_form_on_it_once()
    {
        var (f, app) = await WinFormsDesignerShellTests.OpenAsync();
        await using var fixture = f;
        var hosts = new List<FakeWinFormsDesignerHost>();
        TaskCompletionSource? hold = null;
        var starts = 0;
        var building = false;
        WinFormsDesignerShellTests.UseFakeDesignerHost(f, hosts, starting: () =>
        {
            starts++;
            return hold?.Task ?? Task.CompletedTask;
        }, isBuilding: () => building);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 300,
            () => WinFormsDesignerShellTests.DescribeToolbox(f, "the C# service"));
        var project = Path.Combine(app, "WinFormsApp.csproj");

        var form = Path.Combine(app, "OrderForm.cs");
        Assert.Null(await f.Shell.OpenFileAsync(form));
        var designer = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
        await ShellFixture.PumpUntil(() => designer.FrameCount > 0, seconds: 120, () => $"a frame: {designer.Status}\n{f.Shell.WinFormsDesignerLog.GetText()}");
        var host1 = Assert.Single(hosts);
        designer.SetSelection(["placeOrderButton"], notify: true);

        async Task SettleAsync()
        {
            await ShellFixture.PumpUntil(() => !designer.IsRestarting && !designer.IsBusy, seconds: 60, () => $"the designer to settle: {designer.Status}");
            for (var i = 0; i < 5; i++)
            {
                ShellFixture.Pump();
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }

        // A build that replaced nothing the host loaded (an unrelated project, content), and a canceled one: no restart.
        f.Shell.OnWinFormsBuildFinished(Build(project));
        await SettleAsync();
        Assert.Single(hosts);
        host1.Outdated = true;
        f.Shell.OnWinFormsBuildFinished(Build(project, canceled: true));
        await SettleAsync();
        Assert.Single(hosts);

        // A build that replaced it: the form on a new host, once; the old one ended; the selection kept.
        f.Shell.OnWinFormsBuildFinished(Build(project));
        await ShellFixture.PumpUntil(() => hosts.Count == 2 && designer.Host == hosts[1] && designer.FrameCount > 0, seconds: 60, () => $"a new host: {designer.Status}\n{f.Shell.WinFormsDesignerLog.GetText()}");
        await SettleAsync();
        Assert.Equal(2, hosts.Count);
        Assert.True(host1.Disposed);
        Assert.Equal(1, hosts[1].Loads);
        Assert.Equal("OrderForm", hosts[1].Model!.ClassName);
        Assert.Equal(["placeOrderButton"], designer.Selection);
        Assert.Contains("A build replaced WinFormsApp.dll: loading OrderForm.cs on a new designer host.", f.Shell.WinFormsDesignerLog.GetText(), StringComparison.Ordinal);

        // Another document is active: the build's end leaves the designer alone; it loads the new build when it's active again.
        var host2 = hosts[1];
        host2.Outdated = true;
        await f.Shell.OpenFileAsync(Path.Combine(app, "Counter.cs"));
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == Path.Combine(app, "Counter.cs"));
        f.Shell.OnWinFormsBuildFinished(Build(project));
        await SettleAsync();
        Assert.Equal(2, hosts.Count);
        f.Shell.Layout.OpenDocument(designer, focus: true);
        await ShellFixture.PumpUntil(() => hosts.Count == 3 && designer.Host == hosts[2], seconds: 60, () => $"a new host on activation: {designer.Status}");
        await SettleAsync();
        Assert.Equal(3, hosts.Count);
        Assert.True(host2.Disposed);

        // A build's end and an activation in the same turn: one new host (the second check sees the new host's copy).
        hosts[2].Outdated = true;
        f.Shell.OnWinFormsBuildFinished(Build(project));
        _ = designer.OnActivatedAsync();
        _ = designer.OnActivatedAsync();
        await ShellFixture.PumpUntil(() => hosts.Count == 4 && designer.Host == hosts[3], seconds: 60, () => $"a new host: {designer.Status}");
        await SettleAsync();
        Assert.Equal(4, hosts.Count);

        // A change made while the host starts again waits, then applies on the new host: never dropped, never sent to the old one.
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Views.DesignerSurface>().Any(s => s.IsEffectivelyVisible));
        var surface = f.Window.GetVisualDescendants().OfType<Views.DesignerSurface>().First(s => s.IsEffectivelyVisible);
        var host4 = hosts[3];
        host4.Outdated = true;
        hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startsBefore = starts;
        f.Shell.OnWinFormsBuildFinished(Build(project));
        await ShellFixture.PumpUntil(() => designer.IsRestarting && starts == startsBefore + 1, seconds: 60, () => $"the restart to wait for its host: {designer.Status}");
        Assert.True(host4.Disposed);
        var create = designer.CreateAsync("System.Windows.Forms.Label", 8 + 50, 31 + 300);
        var gift = designer.Find("giftCheckBox")!;
        ShellFixture.RenderFrame(f.Window);
        var point = surface.TranslatePoint(new Point(gift.X + 5, gift.Y + 5), f.Window)!.Value;
        f.Window.MouseDown(point, MouseButton.Left);
        f.Window.MouseUp(point, MouseButton.Left);
        for (var i = 0; i < 10; i++)
        {
            ShellFixture.Pump();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.False(create.IsCompleted);
        Assert.Equal(["placeOrderButton"], designer.Selection); // the surface took no click
        Assert.Empty(host4.Creates);
        hold.SetResult();
        hold = null;
        await create;
        await SettleAsync();
        Assert.Equal(5, hosts.Count);
        var created = Assert.Single(hosts[4].Creates);
        Assert.Equal(("System.Windows.Forms.Label", "label1"), (created.Type, created.Name));
        Assert.Empty(host4.Creates);
        Assert.Contains("            label1 = new Label();\n", designer.DesignerCode!.Text.Text, StringComparison.Ordinal);
        Assert.Equal(["label1"], designer.Selection);

        // A designer opened while a build runs waits for it (no "A build is already running."), then starts.
        building = true;
        var opening = f.Shell.OpenFileAsync(Path.Combine(app, "MainForm.cs")); // it returns once the designer has loaded
        WinFormsDesignerDocument? main = null;
        await ShellFixture.PumpUntil(() => (main = f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>().SingleOrDefault(d => d.CodePath == Path.Combine(app, "MainForm.cs"))) is { Status: "Waiting for the build to finish…" },
            seconds: 60, () => $"the wait for the build: {main?.Status}");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        ShellFixture.Pump();
        Assert.Equal(5, hosts.Count);
        Assert.Equal("Waiting for the build to finish…", main!.Status);
        Assert.False(opening.IsCompleted);
        building = false;
        await ShellFixture.PumpUntil(() => main.FrameCount > 0, seconds: 60, () => $"MainForm's frame: {main.Status}");
        Assert.Null(await opening);
        Assert.Equal(6, hosts.Count);
        Assert.DoesNotContain("already running", main.Status, StringComparison.Ordinal);
    }
}
