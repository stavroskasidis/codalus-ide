using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Tests;

/// <summary>The Solution Explorer's members under code files (deferred from M12a): types and members from the language service.</summary>
public sealed class SolutionExplorerMembersTests
{
    [AvaloniaFact]
    public async Task Code_files_expand_into_their_types_and_members()
    {
        await using var f = ShellFixture.Create();
        var folder = await f.OpenLanguageFeaturesAsync();
        var explorer = f.Shell.Explorer;
        var path = Path.Combine(folder, "src", "Lib", "Shapes.cs");
        ExplorerRowViewModel Row(Func<ExplorerRowViewModel, bool> match) => explorer.Rows.First(match);

        // Reveal the file, then expand it: its types come in place of "Loading…".
        await ShellFixture.PumpUntil(() => explorer.Root is not null && FindId(explorer.Root, path) is not null, seconds: 60);
        explorer.Reveal(FindId(explorer.Root!, path)!);
        var file = Row(r => r.Node.Kind == ExplorerNodeKind.File && r.Node.Path == path);
        Assert.True(file.HasChildren);
        explorer.SetExpanded(file, true);
        await ShellFixture.PumpUntil(() => explorer.Rows.Any(r => r.Node is { Kind: ExplorerNodeKind.Member, Name: "Circle" }), seconds: 60);
        Assert.Contains(explorer.Rows, r => r.Node is { Kind: ExplorerNodeKind.Member, Name: "IShape" });
        Assert.DoesNotContain(explorer.Rows, r => r.Node.Kind == ExplorerNodeKind.Member && r.Node.Name.StartsWith("Lib", StringComparison.Ordinal)); // namespaces left out

        // A type expands into its members, and choosing one goes to its declaration.
        var circle = Row(r => r.Node is { Kind: ExplorerNodeKind.Member, Name: "Circle" });
        explorer.SetExpanded(circle, true);
        var area = Row(r => r.Node.Kind == ExplorerNodeKind.Member && r.Node.Name.StartsWith("Area", StringComparison.Ordinal) && r.Depth == circle.Depth + 1);
        await explorer.ActivateAsync(area);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is { Controller: not null } editor && editor.Model.Path == path);
        var controller = f.Shell.ActiveEditor!.Controller!;
        Assert.Equal(area.Node.Position!.Value.Line + 1, controller.Caret.Line);

        // An edit adds a type: the members follow.
        controller.Model.Text.Insert(controller.Model.Text.TextLength, "\npublic sealed class Hexagon { }\n");
        await ShellFixture.PumpUntil(() => explorer.Rows.Any(r => r.Node is { Kind: ExplorerNodeKind.Member, Name: "Hexagon" }), seconds: 60);

        // Turned off, files have no members.
        await f.Shell.Workbench.Settings.SetAsync(SolutionExplorerViewModel.ShowMembersSetting, false, Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => !explorer.Rows.Any(r => r.Node.Kind == ExplorerNodeKind.Member));
    }

    private static string? FindId(ExplorerNode node, string path) =>
        node.Kind == ExplorerNodeKind.File && node.Path == path ? node.Id : node.Children.Select(c => FindId(c, path)).FirstOrDefault(id => id is not null);
}
