using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Location = DaedalusStudio.LanguageServices.Protocol.Location;
using Range = DaedalusStudio.LanguageServices.Protocol.Range;

namespace DaedalusStudio.LanguageServices.Host.Razor;

/// <summary>
/// Razor through the Razor source generator (ADR 0023): a <c>.razor</c> or <c>.cshtml</c> file is an additional
/// document of its project; the generator turns it into C# with enhanced <c>#line</c> directives around every piece of
/// C# it copied (expressions, event handlers, <c>@code</c> blocks). Those directives map positions both ways.
/// </summary>
internal static class RazorFiles
{
    public static bool IsRazor(string path) =>
        path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

    /// <summary>The additional document of a Razor file (in a loaded project), or null.</summary>
    public static TextDocument? Find(Solution solution, string path)
    {
        foreach (var id in solution.GetDocumentIdsWithFilePath(Path.GetFullPath(path)))
        {
            if (solution.GetAdditionalDocument(id) is { } document)
            {
                return document;
            }
        }

        return null;
    }

    /// <summary>
    /// The generated document of a Razor file: the one whose <c>#pragma checksum</c> names it (every generated file
    /// also maps <c>_Imports.razor</c>'s usings, so mappings alone don't identify it).
    /// </summary>
    public static async Task<SourceGeneratedDocument?> GeneratedAsync(Project project, string path, CancellationToken cancellationToken)
    {
        foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!generated.HintName.EndsWith("_razor.g.cs", StringComparison.Ordinal) && !generated.HintName.EndsWith("_cshtml.g.cs", StringComparison.Ordinal))
            {
                continue;
            }

            if (await generated.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is { } root
                && root.GetLeadingTrivia().Select(t => t.GetStructure()).OfType<PragmaChecksumDirectiveTriviaSyntax>().FirstOrDefault() is { } checksum
                && Paths.Comparer.Equals(Path.GetFullPath(checksum.File.ValueText), Path.GetFullPath(path)))
            {
                return generated;
            }
        }

        return null;
    }
}

/// <summary>A Razor file and its generated C#, with the C# spans that map between them.</summary>
internal sealed class RazorMapping
{
    private readonly List<(TextSpan Razor, TextSpan Generated)> _spans;

    private RazorMapping(string path, TextDocument razor, SourceText razorText, SourceGeneratedDocument generated, SourceText generatedText, List<(TextSpan, TextSpan)> spans)
    {
        Path = path;
        Razor = razor;
        RazorText = razorText;
        Generated = generated;
        GeneratedText = generatedText;
        _spans = spans;
    }

    public string Path { get; }

    public TextDocument Razor { get; }

    public SourceText RazorText { get; }

    public SourceGeneratedDocument Generated { get; }

    public SourceText GeneratedText { get; }

    /// <summary>The C# spans of the Razor file and where they are in the generated document.</summary>
    public IReadOnlyList<(TextSpan Razor, TextSpan Generated)> Spans => _spans;

    public static async Task<RazorMapping?> CreateAsync(Solution solution, string path, CancellationToken cancellationToken)
    {
        if (RazorFiles.Find(solution, path) is not { } razor
            || await RazorFiles.GeneratedAsync(razor.Project, path, cancellationToken).ConfigureAwait(false) is not { } generated
            || await generated.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false) is not { } tree)
        {
            return null;
        }

        var razorText = await razor.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var generatedText = await generated.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var spans = new List<(TextSpan, TextSpan)>();
        foreach (var mapping in tree.GetLineMappings(cancellationToken))
        {
            if (!mapping.MappedSpan.HasMappedPath || !Paths.Comparer.Equals(System.IO.Path.GetFullPath(mapping.MappedSpan.Path), System.IO.Path.GetFullPath(path))
                || mapping.Span.Start.Line >= generatedText.Lines.Count)
            {
                continue;
            }

            var razorStart = Offset(razorText, mapping.MappedSpan.StartLinePosition);
            var razorEnd = Offset(razorText, mapping.MappedSpan.EndLinePosition);
            var generatedStart = generatedText.Lines[mapping.Span.Start.Line].Start + Math.Max(0, mapping.CharacterOffset ?? mapping.Span.Start.Character);
            // The generator's end column runs one past the C# (Roslyn reads it as inclusive); the C# is copied verbatim,
            // so the span is as long as the two texts agree.
            var length = 0;
            while (razorStart + length < razorEnd && generatedStart + length < generatedText.Length
                   && razorText[razorStart + length] == generatedText[generatedStart + length])
            {
                length++;
            }

            if (razorEnd < razorStart)
            {
                continue;
            }

            spans.Add((new TextSpan(razorStart, length), new TextSpan(generatedStart, length)));
        }

        return new RazorMapping(path, razor, razorText, generated, generatedText, spans);
    }

    private static int Offset(SourceText text, LinePosition position) =>
        position.Line >= text.Lines.Count ? text.Length : Math.Min(text.Lines[position.Line].Start + position.Character, text.Lines[position.Line].End);

    /// <summary>The generated offset of a Razor offset in C# (an end position counts), or null in markup.</summary>
    public int? ToGenerated(int razorOffset)
    {
        foreach (var (razor, generated) in _spans)
        {
            if (razorOffset >= razor.Start && razorOffset <= razor.End)
            {
                return generated.Start + (razorOffset - razor.Start);
            }
        }

        return null;
    }

    /// <summary>The Razor span of a generated span inside one C# span, or null (code the generator wrote).</summary>
    public TextSpan? ToRazor(TextSpan generatedSpan)
    {
        foreach (var (razor, generated) in _spans)
        {
            if (generatedSpan.Start >= generated.Start && generatedSpan.End <= generated.End)
            {
                return new TextSpan(razor.Start + (generatedSpan.Start - generated.Start), generatedSpan.Length);
            }
        }

        return null;
    }

    public Range? ToRazor(Range generated)
    {
        var start = Positions.ToOffset(GeneratedText, generated.Start);
        var end = Positions.ToOffset(GeneratedText, generated.End);
        return ToRazor(TextSpan.FromBounds(start, Math.Max(start, end))) is { } span ? Positions.ToRange(RazorText, span) : null;
    }

    /// <summary>Locations in the generated document move to the Razor file (dropped if they are generated code); others stay.</summary>
    public Location[] MapLocations(IEnumerable<Location> locations)
    {
        var generatedUri = WorkspaceManager.UriOf(Generated);
        var razorUri = DocumentUris.FromPath(Path);
        var result = new List<Location>();
        foreach (var location in locations)
        {
            if (location.Uri != generatedUri)
            {
                result.Add(location);
            }
            else if (ToRazor(location.Range) is { } range)
            {
                result.Add(new Location(razorUri, range));
            }
        }

        return [.. result.Distinct()];
    }
}

/// <summary>Locations in the C# the Razor generator wrote, moved to the Razor file they came from.</summary>
internal static class RazorLocations
{
    /// <summary>
    /// The Razor location of a location in a Razor-generated document: its mapped span when the C# came from the file,
    /// else the file's start (a component's generated class). Null for other documents.
    /// </summary>
    public static Location? Map(Microsoft.CodeAnalysis.Location location)
    {
        if (location.SourceTree is not { } tree || !IsRazorGenerated(tree.FilePath))
        {
            return null;
        }

        var mapped = location.GetMappedLineSpan();
        if (mapped.HasMappedPath && RazorFiles.IsRazor(mapped.Path))
        {
            return new Location(DocumentUris.FromPath(System.IO.Path.GetFullPath(mapped.Path)), Positions.ToRange(mapped.Span));
        }

        return SourceFile(tree) is { } file ? new Location(DocumentUris.FromPath(file), new Range(new Position(0, 0), new Position(0, 0))) : null;
    }

    public static bool IsRazorGenerated(string? path) =>
        path is not null && (path.EndsWith("_razor.g.cs", StringComparison.Ordinal) || path.EndsWith("_cshtml.g.cs", StringComparison.Ordinal));

    /// <summary>The Razor file a generated tree came from (its <c>#pragma checksum</c>).</summary>
    public static string? SourceFile(SyntaxTree tree) =>
        tree.GetRoot().GetLeadingTrivia().Select(t => t.GetStructure()).OfType<PragmaChecksumDirectiveTriviaSyntax>().FirstOrDefault() is { } checksum
            ? System.IO.Path.GetFullPath(checksum.File.ValueText)
            : null;
}
