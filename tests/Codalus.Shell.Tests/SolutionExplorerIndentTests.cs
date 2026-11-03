using Avalonia;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Tests;

/// <summary>
/// The Solution Explorer indents each level 12 px, the classic IDE's, so a deep tree stays compact horizontally (it was
/// 16 px, and a file five levels down sat 20 px further right than the classic IDE's).
/// </summary>
public class SolutionExplorerIndentTests
{
    [Fact]
    public void Each_level_is_indented_12_px()
    {
        static ExplorerRowViewModel Row(int depth) => new(new ExplorerNode("id", ExplorerNodeKind.Folder, "Helpers"), depth, false);

        Assert.Equal(new Thickness(4, 0, 0, 0), Row(0).Indent);
        Assert.Equal(new Thickness(4 + 12, 0, 0, 0), Row(1).Indent);
        Assert.Equal(new Thickness(4 + (5 * 12), 0, 0, 0), Row(5).Indent);
    }
}
