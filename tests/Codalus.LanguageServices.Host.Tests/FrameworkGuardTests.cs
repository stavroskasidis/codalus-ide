using System.Reflection;
using Codalus.LanguageServices.Host.Features;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using CompletionContext = Codalus.LanguageServices.Protocol.CompletionContext;
using CompletionTriggerKind = Codalus.LanguageServices.Protocol.CompletionTriggerKind;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// The projects of a solution opened before its restore get their framework one design-time build at a time. Roslyn
/// disables its completion and Quick Info providers for good the first time one throws on a compilation without
/// <c>System.Object</c>, so the features answer nothing until no project the document's compiles from lacks it.
/// RestoreRaceTests shows it end to end in the real hosts, when the timing falls this way.
/// </summary>
public sealed class FrameworkGuardTests
{
    private static readonly HostServices s_host = MefHostServices.Create(
        MefHostServices.DefaultAssemblies
            .Concat([typeof(Microsoft.CodeAnalysis.Completion.CompletionService).Assembly, Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features")])
            .Distinct());

    private static string Sample(params string[] path) => Path.Combine([RepoPaths.Root, "samples", "VisualBasic", .. path]);

    [Fact]
    public async Task A_project_with_its_framework_whose_library_has_none_yet_completes_once_the_library_has_it()
    {
        // macOS CI, job 120942281263: VbConsole had its framework, CsLibrary not yet; "formatter." then never completed
        // PriceFormatter's members, though both projects had their framework a moment later.
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new AdhocWorkspace(s_host);
        var framework = Codalus.LanguageServices.Host.WorkspaceManager.FrameworkReferences(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        var library = ProjectId.CreateNewId("CsLibrary");
        var console = ProjectId.CreateNewId("VbConsole");
        var program = DocumentId.CreateNewId(console);
        var text = await File.ReadAllTextAsync(Sample("VbConsole", "Program.vb"), ct);
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(library, VersionStamp.Create(), "CsLibrary", "CsLibrary", LanguageNames.CSharp,
                compilationOptions: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)))
            .AddProject(ProjectInfo.Create(console, VersionStamp.Create(), "VbConsole", "VbConsole", LanguageNames.VisualBasic,
                compilationOptions: new Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions(OutputKind.ConsoleApplication),
                metadataReferences: framework,
                projectReferences: [new ProjectReference(library)]))
            .AddDocument(DocumentId.CreateNewId(library), "PriceFormatter.cs", SourceText.From(await File.ReadAllTextAsync(Sample("CsLibrary", "PriceFormatter.cs"), ct)))
            .AddDocument(program, "Program.vb", SourceText.From(text));
        Assert.True(workspace.TryApplyChanges(solution));

        var completion = new CompletionFeature();
        var offset = text.IndexOf("Console.WriteLine(formatter.Format(", StringComparison.Ordinal) + "Console.WriteLine(formatter.".Length;
        async Task<Protocol.CompletionList> CompleteAsync() =>
            await completion.CompleteAsync(workspace.CurrentSolution.GetDocument(program)!, offset, new CompletionContext(CompletionTriggerKind.Invoked, null), ct);

        // Asked while the library has no framework: nothing yet, and asked again (Roslyn's providers would throw here).
        var before = await CompleteAsync();
        Assert.True(before.IsIncomplete);
        Assert.Empty(before.Items);
        Assert.Null(await HoverFeature.HoverAsync(workspace.CurrentSolution.GetDocument(program)!, offset - 2, ct));

        Assert.True(workspace.TryApplyChanges(workspace.CurrentSolution.WithProjectMetadataReferences(library, framework)));
        Assert.Contains("Currency", (await CompleteAsync()).Items.Select(i => i.Label));
        Assert.Contains("PriceFormatter", (await HoverFeature.HoverAsync(workspace.CurrentSolution.GetDocument(program)!, offset - 2, ct))?.Contents.Value, StringComparison.Ordinal);
    }
}
