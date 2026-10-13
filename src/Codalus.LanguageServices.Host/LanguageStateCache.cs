using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Diagnostic = Codalus.LanguageServices.Protocol.Diagnostic;

namespace Codalus.LanguageServices.Host;

/// <summary>
/// What the language host keeps of a project between sessions (M34c, ADR 0049), in its intermediate folder next to the
/// compiler's output (<c>obj/Debug/net10.0/</c>): the background pass's diagnostics (<c>codalus.language.json</c>) and a
/// reference assembly (<c>codalus.language.ref.dll</c>), each under a key of everything that decides them
/// (<see cref="ProjectKeys"/>). A project whose key is unchanged keeps its diagnostics without being compiled, and the
/// projects that reference it compile against its reference assembly instead of its compilation.
/// <c>CODALUS_LANGUAGE_CACHE=0</c> turns it off.
/// </summary>
internal static class LanguageStateCache
{
    /// <summary>Changes whenever what is stored, or what the key covers, changes.</summary>
    public const int Schema = 2;

    public const string StateFile = "codalus.language.json";
    public const string ReferenceFile = "codalus.language.ref.dll";
    public const string DocumentationFile = "codalus.language.ref.xml";

    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("CODALUS_LANGUAGE_CACHE") != "0";

    internal sealed record Entry(int Schema, string Key, string Scope, int Documents, FileDiagnostics[] Files)
    {
        /// <summary>The key the reference assembly next to it was emitted under (null: none).</summary>
        public string? ReferenceKey { get; init; }
    }

    internal sealed record FileDiagnostics(string Path, Diagnostic[] Items);

    /// <summary>The folder of a project's state: the compiler's output folder in <c>obj/</c>, when it exists.</summary>
    public static string? Folder(ProjectLoader.Parsed? parsed) =>
        parsed?.IntermediateAssembly is { } output && Path.GetDirectoryName(output) is { } folder && Directory.Exists(folder) ? folder : null;

    public static Entry? Read(string folder)
    {
        try
        {
            var path = Path.Combine(folder, StateFile);
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            var entry = JsonSerializer.Deserialize(stream, LanguageStateJsonContext.Default.Entry);
            return entry is { Schema: Schema } ? entry : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null; // a corrupt or unreadable file is a miss
        }
    }

    public static void Write(string folder, Entry entry)
    {
        var path = Path.Combine(folder, StateFile);
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, entry, LanguageStateJsonContext.Default.Entry);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
        }
    }

    /// <summary>
    /// Emits <paramref name="compilation"/>'s metadata (internals included, for InternalsVisibleTo) as the project's
    /// reference assembly, with its documentation comments (Quick Info and signature help).
    /// </summary>
    public static bool WriteReference(string folder, Compilation compilation, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, ReferenceFile);
        var documentation = Path.Combine(folder, DocumentationFile);
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        var temporaryDocumentation = documentation + "." + Environment.ProcessId + ".tmp";
        try
        {
            EmitResult result;
            using (var stream = File.Create(temporary))
            using (var xml = File.Create(temporaryDocumentation))
            {
                result = compilation.Emit(stream, xmlDocumentationStream: xml, options: new EmitOptions(metadataOnly: true), cancellationToken: cancellationToken);
            }

            if (!result.Success)
            {
                TryDelete(temporary);
                TryDelete(temporaryDocumentation);
                return false;
            }

            File.Move(temporaryDocumentation, documentation, overwrite: true);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            TryDelete(temporaryDocumentation);
            return false;
        }
    }

    /// <summary>
    /// A reference assembly, read into memory (no file stays open, so the next session may replace it), with the
    /// documentation file next to it when there is one.
    /// </summary>
    public static MetadataReference? Reference(string path, string? documentation)
    {
        try
        {
            return MetadataReference.CreateFromImage(
                File.ReadAllBytes(path),
                documentation: documentation is not null && File.Exists(documentation) ? XmlDocumentationProvider.CreateFromFile(documentation) : null,
                filePath: path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LanguageStateCache.Entry))]
internal sealed partial class LanguageStateJsonContext : JsonSerializerContext;

/// <summary>
/// Keys of a snapshot of the loaded projects (M34c): a hash of the compiler command line, every input file (sources,
/// additional files, <c>.editorconfig</c> files, references outside the solution and analyzers, by length and time
/// written; an open document by its text unless it matches the file), the host's Roslyn, and the keys of the projects
/// it references. Computed on demand and remembered for the snapshot.
/// </summary>
internal sealed class ProjectKeys
{
    private static readonly string s_compiler = typeof(Compilation).Assembly.GetName().Version?.ToString() ?? "?";

    private readonly IReadOnlyDictionary<ProjectId, WorkspaceManager.ProjectSnapshot> _projects;
    private readonly IReadOnlyDictionary<string, SourceText> _open;
    private readonly HashSet<string> _outputs;
    private readonly Dictionary<ProjectId, string?> _keys = [];
    private readonly Dictionary<string, string> _files = new(Paths.Comparer);
    private readonly Lock _gate = new();

    public ProjectKeys(IReadOnlyDictionary<ProjectId, WorkspaceManager.ProjectSnapshot> projects, IReadOnlyDictionary<string, SourceText> open)
    {
        _projects = projects;
        _open = open;
        _outputs = new HashSet<string>(Paths.Comparer);
        foreach (var project in projects.Values)
        {
            foreach (var output in new[] { project.Parsed.Data.OutputPath, project.Parsed.Data.OutputRefPath, project.Parsed.IntermediateAssembly, project.Parsed.IntermediateRefAssembly })
            {
                if (output is not null)
                {
                    _outputs.Add(output);
                }
            }
        }
    }

    public WorkspaceManager.ProjectSnapshot? Project(ProjectId id) => _projects.GetValueOrDefault(id);

    /// <summary>The project's key, or null when it isn't a loaded project (Miscellaneous Files).</summary>
    public string? Key(ProjectId id)
    {
        lock (_gate)
        {
            return KeyLocked(id, []);
        }
    }

    private string? KeyLocked(ProjectId id, HashSet<ProjectId> visiting)
    {
        if (_keys.TryGetValue(id, out var known))
        {
            return known;
        }

        if (!_projects.TryGetValue(id, out var project) || !visiting.Add(id))
        {
            return null; // not loaded, or a cycle (never valid in MSBuild)
        }

        var parsed = project.Parsed;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        Append($"schema {LanguageStateCache.Schema} roslyn {s_compiler}");
        Append(parsed.Data.Path);
        Append(parsed.Data.TargetFramework);
        foreach (var argument in parsed.Data.Arguments)
        {
            Append(argument);
        }

        foreach (var (path, _) in parsed.Sources)
        {
            Append(File(path));
        }

        foreach (var path in parsed.AdditionalFiles.Concat(parsed.AnalyzerConfigFiles))
        {
            Append(File(path));
        }

        var referencedAssemblies = project.References
            .Select(r => _projects.GetValueOrDefault(r)?.Parsed.AssemblyName)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _) in parsed.References)
        {
            // Another project's output stands for that project: its key, below.
            if (!_outputs.Contains(path) && !referencedAssemblies.Contains(Path.GetFileNameWithoutExtension(path)))
            {
                Append(File(path));
            }
        }

        foreach (var analyzer in parsed.Analyzers)
        {
            Append(analyzer.FullPath is { } path ? File(path) : analyzer.Display);
        }

        foreach (var reference in project.References.OrderBy(r => _projects.GetValueOrDefault(r)?.Parsed.Data.Path, Paths.Comparer))
        {
            if (KeyLocked(reference, visiting) is not { } key)
            {
                _keys[id] = null; // a referenced project that isn't loaded: no key
                return null;
            }

            Append("project " + key);
        }

        visiting.Remove(id);
        var result = Convert.ToHexString(hash.GetHashAndReset());
        _keys[id] = result;
        return result;
    }

    /// <summary>Whether a file is open in the editor with text other than the file's.</summary>
    public bool IsOpenWithOtherText(string path)
    {
        if (!_open.ContainsKey(path))
        {
            return false;
        }

        lock (_gate)
        {
            return File(path).Contains("|open|", StringComparison.Ordinal);
        }
    }

    /// <summary>A file's length and time written, or an open document's text when it differs from the file.</summary>
    private string File(string path)
    {
        if (_files.TryGetValue(path, out var known))
        {
            return known;
        }

        string result;
        try
        {
            var info = new FileInfo(path);
            result = info.Exists ? $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : $"{path}|missing";
            if (_open.TryGetValue(path, out var text) && !(info.Exists && SameText(path, text)))
            {
                result = $"{path}|open|{Convert.ToHexString(text.GetChecksum().AsSpan())}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = $"{path}|unreadable";
        }

        _files[path] = result;
        return result;
    }

    private static bool SameText(string path, SourceText text)
    {
        try
        {
            return SourceText.From(System.IO.File.ReadAllText(path)).ContentEquals(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
