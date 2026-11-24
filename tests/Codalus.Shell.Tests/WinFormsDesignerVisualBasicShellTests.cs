using Avalonia.Headless.XUnit;
using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Tests;

/// <summary>
/// ADR 0073, Visual Basic forms: samples/WindowsDesktop's Visual Basic app in the shell with a stand-in designer host
/// (on every OS). OrderForm.vb opens in the designer; a Toolbox item becomes a <c>Friend WithEvents</c> component named
/// as the classic IDE names it in Visual Basic (<c>Button1</c>); its default event becomes a handler with a
/// <c>Handles</c> clause in OrderForm.vb; deleting it takes its <c>Handles</c> item and leaves the method.
/// </summary>
public sealed class WinFormsDesignerVisualBasicShellTests
{
    [AvaloniaFact]
    public async Task A_Visual_Basic_form_opens_in_the_designer_and_changes_become_Visual_Basic()
    {
        var f = ShellFixture.Create();
        await using var _ = f;
        var folder = f.CopySample("WindowsDesktop");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "WinFormsVbApp.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);
        var hosts = new List<FakeWinFormsDesignerHost>();
        WinFormsDesignerShellTests.UseFakeDesignerHost(f, hosts);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 300);

        // The code file opens in the designer, which edits its Designer.vb.
        var app = Path.Combine(folder, "WinFormsVbApp");
        var form = Path.Combine(app, "OrderForm.vb");
        Assert.Null(await f.Shell.OpenFileAsync(form));
        var designer = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
        Assert.Equal("OrderForm.vb [Design]", designer.Title);
        Assert.Equal(Path.Combine(app, "OrderForm.Designer.vb"), designer.DesignerPath);
        Assert.EndsWith("form-vb", designer.TabIcon, StringComparison.Ordinal);
        try
        {
            await ShellFixture.PumpUntil(() => designer.FrameCount > 0, seconds: 120);
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail($"No frame: {designer.Status}\n{f.Shell.WinFormsDesignerLog.GetText()}");
        }

        await WinFormsDesignerShellTests.WhenReadCompleteAsync(f, designer);
        var host = Assert.Single(hosts);
        Assert.Equal(DesignerLanguages.VisualBasic, host.Model!.Language);
        Assert.Contains(host.Model.Statements, s => s is { Kind: DesignerStatementKinds.Event, Target: "PlaceOrderButton", Member: "Click", Handler: "OnPlaceOrder" });

        // A Button from the Toolbox: Button1, as Visual Basic names it, written in Visual Basic.
        var code = designer.DesignerCode!;
        await designer.CreateAsync("System.Windows.Forms.Button", 8 + 20, 31 + 200);
        Assert.Contains("        Button1 = New Button()\n", code.Text.Text, StringComparison.Ordinal);
        Assert.Contains("        Button1.Location = New Point(20, 200)\n", code.Text.Text, StringComparison.Ordinal);
        Assert.Contains("        Controls.Add(Button1)\n        Controls.Add(CancelOrderButton)\n", code.Text.Text, StringComparison.Ordinal);
        Assert.Contains("    Friend WithEvents Button1 As Button\n", code.Text.Text, StringComparison.Ordinal);
        Assert.Equal(["Button1"], designer.Selection);

        // Its default event: a handler hooked by its Handles clause in OrderForm.vb, shown there.
        var button = designer.Find("Button1")!;
        await designer.DefaultEventAsync(button.X + 5, button.Y + 5);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == form);
        var formCode = f.Shell.ActiveEditor!.Model;
        Assert.Contains("    Private Sub Button1_Click(sender As Object, e As EventArgs) Handles Button1.Click\n", formCode.Text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHandler", code.Text.Text, StringComparison.Ordinal);

        // Deleted: its Handles item goes with it, the method stays (as the classic IDE leaves it).
        f.Shell.Layout.OpenDocument(designer, focus: true);
        designer.SetSelection(["Button1"], notify: true);
        await designer.DeleteSelectionAsync();
        Assert.DoesNotContain("Button1", code.Text.Text, StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => formCode.Text.Text.Contains("    Private Sub Button1_Click(sender As Object, e As EventArgs)\n", StringComparison.Ordinal), seconds: 30,
            () => $"OrderForm.vb without Button1's Handles item:\n{formCode.Text.Text}\n{designer.Status}");

        // Saved: both files on disk, and the designer closes.
        await f.Shell.SaveAllAsync();
        Assert.False(designer.IsModified);
        Assert.DoesNotContain("Button1", await File.ReadAllTextAsync(Path.Combine(app, "OrderForm.Designer.vb"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("    Private Sub Button1_Click(sender As Object, e As EventArgs)\n", await File.ReadAllTextAsync(form, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        f.Shell.Layout.CloseDocument(designer);
        Assert.Empty(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
    }
}
