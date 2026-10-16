using System.Collections.Concurrent;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host;

/// <summary>
/// Turns a design-time compiler command line (<see cref="ProjectData"/>) into what Roslyn needs: options, documents,
/// metadata references (shared across projects), analyzer references, and references that stand for other projects.
/// </summary>
internal sealed class ProjectLoader
{
    private readonly ConcurrentDictionary<string, (DateTime Stamp, PortableExecutableReference Reference)> _metadata = new(Paths.Comparer);
    private readonly ConcurrentDictionary<string, AnalyzerFileReference> _analyzers = new(Paths.Comparer);
    private readonly AnalyzerLoader _analyzerLoader = new();

    public event EventHandler<string>? Log;

    /// <summary>A parsed project. <see cref="OutputReferences"/> are references to files that may be other projects' outputs.</summary>
    public sealed record Parsed(
        ProjectData Data,
        CompilationOptions CompilationOptions,
        ParseOptions ParseOptions,
        string AssemblyName,
        IReadOnlyList<(string Path, IReadOnlyList<string> Folders)> Sources,
        IReadOnlyList<string> AdditionalFiles,
        IReadOnlyList<string> AnalyzerConfigFiles,
        IReadOnlyList<(string Path, MetadataReferenceProperties Properties)> References,
        IReadOnlyList<AnalyzerReference> Analyzers)
    {
        /// <summary>The compiler's output (<c>/out:</c>, in <c>obj/</c>): rewritten by every build of the project.</summary>
        public string? IntermediateAssembly { get; init; }

        /// <summary>The compiler's reference assembly (<c>/refout:</c>), when the project produces one.</summary>
        public string? IntermediateRefAssembly { get; init; }

        /// <summary>The documentation file the build writes (<c>/doc:</c>), when it writes one.</summary>
        public string? DocumentationFile { get; init; }
    }

    /// <summary>Roslyn's language name of a design-time build's language: C# and Visual Basic (M35); null for others.</summary>
    public static string? RoslynLanguage(string language) => language switch
    {
        "C#" => LanguageNames.CSharp,
        "VB" => LanguageNames.VisualBasic,
        _ => null,
    };

    /// <summary>Parses C# and Visual Basic command lines (<c>csc</c>'s, <c>vbc</c>'s); other languages aren't this host's (null).</summary>
    public Parsed? Parse(ProjectData data)
    {
        if (RoslynLanguage(data.Language) is not { } language)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(data.Path)!;
        CommandLineArguments args = language == LanguageNames.VisualBasic
            ? Microsoft.CodeAnalysis.VisualBasic.VisualBasicCommandLineParser.Default.Parse(VisualBasicArguments(data.Arguments), directory, sdkDirectory: null)
            : CSharpCommandLineParser.Default.Parse(data.Arguments, directory, sdkDirectory: null);
        foreach (var error in args.Errors.Where(e => e.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
        {
            Log?.Invoke(this, $"{Path.GetFileName(data.Path)}: {error.GetMessage(System.Globalization.CultureInfo.CurrentCulture)}");
        }

        var options = args.CompilationOptions
            .WithMetadataReferenceResolver(null)
            .WithXmlReferenceResolver(new XmlFileResolver(directory))
            .WithSourceReferenceResolver(new SourceFileResolver([], directory))
            .WithStrongNameProvider(new DesktopStrongNameProvider([]))
            .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default);

        var sources = args.SourceFiles
            .Select(f => (Path.GetFullPath(f.Path), Folders(directory, f.Path)))
            .DistinctBy(f => f.Item1, Paths.Comparer)
            .Select(f => (f.Item1, (IReadOnlyList<string>)f.Item2))
            .ToList();
        var references = args.MetadataReferences
            .Select(r => (Path.GetFullPath(Path.Combine(directory, r.Reference)), r.Properties))
            .DistinctBy(r => r.Item1, Paths.Comparer)
            .ToList();
        var analyzers = args.AnalyzerReferences
            .Select(a => SdkAnalyzers.Resolve(Path.GetFullPath(Path.Combine(directory, a.FilePath)), message => Log?.Invoke(this, message)))
            .OfType<string>()
            .Distinct(Paths.Comparer)
            .Where(File.Exists)
            .Select(Analyzer)
            .ToList();

        return new Parsed(
            data,
            options,
            // Without /doc the compiler ignores documentation comments; the IDE needs them (Quick Info, signature help).
            args.ParseOptions.DocumentationMode == DocumentationMode.None ? args.ParseOptions.WithDocumentationMode(DocumentationMode.Parse) : args.ParseOptions,
            args.CompilationName ?? Path.GetFileNameWithoutExtension(args.OutputFileName) ?? data.Name,
            sources,
            [.. args.AdditionalFiles.Select(f => Path.GetFullPath(f.Path)).Distinct(Paths.Comparer)],
            [.. args.AnalyzerConfigPaths.Select(Path.GetFullPath).Distinct(Paths.Comparer)],
            references,
            analyzers)
        {
            IntermediateAssembly = args.OutputFileName is { Length: > 0 } output && args.OutputDirectory is { Length: > 0 } outputDirectory
                ? Path.GetFullPath(Path.Combine(outputDirectory, output))
                : null,
            IntermediateRefAssembly = args.OutputRefFilePath is { Length: > 0 } refOutput ? Path.GetFullPath(Path.Combine(directory, refOutput)) : null,
            DocumentationFile = args.DocumentationPath is { Length: > 0 } documentation ? Path.GetFullPath(Path.Combine(directory, documentation)) : null,
        };
    }

    /// <summary>
    /// vbc's arguments as its parser expects them. A define keeps the response file's quoting
    /// (<c>/define:"CONFIG=\"Debug\",…"</c>), which a shell would have removed, so it's split again as a command line;
    /// MSBuild on Linux and macOS also turns the backslash of <c>\"</c> into a slash (its path normalization of item
    /// specs), so its <c>/"</c> is put back first. Other arguments are left as they are.
    /// </summary>
    internal static IEnumerable<string> VisualBasicArguments(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("/define:", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("-define:", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("/d:", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("-d:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in CommandLineParser.SplitCommandLineIntoArguments(argument.Replace("/\"", "\\\"", StringComparison.Ordinal), removeHashComments: false))
                {
                    yield return part;
                }
            }
            else
            {
                yield return argument;
            }
        }
    }

    /// <summary>A metadata reference to a file, shared by every project that references it while the file is unchanged.</summary>
    public MetadataReference? Metadata(string path, MetadataReferenceProperties properties)
    {
        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path);
            if (stamp.Year < 1700)
            {
                return null; // missing
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var entry = _metadata.AddOrUpdate(
            path,
            p => (stamp, MetadataReference.CreateFromFile(p)),
            (p, existing) => existing.Stamp == stamp ? existing : (stamp, MetadataReference.CreateFromFile(p)));
        return properties == MetadataReferenceProperties.Assembly ? entry.Reference : entry.Reference.WithProperties(properties);
    }

    private AnalyzerFileReference Analyzer(string path) => _analyzers.GetOrAdd(path, p =>
    {
        var reference = new AnalyzerFileReference(p, _analyzerLoader);
        reference.AnalyzerLoadFailed += (_, e) => Log?.Invoke(this, AnalyzerFailure(Path.GetFileName(p), e));
        return reference;
    });

    /// <summary>
    /// Why an analyzer or source generator didn't load. One built for a newer compiler than the host's Roslyn (a new
    /// SDK's Razor generator, ADR 0025) loads only once Codalus catches up: its diagnostics and code are missing.
    /// </summary>
    internal static string AnalyzerFailure(string file, AnalyzerLoadFailureEventArgs e) =>
        e.ErrorCode == AnalyzerLoadFailureEventArgs.FailureErrorCode.ReferencesNewerCompiler && e.ReferencedCompilerVersion is { } needed
            ? $"Analyzer {file} needs version {needed} of the C# compiler, and this version of Codalus has {typeof(Compilation).Assembly.GetName().Version}. "
                + "Its diagnostics and generated code are missing until Codalus is updated."
            : $"Analyzer {file}: {e.Message}";

    /// <summary>Folders of a source file relative to the project directory (none for files outside it).</summary>
    private static string[] Folders(string projectDirectory, string file)
    {
        var relative = Path.GetRelativePath(projectDirectory, Path.GetFullPath(Path.Combine(projectDirectory, file)));
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return [];
        }

        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts[..^1];
    }
}

internal static class Paths
{
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>Conversions between LSP positions (UTF-16 line/character, like Roslyn's) and offsets.</summary>
internal static class Positions
{
    public static int ToOffset(SourceText text, Position position)
    {
        if (position.Line >= text.Lines.Count)
        {
            return text.Length;
        }

        var line = text.Lines[Math.Max(0, position.Line)];
        return Math.Min(line.Start + Math.Max(0, position.Character), line.End);
    }

    public static Position ToPosition(SourceText text, int offset)
    {
        var line = text.Lines.GetLinePosition(Math.Clamp(offset, 0, text.Length));
        return new Position(line.Line, line.Character);
    }

    public static Protocol.Range ToRange(SourceText text, TextSpan span) => new(ToPosition(text, span.Start), ToPosition(text, span.End));

    public static Protocol.Range ToRange(LinePositionSpan span) =>
        new(new Position(span.Start.Line, span.Start.Character), new Position(span.End.Line, span.End.Character));

    public static TextSpan ToSpan(SourceText text, Protocol.Range range)
    {
        var start = ToOffset(text, range.Start);
        return TextSpan.FromBounds(start, Math.Max(start, ToOffset(text, range.End)));
    }
}
