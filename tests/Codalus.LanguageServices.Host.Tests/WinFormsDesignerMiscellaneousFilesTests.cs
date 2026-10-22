using Codalus.LanguageServices.Host.WinForms;
using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// #76: the designer opens a form before the C# host has its project, with the form's code closed. Only the designer
/// file is open, so it sits alone in Miscellaneous Files, whose part of the class names no base: the form's base was
/// <c>object</c>, and it opened as code saying it "isn't a form". The read takes the code file's part into its own copy
/// of the solution, as when the code is open, so the base comes from the code file, a rename reaches the code file, and
/// new code keeps the project's implicit usings. Roslyn only, with the real workspace and no build host or restore, so
/// it runs on every OS (Miscellaneous Files lacks the code file's part everywhere).
/// </summary>
public sealed class WinFormsDesignerMiscellaneousFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-winforms-misc-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceManager _workspace = new();

    public WinFormsDesignerMiscellaneousFilesTests()
    {
        var sample = Path.Combine(RepoPaths.Root, "samples", "WindowsDesktop", "WinFormsApp");
        foreach (var file in Directory.EnumerateFiles(sample, "*", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var target = Path.Combine(_root, Path.GetRelativePath(sample, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string P(params string[] path) => Path.Combine([_root, .. path]);

    private void Open(string path) => _workspace.Open(path, File.ReadAllText(path), 1);

    private Task<WinFormsReadResult> ReadAsync(string path) => WinFormsFeature.ReadAsync(_workspace.CurrentSolution, path, _workspace.OpenText, Ct);

    private Task<WinFormsEditResult> EditAsync(string path, params DesignerEdit[] edits) =>
        WinFormsFeature.EditAsync(_workspace.CurrentSolution, new WinFormsEditParams(new TextDocumentIdentifier(DocumentUris.FromPath(path)), edits), _workspace.OpenText, _workspace.Version, Ct);

    private static TextDocumentEdit? EditOf(WinFormsEditResult result, string path) =>
        result.Edit.DocumentChanges.SingleOrDefault(d => Paths.Comparer.Equals(DocumentUris.ToPath(d.TextDocument.Uri), path));

    [Fact]
    public async Task A_form_read_from_its_designer_file_alone_takes_its_base_type_from_the_code_file()
    {
        var designer = P("OrderForm.Designer.cs");
        Open(designer);

        // As the shell's designer opens it while the C# host has no project: the designer file alone, in Miscellaneous Files.
        var solution = _workspace.CurrentSolution;
        var document = await WorkspaceManager.FindDocumentAsync(solution, DocumentUris.FromPath(designer), Ct);
        Assert.Equal(_workspace.MiscellaneousProjectId, document!.Project.Id);
        Assert.Null(await WorkspaceManager.FindDocumentAsync(solution, DocumentUris.FromPath(P("OrderForm.cs")), Ct));

        foreach (var path in new[] { P("OrderForm.cs"), designer })
        {
            var read = await ReadAsync(path);
            Assert.True(read.IsDesignable, $"{Path.GetFileName(path)}: {read.Problem}");
            Assert.Null(read.Problem);
            Assert.Equal("System.Windows.Forms.Form", read.Model!.BaseType);
            Assert.Null(read.Project);
            Assert.Equal("WinFormsApp.OrderForm", read.ClassFullName);
            Assert.Equal("WinFormsApp.Controls.CounterControl", read.Model.Components.Single(c => c.Name == "counterControl").Type);
            Assert.Contains("System.Windows.Forms", read.Model.Imports);
            Assert.Contains("System", read.Model.Imports);
            Assert.True(read.IsPartial);
        }

        // A user control in a folder of its own, as the Toolbox's double-click opens it.
        var counter = P("Controls", "CounterControl.Designer.cs");
        Open(counter);
        var control = await ReadAsync(counter);
        Assert.True(control.IsDesignable, control.Problem);
        Assert.Equal("System.Windows.Forms.UserControl", control.Model!.BaseType);
        Assert.True(control.IsPartial);

        // The workspace's own Miscellaneous Files still has only what the editor opened.
        Assert.Null(await WorkspaceManager.FindDocumentAsync(_workspace.CurrentSolution, DocumentUris.FromPath(P("OrderForm.cs")), Ct));
    }

    [Fact]
    public async Task A_rename_with_only_the_designer_file_open_renames_the_uses_in_the_code_file()
    {
        var designer = P("OrderForm.Designer.cs");
        var code = P("OrderForm.cs");
        Open(designer);

        var result = await EditAsync(designer, new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "placeOrderButton", NewName = "orderButton" });
        Assert.Null(result.Error);
        var codeEdit = EditOf(result, code);
        Assert.True(codeEdit is not null, $"No edit of OrderForm.cs: {string.Join(", ", result.Edit.DocumentChanges.Select(d => Path.GetFileName(DocumentUris.ToPath(d.TextDocument.Uri))))}");
        Assert.Null(codeEdit.TextDocument.Version);
        var edit = Assert.Single(codeEdit.Edits);
        Assert.InRange(20, edit.Range.Start.Line, Math.Max(edit.Range.Start.Line, edit.Range.End.Line - 1));
        Assert.Contains("orderButton.Enabled = customerTextBox.TextLength > 0;", edit.NewText, StringComparison.Ordinal);
        var designerEdit = EditOf(result, designer)!;
        Assert.Contains(designerEdit.Edits, e => e.NewText.Contains("orderButton.Click += OnPlaceOrderClicked;", StringComparison.Ordinal));
        Assert.Contains("orderButton", result.Model!.Components.Select(c => c.Name));
    }

    [Fact]
    public async Task A_partial_class_no_part_of_which_names_a_base_still_opens_as_code()
    {
        File.WriteAllText(P("Helper.cs"), "namespace WinFormsApp;\n\npartial class Helper\n{\n}\n");
        File.WriteAllText(P("Helper.Designer.cs"), "namespace WinFormsApp;\n\npartial class Helper\n{\n    private void InitializeComponent()\n    {\n    }\n}\n");

        // The designer file alone (its code file's part joins the read), then both open.
        Open(P("Helper.Designer.cs"));
        var alone = await ReadAsync(P("Helper.cs"));
        Open(P("Helper.cs"));
        var both = await ReadAsync(P("Helper.cs"));
        foreach (var read in new[] { alone, both })
        {
            Assert.False(read.IsDesignable);
            Assert.Equal("Helper isn't a form, a user control or another control, so it opens as code.", read.Problem);
        }
    }

    [Fact]
    public async Task With_its_code_open_too_an_unresolved_base_decides_by_name()
    {
        // Today's path, which the copy mirrors: both parts in Miscellaneous Files.
        var designer = P("OrderForm.Designer.cs");
        var code = P("OrderForm.cs");
        Open(designer);
        Open(code);

        var read = await ReadAsync(code);
        Assert.True(read.IsDesignable, read.Problem);
        Assert.Equal("System.Windows.Forms.Form", read.Model!.BaseType);
        Assert.True(read.IsPartial);

        var result = await EditAsync(designer, new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "placeOrderButton", NewName = "orderButton" });
        Assert.Null(result.Error);
        var codeEdit = EditOf(result, code)!;
        Assert.Equal(1, codeEdit.TextDocument.Version);
        Assert.Contains(codeEdit.Edits, e => e.NewText.Contains("orderButton.Enabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_handler_written_from_Miscellaneous_Files_uses_the_projects_implicit_usings()
    {
        var designer = P("OrderForm.Designer.cs");
        Open(designer);

        // What the shell sends from the designer host for the form's FormClosing (not hooked in the fixture).
        var result = await EditAsync(designer, new DesignerEdit
        {
            Kind = DesignerEditKinds.AddEvent,
            Component = string.Empty,
            Event = "FormClosing",
            HandlerParameters = ["System.Object sender", "System.Windows.Forms.FormClosingEventArgs e"],
            DelegateType = "System.Windows.Forms.FormClosingEventHandler",
        });
        Assert.Null(result.Error);
        var codeEdit = EditOf(result, P("OrderForm.cs"))!;
        Assert.Contains(codeEdit.Edits, e => e.NewText.Contains("private void OrderForm_FormClosing(object? sender, FormClosingEventArgs e)", StringComparison.Ordinal));
        Assert.Contains(EditOf(result, designer)!.Edits, e => e.NewText.Contains("FormClosing += OrderForm_FormClosing;", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _workspace.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
