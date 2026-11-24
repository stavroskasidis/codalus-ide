using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>samples/WindowsDesktop's Visual Basic Windows Forms app (WinFormsVbApp.slnx) in the real build and language hosts, on every OS.</summary>
public sealed class WinFormsVbLanguageFixture : LanguageFixture
{
    protected override (string Sample, string Solution, int Projects) Sample => ("WindowsDesktop", "WinFormsVbApp.slnx", 1);

    /// <summary>The design-time build after the restore: Windows Forms' references on the compiler's command line.</summary>
    protected override async Task ReadyAsync()
    {
        try
        {
            await WaitUntilAsync(() => Solutions.CompilerInputs.Count(i => i.Value.Arguments.Any(a => a.Contains("System.Windows.Forms.dll", StringComparison.OrdinalIgnoreCase))) == 1
                                       && Languages.Status("csharp") is { State: WorkspaceStates.Ready, Projects: 1 });
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail("The Visual Basic Windows Forms app's design-time build has no Windows Forms references: "
                + string.Join("; ", Solutions.CompilerInputs.Select(i => $"{Path.GetFileName(i.Key)} {i.Value.State} {i.Value.Error}")) + "\n" + string.Join('\n', Log.TakeLast(40)));
        }
    }
}

/// <summary>
/// ADR 0073, Visual Basic forms: the Windows Forms designer's requests in the language host on a Visual Basic project's
/// compilation: the model's types named by the compiler (the project's root namespace and imports), the events its
/// Handles clauses hook, a new handler hooked with Handles in the code file (its component made WithEvents when it
/// wasn't), removed events and components taking their Handles items, a rename's Handles clauses, and the Toolbox.
/// </summary>
public sealed class WinFormsDesignerVisualBasicLanguageTests(WinFormsVbLanguageFixture winForms) : IClassFixture<WinFormsVbLanguageFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Form(string name) => winForms.File("WinFormsVbApp", name);

    private static string Uri(string path) => DocumentUris.FromPath(path);

    private async Task<WinFormsEditResult> EditAsync(string designer, params DesignerEdit[] edits)
    {
        var result = await winForms.Client.WinFormsEditAsync(new WinFormsEditParams(new TextDocumentIdentifier(Uri(Form(designer))), edits), Ct);
        Assert.NotNull(result);
        Assert.Null(result.Error);
        return result;
    }

    /// <summary>A file's text with an edit's changes (the edit isn't applied to the fixture: each test starts from the sample).</summary>
    private static string Applied(string path, TextDocumentEdit edit)
    {
        var text = Microsoft.CodeAnalysis.Text.SourceText.From(File.ReadAllText(path));
        return text.WithChanges(edit.Edits.Select(e => new Microsoft.CodeAnalysis.Text.TextChange(Positions.ToSpan(text, e.Range), e.NewText))).ToString();
    }

    private TextDocumentEdit EditOf(WinFormsEditResult result, string file) => result.Edit.DocumentChanges.Single(d => d.TextDocument.Uri == Uri(Form(file)));

    [Fact]
    public async Task Reads_a_form_through_the_compilation()
    {
        var result = await winForms.Client.WinFormsReadAsync(Uri(Form("OrderForm.vb")), Ct);
        Assert.NotNull(result);
        Assert.True(result.IsDesignable, result.Problem);
        Assert.Null(result.Problem);
        Assert.False(result.IsPartial);
        Assert.Equal(Form("OrderForm.Designer.vb"), result.DesignerFile);
        Assert.Equal(Form("OrderForm.resx"), result.ResourceFile);
        Assert.Equal(winForms.File("WinFormsVbApp", "WinFormsVbApp.vbproj"), result.Project);
        Assert.Equal("WinFormsVbApp.OrderForm", result.ClassFullName);
        var model = result.Model!;
        Assert.Equal(DesignerLanguages.VisualBasic, model.Language);
        Assert.Equal("System.Windows.Forms.Form", model.BaseType);

        // The class is in the project's root namespace without saying so; the project's imports are the compilation's.
        Assert.Equal("WinFormsVbApp", model.Namespace);
        Assert.Contains("System.Windows.Forms", model.Imports);
        Assert.Contains("Microsoft.VisualBasic", model.Imports);

        // The compiler names the types: an enum's member, a shared property, the project's resources.
        DesignerValue Value(string target, string member) => model.Statements.Single(s => s is { Kind: DesignerStatementKinds.Assign } && s.Target == target && s.Member == member).Value!;
        Assert.Equal(DesignerValueKinds.Enum, Value("CancelOrderButton", "DialogResult").Kind);
        Assert.Equal(DesignerValueKinds.Member, Value("PlaceOrderButton", "BackColor").Kind);
        Assert.Equal(("WinFormsVbApp.My.Resources.Resources", "PlaceOrder"), (Value("PlaceOrderButton", "Text").Type, Value("PlaceOrderButton", "Text").Value));

        // The events its Handles clauses hook, in OrderForm.vb.
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Event, Target: "", Member: "Load", Handler: "OrderForm_Load" });
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Event, Target: "CancelOrderButton", Member: "Click", Handler: "OnPlaceOrder" });

        // From the designer file too; and the .NET Framework style's AddHandler.
        var about = await winForms.Client.WinFormsReadAsync(Uri(Form("AboutForm.Designer.vb")), Ct);
        Assert.True(about!.IsDesignable, about.Problem);
        Assert.True(about.Model!.UsesThis);
        Assert.Contains(about.Model.Statements, s => s is { Kind: DesignerStatementKinds.Event, Target: "OkButton", Member: "Click", Handler: "OkButton_Click" });
    }

    [Fact]
    public async Task A_new_event_handler_goes_in_the_code_file_with_a_Handles_clause()
    {
        var result = await EditAsync("OrderForm.Designer.vb", new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = "GiftCheckBox", Event = "CheckedChanged" });

        // Nothing in InitializeComponent: the method, with the event's parameters, hooked by its Handles clause.
        Assert.DoesNotContain(result.Edit.DocumentChanges, d => d.TextDocument.Uri == Uri(Form("OrderForm.Designer.vb")));
        var code = Applied(Form("OrderForm.vb"), EditOf(result, "OrderForm.vb"));
        Assert.EndsWith("    End Sub\n\n    Private Sub GiftCheckBox_CheckedChanged(sender As Object, e As EventArgs) Handles GiftCheckBox.CheckedChanged\n\n    End Sub\nEnd Class\n", code, StringComparison.Ordinal);
        Assert.Equal(Uri(Form("OrderForm.vb")), result.Handler!.Uri);
        Assert.Equal(new Position(code.Split('\n').Length - 4, 8), result.Handler.Range.Start);
        Assert.Contains(result.Model!.Statements, s => s is { Kind: DesignerStatementKinds.Event, Target: "GiftCheckBox", Member: "CheckedChanged", Handler: "GiftCheckBox_CheckedChanged" });

        // The event's own parameter types, through the project's imports.
        var mouse = await EditAsync("OrderForm.Designer.vb", new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = "LogoPictureBox", Event = "MouseDown" });
        Assert.Contains("Private Sub LogoPictureBox_MouseDown(sender As Object, e As MouseEventArgs) Handles LogoPictureBox.MouseDown",
            Applied(Form("OrderForm.vb"), EditOf(mouse, "OrderForm.vb")), StringComparison.Ordinal);

        // A handler that exists: its Handles clause gets the event; the form's own events are MyBase's.
        var existing = await EditAsync("OrderForm.Designer.vb", new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = "", Event = "Shown", Handler = "OrderForm_Load" });
        Assert.Contains("Private Sub OrderForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load, MyBase.Shown\n",
            Applied(Form("OrderForm.vb"), EditOf(existing, "OrderForm.vb")), StringComparison.Ordinal);
        Assert.Contains("OrderDatePicker.Value", (await File.ReadAllLinesAsync(Form("OrderForm.vb"), Ct))[existing.Handler!.Range.Start.Line], StringComparison.Ordinal);

        // Another handler for a hooked event: the event moves from the old method's Handles clause to the new one's.
        var moved = await EditAsync("OrderForm.Designer.vb", new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = "CancelOrderButton", Event = "Click", Handler = "OnCancel" });
        var movedCode = Applied(Form("OrderForm.vb"), EditOf(moved, "OrderForm.vb"));
        Assert.Contains("Handles PlaceOrderButton.Click\n", movedCode, StringComparison.Ordinal);
        Assert.Contains("Private Sub OnCancel(sender As Object, e As EventArgs) Handles CancelOrderButton.Click\n", movedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_component_that_isnt_WithEvents_becomes_it_for_a_new_handler()
    {
        // AboutForm's OkButton (the .NET Framework designer's AddHandler model): its Click stays an AddHandler, a new event is a Handles clause.
        var click = await EditAsync("AboutForm.Designer.vb", new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = "OkButton", Event = "Click", Handler = "OnOk" });
        var clickDesigner = Applied(Form("AboutForm.Designer.vb"), EditOf(click, "AboutForm.Designer.vb"));
        Assert.Contains("        AddHandler Me.OkButton.Click, AddressOf Me.OnOk\n", clickDesigner, StringComparison.Ordinal);
        Assert.Contains("    Private Sub OnOk(sender As Object, e As EventArgs)\n\n    End Sub\n", Applied(Form("AboutForm.vb"), EditOf(click, "AboutForm.vb")), StringComparison.Ordinal);

        var enter = await EditAsync("AboutForm.Designer.vb", new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = "OkButton", Event = "MouseEnter" });
        Assert.Contains("    Friend WithEvents OkButton As System.Windows.Forms.Button\n", Applied(Form("AboutForm.Designer.vb"), EditOf(enter, "AboutForm.Designer.vb")), StringComparison.Ordinal);
        Assert.Contains("    Private Sub OkButton_MouseEnter(sender As Object, e As EventArgs) Handles OkButton.MouseEnter\n",
            Applied(Form("AboutForm.vb"), EditOf(enter, "AboutForm.vb")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removed_events_and_components_take_their_Handles_items()
    {
        var removed = await EditAsync("OrderForm.Designer.vb", new DesignerEdit { Kind = DesignerEditKinds.RemoveEvent, Component = "CustomerTextBox", Event = "TextChanged" });
        var code = Applied(Form("OrderForm.vb"), EditOf(removed, "OrderForm.vb"));
        Assert.Contains("    Private Sub CustomerTextBox_TextChanged(sender As Object, e As EventArgs)\n", code, StringComparison.Ordinal);
        Assert.DoesNotContain(removed.Model!.Statements, s => s is { Kind: DesignerStatementKinds.Event, Target: "CustomerTextBox" });

        // A removed component's (the method stays, as the classic IDE leaves it; its other items stay).
        var component = await EditAsync("OrderForm.Designer.vb", new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = "CancelOrderButton" });
        var after = Applied(Form("OrderForm.vb"), EditOf(component, "OrderForm.vb"));
        Assert.Contains("Private Sub OnPlaceOrder(sender As Object, e As EventArgs) Handles PlaceOrderButton.Click\n", after, StringComparison.Ordinal);
        Assert.DoesNotContain("CancelOrderButton", Applied(Form("OrderForm.Designer.vb"), EditOf(component, "OrderForm.Designer.vb")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rename_renames_the_Handles_clauses_and_the_codes_uses()
    {
        var result = await EditAsync("OrderForm.Designer.vb", new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "PlaceOrderButton", NewName = "SubmitButton" });
        var code = Applied(Form("OrderForm.vb"), EditOf(result, "OrderForm.vb"));
        Assert.Contains("Handles SubmitButton.Click, CancelOrderButton.Click\n", code, StringComparison.Ordinal);
        Assert.Contains("SubmitButton.Enabled = CustomerTextBox.TextLength > 0", code, StringComparison.Ordinal);
        Assert.Contains("If(sender Is SubmitButton,", code, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceOrderButton", Applied(Form("OrderForm.Designer.vb"), EditOf(result, "OrderForm.Designer.vb")), StringComparison.Ordinal);
        Assert.Contains(result.Model!.Statements, s => s is { Kind: DesignerStatementKinds.Event, Target: "SubmitButton", Member: "Click" });
    }

    [Fact]
    public async Task Members_and_Handles_events_written_in_another_case_are_read_as_declared()
    {
        // Visual Basic compiles customerlabel.text and Handles customertextbox.textchanged: the compilation names them.
        var designer = Form("OrderForm.Designer.vb");
        var code = Form("OrderForm.vb");
        await winForms.OpenAsync(designer);
        await winForms.OpenAsync(code);
        const string Text = "CustomerLabel.Text = \"&Customer:\"", CasedText = "customerlabel.text = \"&Customer:\"";
        const string Handles = "Handles CustomerTextBox.TextChanged", CasedHandles = "Handles customertextbox.textchanged";
        await winForms.EditAsync(designer, Text, CasedText);
        await winForms.EditAsync(code, Handles, CasedHandles);
        try
        {
            var read = await winForms.Client.WinFormsReadAsync(Uri(code), Ct);
            Assert.True(read!.IsDesignable, read.Problem);
            var model = read.Model!;
            Assert.Single(model.Statements, s => s is { Kind: DesignerStatementKinds.Assign, Target: "CustomerLabel", Member: "Text" });
            Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Event, Target: "CustomerTextBox", Member: "TextChanged", Handler: "CustomerTextBox_TextChanged" });

            // Its Text changed where it is, as written: no second Text line.
            var result = await EditAsync("OrderForm.Designer.vb", new DesignerEdit { Kind = DesignerEditKinds.SetProperty, Component = "CustomerLabel", Property = "Text", Value = DesignerValue.String("&Name:") });
            var edit = Assert.Single(EditOf(result, "OrderForm.Designer.vb").Edits);
            Assert.Equal("        customerlabel.text = \"&Name:\"\n", edit.NewText);
        }
        finally
        {
            await winForms.EditAsync(designer, CasedText, Text);
            await winForms.EditAsync(code, CasedHandles, Handles);
        }
    }

    [Fact]
    public async Task Toolbox_lists_the_projects_components()
    {
        var toolbox = await winForms.Client.WinFormsToolboxAsync(winForms.File("WinFormsVbApp", "WinFormsVbApp.vbproj"), Ct);
        var badge = Assert.Single(toolbox!.Items, i => i.Project == "WinFormsVbApp");
        Assert.Equal(("WinFormsVbApp.Controls.BadgeControl", "BadgeControl", true), (badge.Type, badge.Name, badge.IsControl));
    }
}
