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
        Assert.Empty(host4.Creates);
        hold.SetResult();
        hold = null;
        await create;
        await add;
        await SettleAsync();
        Assert.Equal(5, hosts.Count);
        Assert.Equal(2, hosts[4].Creates.Count);
        Assert.Contains(hosts[4].Creates, c => (c.Type, c.Name, c.AtDefaultLocation) == ("System.Windows.Forms.Label", "label1", false));
        Assert.Contains(hosts[4].Creates, c => (c.Type, c.Name, c.AtDefaultLocation) == ("System.Windows.Forms.TextBox", "textBox1", true));
        Assert.Empty(host4.Creates);
        Assert.Contains("            label1 = new Label();\n", designer.DesignerCode!.Text.Text, StringComparison.Ordinal);
        Assert.Contains("            textBox1 = new TextBox();\n", designer.DesignerCode!.Text.Text, StringComparison.Ordinal);
        Assert.Equal([hosts[4].Creates[1].Name], designer.Selection);

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
