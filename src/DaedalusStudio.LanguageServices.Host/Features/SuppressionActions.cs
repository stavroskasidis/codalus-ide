using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;

namespace DaedalusStudio.LanguageServices.Host.Features;

/// <summary>
/// The lightbulb's "Suppress or configure" actions for a warning or suggestion (M12b), which Roslyn implements with
/// internal provider types we can't load: suppress in source with <c>#pragma warning disable/restore</c> around the line,
/// and configure the rule's severity in the nearest <c>.editorconfig</c> (created next to the project when there is none).
/// </summary>
internal static class SuppressionActions
{
    private static readonly string[] s_severities = ["error", "warning", "suggestion", "silent", "none"];

    public static IEnumerable<RoslynCodeAction> For(Document document, Diagnostic diagnostic, SourceText text)
    {
        if (diagnostic.Severity == DiagnosticSeverity.Error || !diagnostic.Location.IsInSource || diagnostic.Location.SourceTree?.FilePath != document.FilePath)
        {
            yield break;
        }

        var id = diagnostic.Id;
        yield return RoslynCodeAction.Create(
            $"Suppress {id}: in source (#pragma)",
            ct => Task.FromResult(Pragma(document, text, diagnostic)),
            equivalenceKey: "suppress-pragma:" + id);
        yield return RoslynCodeAction.Create(
            $"Configure {id} severity",
            [.. s_severities.Select(severity => RoslynCodeAction.Create(severity, ct => ConfigureAsync(document, id, severity, ct), equivalenceKey: $"configure:{id}:{severity}"))],
            isInlinable: false);
    }

    private static Solution Pragma(Document document, SourceText text, Diagnostic diagnostic)
    {
        var span = diagnostic.Location.SourceSpan;
        var first = text.Lines.GetLineFromPosition(span.Start);
        var last = text.Lines.GetLineFromPosition(span.End);
        var indent = first.ToString()[..(first.ToString().Length - first.ToString().TrimStart().Length)];
        var newLine = first.EndIncludingLineBreak > first.End ? text.ToString(TextSpan.FromBounds(first.End, first.EndIncludingLineBreak)) : Environment.NewLine;
        var lastEnd = last.EndIncludingLineBreak > last.End ? last.EndIncludingLineBreak : last.End;
        var changes = new[]
        {
            new TextChange(new TextSpan(first.Start, 0), $"{indent}#pragma warning disable {diagnostic.Id}{newLine}"),
            new TextChange(new TextSpan(lastEnd, 0), (lastEnd == last.End ? newLine : string.Empty) + $"{indent}#pragma warning restore {diagnostic.Id}{newLine}"),
        };
        return document.WithText(text.WithChanges(changes)).Project.Solution;
    }

    /// <summary>Sets <c>dotnet_diagnostic.&lt;id&gt;.severity</c> in the nearest .editorconfig's C# section.</summary>
    private static async Task<Solution> ConfigureAsync(Document document, string id, string severity, CancellationToken cancellationToken)
    {
        var project = document.Project;
        var directory = Path.GetDirectoryName(document.FilePath) ?? string.Empty;
        var config = project.AnalyzerConfigDocuments
            .Where(d => d.FilePath is { } path && Path.GetFileName(path).Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)
                        && directory.StartsWith(Path.GetDirectoryName(path)!, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.FilePath!.Length)
            .FirstOrDefault();
        var key = $"dotnet_diagnostic.{id}.severity";
        var line = $"{key} = {severity}";
        if (config is null)
        {
            var folder = Path.GetDirectoryName(project.FilePath) ?? directory;
            var path = Path.Combine(folder, ".editorconfig");
            var created = $"[*.cs]{Environment.NewLine}{line}{Environment.NewLine}";
            return project.AddAnalyzerConfigDocument(".editorconfig", SourceText.From(created, Encoding.UTF8), filePath: path).Project.Solution;
        }

        var text = await config.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var content = text.ToString();
        var newLine = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // An existing setting anywhere: change it in place.
        var existing = lines.FindIndex(l => l.TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase) && l.Contains('=', StringComparison.Ordinal));
        if (existing >= 0)
        {
            lines[existing] = line;
        }
        else
        {
            // The C# section ([*.cs], [*.{cs,vb}], [*]): the end of it; else a new [*.cs] section at the end.
            var section = lines.FindIndex(l => l.Trim() is "[*.cs]" or "[*.{cs,vb}]" or "[*.{vb,cs}]");
            if (section < 0)
            {
                if (lines.Count > 0 && lines[^1].Length == 0)
                {
                    lines.RemoveAt(lines.Count - 1);
                }

                lines.AddRange([string.Empty, "[*.cs]", line, string.Empty]);
            }
            else
            {
                var end = lines.FindIndex(section + 1, l => l.TrimStart().StartsWith('['));
                var insertAt = end < 0 ? lines.Count : end;
                while (insertAt > section + 1 && lines[insertAt - 1].Trim().Length == 0)
                {
                    insertAt--;
                }

                lines.Insert(insertAt, line);
            }
        }

        return config.Project.Solution.WithAnalyzerConfigDocumentText(config.Id, SourceText.From(string.Join(newLine, lines), text.Encoding ?? Encoding.UTF8));
    }
}
