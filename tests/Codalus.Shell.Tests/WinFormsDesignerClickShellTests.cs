using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;
using static Codalus.Shell.Tests.WinFormsDesignerTestHost;

namespace Codalus.Shell.Tests;

/// <summary>
/// #461: a control library's design-time buttons (DevExpress's grid's Add level, Retrieve Details, Run Designer) in the
/// headless shell with a fake designer host (any OS), on samples/WindowsDesktop's Windows Forms app: a click on a control
/// whose designer takes clicks goes to the designer host, and what it changed is written as code.
/// </summary>
public sealed class WinFormsDesignerClickShellTests
{
    [AvaloniaFact]
    public async Task A_click_on_a_control_whose_designer_takes_clicks_goes_to_the_designer_host()
    {
        var (f, app, hosts) = await OpenAsync(host => host.ClickTakers.Add("expressRadioButton"));
        await using var _ = f;
        var designer = await DesignAsync(f, Path.Combine(app, "OrderForm.cs"));
        var host = Assert.Single(hosts);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Views.WinFormsDesignerView>().Any(v => v.IsEffectivelyVisible));
        var view = f.Window.GetVisualDescendants().OfType<Views.WinFormsDesignerView>().First(v => v.IsEffectivelyVisible);
        ShellFixture.RenderFrame(f.Window);
        Point At(string name)
        {
            var control = designer.Find(name)!;
            return view.Surface.TranslatePoint(new Point(control.X + (control.Width / 2), control.Y + (control.Height / 2)), f.Window)!.Value;
        }

        Assert.True(designer.Find("expressRadioButton")!.TakesClicks);

        // A click on another control selects it, and nothing goes to the host.
        f.Window.MouseDown(At("standardRadioButton"), MouseButton.Left);
        f.Window.MouseUp(At("standardRadioButton"), MouseButton.Left);
        Assert.Equal(["standardRadioButton"], designer.Selection);
        Assert.Empty(host.Clicks);

        // A click on the control whose designer takes it selects it and goes to the host, at the frame's point; the
        // designer's change is written.
        var express = designer.Find("expressRadioButton")!;
        f.Window.MouseDown(At("expressRadioButton"), MouseButton.Left);
        f.Window.MouseUp(At("expressRadioButton"), MouseButton.Left);
        Assert.Equal(["expressRadioButton"], designer.Selection);
        await ShellFixture.PumpUntil(() => Code(designer).Contains("expressRadioButton.Text = \"Clicked\";", StringComparison.Ordinal), seconds: 30, () => Code(designer));
        var click = Assert.Single(host.Clicks);
        Assert.Equal((express.X + (express.Width / 2), express.Y + (express.Height / 2)), (click.X, click.Y), new ToleranceComparer());

        // A drag that starts on it moves it: no click. It starts away from the click, which a press at its point within
        // the double-click time would make a double-click (the default event hooked, nothing moved).
        var start = At("expressRadioButton") + new Point(12, 0);
        f.Window.MouseDown(start, MouseButton.Left);
        f.Window.MouseMove(start + new Point(24, 0));
        f.Window.MouseUp(start + new Point(24, 0), MouseButton.Left);
        await ShellFixture.PumpUntil(() => designer.Find("expressRadioButton")!.X > express.X, seconds: 30, () => $"expressRadioButton at {designer.Find("expressRadioButton")!.X}");
        Assert.Single(host.Clicks);
        Assert.DoesNotContain("expressRadioButton.Click +=", Code(designer), StringComparison.Ordinal);

        // A click whose change is a library's objects that aren't components (DevExpress's Add level: a GridLevelNode)
        // writes them as locals: declared at the top, their properties set before the collection's AddRange.
        host.ClickChange = (taker, render) => new DesignerChangeResult([], render)
        {
            Collections =
            [
                new DesignerCollectionChange(taker, "Lanes.Nodes", "AddRange", "Contoso.Lane", [DesignerValue.ReferenceTo("lane1")])
                {
                    Locals = [new DesignerLocal("lane1", DesignerValue.NewOf("Contoso.Lane"))],
                    Statements = [new DesignerStatement { Kind = DesignerStatementKinds.Assign, Target = "lane1", Member = "Title", Value = DesignerValue.String("Level1") }],
                },
            ],
        };
        await Task.Delay(600, TestContext.Current.CancellationToken); // past the double-click time
        f.Window.MouseDown(At("expressRadioButton"), MouseButton.Left);
        f.Window.MouseUp(At("expressRadioButton"), MouseButton.Left);
        await ShellFixture.PumpUntil(() => Code(designer).Contains("Lanes.Nodes.AddRange", StringComparison.Ordinal), seconds: 30, () => Code(designer));
        var code = Code(designer).ReplaceLineEndings("\n");
        Assert.Contains("            Contoso.Lane lane1 = new Contoso.Lane();\n", code, StringComparison.Ordinal);
        Assert.Contains("            lane1.Title = \"Level1\";\n            expressRadioButton.Lanes.Nodes.AddRange(new Contoso.Lane[] { lane1 });\n", code, StringComparison.Ordinal);
    }

    /// <summary>Points equal to the pixel (the surface's transform rounds).</summary>
    private sealed class ToleranceComparer : IEqualityComparer<(double X, double Y)>
    {
        public bool Equals((double X, double Y) a, (double X, double Y) b) => Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1;

        public int GetHashCode((double X, double Y) obj) => 0;
    }
}
