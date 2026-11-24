using Codalus.LanguageServices.Host.WinForms;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// ADR 0073, Visual Basic forms: Visual Basic's names ignore case, so a designer file may write a member in another case
/// than its declaration's (<c>BUTTON1.text = "Go"</c>), which the compiler takes. The reader names it as declared, through
/// the compilation, so the designer and the writer see the one property <c>Text</c> (not a second one, or a new line for
/// it); edited text, which the compilation hasn't seen, is named the same way. On an in-memory compilation of the
/// designer file with stand-ins for Windows Forms' types (Roslyn only, every OS).
/// </summary>
public sealed class WinFormsDesignerVisualBasicMemberCaseTests
{
    private const string Stubs = """
        Namespace System.Windows.Forms
            Public Class Control
                Public Property Text As String
                Public Property Location As System.Drawing.Point
                Public Property Anchor As AnchorStyles
                Public ReadOnly Property Controls As ControlCollection
                Public Event Click As System.EventHandler
                Public Sub SuspendLayout()
                End Sub
                Public Sub ResumeLayout(performLayout As Boolean)
                End Sub
            End Class
            Public Class ControlCollection
                Public Sub Add(value As Control)
                End Sub
            End Class
            Public Class Form
                Inherits Control
                Public Event Load As System.EventHandler
            End Class
            Public Class Button
                Inherits Control
            End Class
            <System.Flags> Public Enum AnchorStyles
                None = 0
                Top = 1
                Bottom = 2
                Left = 4
                Right = 8
            End Enum
        End Namespace
        Namespace System.Drawing
            Public Structure Point
                Public Sub New(x As Integer, y As Integer)
                End Sub
            End Structure
        End Namespace
        """;

    // What the classic IDE writes, in other cases: the compiler takes each of them.
    private const string Designer = """
        Imports System.Windows.Forms
        Imports System.Drawing

        Partial Class CasedForm
            Inherits Form

            Private Sub InitializeComponent()
                button1 = New button()
                suspendlayout()
                '
                ' Button1
                '
                BUTTON1.anchor = anchorstyles.top Or AnchorStyles.LEFT
                button1.location = New point(12, 12)
                BUTTON1.text = "Go"
                AddHandler button1.click, AddressOf onclick
                '
                ' CasedForm
                '
                controls.add(button1)
                text = "Cased"
                resumelayout(False)
            End Sub

            Friend WithEvents Button1 As Button

            Private Sub OnClick(sender As Object, e As System.EventArgs)
            End Sub
        End Class

        """;

    private static readonly VisualBasicParseOptions s_options = new();

    /// <summary>The designer file read through its compilation, and a writer whose reads of edited text replay that read.</summary>
    private static (DesignerSource Source, DesignerWriter Writer) ReadThroughCompilation()
    {
        var tree = VisualBasicSyntaxTree.ParseText(Designer, s_options, "CasedForm.Designer.vb", cancellationToken: TestContext.Current.CancellationToken);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = VisualBasicCompilation.Create("Cased", [tree, VisualBasicSyntaxTree.ParseText(Stubs, s_options, cancellationToken: TestContext.Current.CancellationToken)], references,
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));

        var resolver = new ReplayDesignerTypeResolver { Semantic = new SemanticDesignerTypeResolver(compilation.GetSemanticModel(tree)), SemanticTree = tree };
        DesignerSource Read(SourceText text) =>
            DesignerReader.Read(text.ContentEquals(tree.GetText()) ? tree : VisualBasicSyntaxTree.ParseText(text, s_options, "CasedForm.Designer.vb"), "System.Windows.Forms.Form", ["System.Windows.Forms", "System.Drawing"], resolver);
        return (Read(tree.GetText()), new DesignerWriter(Read));
    }

    private static DesignerStatement Assignment(DesignerModel model, string target, string member) =>
        Assert.Single(model.Statements, s => s.Kind == DesignerStatementKinds.Assign && s.Target == target && s.Member == member);

    [Fact]
    public void Members_written_in_another_case_are_read_as_declared()
    {
        var model = ReadThroughCompilation().Source.Model;
        Assert.Equal("Button1", Assert.Single(model.Components, c => !c.IsRoot && !c.IsLocal).Name);
        Assert.Equal("System.Windows.Forms.Button", model.Components.Single(c => c.Name == "Button1").Type);
        Assert.Equal("Go", Assignment(model, "Button1", "Text").Value!.Value);
        Assert.Equal("Cased", Assignment(model, string.Empty, "Text").Value!.Value);
        Assert.Equal("System.Drawing.Point", Assignment(model, "Button1", "Location").Value!.Type);
        var anchor = Assignment(model, "Button1", "Anchor").Value!;
        Assert.Equal("System.Windows.Forms.AnchorStyles", anchor.Type);
        Assert.Equal(["Top", "Left"], anchor.Members!);
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Call, Target: "", Member: "Controls.Add" });
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Layout, Target: "", Member: "SuspendLayout" });
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Layout, Target: "", Member: "ResumeLayout" });
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Event, Target: "Button1", Member: "Click", Handler: "OnClick" });
    }

    [Fact]
    public void A_property_written_in_another_case_is_changed_where_it_is()
    {
        var (_, writer) = ReadThroughCompilation();
        var text = SourceText.From(Designer);

        // Text, then Location: the second read is of edited text, which replays what the compilation said.
        var after = writer.Apply(text,
        [
            new DesignerEdit { Kind = DesignerEditKinds.SetProperty, Component = "Button1", Property = "Text", Value = DesignerValue.String("Stop") },
            new DesignerEdit { Kind = DesignerEditKinds.SetProperty, Component = "Button1", Property = "Location", Value = DesignerValue.NewOf("System.Drawing.Point", DesignerValue.Int(20), DesignerValue.Int(12)) },
            new DesignerEdit { Kind = DesignerEditKinds.SetProperty, Component = string.Empty, Property = "Text", Value = DesignerValue.String("Renamed") },
            new DesignerEdit { Kind = DesignerEditKinds.RemoveEvent, Component = "Button1", Event = "Click" },
        ]).ToString();
        Assert.Equal(Designer
            .Replace("BUTTON1.text = \"Go\"", "BUTTON1.text = \"Stop\"", StringComparison.Ordinal)
            .Replace("button1.location = New point(12, 12)", "button1.location = New Point(20, 12)", StringComparison.Ordinal)
            .Replace("text = \"Cased\"", "text = \"Renamed\"", StringComparison.Ordinal)
            .Replace("        AddHandler button1.click, AddressOf onclick\n", string.Empty, StringComparison.Ordinal), after);
    }

    [Fact]
    public void Without_a_compilation_members_are_read_as_written_and_the_designers_own_calls_as_declared()
    {
        var model = DesignerReader.Read(VisualBasicSyntaxTree.ParseText(Designer, cancellationToken: TestContext.Current.CancellationToken), "System.Windows.Forms.Form", ["System.Windows.Forms", "System.Drawing"]).Model;
        Assert.Equal("Go", Assignment(model, "Button1", "text").Value!.Value);
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Layout, Target: "", Member: "SuspendLayout" });
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Layout, Target: "", Member: "ResumeLayout" });
    }

    [Fact]
    public void A_Handles_clauses_event_is_named_as_declared()
    {
        var model = ReadThroughCompilation().Source.Model;
        var code = VisualBasicSyntaxTree.ParseText("""
            Partial Class CasedForm
                Private Sub CasedForm_Load(sender As Object, e As System.EventArgs) Handles mybase.load
                End Sub
            End Class
            """, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
        var declared = VisualBasicHandles.WithHandles(model, (component, written) => written.Equals("load", StringComparison.OrdinalIgnoreCase) ? "Load" : written, code);
        Assert.Contains(declared.Statements, s => s is { Kind: DesignerStatementKinds.Event, Target: "", Member: "Load", Handler: "CasedForm_Load", Line: -1 });
    }
}
