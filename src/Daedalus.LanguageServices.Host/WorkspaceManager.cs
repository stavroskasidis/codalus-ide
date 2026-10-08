using System.Collections.Concurrent;
using Daedalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using TextEdit = Daedalus.LanguageServices.Protocol.TextEdit;

namespace Daedalus.LanguageServices.Host;

/// <summary>
/// Owns the Roslyn solution snapshots. Edits are applied to in-memory snapshots only:
/// <see cref="Workspace.TryApplyChanges(Solution)"/> on an MSBuildWorkspace writes to disk, which the
/// editor must control explicitly.
/// Files that do not belong to the loaded solution are placed in a "miscellaneous files" project.
/// </summary>
public sealed class WorkspaceManager : IDisposable
{
    private static readonly StringComparer s_pathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly Lazy<MetadataReference[]> s_frameworkReferences = new(() =>
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        return tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal)
                        || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    });

    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, OpenDocumentState> _open = new(s_pathComparer);
    private readonly ProjectId _miscProjectId;
    private MSBuildWorkspace? _msbuild;
    private Solution? _solution;
    private Solution _misc;

    public WorkspaceManager()
    {
        var adhoc = new AdhocWorkspace();
        _miscProjectId = ProjectId.CreateNewId("Miscellaneous Files");
        var info = ProjectInfo.Create(
            _miscProjectId,
            VersionStamp.Create(),
            name: "Miscellaneous Files",
            assemblyName: "MiscellaneousFiles",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            metadataReferences: s_frameworkReferences.Value);
        _misc = adhoc.CurrentSolution.AddProject(info);
    }

    public async Task<OpenSolutionResult> OpenSolutionAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var workspace = MSBuildWorkspace.Create();
        var diagnostics = new List<string>();
        workspace.RegisterWorkspaceFailedHandler(e => { lock (diagnostics) { diagnostics.Add(e.Diagnostic.Message); } });

        var solution = fullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? (await workspace.OpenProjectAsync(fullPath, cancellationToken: cancellationToken).ConfigureAwait(false)).Solution
            : await workspace.OpenSolutionAsync(fullPath, cancellationToken: cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _msbuild?.Dispose();
            _msbuild = workspace;
            _solution = solution;
        }

        string[] diagnosticsSnapshot;
        lock (diagnostics)
        {
            diagnosticsSnapshot = [.. diagnostics];
        }

        return new OpenSolutionResult(SolutionTreeBuilder.Build(fullPath, solution), diagnosticsSnapshot);
    }

    public void OpenDocument(string path, string text, int version)
    {
        var fullPath = Path.GetFullPath(path);
        var sourceText = SourceText.From(text);
        lock (_gate)
        {
            var id = _solution?.GetDocumentIdsWithFilePath(fullPath).FirstOrDefault();
            if (id is not null)
            {
                _solution = _solution!.WithDocumentText(id, sourceText, PreservationMode.PreserveIdentity);
                _open[fullPath] = new OpenDocumentState(id, IsMisc: false, version, sourceText);
                return;
            }

            var miscId = DocumentId.CreateNewId(_miscProjectId, fullPath);
            _misc = _misc.AddDocument(miscId, Path.GetFileName(fullPath), sourceText, filePath: fullPath);
            _open[fullPath] = new OpenDocumentState(miscId, IsMisc: true, version, sourceText);
        }
    }

    public void ChangeDocument(string path, int version, TextEdit[] changes)
    {
        var fullPath = Path.GetFullPath(path);
        lock (_gate)
        {
            if (!_open.TryGetValue(fullPath, out var doc))
            {
                throw new InvalidOperationException($"Document is not open: {fullPath}");
            }

            var solution = doc.IsMisc ? _misc : _solution!;
            var text = doc.Text;
foreach (var change in changes)
            {
                text = text.WithChanges(new TextChange(new TextSpan(change.Offset, change.Length), change.Text));
            }

            solution = solution.WithDocumentText(doc.Id, text, PreservationMode.PreserveIdentity);
            if (doc.IsMisc)
            {
                _misc = solution;
            }
            else
            {
                _solution = solution;
            }

            _open[fullPath] = doc with { Version = version, Text = text };
        }
    }

    public void CloseDocument(string path)
    {
        var fullPath = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_open.TryRemove(fullPath, out var doc) && doc.IsMisc)
            {
                _misc = _misc.RemoveDocument(doc.Id);
            }
        }
    }

    public async Task<SemanticTokens> GetSemanticTokensAsync(string path, (int Start, int End)? lineRange, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        Document document;
        int version;
        lock (_gate)
        {
            if (!_open.TryGetValue(fullPath, out var doc))
            {
                throw new InvalidOperationException($"Document is not open: {fullPath}");
            }

            document = (doc.IsMisc ? _misc : _solution!).GetDocument(doc.Id)!;
            version = doc.Version;
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var span = new TextSpan(0, text.Length);
        if (lineRange is { } range && text.Lines.Count > 0)
        {
            var startLine = Math.Clamp(range.Start, 0, text.Lines.Count - 1);
            var endLine = Math.Clamp(range.End, startLine + 1, text.Lines.Count);
            span = TextSpan.FromBounds(text.Lines[startLine].Start, text.Lines[endLine - 1].EndIncludingLineBreak);
        }

        var classified = await Classifier.GetClassifiedSpansAsync(document, span, cancellationToken).ConfigureAwait(false);
        var tokens = ClassificationMapper.Map(classified);
        return new SemanticTokens(version, SemanticTokenEncoder.Encode(text, tokens));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _msbuild?.Dispose();
            _msbuild = null;
        }
    }

    /// <summary>The editor owns open-document text; <see cref="Text"/> is the authoritative copy.</summary>
    private sealed record OpenDocumentState(DocumentId Id, bool IsMisc, int Version, SourceText Text);
}
