using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Build;

namespace Codalus.Shell.Tests;

/// <summary>
/// #66 in the headless shell (any OS, with the fake designer host: the real one is Windows only, D31). The Toolbox: a
/// drag from an item starts (the item's button had swallowed the press), a click arms an item drawn as the selection
/// without moving the list (the hint is the designer's status line), a double-click or Enter adds it into the selected
/// container and activates the designer, drops are refused in tab order mode and off the form, and a component drops on
/// the tray. The designer host runs on a copy of the build output, so a build can replace it while a form is open; after
/// a build that replaced what the host loaded, the active designer loads the form on a new host exactly once (another
/// designer when it becomes active), keeping its selection; a change made while the host starts again waits and applies
/// on the new host, and the surface takes no gesture meanwhile; a designer opened while a build runs waits for it.
/// </summary>
public sealed class WinFormsDesignerToolboxShellTests
{
    private static BuildCompleted Build(string project, bool canceled = false) =>
        new(new BuildRequest("Build", project), new BuildResultInfo(!canceled, canceled, 0, 0, 10));

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            ShellFixture.Pump();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Real pointer and key input on the Toolbox's item buttons (a hand-raised DoubleTapped is what hid the bug: the list
    /// moved between the two presses), and Avalonia.Headless's drops on the surface and the tray.
    /// </summary>
    [AvaloniaFact]
    public async Task Toolbox_gestures_add_controls_as_the_classic_ide_does()
    {
        var (f, app) = await WinFormsDesignerShellTests.OpenAsync();
        await using var fixture = f;
        var hosts = new List<FakeWinFormsDesignerHost>();
        WinFormsDesignerShellTests.UseFakeDesignerHost(f, hosts);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 300,
            () => WinFormsDesignerShellTests.DescribeToolbox(f, "the C# service"));
        Assert.Null(await f.Shell.OpenFileAsync(Path.Combine(app, "OrderForm.cs")));
        var designer = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
        await ShellFixture.PumpUntil(() => designer.FrameCount > 0, seconds: 120, () => $"a frame: {designer.Status}\n{f.Shell.WinFormsDesignerLog.GetText()}");
        var host = Assert.Single(hosts);
        var code = designer.DesignerCode!;
        var root = designer.Root!;

        f.Shell.Layout.ShowToolWindow(ToolboxViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Views.ToolboxView>().Any(v => v.IsEffectivelyVisible)
            && f.Window.GetVisualDescendants().OfType<Views.DesignerSurface>().Any(s => s.IsEffectivelyVisible));
        var toolbox = f.Window.GetVisualDescendants().OfType<Views.ToolboxView>().First(v => v.IsEffectivelyVisible);
        var view = f.Window.GetVisualDescendants().OfType<Views.WinFormsDesignerView>().First(v => v.IsEffectivelyVisible);
        var surface = view.Surface;
        Assert.Same(designer, f.Shell.Layout.ActiveDocument);
        Assert.True(f.Shell.Toolbox.IsAvailable);

        IEnumerable<Button> ItemButtons() => toolbox.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("toolboxItem") && b.IsEffectivelyVisible);
        async Task<Button> ItemAsync(string type)
        {
            Button? found = null;
            await ShellFixture.PumpUntil(() => (found = ItemButtons().FirstOrDefault(b => b.DataContext is ToolboxItemViewModel { } item && item.Type == type)) is not null, seconds: 30, () => $"the Toolbox's {type}");
            var item = found!;
            item.BringIntoView();
            ShellFixture.RenderFrame(f.Window);
            await SettleAsync();
            return item;
        }

        Point CenterOf(Visual visual) => visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), f.Window)!.Value;
        Button? ItemAt(Point point) => (f.Window.InputHitTest(point) as Visual)?.FindAncestorOfType<Button>(includeSelf: true);
        void Drop(Point at, IDataTransfer data)
        {
            f.Window.DragDrop(at, RawDragEventType.DragEnter, data, DragDropEffects.Copy);
            f.Window.DragDrop(at, RawDragEventType.DragOver, data, DragDropEffects.Copy);
            f.Window.DragDrop(at, RawDragEventType.Drop, data, DragDropEffects.Copy);
        }

        // A: a drag from the Button item starts (its press reached the Toolbox), its release arms nothing, and its drop on the form creates button1 there.
        var drags = new List<(IDataTransfer Data, DragDropEffects Effects)>();
        toolbox.StartDrag = (_, data, effects) =>
        {
            drags.Add((data, effects));
            return Task.FromResult(DragDropEffects.Copy);
        };
        var buttonItem = await ItemAsync("System.Windows.Forms.Button");
        var start = CenterOf(buttonItem);
        f.Window.MouseDown(start, MouseButton.Left);
        f.Window.MouseMove(start + new Point(10, 0), RawInputModifiers.LeftMouseButton);
        await SettleAsync();
        var drag = Assert.Single(drags);
        Assert.Equal(DragDropEffects.Copy, drag.Effects);
        Assert.Equal("System.Windows.Forms.Button", drag.Data.TryGetValue(Views.WinFormsDesignerView.ToolboxFormat));
        f.Window.MouseUp(start + new Point(10, 0), MouseButton.Left);
        await SettleAsync();
        Assert.Null(f.Shell.Toolbox.Armed);
        Drop(surface.TranslatePoint(new Point(56, 331), f.Window)!.Value, drag.Data);
        await ShellFixture.PumpUntil(() => host.Creates.Count == 1 && designer.Selection is ["button1"], seconds: 30, () => $"the drop's create: {designer.Status}");
        var dropped = host.Creates[0];
        Assert.Equal(("System.Windows.Forms.Button", "button1", (string?)null, 56d, 331d, false), (dropped.Type, dropped.Name, dropped.Parent, dropped.X, dropped.Y, dropped.AtDefaultLocation));
        Assert.Contains("            button1 = new Button();\n", code.Text.Text, StringComparison.Ordinal);
        Assert.Contains("            Controls.Add(button1);\n", code.Text.Text, StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => surface.IsFocused, seconds: 10, () => "the surface's focus after the drop");

        // B: a click arms the CheckBox, drawn as the selection; no item moves (the hint is the designer's status line); the Pointer disarms.
        await Task.Delay(600, TestContext.Current.CancellationToken); // not a double-click with the drag's press
        var checkItem = await ItemAsync("System.Windows.Forms.CheckBox");
        var places = ItemButtons().ToDictionary(b => b, b => b.TranslatePoint(default, f.Window));
        var checkCenter = CenterOf(checkItem);
        f.Window.MouseDown(checkCenter, MouseButton.Left);
        f.Window.MouseUp(checkCenter, MouseButton.Left);
        ShellFixture.RenderFrame(f.Window);
        await SettleAsync();
        Assert.Equal("System.Windows.Forms.CheckBox", f.Shell.Toolbox.Armed?.Type);
        Assert.Contains("armed", checkItem.Classes);
        Assert.Single(ItemButtons(), b => b.Classes.Contains("armed"));
        Assert.All(places, p => Assert.Equal(p.Value, p.Key.TranslatePoint(default, f.Window)));
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Click or drag on the form to add a CheckBox (Esc cancels).");

        // The selection's colors while the Toolbox has the focus, the inactive selection's once the designer has it.
        Color Background() => ((ISolidColorBrush)checkItem.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter").Background!).Color;
        Color Token(string key) => toolbox.TryFindResource(key, toolbox.ActualThemeVariant, out var found) && found is ISolidColorBrush brush ? brush.Color : default;
        Assert.True(checkItem.IsFocused);
        Assert.Equal(Token("Shell.Selection"), Background());
        surface.Focus();
        await SettleAsync();
        Assert.Equal(Token("Shell.Selection.Inactive"), Background());
        Assert.Equal("System.Windows.Forms.CheckBox", f.Shell.Toolbox.Armed?.Type);
        var pointer = ItemButtons().First(b => b.DataContext is ToolboxItemViewModel { IsPointer: true });
        var pointerCenter = CenterOf(pointer);
        f.Window.MouseDown(pointerCenter, MouseButton.Left);
        f.Window.MouseUp(pointerCenter, MouseButton.Left);
        await SettleAsync();
        Assert.Null(f.Shell.Toolbox.Armed);
        Assert.DoesNotContain("armed", checkItem.Classes);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("Click or drag on the form", StringComparison.Ordinal) == true);

        // C: drops that create nothing: in tab order mode, and off the form (beside it, on the surface's margin).
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(Views.WinFormsDesignerView.ToolboxFormat, "System.Windows.Forms.Label"));
        designer.IsTabOrderMode = true;
        Drop(surface.TranslatePoint(new Point(56, 331), f.Window)!.Value, data);
        await SettleAsync();
        designer.IsTabOrderMode = false;
        Drop(surface.TranslatePoint(new Point(-8, 200), f.Window)!.Value, data);
        await SettleAsync();
        Assert.Single(host.Creates);

        // D: the tray takes a component (a Timer), not a control.
        var trayItem = view.GetVisualDescendants().OfType<Button>().First(b => b.Tag is "orderToolTip");
        var timer = new DataTransfer();
        timer.Add(DataTransferItem.Create(Views.WinFormsDesignerView.ToolboxFormat, "System.Windows.Forms.Timer"));
        Drop(CenterOf(trayItem), timer);
        await ShellFixture.PumpUntil(() => host.Creates.Count == 2 && designer.Tray.Any(t => t.Name == "timer1"), seconds: 30, () => $"the Timer in the tray: {designer.Status}");
        Assert.Equal(("System.Windows.Forms.Timer", (string?)null), (host.Creates[1].Type, host.Creates[1].Parent));
        Assert.Contains("            timer1 = new Timer(components);\n", code.Text.Text, StringComparison.Ordinal);
        var button = new DataTransfer();
        button.Add(DataTransferItem.Create(Views.WinFormsDesignerView.ToolboxFormat, "System.Windows.Forms.Button"));
        Drop(CenterOf(view.GetVisualDescendants().OfType<Button>().First(b => b.Tag is "orderToolTip")), button);
        await SettleAsync();
        Assert.Equal(2, host.Creates.Count);

        // E: a double-click on the CheckBox with placeOrderButton selected adds checkBox1 into buttonPanel at its default place,
        // selected, nothing armed, the designer active and its surface focused. Both presses land on the same item.
        designer.SetSelection(["placeOrderButton"], notify: true);
        await Task.Delay(600, TestContext.Current.CancellationToken);
        checkItem = await ItemAsync("System.Windows.Forms.CheckBox");
        checkCenter = CenterOf(checkItem);
        f.Window.MouseDown(checkCenter, MouseButton.Left);
        f.Window.MouseUp(checkCenter, MouseButton.Left);
        ShellFixture.RenderFrame(f.Window);
        ShellFixture.Pump();
        Assert.Same(checkItem, ItemAt(checkCenter));
        Assert.Equal("System.Windows.Forms.CheckBox", f.Shell.Toolbox.Armed?.Type);
        f.Window.MouseDown(checkCenter, MouseButton.Left);
        f.Window.MouseUp(checkCenter, MouseButton.Left);
        await ShellFixture.PumpUntil(() => host.Creates.Count == 3 && designer.Selection is ["checkBox1"], seconds: 30,
            () => $"the double-click's create ({host.Creates.Count} creates, armed {f.Shell.Toolbox.Armed?.Name ?? "nothing"}): {designer.Status}");
        await SettleAsync();
        var added = host.Creates[2];
        Assert.Equal(("System.Windows.Forms.CheckBox", "checkBox1", true, root.ClientX, root.ClientY), (added.Type, added.Name, added.AtDefaultLocation, added.X, added.Y));
        Assert.Equal(["placeOrderButton"], added.Selected);
        Assert.Contains("            buttonPanel.Controls.Add(checkBox1);\n", code.Text.Text, StringComparison.Ordinal);
        Assert.Null(f.Shell.Toolbox.Armed);
        Assert.Equal(3, host.Creates.Count);
        Assert.Same(designer, f.Shell.Layout.ActiveDocument);
        await ShellFixture.PumpUntil(() => surface.IsFocused, seconds: 10, () => "the surface's focus after the double-click");

        // F: Enter on the TextBox item adds it at once (nothing armed); Space arms it and adds nothing.
        var textItem = await ItemAsync("System.Windows.Forms.TextBox");
        textItem.Focus();
        await SettleAsync();
        Assert.True(textItem.IsFocused);
        f.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        f.Window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await ShellFixture.PumpUntil(() => host.Creates.Count == 4 && designer.Selection is ["textBox1"], seconds: 30, () => $"Enter's create: {designer.Status}");
        await SettleAsync();
        Assert.True(host.Creates[3] is { Type: "System.Windows.Forms.TextBox", AtDefaultLocation: true });
        Assert.Equal(["checkBox1"], host.Creates[3].Selected);
        Assert.Null(f.Shell.Toolbox.Armed);
        textItem.Focus();
        await SettleAsync();
        f.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        f.Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        await SettleAsync();
        Assert.Equal("System.Windows.Forms.TextBox", f.Shell.Toolbox.Armed?.Type);
        Assert.Equal(4, host.Creates.Count);
        f.Shell.Toolbox.Disarm();
    }

    /// <summary>
    /// Builds through the shell's own build service (its end and whether a build runs are what the designer follows), run
    /// by a stand-in for the build host: each build runs until the test ends it, with the result the test gives.
    /// </summary>
    private sealed class BuildStandIn
    {
        private readonly ShellFixture _f;
        private TaskCompletionSource<BuildResultInfo>? _running;

        public BuildStandIn(ShellFixture f)
        {
            _f = f;
            f.Shell.Builds.Runner = (_, _) => (_running = new TaskCompletionSource<BuildResultInfo>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        /// <summary>Starts a build of <paramref name="project"/>; it runs until <see cref="End"/>.</summary>
        public Task<BuildResultInfo> Start(string project)
        {
            var build = _f.Shell.Builds.BuildAsync("Build", project, CancellationToken.None);
            Assert.True(_f.Shell.Builds.IsBuilding);
            return build;
        }

        public void End(bool succeeded = true, bool canceled = false) =>
            _running!.SetResult(new BuildResultInfo(succeeded && !canceled, canceled, 0, 0, 10));

        /// <summary>A build that ends at once with the given result.</summary>
        public async Task RunAsync(string project, bool succeeded = true, bool canceled = false)
        {
            var build = Start(project);
            End(succeeded, canceled);
            await build;
        }
    }

    [AvaloniaFact]
    public async Task A_build_that_replaced_the_designed_output_loads_the_form_on_it_once()
    {
        var (f, app) = await WinFormsDesignerShellTests.OpenAsync();
        await using var fixture = f;
        var hosts = new List<FakeWinFormsDesignerHost>();
        TaskCompletionSource? hold = null;
        var starts = 0;
        WinFormsDesignerShellTests.UseFakeDesignerHost(f, hosts, starting: () =>
        {
            starts++;
            return hold?.Task ?? Task.CompletedTask;
        });
        var builds = new BuildStandIn(f);
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

        // The builds run through the shell's build service: its end is what reaches the designer.
        // A build that replaced nothing the host loaded (an unrelated project, content): no restart.
        await builds.RunAsync(project);
        await SettleAsync();
        Assert.Single(hosts);

        // A build that replaced it: the form on a new host, once; the old one ended; the selection kept.
        host1.Outdated = true;
        await builds.RunAsync(project);
        await ShellFixture.PumpUntil(() => hosts.Count == 2 && designer.Host == hosts[1] && designer.FrameCount > 0, seconds: 60, () => $"a new host: {designer.Status}\n{f.Shell.WinFormsDesignerLog.GetText()}");
        await SettleAsync();
        Assert.Equal(2, hosts.Count);
        Assert.True(host1.Disposed);
        Assert.Equal(1, hosts[1].Loads);
        Assert.Equal("OrderForm", hosts[1].Model!.ClassName);
        Assert.Equal(["placeOrderButton"], designer.Selection);
        Assert.Contains("A build replaced WinFormsApp.dll: loading OrderForm.cs on a new designer host.", f.Shell.WinFormsDesignerLog.GetText(), StringComparison.Ordinal);

        // A canceled build that had already replaced it (canceled after the app's assembly was copied): the new build shows too.
        var host2 = hosts[1];
        host2.Outdated = true;
        await builds.RunAsync(project, canceled: true);
        await ShellFixture.PumpUntil(() => hosts.Count == 3 && designer.Host == hosts[2], seconds: 60, () => $"a new host after a canceled build: {designer.Status}");
        await SettleAsync();
        Assert.True(host2.Disposed);

        // Another document is active: the build's end leaves the designer alone; it loads the new build when it's active again.
        var host3 = hosts[2];
        host3.Outdated = true;
        await f.Shell.OpenFileAsync(Path.Combine(app, "Counter.cs"));
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == Path.Combine(app, "Counter.cs"));
        await builds.RunAsync(project);
        await SettleAsync();
        Assert.Equal(3, hosts.Count);
        f.Shell.Layout.OpenDocument(designer, focus: true);
        await ShellFixture.PumpUntil(() => hosts.Count == 4 && designer.Host == hosts[3], seconds: 60, () => $"a new host on activation: {designer.Status}");
        await SettleAsync();
        Assert.Equal(4, hosts.Count);
        Assert.True(host3.Disposed);

        // A build's end and an activation in the same turn: one new host (the second check sees the new host's copy).
        hosts[3].Outdated = true;
        f.Shell.OnWinFormsBuildFinished(Build(project));
        _ = designer.OnActivatedAsync();
        _ = designer.OnActivatedAsync();
        await ShellFixture.PumpUntil(() => hosts.Count == 5 && designer.Host == hosts[4], seconds: 60, () => $"a new host: {designer.Status}");
        await SettleAsync();
        Assert.Equal(5, hosts.Count);

        // A change made while the host starts again waits, then applies on the new host: never dropped, never sent to the old one.
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Views.DesignerSurface>().Any(s => s.IsEffectivelyVisible));
        var surface = f.Window.GetVisualDescendants().OfType<Views.DesignerSurface>().First(s => s.IsEffectivelyVisible);
        var host5 = hosts[4];
        host5.Outdated = true;
        hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startsBefore = starts;
        await builds.RunAsync(project);
        await ShellFixture.PumpUntil(() => designer.IsRestarting && starts == startsBefore + 1, seconds: 60, () => $"the restart to wait for its host: {designer.Status}");
        Assert.True(host5.Disposed);
        var create = designer.CreateAsync("System.Windows.Forms.Label", 8 + 50, 31 + 300);
        var add = designer.AddFromToolboxAsync("System.Windows.Forms.TextBox"); // the Toolbox's double-click or Enter
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
        Assert.False(add.IsCompleted);
        Assert.Equal(["placeOrderButton"], designer.Selection); // the surface took no click
        Assert.Empty(host5.Creates);
        hold.SetResult();
        hold = null;
        await create;
        await add;
        await SettleAsync();
        Assert.Equal(6, hosts.Count);
        Assert.Equal(2, hosts[5].Creates.Count);
        Assert.Contains(hosts[5].Creates, c => (c.Type, c.Name, c.AtDefaultLocation) == ("System.Windows.Forms.Label", "label1", false));
        Assert.Contains(hosts[5].Creates, c => (c.Type, c.Name, c.AtDefaultLocation) == ("System.Windows.Forms.TextBox", "textBox1", true));
        Assert.Empty(host5.Creates);
        Assert.Contains("            label1 = new Label();\n", designer.DesignerCode!.Text.Text, StringComparison.Ordinal);
        Assert.Contains("            textBox1 = new TextBox();\n", designer.DesignerCode!.Text.Text, StringComparison.Ordinal);
        Assert.Equal([hosts[5].Creates[1].Name], designer.Selection);

        // A designer opened while a build runs waits for it (the shell's own "a build runs", no "A build is already running."), then starts.
        var running = builds.Start(project);
        var opening = f.Shell.OpenFileAsync(Path.Combine(app, "MainForm.cs")); // it returns once the designer has loaded
        WinFormsDesignerDocument? main = null;
        await ShellFixture.PumpUntil(() => (main = f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>().SingleOrDefault(d => d.CodePath == Path.Combine(app, "MainForm.cs"))) is { Status: "Waiting for the build to finish…" },
            seconds: 60, () => $"the wait for the build: {main?.Status}");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        ShellFixture.Pump();
        Assert.Equal(6, hosts.Count);
        Assert.Equal("Waiting for the build to finish…", main!.Status);
        Assert.False(opening.IsCompleted);
        builds.End();
        await running;
        await ShellFixture.PumpUntil(() => main.FrameCount > 0, seconds: 60, () => $"MainForm's frame: {main.Status}");
        Assert.Null(await opening);
        Assert.Equal(7, hosts.Count);
        Assert.DoesNotContain("already running", main.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// A designer left without a host (its start failed, or its restart after a build did) starts again after the next
    /// successful build, or when it becomes active and its output changed since, not on every activation or failed
    /// build; a designer closed while it waits for a build (opening, or starting again) stops there: no build of its
    /// own, no host; the IDE's exit stops the open designers' hosts (#66).
    /// </summary>
    [AvaloniaFact]
    public async Task A_designer_without_a_host_starts_after_a_build_and_a_closed_one_starts_nothing()
    {
        var (f, app) = await WinFormsDesignerShellTests.OpenAsync();
        var exited = false;
        try
        {
            var hosts = new List<FakeWinFormsDesignerHost>();
            var failures = 0; // how many of the next starts fail
            var attempts = 0;
            var designerBuilds = 0;
            const string Failure = "The designer didn't start: copying WinFormsApp.dll failed: The process cannot access the file because it is being used by another process.";
            WinFormsDesignerShellTests.UseFakeDesignerHost(f, hosts, starting: () =>
            {
                attempts++;
                if (failures > 0)
                {
                    failures--;
                    return Task.FromException(new Codalus.Workbench.Previewer.PreviewerException(Failure));
                }

                return Task.CompletedTask;
            }, build: () =>
            {
                designerBuilds++;
                return Task.FromResult(true);
            });
            var builds = new BuildStandIn(f);
            await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 300,
                () => WinFormsDesignerShellTests.DescribeToolbox(f, "the C# service"));
            var project = Path.Combine(app, "WinFormsApp.csproj");
            var counter = Path.Combine(app, "Counter.cs");
            var form = Path.Combine(app, "OrderForm.cs");

            async Task SettleAsync()
            {
                await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>().All(d => !d.IsRestarting && !d.IsBusy), seconds: 60, () => "the designers to settle");
                for (var i = 0; i < 10; i++)
                {
                    ShellFixture.Pump();
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                }
            }

            async Task ActivateAsync(WinFormsDesignerDocument designer)
            {
                await f.Shell.OpenFileAsync(counter);
                await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == counter);
                f.Shell.Layout.OpenDocument(designer, focus: true);
                await SettleAsync();
            }

            // The start fails: no host, the reason in the status line.
            failures = 1;
            Assert.Null(await f.Shell.OpenFileAsync(form));
            var designer = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
            await SettleAsync();
            Assert.Null(designer.Host);
            Assert.Equal(Failure, designer.Status);
            Assert.Equal(1, attempts);

            // Nothing changed: an activation, a failed build and a canceled one don't start it again.
            await ActivateAsync(designer);
            await builds.RunAsync(project, succeeded: false);
            await SettleAsync();
            await builds.RunAsync(project, canceled: true);
            await SettleAsync();
            Assert.Equal(1, attempts);
            Assert.Null(designer.Host);

            // A successful build: it starts, and the form loads.
            await builds.RunAsync(project);
            await ShellFixture.PumpUntil(() => designer.Host is not null && designer.FrameCount > 0, seconds: 60, () => $"the start after the build: {designer.Status}\n{f.Shell.WinFormsDesignerLog.GetText()}");
            await SettleAsync();
            Assert.Equal(2, attempts);
            Assert.Same(Assert.Single(hosts), designer.Host);
            Assert.Contains("A build ended after the designer for OrderForm.cs failed to start: starting it again.", f.Shell.WinFormsDesignerLog.GetText(), StringComparison.Ordinal);

            // The restart after a build fails (a file of the output held): no host; the next successful build starts it.
            hosts[0].Outdated = true;
            failures = 1;
            await builds.RunAsync(project);
            await ShellFixture.PumpUntil(() => attempts == 3, seconds: 60, () => $"the restart: {designer.Status}");
            await SettleAsync();
            Assert.True(hosts[0].Disposed);
            Assert.Null(designer.Host);
            Assert.Equal(Failure, designer.Status);
            await builds.RunAsync(project);
            await ShellFixture.PumpUntil(() => designer.Host is not null, seconds: 60, () => $"the start after the next build: {designer.Status}");
            await SettleAsync();
            Assert.Equal(4, attempts);
            Assert.Equal(2, hosts.Count);

            // A build outside the IDE (or in another tab) while it had no host: it starts when it becomes active.
            hosts[1].Outdated = true;
            failures = 1;
            await builds.RunAsync(project);
            await ShellFixture.PumpUntil(() => attempts == 5, seconds: 60, () => $"the failed restart: {designer.Status}");
            await SettleAsync();
            Assert.Null(designer.Host);
            await f.Shell.OpenFileAsync(counter);
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == counter);
            var target = await f.Shell.WinFormsDesigner.ResolveAsync(form, TestContext.Current.CancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(target.ProjectAssembly)!);
            await File.WriteAllTextAsync(target.ProjectAssembly, "built outside the IDE", TestContext.Current.CancellationToken);
            f.Shell.Layout.OpenDocument(designer, focus: true);
            await ShellFixture.PumpUntil(() => designer.Host is not null, seconds: 60, () => $"the start on activation: {designer.Status}");
            await SettleAsync();
            Assert.Equal(6, attempts);
            Assert.Equal(3, hosts.Count);
            Assert.Contains("WinFormsApp.dll changed after the designer for OrderForm.cs failed to start: starting it again.", f.Shell.WinFormsDesignerLog.GetText(), StringComparison.Ordinal);
            File.Delete(target.ProjectAssembly); // the output needs a build again, so a designer that went on would build it

            // A designer closed while it waits for a build to open stops waiting: no build of its own, no host.
            var hostsBefore = hosts.Count;
            var running = builds.Start(project);
            var opening = f.Shell.OpenFileAsync(Path.Combine(app, "MainForm.cs"));
            WinFormsDesignerDocument? main = null;
            await ShellFixture.PumpUntil(() => (main = f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>().SingleOrDefault(d => d.CodePath == Path.Combine(app, "MainForm.cs"))) is { Status: "Waiting for the build to finish…" },
                seconds: 60, () => $"the wait for the build: {main?.Status}");
            var buildsBefore = designerBuilds;
            f.Shell.Layout.CloseDocument(main!);
            await ShellFixture.PumpUntil(() => opening.IsCompleted, seconds: 10, () => $"the closed designer to stop waiting: {main!.Status}");
            builds.End();
            await running;
            await SettleAsync();
            Assert.Equal(buildsBefore, designerBuilds);
            Assert.Equal(6, attempts);
            Assert.Equal(hostsBefore, hosts.Count);

            // A designer closed while it starts again (Load the form again during a build) stops there too.
            running = builds.Start(project);
            designer.RefreshCommand.Execute(null);
            await ShellFixture.PumpUntil(() => designer.IsRestarting && designer.Status == "Waiting for the build to finish…", seconds: 60, () => $"the restart to wait for the build: {designer.Status}");
            Assert.True(hosts[^1].Disposed);
            f.Shell.Layout.CloseDocument(designer);
            for (var i = 0; i < 10; i++)
            {
                ShellFixture.Pump();
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            builds.End();
            await running;
            for (var i = 0; i < 25; i++)
            {
                ShellFixture.Pump();
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.Equal(buildsBefore, designerBuilds);
            Assert.Equal(6, attempts);
            Assert.Equal(hostsBefore, hosts.Count);

            // The IDE exits with a form open (the session reopens it next time): its host stops, so its copy goes.
            Assert.Null(await f.Shell.OpenFileAsync(Path.Combine(app, "InventoryForm.cs")));
            var inventory = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
            await ShellFixture.PumpUntil(() => inventory.Host is not null, seconds: 60, () => $"InventoryForm's host: {inventory.Status}");
            var last = (FakeWinFormsDesignerHost)inventory.Host!;
            exited = true;
            await f.DisposeAsync();
            Assert.True(last.Disposed);
        }
        finally
        {
            if (!exited)
            {
                await f.DisposeAsync();
            }
        }
    }
}
