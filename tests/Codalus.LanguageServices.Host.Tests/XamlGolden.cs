using System.Text;
using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// Golden files for the XAML refactorings (M64): a workspace edit applied to the fixture's files and written as the
/// lines it changes, file by file (<c>=== path</c>, hunks at <c>@@ line</c>, <c>-</c> old, <c>+</c> new; a new file is
/// all <c>+</c>). They live in <c>Golden/Xaml/</c>; <c>CODALUS_UPDATE_GOLDEN_FILES=1</c> writes them again from the
/// current results instead of failing.
/// </summary>
internal static class XamlGolden
{
    public static string Directory => Path.Combine(RepoPaths.Root, "tests", "Codalus.LanguageServices.Host.Tests", "Golden", "Xaml");

    /// <summary>The edit's changes to the files under <paramref name="root"/>, whose current text <paramref name="textOf"/> gives (null: not there).</summary>
    public static string Render(WorkspaceEdit edit, string root, Func<string, string?> textOf)
    {
        var output = new StringBuilder();
        foreach (var change in edit.DocumentChanges
                     .Select(c => (Change: c, Path: DocumentUris.ToPath(c.TextDocument.Uri)!))
                     .OrderBy(c => Path.GetRelativePath(root, c.Path).Replace('\\', '/'), StringComparer.Ordinal))
        {
            var before = (textOf(change.Path) ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
            var after = Apply(before, change.Change.Edits);
            output.Append("=== ").Append(Path.GetRelativePath(root, change.Path).Replace('\\', '/')).Append('\n');
            Diff(before.Length == 0 ? [] : before.Split('\n'), after.Split('\n'), output);
        }

        foreach (var operation in edit.ResourceOperations)
        {
            output.Append("=== ").Append(operation.Kind).Append(' ').Append(Path.GetFileName(DocumentUris.ToPath(operation.Uri))).Append('\n');
        }

        return output.ToString();
    }

    /// <summary>Compares <paramref name="actual"/> with the golden file <paramref name="name"/>, or writes it with <c>CODALUS_UPDATE_GOLDEN_FILES=1</c>.</summary>
    public static void Check(string name, string actual)
    {
        var path = Path.Combine(Directory, name + ".txt");
        if (Environment.GetEnvironmentVariable("CODALUS_UPDATE_GOLDEN_FILES") == "1")
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"No golden file {name}.txt (CODALUS_UPDATE_GOLDEN_FILES=1 writes it). The result:\n{actual}");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.True(expected == actual, $"{name}.txt differs from the result (CODALUS_UPDATE_GOLDEN_FILES=1 writes it again).\n--- expected\n{expected}\n--- actual\n{actual}");
    }

    public static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        var lines = text.Split('\n');
        int Offset(Position p) => lines.Take(p.Line).Sum(l => l.Length + 1) + p.Character;
        foreach (var edit in edits.OrderByDescending(e => Offset(e.Range.Start)).ThenByDescending(e => Offset(e.Range.End)))
        {
            var start = Offset(edit.Range.Start);
            text = text[..start] + edit.NewText.Replace("\r\n", "\n", StringComparison.Ordinal) + text[Offset(edit.Range.End)..];
        }

        return text;
    }

    /// <summary>A line diff (longest common subsequence) as hunks of removed and added lines.</summary>
    private static void Diff(string[] a, string[] b, StringBuilder output)
    {
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        var inHunk = false;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
            {
                x++;
                y++;
                inHunk = false;
                continue;
            }

            if (!inHunk)
            {
                output.Append("@@ ").Append(x + 1).Append('\n');
                inHunk = true;
            }

            if (x < a.Length && (y >= b.Length || lcs[x + 1, y] >= lcs[x, y + 1]))
            {
                output.Append("- ").Append(a[x++]).Append('\n');
            }
            else
            {
                output.Append("+ ").Append(b[y++]).Append('\n');
            }
        }
    }
}
