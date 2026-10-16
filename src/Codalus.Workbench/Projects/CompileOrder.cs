using Codalus.Projects.Protocol;

namespace Codalus.Workbench.Projects;

/// <summary>
/// F#'s compile order (M36): a file can only use what the files before it define, so the order of an F# project's
/// <c>Compile</c> items matters, and Solution Explorer shows and edits it. A signature file (<c>Shapes.fsi</c>) moves
/// with its implementation (<c>Shapes.fs</c>), which it comes right before. Moves stay within a folder.
/// </summary>
public static class CompileOrder
{
    /// <summary>The project's compiled files, in compile order (the build host reports items in evaluation order).</summary>
    public static IReadOnlyList<string> Of(ProjectInfo info)
    {
        var seen = new HashSet<string>(SolutionService.PathComparer);
        return [.. info.Items.Where(i => i.Kind.Equals("Compile", StringComparison.OrdinalIgnoreCase) && i.Link is null && seen.Add(i.Path)).Select(i => i.Path)];
    }

    /// <summary>
    /// The <c>items/move</c> edits (each a file and the file it goes before) that take <paramref name="file"/>, with its
    /// signature file, one place up or down among the compiled files of its folder; empty when it can't move.
    /// </summary>
    public static IReadOnlyList<(string Path, string Before)> Step(IReadOnlyList<string> order, string file, bool down)
    {
        var blocks = Blocks(order);
        var index = blocks.FindIndex(b => b.Contains(file, SolutionService.PathComparer));
        var other = down ? index + 1 : index - 1;
        if (index < 0 || other < 0 || other >= blocks.Count || !SameFolder(blocks[index][0], blocks[other][0]))
        {
            return [];
        }

        // Swap two neighbouring blocks: each file of the later one goes before the first file of the earlier one.
        var (earlier, later) = down ? (blocks[index], blocks[other]) : (blocks[other], blocks[index]);
        return [.. later.Select(path => (path, earlier[0]))];
    }

    /// <summary>The files in order, a signature file grouped with the implementation right after it.</summary>
    private static List<string[]> Blocks(IReadOnlyList<string> order)
    {
        var blocks = new List<string[]>();
        for (var i = 0; i < order.Count; i++)
        {
            if (i + 1 < order.Count && IsSignatureOf(order[i], order[i + 1]))
            {
                blocks.Add([order[i], order[i + 1]]);
                i++;
            }
            else
            {
                blocks.Add([order[i]]);
            }
        }

        return blocks;
    }

    private static bool IsSignatureOf(string signature, string implementation) =>
        signature.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase) && implementation.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
        && SolutionService.PathComparer.Equals(Path.ChangeExtension(signature, ".fs"), implementation);

    private static bool SameFolder(string a, string b) => SolutionService.PathComparer.Equals(Path.GetDirectoryName(a), Path.GetDirectoryName(b));
}
