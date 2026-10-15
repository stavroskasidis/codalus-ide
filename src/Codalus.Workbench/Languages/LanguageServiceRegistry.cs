using Codalus.Rpc;

namespace Codalus.Workbench.Languages;

/// <summary>
/// A language service: which files and projects it owns and how to start its host (an LSP server, ADR 0005).
/// <see cref="ProjectLanguage"/> is the design-time build's language (<c>C#</c>, <c>VB</c>, <c>F#</c>) whose projects
/// it receives, or null for services without a project model.
/// </summary>
public sealed record LanguageServiceDescriptor(
    string LanguageId,
    string DisplayName,
    IReadOnlyList<string> FileExtensions,
    string? ProjectLanguage,
    Func<ProcessLaunchInfo?> Launch)
{
    /// <summary>The language ids (<see cref="LanguageRegistry"/>) whose documents it serves; default: <see cref="LanguageId"/>.</summary>
    public IReadOnlyList<string>? Languages { get; init; }

    public System.Text.Json.JsonElement? InitializationOptions { get; init; }

    /// <summary>The settings section it reads through <c>workspace/configuration</c>.</summary>
    public string? SettingsSection { get; init; }

    /// <summary>Who registered it: <c>shell</c> or a plugin id.</summary>
    public string Source { get; init; } = "shell";

    /// <summary>
    /// A linter (ESLint, ADR 0027): it serves its languages' documents next to their language service, for diagnostics
    /// and quick fixes, and never answers completion, hover or navigation.
    /// </summary>
    public bool IsLinter { get; init; }

    /// <summary>Which documents a linter takes (those its configuration covers); null: all of its languages'.</summary>
    public Func<string, bool>? Accepts { get; init; }

    /// <summary>Settings by section and document (<c>scopeUri</c>), for servers whose settings depend on the file.</summary>
    public Func<string?, string?, System.Text.Json.JsonElement?>? ScopedConfiguration { get; init; }

    /// <summary>
    /// The server's own settings (<c>workspace/didChangeConfiguration</c>), sent instead of the shared ones: FsAutoComplete
    /// reads an <c>FSharp</c> section (M36). Read again each time the settings are sent.
    /// </summary>
    public Func<System.Text.Json.JsonElement>? Settings { get; init; }

    /// <summary>The server's own requests the client answers with null.</summary>
    public IReadOnlyList<string> AcceptedRequests { get; init; } = [];

    public bool Serves(string languageId) => (Languages ?? [LanguageId]).Contains(languageId, StringComparer.Ordinal);

    /// <summary>
    /// More design-time build languages whose projects it receives, beside <see cref="ProjectLanguage"/>: the C# host
    /// takes Visual Basic's too (M35), so references between C# and VB projects are within one workspace.
    /// </summary>
    public IReadOnlyList<string> MoreProjectLanguages { get; init; } = [];

    /// <summary>Whether it receives the projects of design-time build language <paramref name="language"/>.</summary>
    public bool TakesProjects(string language) => ProjectLanguage == language || MoreProjectLanguages.Contains(language, StringComparer.Ordinal);
}

/// <summary>
/// The registered language services: C# (the shell's built-in) and the LSP servers plugins contribute
/// (<c>contributes.languageServers</c>, ADR 0016).
/// </summary>
public sealed class LanguageServiceRegistry
{
    private readonly List<LanguageServiceDescriptor> _services = [];
    private readonly Lock _gate = new();

    public void Register(LanguageServiceDescriptor descriptor)
    {
        lock (_gate)
        {
            _services.RemoveAll(s => s.LanguageId == descriptor.LanguageId);
            _services.Add(descriptor);
        }
    }

    public IReadOnlyList<LanguageServiceDescriptor> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _services];
            }
        }
    }

    public LanguageServiceDescriptor? ForFile(string path)
    {
        var extension = Path.GetExtension(path);
        lock (_gate)
        {
            return _services.FirstOrDefault(s => !s.IsLinter && s.FileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>The service of a language id (a document's language), or null. Linters aren't.</summary>
    public LanguageServiceDescriptor? ForLanguage(string languageId)
    {
        lock (_gate)
        {
            return _services.LastOrDefault(s => !s.IsLinter && s.Serves(languageId));
        }
    }

    /// <summary>The linters of a language id.</summary>
    public IReadOnlyList<LanguageServiceDescriptor> LintersFor(string languageId)
    {
        lock (_gate)
        {
            return [.. _services.Where(s => s.IsLinter && s.Serves(languageId))];
        }
    }

    /// <summary>Removes the services <paramref name="source"/> (a plugin id) registered.</summary>
    public void Unregister(string source)
    {
        lock (_gate)
        {
            _services.RemoveAll(s => s.Source == source);
        }
    }

    public LanguageServiceDescriptor? ForProjectLanguage(string language)
    {
        lock (_gate)
        {
            return _services.FirstOrDefault(s => s.TakesProjects(language));
        }
    }

    public LanguageServiceDescriptor? Get(string languageId)
    {
        lock (_gate)
        {
            return _services.FirstOrDefault(s => s.LanguageId == languageId);
        }
    }
}
