using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>samples/WindowsDesktop's Windows Forms app (WinFormsApp.slnx) in the real build and language hosts, on every OS.</summary>
public sealed class WinFormsLanguageFixture : LanguageFixture
{
    protected override (string Sample, string Solution, int Projects) Sample => ("WindowsDesktop", "WinFormsApp.slnx", 1);

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
            Assert.Fail("The Windows Forms app's design-time build has no Windows Forms references: "
                + string.Join("; ", Solutions.CompilerInputs.Select(i => $"{Path.GetFileName(i.Key)} {i.Value.State} {i.Value.Error}")) + "\n" + string.Join('\n', Log.TakeLast(40)));
        }
    }
}

/// <summary>
/// M41a (ADR 0073): the Windows Forms designer's requests in the C# host with the project's compilation: the model's
/// types named by the compiler, why a file opens as code, the edits of the designer file and of the code file (a new
/// event handler with the event's own parameters, a rename's other uses), and the project's components for the Toolbox.
/// </summary>
public sealed class WinFormsDesignerLanguageTests(WinFormsLanguageFixture winForms) : IClassFixture<WinFormsLanguageFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Form(string name) => winForms.File("WinFormsApp", name);

    private static string Uri(string path) => DocumentUris.FromPath(path);

    [Fact]
    public async Task Reads_a_form_through_the_compilation()
    {
        var result = await winForms.Client.WinFormsReadAsync(Uri(Form("OrderForm.cs")), Ct);
        Assert.NotNull(result);
        Assert.True(result.IsDesignable);
        Assert.Null(result.Problem);
        Assert.Equal(Form("OrderForm.Designer.cs"), result.DesignerFile);
        Assert.Equal(Form("OrderForm.resx"), result.ResourceFile);
        Assert.Equal(winForms.File("WinFormsApp", "WinFormsApp.csproj"), result.Project);
        Assert.Equal("WinFormsApp.OrderForm", result.ClassFullName);
        var model = result.Model!;
        Assert.Equal("System.Windows.Forms.Form", model.BaseType);
        Assert.Contains("System.Windows.Forms", model.Imports);
        Assert.Contains("System.Drawing", model.Imports);
        Assert.Equal("WinFormsApp.Controls.CounterControl", model.Components.Single(c => c.Name == "counterControl").Type);

        // The compiler says which members are an enum's: DockStyle.Fill is, Color.SteelBlue and SystemColors.GrayText aren't.
        DesignerValue Value(string target, string member) => model.Statements.Single(s => s is { Kind: DesignerStatementKinds.Assign } && s.Target == target && s.Member == member).Value!;
        Assert.Equal(DesignerValueKinds.Enum, Value("historyListBox", "Dock").Kind);
        Assert.Equal(DesignerValueKinds.Member, Value("placeOrderButton", "BackColor").Kind);
        Assert.Equal(DesignerValueKinds.Member, Value("notesTextBox", "ForeColor").Kind);

        // From the designer file too, and the .NET Framework style.
        var about = await winForms.Client.WinFormsReadAsync(Uri(Form("AboutForm.Designer.cs")), Ct);
        Assert.True(about!.Model!.QualifiedNames);
        Assert.Equal(Form("AboutForm.cs"), about.CodeFile);

        // A user control.
        var control = await winForms.Client.WinFormsReadAsync(Uri(winForms.File("WinFormsApp", "Controls", "CounterControl.cs")), Ct);
        Assert.Equal("System.Windows.Forms.UserControl", control!.Model!.BaseType);
    }

    [Fact]
    public async Task Code_the_designer_cant_read_opens_as_code_with_the_reason()
    {
        var unreadable = await winForms.Client.WinFormsReadAsync(Uri(Form("UnreadableForm.cs")), Ct);
        Assert.True(unreadable!.IsDesignable);
        Assert.Null(unreadable.Model);
        var line = (await File.ReadAllLinesAsync(Form("UnreadableForm.Designer.cs"), Ct)).ToList().FindIndex(l => l.Contains("=> Close()", StringComparison.Ordinal));
        Assert.Equal(line, unreadable.ProblemLine);
        Assert.Contains($"line {line + 1}", unreadable.Problem, StringComparison.Ordinal);
        Assert.Contains("lambda", unreadable.Problem, StringComparison.Ordinal);

        var program = await winForms.Client.WinFormsReadAsync(Uri(Form("Program.cs")), Ct);
        Assert.Null(program!.Model);
        Assert.Contains("Program.Designer.cs doesn't exist", program.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_event_handler_goes_in_the_code_file_with_the_events_parameters()
    {
        var designer = Form("OrderForm.Designer.cs");
        var code = Form("OrderForm.cs");
        var result = await winForms.Client.WinFormsEditAsync(new WinFormsEditParams(new TextDocumentIdentifier(Uri(designer)),
            [new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = "giftCheckBox", Event = "CheckedChanged" }]), Ct);
        Assert.NotNull(result);
        Assert.Null(result.Error);
        var designerEdit = result.Edit.DocumentChanges.Single(d => d.TextDocument.Uri == Uri(designer));
        Assert.Equal("            giftCheckBox.CheckedChanged += giftCheckBox_CheckedChanged;\n", Assert.Single(designerEdit.Edits).NewText);
        var codeEdit = result.Edit.DocumentChanges.Single(d => d.TextDocument.Uri == Uri(code));
        Assert.Equal("\n        private void giftCheckBox_CheckedChanged(object? sender, EventArgs e)\n        {\n\n        }\n", Assert.Single(codeEdit.Edits).NewText);
        Assert.Equal(Uri(code), result.Handler!.Uri);
        var lines = (await File.ReadAllLinesAsync(code, Ct)).Length;
        Assert.Equal(new Position(lines - 2 + 3, 12), result.Handler.Range.Start);
        Assert.Contains(result.Model!.Statements, s => s is { Kind: DesignerStatementKinds.Event, Target: "giftCheckBox", Handler: "giftCheckBox_CheckedChanged" });

        // A handler that exists: hooked, not written again, and where it is.
        var existing = await winForms.Client.WinFormsEditAsync(new WinFormsEditParams(new TextDocumentIdentifier(Uri(designer)),
            [new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = "customerLabel", Event = "Click", Handler = "OnCustomerChanged" }]), Ct);
        Assert.DoesNotContain(existing!.Edit.DocumentChanges, d => d.TextDocument.Uri == Uri(code));
        Assert.Equal(Uri(code), existing.Handler!.Uri);
        Assert.Contains("placeOrderButton.Enabled", (await File.ReadAllLinesAsync(code, Ct))[existing.Handler.Range.Start.Line], StringComparison.Ordinal);

        // .NET Framework's delegate creation names the event's own type; the handler in a file without nullable context.
        var about = await winForms.Client.WinFormsEditAsync(new WinFormsEditParams(new TextDocumentIdentifier(Uri(Form("AboutForm.Designer.cs"))),
            [new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = "versionLabel", Event = "MouseDown" }]), Ct);
        Assert.Contains("new System.Windows.Forms.MouseEventHandler(this.versionLabel_MouseDown)", about!.Edit.DocumentChanges[0].Edits[0].NewText, StringComparison.Ordinal);
        Assert.Contains("private void versionLabel_MouseDown(object sender, MouseEventArgs e)", about.Edit.DocumentChanges[1].Edits[0].NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rename_reaches_the_code_file_and_a_stale_version_is_refused()
    {
        var designer = Form("OrderForm.Designer.cs");
        var result = await winForms.Client.WinFormsEditAsync(new WinFormsEditParams(new TextDocumentIdentifier(Uri(designer)),
            [new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "placeOrderButton", NewName = "submitButton" }]), Ct);
        Assert.Null(result!.Error);
        var code = result.Edit.DocumentChanges.Single(d => d.TextDocument.Uri == Uri(Form("OrderForm.cs")));
        Assert.Equal("            submitButton.Enabled = customerTextBox.TextLength > 0;\n", Assert.Single(code.Edits).NewText);
        Assert.Contains(result.Model!.Components, c => c.Name == "submitButton");

        await winForms.OpenAsync(designer);
        var stale = await winForms.Client.WinFormsEditAsync(new WinFormsEditParams(new TextDocumentIdentifier(Uri(designer)),
            [new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = "giftCheckBox" }]) { Version = 7 }, Ct);
        Assert.Contains("changed meanwhile", stale!.Error, StringComparison.Ordinal);
        Assert.Empty(stale.Edit.DocumentChanges);
    }

    [Fact]
    public async Task A_localizable_forms_resources_are_read_and_edited_with_its_code()
    {
        // M41b: the read says which forms are localizable, their cultures and the tray's order.
        var about = await winForms.Client.WinFormsReadAsync(Uri(Form("AboutForm.cs")), Ct);
        Assert.True(about!.IsLocalizable);
        Assert.Equal(["fr"], about.Cultures);
        var inventory = await winForms.Client.WinFormsReadAsync(Uri(Form("InventoryForm.cs")), Ct);
        Assert.False(inventory!.IsLocalizable);
        Assert.Equal("mainMenuStrip", inventory.TrayOrder[0]);
        Assert.Equal("inventoryErrorProvider", inventory.TrayOrder[^1]);
        Assert.Equal("System.Windows.Forms.BindingSource", inventory.Model!.Components.Single(c => c.Name == "itemBindingSource").Type);

        // A rename of a localized component: the designer file, every culture's .resx, and the handler's file untouched.
        var renamed = await winForms.Client.WinFormsEditAsync(new WinFormsEditParams(new TextDocumentIdentifier(Uri(Form("AboutForm.Designer.cs"))),
            [new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "okButton", NewName = "closeButton" }]), Ct);
        Assert.Null(renamed!.Error);
        Assert.Contains(renamed.Edit.DocumentChanges, d => d.TextDocument.Uri == Uri(Form("AboutForm.Designer.cs")));
        var resx = renamed.Edit.DocumentChanges.Single(d => d.TextDocument.Uri == Uri(Form("AboutForm.resx")));
        Assert.Equal(9, resx.Edits.Length);
        Assert.Contains(resx.Edits, e => e.NewText.Contains("<value>closeButton</value>", StringComparison.Ordinal));
        Assert.Single(renamed.Edit.DocumentChanges.Single(d => d.TextDocument.Uri == Uri(Form("AboutForm.fr.resx"))).Edits);

        // A new culture's file: created by the edit (no version), with only its key.
        var german = await winForms.Client.WinFormsEditAsync(new WinFormsEditParams(new TextDocumentIdentifier(Uri(Form("AboutForm.Designer.cs"))),
            [new DesignerEdit { Kind = DesignerEditKinds.SetResource, Key = "okButton.Text", Culture = "de", Resource = DesignerResource.String("Schließen") }]), Ct);
        var created = german!.Edit.DocumentChanges.Single();
        Assert.Equal((Uri(Form("AboutForm.de.resx")), (int?)null), (created.TextDocument.Uri, created.TextDocument.Version));
        Assert.Contains("<value>Schließen</value>", Assert.Single(created.Edits).NewText, StringComparison.Ordinal);
        Assert.Equal(new Position(0, 0), created.Edits[0].Range.Start);
    }

    [Fact]
    public async Task The_toolbox_lists_the_projects_own_controls()
    {
        var toolbox = await winForms.Client.WinFormsToolboxAsync(winForms.File("WinFormsApp", "WinFormsApp.csproj"), Ct);
        var counter = Assert.Single(toolbox!.Items);
        Assert.Equal(("WinFormsApp.Controls.CounterControl", "CounterControl", "WinFormsApp", true), (counter.Type, counter.Name, counter.Project, counter.IsControl));
    }
}
