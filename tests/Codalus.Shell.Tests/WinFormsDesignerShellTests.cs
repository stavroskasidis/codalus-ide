using Avalonia.Headless.XUnit;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.WinForms;

namespace Codalus.Shell.Tests;

/// <summary>
/// M41a (ADR 0073, D31) in the headless shell on samples/WindowsDesktop's Windows Forms app. Off Windows a form opens as
/// code and View Designer says the designer is Windows only. With a fake designer host (any OS): the form's code file
/// opens in the designer; the Toolbox has the framework's controls and the project's own; a Toolbox item created on the
/// surface, a property set in the Properties window, an event handler from the Events view and a delete become code in
/// the designer file (and the code file), undo goes back through the designer file's own undo stack, F7 shows the code;
/// code the designer can't read opens as code with the reason.
/// </summary>
public sealed class WinFormsDesignerShellTests
{
    private static async Task<(ShellFixture Fixture, string App)> OpenAsync()
    {
        var f = ShellFixture.Create();
        var folder = f.CopySample("WindowsDesktop");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "WinFormsApp.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);
        return (f, Path.Combine(folder, "WinFormsApp"));
    }

    [AvaloniaFact]
    public async Task Off_windows_a_form_opens_as_code_and_view_designer_says_windows_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows the form opens in the designer (the Windows-only tests and smoke step).");
        var (f, app) = await OpenAsync();
        await using var _ = f;
        var form = Path.Combine(app, "OrderForm.cs");
        var editor = await f.Shell.OpenFileAsync(form);
        Assert.NotNull(editor);
        Assert.Equal(form, editor.Model.Path);
        Assert.Empty(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
        Assert.Contains("Windows only", f.Shell.StatusBar.Message, StringComparison.Ordinal);

        // View Designer (Shift+F7): says so, and the code stays.
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Context.GetValue("editorHasWinFormsDesigner") is true);
        f.Shell.Run("winforms.viewDesigner");
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("Windows Forms designer runs on Windows only", StringComparison.Ordinal)));
        Assert.Empty(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
        Assert.Same(editor, f.Shell.ActiveEditor);
        Assert.False(f.Shell.Toolbox.IsAvailable);
        Assert.NotNull(f.Shell.Toolbox.Message);
    }

    [AvaloniaFact]
    public async Task With_a_designer_host_forms_open_in_the_designer_and_changes_become_code()
    {
        var (f, app) = await OpenAsync();
        await using var _ = f;
        var hosts = new List<FakeWinFormsDesignerHost>();
        f.Shell.WinFormsDesigner = new WinFormsDesignerService(f.Shell.Solutions, _ => true, (_, _) => Task.FromResult(true), async (project, names, ct) =>
        {
            var properties = await f.Shell.ProjectManagement.GetPropertiesAsync(project, names, null, ct);
            return properties.Properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        })
        {
            IsWindows = () => true,
            DesignerHost = () => typeof(WinFormsDesignerShellTests).Assembly.Location,
            HostFactory = (_, _) =>
            {
                var host = new FakeWinFormsDesignerHost();
                hosts.Add(host);
                return Task.FromResult<IWinFormsDesignerHost>(host);
            },
        };
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 300);

        // The code file opens in the designer: the model from the C# host, loaded in the designer host, drawn.
        var form = Path.Combine(app, "OrderForm.cs");
        Assert.Null(await f.Shell.OpenFileAsync(form));
        var designer = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
        Assert.Same(designer, f.Shell.Layout.ActiveDocument);
        Assert.Equal("OrderForm.cs [Design]", designer.Title);
        try
        {
            await ShellFixture.PumpUntil(() => designer.FrameCount > 0, seconds: 120);
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail($"No frame: {designer.Status}\n{f.Shell.WinFormsDesignerLog.GetText()}");
        }

        var host = Assert.Single(hosts);
        Assert.Equal("OrderForm", host.Model!.ClassName);
        Assert.Equal("WinFormsApp.Controls.CounterControl", host.Model.Components.Single(c => c.Name == "counterControl").Type);
        Assert.Contains(designer.Components, c => c.Name == "placeOrderButton" && c.Parent == "buttonPanel");
        Assert.Contains(designer.Tray, t => t.Name == "orderToolTip");
        Assert.Equal((400d, 450d), (designer.FrameWidth, designer.FrameHeight));

        // The Toolbox: available, the framework's categories and the project's own control.
        Assert.True(f.Shell.Toolbox.IsAvailable);
        Assert.Contains(f.Shell.Toolbox.Categories, c => c.Title == "Common Controls");
        Assert.Contains(f.Shell.Toolbox.Categories, c => c.Title == "Containers");
        await ShellFixture.PumpUntil(() => f.Shell.Toolbox.Items.Any(i => i.Type == "WinFormsApp.Controls.CounterControl"), seconds: 60);
        Assert.Contains(f.Shell.Toolbox.Categories, c => c.Title == "WinFormsApp Components");
        var buttonItem = f.Shell.Toolbox.Items.First(i => i.Type == "System.Windows.Forms.Button");
        f.Shell.Toolbox.Filter = "Combo";
        Assert.All(f.Shell.Toolbox.Items, i => Assert.Contains("Combo", i.Name, StringComparison.Ordinal));
        f.Shell.Toolbox.Filter = string.Empty;

        // A click on the surface with the Button armed: button1, in the designer file, at the front of the form's controls.
        var code = designer.DesignerCode!;
        f.Shell.Toolbox.Armed = buttonItem;
        await designer.CreateAsync(f.Shell.Toolbox.Armed!.Type, 8 + 290, 31 + 300);
        Assert.Null(f.Shell.Toolbox.Armed);
        Assert.Contains("            button1 = new Button();\n", code.Text.Text, StringComparison.Ordinal);
        Assert.Contains("            button1.Location = new Point(290, 300);\n", code.Text.Text, StringComparison.Ordinal);
        Assert.Contains("            Controls.Add(button1);\n            Controls.Add(counterControl);\n", code.Text.Text, StringComparison.Ordinal);
        Assert.Contains("        private Button button1;\n", code.Text.Text, StringComparison.Ordinal);
        Assert.True(designer.IsModified);
        Assert.Equal(["button1"], designer.Selection);
        Assert.Equal(1, host.Loads); // the designer host already shows it: no reload for its own edit

        // The Properties window follows the designer's selection: Text set there is code.
        f.Shell.Layout.ShowToolWindow(PropertiesWindowViewModel.ToolWindowId, focus: false);
        var properties = f.Shell.PropertiesWindow;
        await properties.RefreshAsync();
        Assert.True(properties.HasDesignerSource);
        Assert.Equal("button1 System.Windows.Forms.Button", properties.Title);
        var text = properties.Categories.SelectMany(c => c.Rows).Single(r => r.Name == "Text");
        Assert.Equal("button1", text.Value);
        Assert.Contains(properties.Categories.SelectMany(c => c.Rows), r => r is { Name: "Anchor", IsAnchor: true } && r.AnchorTop!.IsChecked && !r.AnchorRight!.IsChecked);
        Assert.Contains(properties.Categories.SelectMany(c => c.Rows), r => r is { Name: "Enabled", IsChoice: true });
        Assert.Contains(properties.Categories.SelectMany(c => c.Rows), r => r is { Name: "BackColor", IsColor: true });
        text.Text = "Hello";
        await ShellFixture.PumpUntil(() => code.Text.Text.Contains("button1.Text = \"Hello\";", StringComparison.Ordinal));
        properties.Alphabetical = true;
        await properties.RefreshAsync();
        Assert.Single(properties.Categories);

        // Undo: the designer file's own undo stack, and the form loaded again from the code.
        designer.Undo();
        await ShellFixture.PumpUntil(() => !code.Text.Text.Contains("\"Hello\"", StringComparison.Ordinal) && host.Loads == 2, seconds: 30);
        Assert.Contains("button1.Text = \"button1\";", code.Text.Text, StringComparison.Ordinal);
        designer.Redo();
        await ShellFixture.PumpUntil(() => code.Text.Text.Contains("\"Hello\"", StringComparison.Ordinal) && host.Loads == 3, seconds: 30);

        // The Events view: the button's events; its action writes the handler in the code file and shows it.
        properties.ShowEvents = true;
        await properties.RefreshAsync();
        var click = properties.Categories.SelectMany(c => c.Rows).Single(r => r.Name == "Click");
        Assert.True(click.HasAction);
        click.Action!.Execute(null);
        await ShellFixture.PumpUntil(() => code.Text.Text.Contains("button1.Click += button1_Click;", StringComparison.Ordinal));
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == form);
        Assert.Contains("private void button1_Click(object? sender, EventArgs e)", f.Shell.ActiveEditor!.Model.Text.Text, StringComparison.Ordinal);
        properties.ShowEvents = false;

        // F7 from the designer shows the code; double-clicking a control with a handler goes to it.
        f.Shell.Layout.OpenDocument(designer, focus: true);
        f.Shell.Run("winforms.viewCode");
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == form);
        f.Shell.Layout.OpenDocument(designer, focus: true);
        var place = designer.Find("placeOrderButton")!;
        await designer.DefaultEventAsync(place.X + 5, place.Y + 5);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == form && f.Shell.ActiveEditor.Controller?.Editor.TextArea.Caret.Line > 1);
        Assert.DoesNotContain("OnPlaceOrderClicked_1", code.Text.Text, StringComparison.Ordinal);

        // A move, a nudge and a delete.
        f.Shell.Layout.OpenDocument(designer, focus: true);
        designer.SetSelection(["giftCheckBox"], notify: true);
        await designer.MoveSelectionAsync(10, 5, 120, 120);
        Assert.Contains("giftCheckBox.Location = new Point(106, 75);", code.Text.Text, StringComparison.Ordinal);
        await designer.NudgeAsync(1, 0, resize: false);
        Assert.Contains("giftCheckBox.Location = new Point(107, 75);", code.Text.Text, StringComparison.Ordinal);
        designer.SetSelection(["button1"], notify: true);
        await designer.DeleteSelectionAsync();
        Assert.DoesNotContain("button1 =", code.Text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("button1.Click", code.Text.Text, StringComparison.Ordinal);

        // Copy and paste: a new name, a little lower and to the right, the form loaded again.
        designer.SetSelection(["cancelButton"], notify: true);
        designer.Copy();
        designer.SetSelection(["buttonPanel"], notify: true);
        await designer.PasteAsync();
        await ShellFixture.PumpUntil(() => code.Text.Text.Contains("cancelButton1 = new Button();", StringComparison.Ordinal));
        Assert.Contains("buttonPanel.Controls.Add(cancelButton1);", code.Text.Text, StringComparison.Ordinal);
        Assert.Contains("cancelButton1.Location = new Point(313, 24);", code.Text.Text, StringComparison.Ordinal);
        Assert.Contains("cancelButton1.Name = \"cancelButton1\";", code.Text.Text, StringComparison.Ordinal);

        // A localizable form's components are refused (their properties are in the .resx: M41b).
        // Save writes the designer file.
        await f.Shell.SaveAllAsync();
        Assert.Contains("cancelButton1", await File.ReadAllTextAsync(Path.Combine(app, "OrderForm.Designer.cs"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.False(designer.IsModified);

        // Code the designer can't read: the designer closes and the code opens at the statement, with the reason.
        var unreadable = Path.Combine(app, "UnreadableForm.cs");
        await f.Shell.OpenFileAsync(unreadable);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("lambda", StringComparison.Ordinal)), seconds: 60);
        Assert.DoesNotContain(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>(), d => d.CodePath == unreadable);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == Path.Combine(app, "UnreadableForm.Designer.cs"));

        // Closing the designer stops its host and lets the text go.
        f.Shell.Layout.CloseDocument(designer);
        await ShellFixture.PumpUntil(() => f.Shell.PropertySource is null);
    }
}
